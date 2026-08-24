using GameLauncher.Core.Api;
using GameLauncher.Core.Models;
using GameLauncher.Core.Platform;
using GameLauncher.Core.Updates;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using NSubstitute.ExceptionExtensions;

namespace GameLauncher.Core.Tests.Updates;

/// <summary>
/// The rejection path is the point. A check that accepts a genuine release and also accepts a
/// replayed one, or one signed by somebody else, is worth nothing at all — so every test here
/// but the first two is about something being refused.
/// </summary>
public sealed class UpdateCheckerTests
{
    private const string ArtifactUrl = "https://files.example.test/launcher/9f/86/9f86.zip";

    private readonly ILauncherReleaseApi _releases = Substitute.For<ILauncherReleaseApi>();

    private readonly IRuntimePlatform _runtime = Substitute.For<IRuntimePlatform>();

    public UpdateCheckerTests()
    {
        _runtime.Platform.Returns(GamePlatform.Windows);
        _runtime.Architecture.Returns(BuildArchitecture.X64);
    }

    private UpdateChecker CreateChecker(
        string currentVersion = "0.1.0",
        string channel = "stable",
        string publicKey = ReleaseSigningFixture.PublicKeyBase64) =>
        new(
            _releases,
            _runtime,
            new UpdateSettings
            {
                CurrentVersion = currentVersion,
                Channel = channel,
                PublicKeyBase64 = publicKey,
            },
            NullLogger<UpdateChecker>.Instance);

    private void ServerOffers(string document, string? signature = null, string url = ArtifactUrl) =>
        _releases
            .GetLatestAsync(
                Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(new LauncherReleaseResponse
            {
                Document = document,
                Signature = signature ?? ReleaseSigningFixture.Sign(document),
                Url = url,
            });

    [Fact]
    public async Task ASignedNewerReleaseIsOffered()
    {
        ServerOffers(ReleaseSigningFixture.CanonicalDocument);

        UpdateCheckResult result = await CreateChecker()
            .CheckAsync(TestContext.Current.CancellationToken);

        Assert.True(result.IsAvailable);
        Assert.Equal(new ReleaseVersion(0, 2, 0), result.Release!.Version);
        Assert.Equal(ArtifactUrl, result.ArtifactUrl);
    }

    [Fact]
    public async Task TheRequestNamesThisLaunchersChannelPlatformAndArchitecture()
    {
        _runtime.Platform.Returns(GamePlatform.MacOs);
        _runtime.Architecture.Returns(BuildArchitecture.Arm64);
        ServerOffers(ReleaseSigningFixture.DocumentFor("0.2.0", "beta", "macos", "arm64"));

        await CreateChecker(channel: "beta").CheckAsync(TestContext.Current.CancellationToken);

        await _releases.Received(1).GetLatestAsync(
            "beta", "macos", "arm64", Arg.Any<CancellationToken>());
    }

    // The replay. A correctly signed document that is simply old is the one attack a signature
    // cannot answer by itself.
    [Theory]
    [InlineData("0.1.0")]
    [InlineData("0.0.9")]
    public async Task AVersionThatIsNotStrictlyNewerIsRefused(string offered)
    {
        ServerOffers(ReleaseSigningFixture.DocumentFor(offered));

        UpdateCheckResult result = await CreateChecker(currentVersion: "0.1.0")
            .CheckAsync(TestContext.Current.CancellationToken);

        Assert.Equal(UpdateAvailability.UpToDate, result.Availability);
        Assert.Null(result.Release);
    }

    [Fact]
    public async Task ADocumentTamperedWithAfterSigningIsRefused()
    {
        string signature = ReleaseSigningFixture.Sign(ReleaseSigningFixture.CanonicalDocument);
        string tampered = ReleaseSigningFixture.CanonicalDocument.Replace(
            "\"size\":83442176", "\"size\":83442177", StringComparison.Ordinal);

        ServerOffers(tampered, signature);

        UpdateCheckResult result = await CreateChecker()
            .CheckAsync(TestContext.Current.CancellationToken);

        Assert.Equal(UpdateAvailability.Undetermined, result.Availability);
    }

    [Fact]
    public async Task ADocumentSignedByAnotherKeyIsRefused()
    {
        ServerOffers(
            ReleaseSigningFixture.CanonicalDocument,
            ReleaseSigningFixture.Sign(
                ReleaseSigningFixture.CanonicalDocument,
                ReleaseSigningFixture.OtherPrivateKeyBase64));

        UpdateCheckResult result = await CreateChecker()
            .CheckAsync(TestContext.Current.CancellationToken);

        Assert.Equal(UpdateAvailability.Undetermined, result.Availability);
    }

    // Signed by the right key and still not for this launcher. This is what signing the
    // document instead of the artifact buys: the platform is inside what the key vouched for.
    [Theory]
    [InlineData("stable", "linux", "x64")]
    [InlineData("stable", "windows", "arm64")]
    [InlineData("beta", "windows", "x64")]
    public async Task AReleaseForAnotherTargetIsRefusedEvenSigned(
        string channel, string platform, string arch)
    {
        ServerOffers(ReleaseSigningFixture.DocumentFor("0.2.0", channel, platform, arch));

        UpdateCheckResult result = await CreateChecker()
            .CheckAsync(TestContext.Current.CancellationToken);

        Assert.Equal(UpdateAvailability.Undetermined, result.Availability);
    }

    [Fact]
    public async Task ASignedDocumentThisLauncherCannotReadIsRefused()
    {
        ServerOffers("""{"schema":2,"whatever":true}""");

        UpdateCheckResult result = await CreateChecker()
            .CheckAsync(TestContext.Current.CancellationToken);

        Assert.Equal(UpdateAvailability.Undetermined, result.Availability);
    }

    // The URL is a convenience, not something to trust — and what schemes this client speaks is
    // this client's decision, since the server names the host.
    [Theory]
    [InlineData("ftp://files.example.test/launcher.zip")]
    [InlineData("file:///C:/Windows/System32/launcher.zip")]
    [InlineData("launcher.zip")]
    [InlineData("")]
    public async Task AnArtifactUrlThisClientWillNotFetchIsRefused(string url)
    {
        ServerOffers(ReleaseSigningFixture.CanonicalDocument, url: url);

        UpdateCheckResult result = await CreateChecker()
            .CheckAsync(TestContext.Current.CancellationToken);

        Assert.Equal(UpdateAvailability.Undetermined, result.Availability);
    }

    // Rule five: no key means no check, and the route is not even asked.
    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("not-a-key")]
    public async Task WithNoUsableKeyNothingIsAsked(string key)
    {
        UpdateCheckResult result = await CreateChecker(publicKey: key)
            .CheckAsync(TestContext.Current.CancellationToken);

        Assert.Equal(UpdateAvailability.NotConfigured, result.Availability);
        await _releases.DidNotReceiveWithAnyArgs().GetLatestAsync(
            default!, default!, default!, TestContext.Current.CancellationToken);
    }

    // No key on that server, nothing published, or nothing for this platform. From here they
    // are one situation, which is why the server answers 404 to all three.
    [Fact]
    public async Task A404MeansThereIsNothingToUpdateTo()
    {
        _releases
            .GetLatestAsync(
                Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .ThrowsAsync(new ApiException(ApiErrorCode.NotFound, "no release"));

        UpdateCheckResult result = await CreateChecker()
            .CheckAsync(TestContext.Current.CancellationToken);

        Assert.Equal(UpdateAvailability.UpToDate, result.Availability);
    }

    // Rule four. A launcher that would not open because it could not reach the update route
    // would be the worst possible outcome of this feature.
    [Fact]
    public async Task AnUnreachableServerIsNotAFailure()
    {
        _releases
            .GetLatestAsync(
                Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .ThrowsAsync(new ApiException(ApiErrorCode.Network, "no route to host"));

        UpdateCheckResult result = await CreateChecker()
            .CheckAsync(TestContext.Current.CancellationToken);

        Assert.Equal(UpdateAvailability.Undetermined, result.Availability);
    }

    [Fact]
    public async Task NeitherIsAnythingElseThatGoesWrong()
    {
        _releases
            .GetLatestAsync(
                Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .ThrowsAsync(new InvalidOperationException("something nobody has a story for"));

        UpdateCheckResult result = await CreateChecker()
            .CheckAsync(TestContext.Current.CancellationToken);

        Assert.Equal(UpdateAvailability.Undetermined, result.Availability);
    }

    // Cancellation is the caller's, and it is the one thing that is not swallowed.
    [Fact]
    public async Task CancellationIsNotSwallowed()
    {
        using CancellationTokenSource source = new();
        await source.CancelAsync();

        _releases
            .GetLatestAsync(
                Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .ThrowsAsync(new OperationCanceledException());

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => CreateChecker().CheckAsync(source.Token));
    }

    // Nothing can be called newer than a version nobody can read, and guessing would mean
    // accepting whatever arrived.
    [Fact]
    public async Task AnUnreadableCurrentVersionRefusesEverything()
    {
        ServerOffers(ReleaseSigningFixture.CanonicalDocument);

        UpdateCheckResult result = await CreateChecker(currentVersion: "dev")
            .CheckAsync(TestContext.Current.CancellationToken);

        Assert.Equal(UpdateAvailability.Undetermined, result.Availability);
    }
}

using GameLauncher.Core.Api;
using GameLauncher.Core.Configuration;
using GameLauncher.Core.Diagnostics;
using GameLauncher.Core.Models;
using GameLauncher.Core.Platform;
using GameLauncher.Infrastructure.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using NSubstitute.ExceptionExtensions;

namespace GameLauncher.Infrastructure.Tests.Logging;

public sealed class CrashReportUploaderTests : IDisposable
{
    private readonly TemporaryDirectory _root = new();
    private readonly ICrashReportApi _api = Substitute.For<ICrashReportApi>();
    private readonly IUserSettingsStore _settings = Substitute.For<IUserSettingsStore>();
    private readonly IServerCapabilityProvider _capabilities =
        Substitute.For<IServerCapabilityProvider>();

    private readonly IPathProvider _paths = Substitute.For<IPathProvider>();

    public CrashReportUploaderTests()
    {
        _paths.LogDirectory.Returns(_root.Path);
        OptedIn(true);
        ServerAccepts(true);
        _api.SubmitAsync(Arg.Any<CrashReport>(), Arg.Any<CancellationToken>())
            .Returns("f".PadRight(64, '0'));
    }

    public void Dispose() => _root.Dispose();

    private CrashReportUploader CreateUploader() => new(
        _api, _settings, _capabilities, _paths, NullLogger<CrashReportUploader>.Instance);

    private void OptedIn(bool value) =>
        _settings.LoadAsync(Arg.Any<CancellationToken>())
            .Returns(new UserSettings { SendCrashReports = value });

    private void ServerAccepts(bool value) =>
        _capabilities.GetAsync(Arg.Any<CancellationToken>())
            .Returns(ServerCapabilities.Fallback with
            {
                CrashReports = new CrashReportCapabilities { Enabled = value },
            });

    private string WritePending(string kind = "unhandled", DateTimeOffset? at = null)
    {
        DateTimeOffset occurredAt = at ?? DateTimeOffset.UnixEpoch;
        CrashReport report = new()
        {
            Kind = kind,
            OccurredAt = occurredAt,
            ExceptionType = "System.IO.IOException",
            Message = "broken",
            StackTrace = "at Thing.Do()",
        };

        string path = Path.Combine(_root.Path, CrashReportFiles.NameFor(occurredAt, kind));
        File.WriteAllText(path, CrashReportFiles.Serialize(report));
        return path;
    }

    private int PendingCount() =>
        Directory.GetFiles(_root.Path, CrashReportFiles.SearchPattern).Length;

    // ---------------------------------------------------------------------------

    [Fact]
    public async Task SendsAPendingReportAndTakesItOffDisk()
    {
        WritePending();

        CrashUploadResult result = await CreateUploader()
            .UploadPendingAsync(TestContext.Current.CancellationToken);

        Assert.Equal(1, result.Sent);
        Assert.Equal(0, PendingCount());
        await _api.Received(1).SubmitAsync(
            Arg.Is<CrashReport>(report => report!.ExceptionType == "System.IO.IOException"),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task DoesNothingWhenThereIsNothingToSend()
    {
        CrashUploadResult result = await CreateUploader()
            .UploadPendingAsync(TestContext.Current.CancellationToken);

        Assert.Equal(0, result.Sent);
        await _settings.DidNotReceive().LoadAsync(Arg.Any<CancellationToken>());
    }

    /// <summary>
    /// Not merely "do not send": somebody who said no should not have a growing pile of unsent
    /// crash reports about them accumulating on their own disk.
    /// </summary>
    [Fact]
    public async Task DiscardsPendingReportsWhenTheUserHasNotOptedIn()
    {
        OptedIn(false);
        WritePending();
        WritePending("startup", DateTimeOffset.UnixEpoch.AddMinutes(1));

        CrashUploadResult result = await CreateUploader()
            .UploadPendingAsync(TestContext.Current.CancellationToken);

        Assert.Equal(0, result.Sent);
        Assert.Equal(2, result.Discarded);
        Assert.Equal(0, PendingCount());
        await _api.DidNotReceive().SubmitAsync(
            Arg.Any<CrashReport>(), Arg.Any<CancellationToken>());
    }

    // A deployment that does not collect them will never take these, so carrying them forever
    // would be carrying them for nothing.
    [Fact]
    public async Task DiscardsThemWhenTheServerDoesNotAcceptReports()
    {
        ServerAccepts(false);
        WritePending();

        CrashUploadResult result = await CreateUploader()
            .UploadPendingAsync(TestContext.Current.CancellationToken);

        Assert.Equal(1, result.Discarded);
        Assert.Equal(0, PendingCount());
    }

    /// <summary>
    /// The unreachable case is the one that must not lose anything: the report is still on disk
    /// for the next start, which is the same mechanism that put it there.
    /// </summary>
    [Fact]
    public async Task KeepsAReportTheServerCouldNotBeAskedAbout()
    {
        WritePending();
        _api.SubmitAsync(Arg.Any<CrashReport>(), Arg.Any<CancellationToken>())
            .ThrowsAsync(new ApiException(ApiErrorCode.Network, "unreachable"));

        CrashUploadResult result = await CreateUploader()
            .UploadPendingAsync(TestContext.Current.CancellationToken);

        Assert.Equal(0, result.Sent);
        Assert.Equal(1, PendingCount());
    }

    [Fact]
    public async Task KeepsAReportTheServerAskedUsToSlowDownAbout()
    {
        WritePending();
        _api.SubmitAsync(Arg.Any<CrashReport>(), Arg.Any<CancellationToken>())
            .ThrowsAsync(new ApiException(ApiErrorCode.RateLimited, "too many"));

        await CreateUploader().UploadPendingAsync(TestContext.Current.CancellationToken);

        Assert.Equal(1, PendingCount());
    }

    // Refused for what it is, rather than for when it arrived: retrying would fail identically
    // forever, and the file would outlive the launcher version that produced it.
    [Fact]
    public async Task DiscardsAReportTheServerRefusedOutright()
    {
        WritePending();
        _api.SubmitAsync(Arg.Any<CrashReport>(), Arg.Any<CancellationToken>())
            .ThrowsAsync(new ApiException(ApiErrorCode.InvalidInput, "stack trace too long"));

        CrashUploadResult result = await CreateUploader()
            .UploadPendingAsync(TestContext.Current.CancellationToken);

        Assert.Equal(1, result.Discarded);
        Assert.Equal(0, PendingCount());
    }

    // One bad file must not block every real report behind it forever.
    [Fact]
    public async Task DiscardsAFileThatIsNotAReport()
    {
        File.WriteAllText(Path.Combine(_root.Path, "crash-truncated.json"), "{ not json");

        CrashUploadResult result = await CreateUploader()
            .UploadPendingAsync(TestContext.Current.CancellationToken);

        Assert.Equal(1, result.Discarded);
        Assert.Equal(0, PendingCount());
    }

    /// <summary>
    /// A launcher that crashed thirty times overnight has one bug, not thirty. The rest are
    /// left for the next start rather than spent against the server's rate limit.
    /// </summary>
    [Fact]
    public async Task SendsAtMostAHandfulPerStart()
    {
        for (int index = 0; index < 9; ++index)
        {
            WritePending("unhandled", DateTimeOffset.UnixEpoch.AddMinutes(index));
        }

        CrashUploadResult result = await CreateUploader()
            .UploadPendingAsync(TestContext.Current.CancellationToken);

        Assert.Equal(5, result.Sent);
        Assert.Equal(4, PendingCount());
    }

    // The file name begins with the timestamp, so a truncated run sends the earliest.
    [Fact]
    public async Task SendsTheOldestFirst()
    {
        WritePending("second", DateTimeOffset.UnixEpoch.AddHours(1));
        WritePending("first", DateTimeOffset.UnixEpoch);

        CrashReport? firstSent = null;
        await _api.SubmitAsync(
            Arg.Do<CrashReport>(report => firstSent ??= report), Arg.Any<CancellationToken>());

        await CreateUploader().UploadPendingAsync(TestContext.Current.CancellationToken);

        Assert.Equal("first", firstSent?.Kind);
    }

    /// <summary>
    /// A launcher that failed to start because it could not report a previous failure would be
    /// the worst possible outcome of this feature.
    /// </summary>
    [Fact]
    public async Task NeverThrows()
    {
        WritePending();
        _settings.LoadAsync(Arg.Any<CancellationToken>())
            .ThrowsAsync(new InvalidOperationException("the settings file is a directory"));

        CrashUploadResult result = await CreateUploader()
            .UploadPendingAsync(TestContext.Current.CancellationToken);

        Assert.Equal(0, result.Sent);
        Assert.Equal(1, PendingCount());
    }
}

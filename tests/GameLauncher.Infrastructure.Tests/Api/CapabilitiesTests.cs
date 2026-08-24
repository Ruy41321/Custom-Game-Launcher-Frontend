using System.Net;
using GameLauncher.Core.Api;
using GameLauncher.Core.Models;
using GameLauncher.Infrastructure.Api;
using GameLauncher.Infrastructure.Publishing;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using NSubstitute.ExceptionExtensions;

namespace GameLauncher.Infrastructure.Tests.Api;

public sealed class CapabilitiesApiClientTests
{
    private static readonly Uri BaseAddress = new("https://launcher.example/api/v1/");

    // The document the server's CapabilitiesController serialises. If a field is ever renamed,
    // this is the test that says so.
    private const string Document = """
        {
          "apiVersion": "v1",
          "serverVersion": "0.1.0",
          "uploads": { "maxChunkBytes": 2097152, "maxBlobBytes": 1073741824,
                       "maxOpenSessionsPerUser": 4, "sessionTtlSeconds": 3600,
                       "defaultQuotaBytes": 1073741824 },
          "manifest": { "maxPathLength": 512, "maxFiles": 1000 },
          "media": { "maxBytes": 1048576, "maxScreenshotsPerGame": 6, "maxAltTextLength": 120,
                     "contentTypes": ["image/png", "image/webp"] },
          "catalog": { "maxPageSize": 50, "defaultPageSize": 10, "maxPatchNotePageSize": 25 },
          "mail": { "enabled": false }
        }
        """;

    [Fact]
    public async Task ReadsEveryLimitTheServerPublishes()
    {
        var handler = StubHttpMessageHandler.RespondingWith(HttpStatusCode.OK, Document);
        var client = new CapabilitiesApiClient(new HttpClient(handler) { BaseAddress = BaseAddress });

        ServerCapabilities capabilities = await client.GetAsync(
            TestContext.Current.CancellationToken);

        Assert.Equal("/api/v1/capabilities", handler.LastRequest.PathAndQuery);
        Assert.Equal("v1", capabilities.ApiVersion);
        Assert.Equal(2 * 1024 * 1024, capabilities.Uploads.MaxChunkBytes);
        Assert.Equal(1024L * 1024 * 1024, capabilities.Uploads.MaxBlobBytes);
        Assert.Equal(512, capabilities.Manifest.MaxPathLength);
        Assert.Equal(1000, capabilities.Manifest.MaxFiles);
        Assert.Equal(6, capabilities.Media.MaxScreenshotsPerGame);
        Assert.Equal(["image/png", "image/webp"], capabilities.Media.ContentTypes);
        Assert.Equal(25, capabilities.Catalog.MaxPatchNotePageSize);
        Assert.False(capabilities.Mail.Enabled);
    }

    // The one capability whose fallback is *true*: a server too old to carry the key does send
    // mail, and reading its silence as "no mail" would hide the way back into an account on
    // every deployment that predates the field.
    [Fact]
    public async Task ADocumentWithNoMailSectionStillOffersTheResetLink()
    {
        var handler = StubHttpMessageHandler.RespondingWith(
            HttpStatusCode.OK, """{"apiVersion":"v1"}""");
        var client = new CapabilitiesApiClient(
            new HttpClient(handler) { BaseAddress = BaseAddress });

        ServerCapabilities capabilities = await client.GetAsync(
            TestContext.Current.CancellationToken);

        Assert.True(capabilities.Mail.Enabled);
        Assert.True(ServerCapabilities.Fallback.Mail.Enabled);
    }

    // The route needs no token, and asking for one would mean refreshing a session before the
    // launcher knows whether it can talk to this server at all.
    [Fact]
    public async Task SendsNoAuthorizationHeader()
    {
        var handler = StubHttpMessageHandler.RespondingWith(HttpStatusCode.OK, Document);
        var client = new CapabilitiesApiClient(new HttpClient(handler) { BaseAddress = BaseAddress });

        await client.GetAsync(TestContext.Current.CancellationToken);

        Assert.Null(handler.LastRequest.Authorization);
    }

    // A newer server may publish limits this client has never heard of.
    [Fact]
    public async Task IgnoresFieldsItDoesNotKnow()
    {
        const string withExtras = """
            { "apiVersion": "v1", "somethingNew": { "x": 1 },
              "uploads": { "maxChunkBytes": 4096, "unknownLimit": 7 } }
            """;

        var handler = StubHttpMessageHandler.RespondingWith(HttpStatusCode.OK, withExtras);
        var client = new CapabilitiesApiClient(new HttpClient(handler) { BaseAddress = BaseAddress });

        ServerCapabilities capabilities = await client.GetAsync(
            TestContext.Current.CancellationToken);

        Assert.Equal(4096, capabilities.Uploads.MaxChunkBytes);
    }

    // An older server sends only some of it, and the rest has to fall back rather than become
    // zero — a maxBlobBytes of 0 would refuse every file.
    [Fact]
    public async Task AbsentSectionsKeepTheirDefaults()
    {
        var handler = StubHttpMessageHandler.RespondingWith(
            HttpStatusCode.OK, """{ "apiVersion": "v1" }""");
        var client = new CapabilitiesApiClient(new HttpClient(handler) { BaseAddress = BaseAddress });

        ServerCapabilities capabilities = await client.GetAsync(
            TestContext.Current.CancellationToken);

        Assert.Equal(ServerCapabilities.Fallback.Uploads.MaxBlobBytes,
            capabilities.Uploads.MaxBlobBytes);
        Assert.Equal(ServerCapabilities.Fallback.Manifest.MaxFiles, capabilities.Manifest.MaxFiles);
    }
}

public sealed class CachedServerCapabilityProviderTests
{
    private readonly ICapabilitiesApi _api = Substitute.For<ICapabilitiesApi>();

    private readonly FakeTimeProvider _time = new(new DateTimeOffset(2026, 8, 5, 12, 0, 0, TimeSpan.Zero));

    private CachedServerCapabilityProvider CreateProvider() =>
        new(_api, _time, NullLogger<CachedServerCapabilityProvider>.Instance);

    private static ServerCapabilities Announcing(long chunkBytes) =>
        ServerCapabilities.Fallback with
        {
            Uploads = ServerCapabilities.Fallback.Uploads with { MaxChunkBytes = chunkBytes },
        };

    [Fact]
    public async Task AsksOnceAndThenAnswersFromMemory()
    {
        _api.GetAsync(Arg.Any<CancellationToken>()).Returns(Announcing(1024));
        CachedServerCapabilityProvider provider = CreateProvider();

        await provider.GetAsync(TestContext.Current.CancellationToken);
        ServerCapabilities second = await provider.GetAsync(TestContext.Current.CancellationToken);

        Assert.Equal(1024, second.Uploads.MaxChunkBytes);
        await _api.Received(1).GetAsync(Arg.Any<CancellationToken>());
    }

    // An operator who reconfigures a limit and restarts the server should not have to make
    // everybody restart their launcher.
    [Fact]
    public async Task AsksAgainOnceTheAnswerIsOldEnough()
    {
        _api.GetAsync(Arg.Any<CancellationToken>()).Returns(Announcing(1024));
        CachedServerCapabilityProvider provider = CreateProvider();
        await provider.GetAsync(TestContext.Current.CancellationToken);

        _time.Advance(CachedServerCapabilityProvider.Lifetime + TimeSpan.FromSeconds(1));
        _api.GetAsync(Arg.Any<CancellationToken>()).Returns(Announcing(4096));

        ServerCapabilities refreshed = await provider.GetAsync(
            TestContext.Current.CancellationToken);

        Assert.Equal(4096, refreshed.Uploads.MaxChunkBytes);
        await _api.Received(2).GetAsync(Arg.Any<CancellationToken>());
    }

    // Refusing to publish because a document *about* publishing could not be read would be
    // worse than the guessing this replaced.
    [Theory]
    [InlineData(ApiErrorCode.NotFound)]
    [InlineData(ApiErrorCode.Network)]
    [InlineData(ApiErrorCode.Unauthenticated)]
    public async Task AServerThatDoesNotAnswerMeansTheBuiltInDefaults(ApiErrorCode code)
    {
        _api.GetAsync(Arg.Any<CancellationToken>()).Throws(new ApiException(code, "no"));

        ServerCapabilities capabilities = await CreateProvider()
            .GetAsync(TestContext.Current.CancellationToken);

        Assert.Equal(ServerCapabilities.Fallback, capabilities);
    }

    // A failure is not cached: the server may simply have been down while the launcher started.
    [Fact]
    public async Task AFailureIsNotRemembered()
    {
        _api.GetAsync(Arg.Any<CancellationToken>()).Throws(new ApiException(ApiErrorCode.Network, "no"));
        CachedServerCapabilityProvider provider = CreateProvider();

        await provider.GetAsync(TestContext.Current.CancellationToken);

        _api.GetAsync(Arg.Any<CancellationToken>()).Returns(Announcing(2048));
        ServerCapabilities second = await provider.GetAsync(TestContext.Current.CancellationToken);

        Assert.Equal(2048, second.Uploads.MaxChunkBytes);
    }
}

public sealed class ChunkSizeTests
{
    [Fact]
    public void TheServersLimitIsWhatTravels()
    {
        Assert.Equal(
            2 * 1024 * 1024,
            BuildPublisher.ChunkSizeFor(Chunked(2 * 1024 * 1024)));
    }

    // A remote number reaching `new byte[]` unchecked is how a misconfigured deployment
    // becomes an out-of-memory failure on somebody's laptop.
    [Fact]
    public void AnAbsurdlyLargeAnnouncementIsClamped()
    {
        Assert.Equal(
            BuildPublisher.MaxChunkBytes,
            BuildPublisher.ChunkSizeFor(Chunked(8L * 1024 * 1024 * 1024)));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void AnAnswerThatMakesNoSenseFallsBack(long announced)
    {
        Assert.Equal(BuildPublisher.FallbackChunkBytes, BuildPublisher.ChunkSizeFor(Chunked(announced)));
    }

    // Inefficient, but correct. Sending more than a server allows is sending nothing at all.
    [Fact]
    public void AVeryStrictServerIsObeyedRatherThanRoundedUp()
    {
        Assert.Equal(128, BuildPublisher.ChunkSizeFor(Chunked(128)));
    }

    private static ServerCapabilities Chunked(long maxChunkBytes) =>
        ServerCapabilities.Fallback with
        {
            Uploads = ServerCapabilities.Fallback.Uploads with { MaxChunkBytes = maxChunkBytes },
        };
}

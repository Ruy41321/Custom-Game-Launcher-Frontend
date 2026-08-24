using System.Net;
using GameLauncher.Core.Api;
using GameLauncher.Infrastructure.Api;

namespace GameLauncher.Infrastructure.Tests.Api;

public sealed class LauncherReleaseApiClientTests
{
    private const string Body =
        """
        {"document":"{\"schema\":1,\"version\":\"0.2.0\"}","signature":"MEUCIQD","url":"https://files.example.test/l.zip"}
        """;

    private static (LauncherReleaseApiClient Client, StubHttpMessageHandler Handler) Create(
        HttpStatusCode status, string body)
    {
        StubHttpMessageHandler handler = StubHttpMessageHandler.RespondingWith(status, body);
        HttpClient http = new(handler) { BaseAddress = new Uri("http://localhost:8080/api/v1/") };

        return (new LauncherReleaseApiClient(http), handler);
    }

    [Fact]
    public async Task TheRouteCarriesTheChannelPlatformAndArchitecture()
    {
        (LauncherReleaseApiClient client, StubHttpMessageHandler handler) =
            Create(HttpStatusCode.OK, Body);

        await client.GetLatestAsync(
            "beta", "macos", "arm64", TestContext.Current.CancellationToken);

        Assert.Equal(
            "/api/v1/launcher/releases/latest?channel=beta&platform=macos&arch=arm64",
            handler.LastRequest.PathAndQuery);
    }

    // The route takes no token, and the client it runs on has no handler that could add one.
    // Asserted here as well as in the DI graph, because this is the request that goes out.
    [Fact]
    public async Task NoBearerTokenIsAttached()
    {
        (LauncherReleaseApiClient client, StubHttpMessageHandler handler) =
            Create(HttpStatusCode.OK, Body);

        await client.GetLatestAsync(
            "stable", "windows", "x64", TestContext.Current.CancellationToken);

        Assert.Null(handler.LastRequest.Authorization);
    }

    /// <summary>
    /// The document arrives as an opaque string and stays one. Deserialising it into an object
    /// here would hand the caller something whose signature it could no longer check.
    /// </summary>
    [Fact]
    public async Task TheDocumentComesBackAsTheTextThatWasSigned()
    {
        (LauncherReleaseApiClient client, _) = Create(HttpStatusCode.OK, Body);

        LauncherReleaseResponse response = await client.GetLatestAsync(
            "stable", "windows", "x64", TestContext.Current.CancellationToken);

        Assert.Equal("""{"schema":1,"version":"0.2.0"}""", response.Document);
        Assert.Equal("MEUCIQD", response.Signature);
        Assert.Equal("https://files.example.test/l.zip", response.Url);
    }

    [Fact]
    public async Task NothingPublishedIsANotFound()
    {
        (LauncherReleaseApiClient client, _) = Create(
            HttpStatusCode.NotFound,
            """{"status":404,"code":"not_found","detail":"no release"}""");

        ApiException exception = await Assert.ThrowsAsync<ApiException>(
            () => client.GetLatestAsync(
                "stable", "windows", "x64", TestContext.Current.CancellationToken));

        Assert.Equal(ApiErrorCode.NotFound, exception.Code);
    }
}

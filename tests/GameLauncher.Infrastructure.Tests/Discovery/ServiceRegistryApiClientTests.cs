using System.Net;
using GameLauncher.Infrastructure.Discovery;
using GameLauncher.Infrastructure.Tests.Api;
using Microsoft.Extensions.Logging.Abstractions;

namespace GameLauncher.Infrastructure.Tests.Discovery;

public sealed class ServiceRegistryApiClientTests
{
    private static readonly Uri Registry = new("https://registry.example.com/");

    private static ServiceRegistryApiClient Client(StubHttpMessageHandler handler) =>
        new(new HttpClient(handler), NullLogger<ServiceRegistryApiClient>.Instance);

    [Fact]
    public async Task AsksTheDocumentedRoute()
    {
        StubHttpMessageHandler handler = StubHttpMessageHandler.RespondingWith(
            HttpStatusCode.OK, """{"payload":"eyJhIjoxfQ=="}""");

        await Client(handler).GetSignedEndpointAsync(
            Registry, "game-launcher-api", "staging", TestContext.Current.CancellationToken);

        Assert.Equal(HttpMethod.Get, handler.LastRequest.Method);
        Assert.Equal(
            "/v1/services/game-launcher-api?environment=staging",
            handler.LastRequest.PathAndQuery);
    }

    /// <summary>
    /// The body comes back as text: the bytes are what the signature covers, so anything that
    /// parsed and re-encoded it would have destroyed the thing being checked.
    /// </summary>
    [Fact]
    public async Task ReturnsTheBodyExactlyAsItArrived()
    {
        const string body = """{"payload":"eyJhIjoxfQ==","signature":"MEUCIQ==","keyId":"abc"}""";
        StubHttpMessageHandler handler = StubHttpMessageHandler.RespondingWith(HttpStatusCode.OK, body);

        string? answer = await Client(handler).GetSignedEndpointAsync(
            Registry, "game-launcher-api", "production", TestContext.Current.CancellationToken);

        Assert.Equal(body, answer);
    }

    [Fact]
    public async Task CarriesNoAuthorizationHeader()
    {
        StubHttpMessageHandler handler = StubHttpMessageHandler.RespondingWith(HttpStatusCode.OK, "{}");

        await Client(handler).GetSignedEndpointAsync(
            Registry, "game-launcher-api", "production", TestContext.Current.CancellationToken);

        Assert.Null(handler.LastRequest.Authorization);
    }

    /// <summary>
    /// Every refusal is the same answer — none — because the caller's response to all of them
    /// is to keep the address it already has.
    /// </summary>
    [Theory]
    [InlineData(HttpStatusCode.NotFound)]
    [InlineData(HttpStatusCode.TooManyRequests)]
    [InlineData(HttpStatusCode.BadRequest)]
    [InlineData(HttpStatusCode.InternalServerError)]
    public async Task ReturnsNothingWhenTheRegistryRefuses(HttpStatusCode status)
    {
        StubHttpMessageHandler handler = StubHttpMessageHandler.RespondingWith(
            status, """{"error":"not_found"}""");

        Assert.Null(await Client(handler).GetSignedEndpointAsync(
            Registry, "game-launcher-api", "production", TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task ReturnsNothingWhenTheRegistryCannotBeReached()
    {
        StubHttpMessageHandler handler = StubHttpMessageHandler.Throwing(
            new HttpRequestException("no route to host"));

        Assert.Null(await Client(handler).GetSignedEndpointAsync(
            Registry, "game-launcher-api", "production", TestContext.Current.CancellationToken));
    }

    /// <summary>
    /// A registry that accepts the connection and then says nothing is the failure that
    /// actually happens (D78); the client's own timeout arrives as a cancellation nobody
    /// asked for, and it must not escape as an exception.
    /// </summary>
    [Fact]
    public async Task ReturnsNothingWhenTheRegistryHangs()
    {
        StubHttpMessageHandler handler = StubHttpMessageHandler.Hanging();
        ServiceRegistryApiClient client = new(
            new HttpClient(handler) { Timeout = TimeSpan.FromMilliseconds(200) },
            NullLogger<ServiceRegistryApiClient>.Instance);

        Assert.Null(await client.GetSignedEndpointAsync(
            Registry, "game-launcher-api", "production", TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task EscapesWhatItPutsInTheRoute()
    {
        StubHttpMessageHandler handler = StubHttpMessageHandler.RespondingWith(HttpStatusCode.OK, "{}");

        await Client(handler).GetSignedEndpointAsync(
            Registry, "a b/c", "production", TestContext.Current.CancellationToken);

        Assert.Equal("/v1/services/a%20b%2Fc?environment=production", handler.LastRequest.PathAndQuery);
    }
}

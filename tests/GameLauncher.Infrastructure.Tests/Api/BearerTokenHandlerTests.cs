using System.Net;
using GameLauncher.Core.Api;
using GameLauncher.Core.Authentication;
using GameLauncher.Core.Models;
using GameLauncher.Infrastructure.Api;
using NSubstitute;
using NSubstitute.ExceptionExtensions;

namespace GameLauncher.Infrastructure.Tests.Api;

public sealed class BearerTokenHandlerTests
{
    private static readonly Uri BaseAddress = new("https://launcher.example/api/v1/");

    private readonly IAuthenticationService _authentication =
        Substitute.For<IAuthenticationService>();

    private CatalogApiClient ClientOver(StubHttpMessageHandler handler)
    {
        var bearer = new BearerTokenHandler(_authentication) { InnerHandler = handler };
        return new CatalogApiClient(new HttpClient(bearer) { BaseAddress = BaseAddress });
    }

    [Fact]
    public async Task EveryRequestCarriesTheAccessToken()
    {
        _authentication.GetAccessTokenAsync(Arg.Any<CancellationToken>()).Returns("token-abc");
        var handler = StubHttpMessageHandler.RespondingWith(
            HttpStatusCode.OK, """{ "items": [], "total": 0, "limit": 20, "offset": 0 }""");

        await ClientOver(handler).ExploreAsync(
            new GameQuery(), TestContext.Current.CancellationToken);

        Assert.Equal("Bearer token-abc", handler.LastRequest.Authorization);
    }

    // The token is fetched per request rather than once, so a rotation that happened in
    // between is picked up without anybody having to rebuild the client.
    [Fact]
    public async Task TheTokenIsReadAtSendTimeAndNotCached()
    {
        _authentication.GetAccessTokenAsync(Arg.Any<CancellationToken>())
            .Returns("first", "second");
        var handler = StubHttpMessageHandler.RespondingWith(
            HttpStatusCode.OK, """{ "items": [], "total": 0, "limit": 20, "offset": 0 }""");
        CatalogApiClient client = ClientOver(handler);

        await client.ExploreAsync(new GameQuery(), TestContext.Current.CancellationToken);
        await client.ExploreAsync(new GameQuery(), TestContext.Current.CancellationToken);

        Assert.Equal("Bearer first", handler.Requests[0].Authorization);
        Assert.Equal("Bearer second", handler.Requests[1].Authorization);
    }

    // No session means the request is never sent at all: there is nothing the server could
    // usefully do with it, and a round trip to be told so is wasted.
    [Fact]
    public async Task NoSessionMeansTheRequestNeverLeaves()
    {
        _authentication.GetAccessTokenAsync(Arg.Any<CancellationToken>())
            .Throws(new ApiException(ApiErrorCode.Unauthenticated, "Not signed in."));
        var handler = StubHttpMessageHandler.RespondingWith(HttpStatusCode.OK, "{}");

        ApiException exception = await Assert.ThrowsAsync<ApiException>(
            () => ClientOver(handler).ExploreAsync(
                new GameQuery(), TestContext.Current.CancellationToken));

        Assert.Equal(ApiErrorCode.Unauthenticated, exception.Code);
        Assert.Empty(handler.Requests);
    }
}

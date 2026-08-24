using System.Net;
using GameLauncher.Core.Api;
using GameLauncher.Core.Models;
using GameLauncher.Infrastructure.Api;

namespace GameLauncher.Infrastructure.Tests.Api;

public sealed class LibraryApiClientTests
{
    private static readonly Uri BaseAddress = new("https://launcher.example/api/v1/");

    private static LibraryApiClient ClientOver(StubHttpMessageHandler handler) =>
        new(new HttpClient(handler) { BaseAddress = BaseAddress });

    [Fact]
    public async Task TheLibraryComesBackAsAFlatList()
    {
        const string body = """
            {
              "items": [
                { "id": "g1", "slug": "orbital-drift", "title": "Orbital Drift",
                  "releaseDate": "", "visibility": "public",
                  "createdAt": "2026-01-02T03:04:05Z", "updatedAt": "2026-01-02T03:04:05Z",
                  "publisher": { "id": "u1", "displayName": "Luigi" } }
              ],
              "total": 1
            }
            """;
        var handler = StubHttpMessageHandler.RespondingWith(HttpStatusCode.OK, body);

        IReadOnlyList<Game> games = await ClientOver(handler).GetLibraryAsync(
            TestContext.Current.CancellationToken);

        Assert.Equal("/api/v1/library", handler.LastRequest.PathAndQuery);
        Assert.Equal("Orbital Drift", Assert.Single(games).Title);
    }

    [Fact]
    public async Task AnEmptyLibraryIsAnEmptyListAndNotAFailure()
    {
        var handler = StubHttpMessageHandler.RespondingWith(
            HttpStatusCode.OK, """{ "items": [], "total": 0 }""");

        Assert.Empty(await ClientOver(handler).GetLibraryAsync(TestContext.Current.CancellationToken));
    }

    // PUT, because adding a game the account already has is not an error.
    [Fact]
    public async Task AddingIsAnIdempotentPut()
    {
        var handler = StubHttpMessageHandler.RespondingWith(
            HttpStatusCode.OK, """{ "status": "added" }""");

        await ClientOver(handler).AddAsync("orbital-drift", TestContext.Current.CancellationToken);

        Assert.Equal(HttpMethod.Put, handler.LastRequest.Method);
        Assert.Equal("/api/v1/library/orbital-drift", handler.LastRequest.PathAndQuery);
    }

    [Fact]
    public async Task RemovingIsADeleteOnTheGameId()
    {
        var handler = StubHttpMessageHandler.RespondingWith(
            HttpStatusCode.OK, """{ "status": "removed" }""");

        await ClientOver(handler).RemoveAsync("g1", TestContext.Current.CancellationToken);

        Assert.Equal(HttpMethod.Delete, handler.LastRequest.Method);
        Assert.Equal("/api/v1/library/g1", handler.LastRequest.PathAndQuery);
    }

    // A draft nobody may see is a 404, never a 403 — the server refuses to confirm that an
    // unannounced title exists, and the client must not turn that back into a confirmation.
    [Fact]
    public async Task AGameTheAccountMayNotSeeIsReportedAsMissing()
    {
        var handler = StubHttpMessageHandler.RespondingWith(
            HttpStatusCode.NotFound,
            """{ "code": "not_found", "detail": "The requested resource does not exist." }""");

        ApiException exception = await Assert.ThrowsAsync<ApiException>(
            () => ClientOver(handler).AddAsync("someones-draft", TestContext.Current.CancellationToken));

        Assert.Equal(ApiErrorCode.NotFound, exception.Code);
    }
}

using System.Net;
using GameLauncher.Core.Api;
using GameLauncher.Core.Models;
using GameLauncher.Infrastructure.Api;

namespace GameLauncher.Infrastructure.Tests.Api;

public sealed class CatalogApiClientTests
{
    private static readonly Uri BaseAddress = new("https://launcher.example/api/v1/");

    private static HttpClient ClientOver(StubHttpMessageHandler handler) =>
        new(handler) { BaseAddress = BaseAddress };

    // The exact document GameController::explore serialises. If the server ever renames a
    // field, this is the test that says so.
    private const string ExplorePage = """
        {
          "items": [
            {
              "id": "5f1d…",
              "slug": "orbital-drift",
              "title": "Orbital Drift",
              "summary": "A short one.",
              "description": "A long one.",
              "releaseDate": "2026-05-04",
              "visibility": "public",
              "createdAt": "2026-01-02T03:04:05Z",
              "updatedAt": "2026-01-02T03:04:05Z",
              "publisher": { "id": "u1", "displayName": "Luigi" }
            }
          ],
          "total": 41,
          "limit": 20,
          "offset": 20
        }
        """;

    [Fact]
    public async Task ExploreReadsEveryFieldTheServerSends()
    {
        var handler = StubHttpMessageHandler.RespondingWith(HttpStatusCode.OK, ExplorePage);
        var client = new CatalogApiClient(ClientOver(handler));

        PagedResult<Game> page = await client.ExploreAsync(
            new GameQuery(), TestContext.Current.CancellationToken);

        Game game = Assert.Single(page.Items);
        Assert.Equal("orbital-drift", game.Slug);
        Assert.Equal("Orbital Drift", game.Title);
        Assert.Equal(new DateOnly(2026, 5, 4), game.ReleaseDate);
        Assert.Equal(GameVisibility.Public, game.Visibility);
        Assert.Equal("Luigi", game.Publisher.DisplayName);
        Assert.Equal(41, page.Total);
        Assert.Equal(2, page.Page);
    }

    [Fact]
    public async Task ExploreSendsTheQueryAsParameters()
    {
        var handler = StubHttpMessageHandler.RespondingWith(HttpStatusCode.OK, ExplorePage);
        var client = new CatalogApiClient(ClientOver(handler));

        await client.ExploreAsync(
            new GameQuery { Search = "drift", Sort = GameSort.Title, Page = 2 },
            TestContext.Current.CancellationToken);

        Assert.Equal(
            "/api/v1/games?search=drift&sort=title&page=2", handler.LastRequest.PathAndQuery);
    }

    [Fact]
    public async Task APlainListingSendsNoQueryStringAtAll()
    {
        var handler = StubHttpMessageHandler.RespondingWith(HttpStatusCode.OK, ExplorePage);
        var client = new CatalogApiClient(ClientOver(handler));

        await client.ExploreAsync(new GameQuery(), TestContext.Current.CancellationToken);

        Assert.Equal("/api/v1/games", handler.LastRequest.PathAndQuery);
    }

    [Fact]
    public async Task TheDeveloperDashboardIsADifferentRoute()
    {
        var handler = StubHttpMessageHandler.RespondingWith(HttpStatusCode.OK, ExplorePage);
        var client = new CatalogApiClient(ClientOver(handler));

        await client.GetMyGamesAsync(new GameQuery(), TestContext.Current.CancellationToken);

        Assert.Equal("/api/v1/me/games", handler.LastRequest.PathAndQuery);
    }

    [Fact]
    public async Task GameDetailCarriesVersionsBuildsAndLibraryMembership()
    {
        const string detailJson = """
            {
              "game": { "id": "g1", "slug": "orbital-drift", "title": "Orbital Drift",
                        "releaseDate": "", "visibility": "unlisted",
                        "createdAt": "2026-01-02T03:04:05Z", "updatedAt": "2026-01-02T03:04:05Z",
                        "publisher": { "id": "u1", "displayName": "Luigi" } },
              "inLibrary": true,
              "versions": [
                { "id": "v1", "gameId": "g1", "semver": "0.2.0", "stage": "beta",
                  "releaseNotes": "Fixed the thing.", "publishedAt": "2026-02-01T00:00:00Z",
                  "published": true, "createdAt": "2026-01-30T00:00:00Z" }
              ],
              "builds": [
                { "id": "b1", "versionId": "v1", "platform": "windows", "architecture": "x64",
                  "status": "ready", "manifestSha256": "abc", "totalSizeBytes": 1234567,
                  "fileCount": 42, "entrypoint": "Game.exe", "launchArgs": "-windowed",
                  "createdAt": "2026-01-30T00:00:00Z", "readyAt": "2026-02-01T00:00:00Z" }
              ]
            }
            """;

        var handler = StubHttpMessageHandler.RespondingWith(HttpStatusCode.OK, detailJson);
        var client = new CatalogApiClient(ClientOver(handler));

        GameDetail detail = await client.GetGameAsync(
            "orbital-drift", TestContext.Current.CancellationToken);

        Assert.Equal("/api/v1/games/orbital-drift", handler.LastRequest.PathAndQuery);
        Assert.True(detail.InLibrary);
        Assert.Null(detail.Game.ReleaseDate);
        Assert.Equal(GameVisibility.Unlisted, detail.Game.Visibility);

        GameVersion version = Assert.Single(detail.Versions);
        Assert.Equal(BuildStage.Beta, version.Stage);
        Assert.True(version.Published);

        GameBuild build = Assert.Single(detail.Builds);
        Assert.Equal(GamePlatform.Windows, build.Platform);
        Assert.Equal(BuildArchitecture.X64, build.Architecture);
        Assert.Equal(BuildStatus.Ready, build.Status);
        Assert.Equal(1234567, build.TotalSizeBytes);
        Assert.Equal("Game.exe", build.Entrypoint);
    }

    // A slug is URL-safe, but an operator can rename a game and the client must not build a
    // broken path out of whatever it was handed.
    [Fact]
    public async Task IdentifiersAreEscapedIntoThePath()
    {
        var handler = StubHttpMessageHandler.RespondingWith(HttpStatusCode.OK, "{}");
        var client = new CatalogApiClient(ClientOver(handler));

        await client.GetGameAsync("a b/c", TestContext.Current.CancellationToken);

        Assert.Equal("/api/v1/games/a%20b%2Fc", handler.LastRequest.PathAndQuery);
    }
}

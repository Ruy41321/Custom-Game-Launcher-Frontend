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

    [Fact]
    public async Task GameDetailCarriesTheArtworkAndTheCoverRidesOnTheGame()
    {
        const string detailJson = """
            {
              "game": { "id": "g1", "slug": "orbital-drift", "title": "Orbital Drift",
                        "releaseDate": "", "visibility": "public",
                        "coverUrl": "https://files.example/media/ab/cd/abcd.png",
                        "createdAt": "2026-01-02T03:04:05Z", "updatedAt": "2026-01-02T03:04:05Z",
                        "publisher": { "id": "u1", "displayName": "Luigi" } },
              "inLibrary": false,
              "versions": [],
              "builds": [],
              "media": [
                { "id": "m3", "gameId": "g1", "kind": "screenshot",
                  "url": "https://files.example/media/33/33/3333.webp",
                  "contentType": "image/webp", "sizeBytes": 300, "altText": "Second",
                  "sortOrder": 2, "createdAt": "2026-01-03T00:00:00Z" },
                { "id": "m1", "gameId": "g1", "kind": "cover",
                  "url": "https://files.example/media/ab/cd/abcd.png",
                  "contentType": "image/png", "sizeBytes": 100, "altText": "The cover",
                  "sortOrder": 0, "createdAt": "2026-01-02T00:00:00Z" },
                { "id": "m2", "gameId": "g1", "kind": "screenshot",
                  "url": "https://files.example/media/22/22/2222.jpg",
                  "contentType": "image/jpeg", "sizeBytes": 200, "altText": "First",
                  "sortOrder": 1, "createdAt": "2026-01-04T00:00:00Z" }
              ]
            }
            """;

        var handler = StubHttpMessageHandler.RespondingWith(HttpStatusCode.OK, detailJson);
        var client = new CatalogApiClient(ClientOver(handler));

        GameDetail detail = await client.GetGameAsync(
            "orbital-drift", TestContext.Current.CancellationToken);

        Assert.True(detail.Game.HasCover);
        Assert.Equal("https://files.example/media/ab/cd/abcd.png", detail.Game.CoverUrl);

        GameMedia cover = Assert.IsType<GameMedia>(detail.Artwork(MediaKind.Cover));
        Assert.Equal("image/png", cover.ContentType);
        Assert.Equal("The cover", cover.AltText);
        Assert.Null(detail.Artwork(MediaKind.Banner));

        // Sorted by the publisher's order, not by the order the server happened to send.
        Assert.Equal(["m2", "m3"], detail.Screenshots.Select(item => item.Id));
    }

    // A game with no artwork must not read as a game whose artwork failed to arrive.
    [Fact]
    public async Task AGameWithNoArtworkHasAnEmptyCoverAndAnEmptyGallery()
    {
        var handler = StubHttpMessageHandler.RespondingWith(HttpStatusCode.OK, ExplorePage);
        var client = new CatalogApiClient(ClientOver(handler));

        PagedResult<Game> page = await client.ExploreAsync(
            new GameQuery(), TestContext.Current.CancellationToken);

        Game game = Assert.Single(page.Items);
        Assert.Equal(string.Empty, game.CoverUrl);
        Assert.False(game.HasCover);
    }

    [Fact]
    public async Task TheDevlogIsItsOwnPagedRoute()
    {
        const string devlogJson = """
            {
              "items": [
                { "id": "n1", "gameId": "g1", "versionId": "v1", "title": "Patch 0.2.0",
                  "bodyMarkdown": "## Fixed\nThe thing.", "publishedAt": "2026-02-01T00:00:00Z",
                  "published": true, "createdAt": "2026-01-30T00:00:00Z",
                  "updatedAt": "2026-01-31T00:00:00Z",
                  "author": { "id": "u1", "displayName": "Luigi" } },
                { "id": "n2", "gameId": "g1", "versionId": "", "title": "What we are up to",
                  "bodyMarkdown": "Notes.", "publishedAt": "", "published": false,
                  "createdAt": "2026-01-20T00:00:00Z", "updatedAt": "2026-01-20T00:00:00Z",
                  "author": { "id": "u1", "displayName": "Luigi" } }
              ],
              "total": 2, "limit": 10, "offset": 0
            }
            """;

        var handler = StubHttpMessageHandler.RespondingWith(HttpStatusCode.OK, devlogJson);
        var client = new CatalogApiClient(ClientOver(handler));

        PagedResult<PatchNote> page = await client.GetPatchNotesAsync(
            "orbital-drift", cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(
            "/api/v1/games/orbital-drift/patch-notes?page=1&pageSize=10",
            handler.LastRequest.PathAndQuery);

        PatchNote published = page.Items[0];
        Assert.Equal("Patch 0.2.0", published.Title);
        Assert.True(published.Published);
        Assert.True(published.HasVersion);
        Assert.Equal(new DateTimeOffset(2026, 2, 1, 0, 0, 0, TimeSpan.Zero), published.PublishedAt);
        Assert.Equal("Luigi", published.Author.DisplayName);

        // A draft: no version, and an empty date that must not become a parse failure.
        PatchNote draft = page.Items[1];
        Assert.False(draft.Published);
        Assert.False(draft.HasVersion);
        Assert.Null(draft.PublishedAt);
    }

    [Fact]
    public async Task TheDevlogAsksForThePageItWasGiven()
    {
        var handler = StubHttpMessageHandler.RespondingWith(
            HttpStatusCode.OK, """{ "items": [], "total": 0, "limit": 5, "offset": 10 }""");
        var client = new CatalogApiClient(ClientOver(handler));

        await client.GetPatchNotesAsync(
            "g1", page: 3, pageSize: 5, cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(
            "/api/v1/games/g1/patch-notes?page=3&pageSize=5", handler.LastRequest.PathAndQuery);
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

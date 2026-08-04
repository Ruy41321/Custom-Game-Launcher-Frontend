using System.Net;
using System.Text;
using GameLauncher.Core.Api;
using GameLauncher.Core.Models;
using GameLauncher.Infrastructure.Api;

namespace GameLauncher.Infrastructure.Tests.Api;

public sealed class PublishingApiClientTests
{
    private static readonly Uri BaseAddress = new("https://launcher.example/api/v1/");

    private const string GameJson = """
        {
          "id": "g1", "slug": "orbital-drift", "title": "Orbital Drift",
          "summary": "", "description": "", "releaseDate": "", "visibility": "draft",
          "createdAt": "2026-01-02T03:04:05Z", "updatedAt": "2026-01-02T03:04:05Z",
          "publisher": { "id": "u1", "displayName": "Luigi" }
        }
        """;

    private static HttpClient ClientOver(StubHttpMessageHandler handler) =>
        new(handler) { BaseAddress = BaseAddress };

    [Fact]
    public async Task CreatingAGameSendsWhatWasFilledInAndNothingElse()
    {
        var handler = StubHttpMessageHandler.RespondingWith(HttpStatusCode.Created, GameJson);
        var client = new PublishingApiClient(ClientOver(handler));

        Game game = await client.CreateGameAsync(
            new CreateGameRequest { Title = "Orbital Drift", Visibility = GameVisibility.Public },
            TestContext.Current.CancellationToken);

        Assert.Equal("/api/v1/games", handler.LastRequest.PathAndQuery);
        Assert.Equal(HttpMethod.Post, handler.LastRequest.Method);
        Assert.Equal(
            """{"title":"Orbital Drift","visibility":"public"}""", handler.LastRequest.Body);
        Assert.Equal("orbital-drift", game.Slug);
    }

    [Fact]
    public async Task ADateIsSentInTheFormTheServerParses()
    {
        var handler = StubHttpMessageHandler.RespondingWith(HttpStatusCode.Created, GameJson);
        var client = new PublishingApiClient(ClientOver(handler));

        await client.CreateGameAsync(
            new CreateGameRequest
            {
                Title = "Orbital Drift",
                ReleaseDate = new DateOnly(2026, 5, 4),
            },
            TestContext.Current.CancellationToken);

        Assert.Contains(
            """"releaseDate":"2026-05-04"""", handler.LastRequest.Body!, StringComparison.Ordinal);
    }

    // An absent field means "leave it alone", so a PATCH must not carry the ones nobody edited.
    [Fact]
    public async Task APatchCarriesOnlyTheFieldsThatChanged()
    {
        var handler = StubHttpMessageHandler.RespondingWith(HttpStatusCode.OK, GameJson);
        var client = new PublishingApiClient(ClientOver(handler));

        await client.UpdateGameAsync(
            "orbital-drift",
            new GameChanges { Summary = "A shorter one." },
            TestContext.Current.CancellationToken);

        Assert.Equal("/api/v1/games/orbital-drift", handler.LastRequest.PathAndQuery);
        Assert.Equal(HttpMethod.Patch, handler.LastRequest.Method);
        Assert.Equal("""{"summary":"A shorter one."}""", handler.LastRequest.Body);
    }

    [Fact]
    public async Task CreatingAVersionAndABuildUsesTheirNestedRoutes()
    {
        const string versionJson = """
            {"id":"v1","gameId":"g1","semver":"0.2.0","stage":"beta","releaseNotes":"",
             "publishedAt":"","published":false,"createdAt":"2026-01-02T03:04:05Z"}
            """;

        var handler = StubHttpMessageHandler.RespondingWith(HttpStatusCode.Created, versionJson);
        var client = new PublishingApiClient(ClientOver(handler));

        GameVersion version = await client.CreateVersionAsync(
            "orbital-drift",
            new CreateVersionRequest { Semver = "0.2.0", Stage = BuildStage.Beta, Publish = true },
            TestContext.Current.CancellationToken);

        Assert.Equal("/api/v1/games/orbital-drift/versions", handler.LastRequest.PathAndQuery);
        Assert.Equal(
            """{"semver":"0.2.0","stage":"beta","publish":true}""", handler.LastRequest.Body);
        Assert.Equal(BuildStage.Beta, version.Stage);
        Assert.False(version.Published);

        const string buildJson = """
            {"id":"b1","versionId":"v1","platform":"windows","architecture":"x64",
             "status":"uploading","manifestSha256":"","totalSizeBytes":0,"fileCount":0,
             "entrypoint":"","launchArgs":"","createdAt":"2026-01-02T03:04:05Z","readyAt":""}
            """;

        var buildHandler = StubHttpMessageHandler.RespondingWith(
            HttpStatusCode.Created, buildJson);
        var buildClient = new PublishingApiClient(ClientOver(buildHandler));

        GameBuild build = await buildClient.CreateBuildAsync(
            "orbital-drift",
            "v1",
            new CreateBuildRequest { Platform = GamePlatform.Windows },
            TestContext.Current.CancellationToken);

        Assert.Equal(
            "/api/v1/games/orbital-drift/versions/v1/builds", buildHandler.LastRequest.PathAndQuery);
        Assert.Equal(
            """{"platform":"windows","architecture":"x64"}""", buildHandler.LastRequest.Body);
        Assert.Equal(BuildStatus.Uploading, build.Status);
        Assert.Null(build.ReadyAt);
    }

    // This is the call that keeps a second build cost only what actually changed.
    [Fact]
    public async Task BlobNegotiationAsksAboutEveryBlobAndReadsBackOnlyTheMissingOnes()
    {
        var handler = StubHttpMessageHandler.RespondingWith(
            HttpStatusCode.OK, """{"missing":["53e5"]}""");
        var client = new PublishingApiClient(ClientOver(handler));

        IReadOnlyList<string> missing = await client.MissingBlobsAsync(
            "b1",
            [new BlobDeclaration("53e5", 21), new BlobDeclaration("8430", 56)],
            TestContext.Current.CancellationToken);

        Assert.Equal("/api/v1/builds/b1/blobs/missing", handler.LastRequest.PathAndQuery);
        Assert.Equal(
            """{"blobs":[{"sha256":"53e5","size":21},{"sha256":"8430","size":56}]}""",
            handler.LastRequest.Body);
        Assert.Equal(["53e5"], missing);
    }

    [Fact]
    public async Task OpeningAnUploadDeclaresTheBlobItIsFor()
    {
        const string sessionJson = """
            {"id":"s1","buildId":"b1","sha256":"53e5","sizeBytes":21,"receivedBytes":0,
             "status":"pending","complete":false}
            """;

        var handler = StubHttpMessageHandler.RespondingWith(HttpStatusCode.Created, sessionJson);
        var client = new PublishingApiClient(ClientOver(handler));

        UploadSession session = await client.BeginUploadAsync(
            "b1", new BlobDeclaration("53e5", 21), TestContext.Current.CancellationToken);

        Assert.Equal("/api/v1/builds/b1/uploads", handler.LastRequest.PathAndQuery);
        Assert.Equal("""{"sha256":"53e5","size":21}""", handler.LastRequest.Body);
        Assert.Equal("s1", session.Id);
        Assert.Equal(0, session.ReceivedBytes);
        Assert.False(session.Complete);
    }

    // The offset is mandatory on the wire: guessing on the client's behalf is exactly how a
    // resumed upload silently duplicates or skips a range.
    [Fact]
    public async Task AChunkCarriesItsOffsetAndItsBytes()
    {
        const string sessionJson = """
            {"id":"s1","buildId":"b1","sha256":"53e5","sizeBytes":21,"receivedBytes":21,
             "status":"completed","complete":true}
            """;

        var handler = StubHttpMessageHandler.RespondingWith(HttpStatusCode.OK, sessionJson);
        var client = new PublishingApiClient(ClientOver(handler));

        UploadSession session = await client.SendChunkAsync(
            "s1",
            10,
            Encoding.UTF8.GetBytes("the rest"),
            TestContext.Current.CancellationToken);

        Assert.Equal("/api/v1/uploads/s1", handler.LastRequest.PathAndQuery);
        Assert.Equal(HttpMethod.Patch, handler.LastRequest.Method);
        Assert.Equal("10", handler.LastRequest.Header("Upload-Offset"));
        Assert.Equal("application/offset+octet-stream", handler.LastRequest.ContentType);
        Assert.Equal("the rest", handler.LastRequest.Body);
        Assert.True(session.Complete);
    }

    [Fact]
    public async Task AWrongOffsetIsAConflictTheClientCanRecoverFrom()
    {
        var handler = StubHttpMessageHandler.RespondingWith(
            HttpStatusCode.Conflict,
            """{"code":"conflict","detail":"the session is at offset 4096","status":409}""");
        var client = new PublishingApiClient(ClientOver(handler));

        ApiException exception = await Assert.ThrowsAsync<ApiException>(() =>
            client.SendChunkAsync(
                "s1", 0, new byte[4], TestContext.Current.CancellationToken));

        Assert.Equal(ApiErrorCode.Conflict, exception.Code);
        Assert.Contains("4096", exception.Message, StringComparison.Ordinal);
    }

    // Sizes are deliberately absent: the server reads them back from the blobs it stored, so a
    // build cannot advertise a download size its content does not have.
    [Fact]
    public async Task TheManifestNamesPathsAndHashesAndNoSizes()
    {
        const string buildJson = """
            {"id":"b1","versionId":"v1","platform":"windows","architecture":"x64",
             "status":"ready","manifestSha256":"86e1","totalSizeBytes":77,"fileCount":2,
             "entrypoint":"Game.exe","launchArgs":"--fullscreen",
             "createdAt":"2026-01-02T03:04:05Z","readyAt":"2026-01-02T04:00:00Z"}
            """;

        var handler = StubHttpMessageHandler.RespondingWith(HttpStatusCode.OK, buildJson);
        var client = new PublishingApiClient(ClientOver(handler));

        GameBuild build = await client.SubmitManifestAsync(
            "b1",
            new ManifestSubmission
            {
                Files =
                [
                    new ManifestFile { Path = "Game.exe", Sha256 = "53e5", Executable = true },
                    new ManifestFile { Path = "data/pak", Sha256 = "8430" },
                ],
                Entrypoint = "Game.exe",
                LaunchArgs = "--fullscreen",
            },
            TestContext.Current.CancellationToken);

        Assert.Equal("/api/v1/builds/b1/manifest", handler.LastRequest.PathAndQuery);
        Assert.Equal(
            """
            {"files":[{"path":"Game.exe","sha256":"53e5","executable":true},{"path":"data/pak","sha256":"8430","executable":false}],"entrypoint":"Game.exe","launchArgs":"--fullscreen"}
            """.Trim(),
            handler.LastRequest.Body);

        Assert.Equal(BuildStatus.Ready, build.Status);
        Assert.Equal("86e1", build.ManifestSha256);
        Assert.Equal(77, build.TotalSizeBytes);
    }

    [Fact]
    public async Task AbandoningAnUploadDeletesTheSession()
    {
        var handler = StubHttpMessageHandler.RespondingWith(
            HttpStatusCode.OK, """{"status":"aborted"}""");
        var client = new PublishingApiClient(ClientOver(handler));

        await client.AbortUploadAsync("s1", TestContext.Current.CancellationToken);

        Assert.Equal("/api/v1/uploads/s1", handler.LastRequest.PathAndQuery);
        Assert.Equal(HttpMethod.Delete, handler.LastRequest.Method);
    }
}

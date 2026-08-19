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

    // A PATCH on a version, and the property that makes it worth having: an omitted field is
    // absent from the body, so publishing a version cannot rewrite its notes as a side effect.
    [Fact]
    public async Task PublishingAVersionPatchesOnlyThatField()
    {
        const string versionJson = """
            {"id":"v1","gameId":"g1","semver":"0.2.0","stage":"beta","releaseNotes":"",
             "publishedAt":"2026-01-02T03:04:05Z","published":true,
             "createdAt":"2026-01-02T03:04:05Z"}
            """;

        var handler = StubHttpMessageHandler.RespondingWith(HttpStatusCode.OK, versionJson);
        var client = new PublishingApiClient(ClientOver(handler));

        GameVersion version = await client.UpdateVersionAsync(
            "orbital-drift",
            "v1",
            new VersionChanges { Published = true },
            TestContext.Current.CancellationToken);

        Assert.Equal("/api/v1/games/orbital-drift/versions/v1", handler.LastRequest.PathAndQuery);
        Assert.Equal(HttpMethod.Patch, handler.LastRequest.Method);
        Assert.Equal("""{"published":true}""", handler.LastRequest.Body);
        Assert.True(version.Published);
    }

    [Fact]
    public async Task ABuildCarriesTheNameItWasGiven()
    {
        const string buildJson = """
            {"id":"b1","versionId":"v1","name":"Nightly","platform":"windows",
             "architecture":"x64","status":"uploading","manifestSha256":"","totalSizeBytes":0,
             "fileCount":0,"entrypoint":"","launchArgs":"",
             "createdAt":"2026-01-02T03:04:05Z","readyAt":""}
            """;

        var handler = StubHttpMessageHandler.RespondingWith(HttpStatusCode.Created, buildJson);
        var client = new PublishingApiClient(ClientOver(handler));

        GameBuild build = await client.CreateBuildAsync(
            "orbital-drift",
            "v1",
            new CreateBuildRequest { Platform = GamePlatform.Windows, Name = "Nightly" },
            TestContext.Current.CancellationToken);

        Assert.Equal(
            """{"platform":"windows","architecture":"x64","name":"Nightly"}""",
            handler.LastRequest.Body);
        Assert.Equal("Nightly", build.Name);
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

    // --- artwork ---------------------------------------------------------------------------

    private const string MediaJson = """
        {
          "id": "m1", "gameId": "g1", "kind": "screenshot",
          "url": "http://files.example/media/ab/cd/abcd.png",
          "contentType": "image/png", "sizeBytes": 4096,
          "altText": "The bridge", "sortOrder": 2,
          "createdAt": "2026-01-02T03:04:05Z"
        }
        """;

    // The image is the body and nothing else travels with it: no multipart, and everything
    // that describes the picture is a query parameter.
    [Fact]
    public async Task AnImageIsUploadedAsTheRawBodyWithItsDescriptionInTheQuery()
    {
        var handler = StubHttpMessageHandler.RespondingWith(HttpStatusCode.Created, MediaJson);
        var client = new PublishingApiClient(ClientOver(handler));

        byte[] png = [0x89, (byte)'P', (byte)'N', (byte)'G', 0x0D, 0x0A, 0x1A, 0x0A, 0x01, 0x02];

        GameMedia media = await client.UploadMediaAsync(
            "orbital-drift",
            new MediaUpload
            {
                Kind = MediaKind.Screenshot,
                Content = png,
                AltText = "The bridge",
                SortOrder = 2,
            },
            TestContext.Current.CancellationToken);

        Assert.Equal(HttpMethod.Post, handler.LastRequest.Method);
        Assert.Equal(
            "/api/v1/games/orbital-drift/media?kind=screenshot&altText=The%20bridge&sortOrder=2",
            handler.LastRequest.PathAndQuery);
        Assert.Equal(png, handler.LastRequest.Bytes);
        Assert.Equal("m1", media.Id);
        Assert.Equal(MediaKind.Screenshot, media.Kind);
    }

    // The server decides what the bytes are and never reads this header, so declaring an image
    // type would be a guess dressed as a fact — and an invitation to trust it later.
    [Fact]
    public async Task AnImageUploadDeclaresNoImageType()
    {
        var handler = StubHttpMessageHandler.RespondingWith(HttpStatusCode.Created, MediaJson);
        var client = new PublishingApiClient(ClientOver(handler));

        await client.UploadMediaAsync(
            "g1",
            new MediaUpload { Kind = MediaKind.Cover, Content = new byte[] { 0xFF, 0xD8, 0xFF } },
            TestContext.Current.CancellationToken);

        Assert.Equal("application/octet-stream", handler.LastRequest.ContentType);
    }

    // The server's enum is lower case; a C# enum's ToString() is not.
    [Theory]
    [InlineData(MediaKind.Cover, "cover")]
    [InlineData(MediaKind.Banner, "banner")]
    [InlineData(MediaKind.Logo, "logo")]
    [InlineData(MediaKind.Screenshot, "screenshot")]
    public async Task TheKindIsSpelledTheWayTheServerSpellsIt(MediaKind kind, string wire)
    {
        var handler = StubHttpMessageHandler.RespondingWith(HttpStatusCode.Created, MediaJson);
        var client = new PublishingApiClient(ClientOver(handler));

        await client.UploadMediaAsync(
            "g1",
            new MediaUpload { Kind = kind, Content = new byte[] { 0xFF, 0xD8, 0xFF } },
            TestContext.Current.CancellationToken);

        Assert.Contains(
            "kind=" + wire, handler.LastRequest.PathAndQuery, StringComparison.Ordinal);
    }

    [Fact]
    public async Task EditingAPictureSendsOnlyWhatChanged()
    {
        var handler = StubHttpMessageHandler.RespondingWith(HttpStatusCode.OK, MediaJson);
        var client = new PublishingApiClient(ClientOver(handler));

        await client.UpdateMediaAsync(
            "m1", new MediaChanges { SortOrder = 5 }, TestContext.Current.CancellationToken);

        Assert.Equal(HttpMethod.Patch, handler.LastRequest.Method);
        Assert.Equal("/api/v1/media/m1", handler.LastRequest.PathAndQuery);
        Assert.Equal("""{"sortOrder":5}""", handler.LastRequest.Body);
    }

    [Fact]
    public async Task RemovingAPictureIsADeleteOnTheMediaId()
    {
        var handler = StubHttpMessageHandler.RespondingWith(HttpStatusCode.NoContent, "{}");
        var client = new PublishingApiClient(ClientOver(handler));

        await client.DeleteMediaAsync("m1", TestContext.Current.CancellationToken);

        Assert.Equal(HttpMethod.Delete, handler.LastRequest.Method);
        Assert.Equal("/api/v1/media/m1", handler.LastRequest.PathAndQuery);
    }

    // --- the devlog ------------------------------------------------------------------------

    private const string PatchNoteJson = """
        {
          "id": "n1", "gameId": "g1", "versionId": "",
          "title": "Docking rework", "bodyMarkdown": "It is better now.",
          "publishedAt": "", "published": false,
          "createdAt": "2026-01-02T03:04:05Z", "updatedAt": "2026-01-02T03:04:05Z",
          "author": { "id": "u1", "displayName": "Luigi" }
        }
        """;

    [Fact]
    public async Task ADraftIsWrittenWithPublishFalse()
    {
        var handler = StubHttpMessageHandler.RespondingWith(HttpStatusCode.Created, PatchNoteJson);
        var client = new PublishingApiClient(ClientOver(handler));

        PatchNote note = await client.CreatePatchNoteAsync(
            "orbital-drift",
            new CreatePatchNoteRequest
            {
                Title = "Docking rework",
                BodyMarkdown = "It is better now.",
                Publish = false,
            },
            TestContext.Current.CancellationToken);

        Assert.Equal("/api/v1/games/orbital-drift/patch-notes", handler.LastRequest.PathAndQuery);
        Assert.Equal(
            """{"title":"Docking rework","bodyMarkdown":"It is better now.","publish":false}""",
            handler.LastRequest.Body);
        Assert.False(note.Published);
        Assert.Null(note.PublishedAt);
    }

    // Publishing and withdrawing are the same field, because a note that went out by mistake
    // has to be able to come back.
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task PublishingAndWithdrawingAreTheSameCall(bool published)
    {
        var handler = StubHttpMessageHandler.RespondingWith(HttpStatusCode.OK, PatchNoteJson);
        var client = new PublishingApiClient(ClientOver(handler));

        await client.UpdatePatchNoteAsync(
            "n1",
            new PatchNoteChanges { Published = published },
            TestContext.Current.CancellationToken);

        Assert.Equal(HttpMethod.Patch, handler.LastRequest.Method);
        Assert.Equal("/api/v1/patch-notes/n1", handler.LastRequest.PathAndQuery);
        Assert.Equal(
            $$"""{"published":{{(published ? "true" : "false")}}}""", handler.LastRequest.Body);
    }

    // An absent field means "leave it alone", so detaching a note from its version is the one
    // thing that has to be said with an empty string rather than with a null.
    [Fact]
    public async Task DetachingANoteFromItsVersionSendsAnEmptyVersionId()
    {
        var handler = StubHttpMessageHandler.RespondingWith(HttpStatusCode.OK, PatchNoteJson);
        var client = new PublishingApiClient(ClientOver(handler));

        await client.UpdatePatchNoteAsync(
            "n1",
            new PatchNoteChanges { VersionId = string.Empty },
            TestContext.Current.CancellationToken);

        Assert.Equal("""{"versionId":""}""", handler.LastRequest.Body);
    }

    [Fact]
    public async Task RemovingADevlogEntryIsADeleteOnTheNoteId()
    {
        var handler = StubHttpMessageHandler.RespondingWith(HttpStatusCode.NoContent, "{}");
        var client = new PublishingApiClient(ClientOver(handler));

        await client.DeletePatchNoteAsync("n1", TestContext.Current.CancellationToken);

        Assert.Equal(HttpMethod.Delete, handler.LastRequest.Method);
        Assert.Equal("/api/v1/patch-notes/n1", handler.LastRequest.PathAndQuery);
    }

    // --- taking things back ------------------------------------------------------------------

    [Fact]
    public async Task ABuildIsDeletedByItsOwnId()
    {
        var handler = StubHttpMessageHandler.RespondingWith(HttpStatusCode.NoContent, "{}");
        var client = new PublishingApiClient(ClientOver(handler));

        await client.DeleteBuildAsync("b1", TestContext.Current.CancellationToken);

        Assert.Equal(HttpMethod.Delete, handler.LastRequest.Method);
        Assert.Equal("/api/v1/builds/b1", handler.LastRequest.PathAndQuery);
    }

    [Fact]
    public async Task AVersionIsDeletedUnderItsGame()
    {
        var handler = StubHttpMessageHandler.RespondingWith(HttpStatusCode.NoContent, "{}");
        var client = new PublishingApiClient(ClientOver(handler));

        await client.DeleteVersionAsync(
            "orbital-drift", "v1", TestContext.Current.CancellationToken);

        Assert.Equal(HttpMethod.Delete, handler.LastRequest.Method);
        Assert.Equal("/api/v1/games/orbital-drift/versions/v1", handler.LastRequest.PathAndQuery);
    }

    [Fact]
    public async Task AGameIsDeletedByIdOrSlug()
    {
        var handler = StubHttpMessageHandler.RespondingWith(HttpStatusCode.NoContent, "{}");
        var client = new PublishingApiClient(ClientOver(handler));

        await client.DeleteGameAsync("orbital-drift", TestContext.Current.CancellationToken);

        Assert.Equal(HttpMethod.Delete, handler.LastRequest.Method);
        Assert.Equal("/api/v1/games/orbital-drift", handler.LastRequest.PathAndQuery);
    }

    // A slug is publisher-chosen text on a path segment, so it is escaped like every other id
    // this client puts in a URL.
    [Fact]
    public async Task ADeletedGameIdIsEscaped()
    {
        var handler = StubHttpMessageHandler.RespondingWith(HttpStatusCode.NoContent, "{}");
        var client = new PublishingApiClient(ClientOver(handler));

        await client.DeleteGameAsync("a/b", TestContext.Current.CancellationToken);

        Assert.Equal("/api/v1/games/a%2Fb", handler.LastRequest.PathAndQuery);
    }
}

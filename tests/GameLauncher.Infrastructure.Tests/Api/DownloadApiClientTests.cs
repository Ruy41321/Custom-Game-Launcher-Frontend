using System.Net;
using System.Security.Cryptography;
using System.Text;
using GameLauncher.Core.Api;
using GameLauncher.Core.Models;
using GameLauncher.Infrastructure.Api;

namespace GameLauncher.Infrastructure.Tests.Api;

public sealed class DownloadApiClientTests
{
    private static readonly Uri BaseAddress = new("https://launcher.example/api/v1/");

    // The exact document DownloadJson::downloadPlanToJson serialises.
    private const string DeltaPlan = """
        {
          "buildId": "b2", "gameId": "g1", "versionId": "v2",
          "kind": "delta",
          "manifestSha256": "86e1", "entrypoint": "Game.exe", "launchArgs": "--fullscreen",
          "files": [
            { "path": "Game.exe", "sha256": "53e5", "size": 21, "executable": true,
              "url": "http://files.example/files/53/e5/53e5?token=abc&expires=1785774748" },
            { "path": "data/moved.pak", "sha256": "8430", "size": 56, "executable": false,
              "url": "http://files.example/files/84/30/8430?token=def&expires=1785774748",
              "copyFrom": "data/pak" }
          ],
          "unchanged": [
            { "path": "data/pak", "sha256": "8430", "size": 56, "executable": false }
          ],
          "remove": ["old.dll"],
          "downloadBytes": 21, "totalBytes": 77,
          "urlsExpireAt": "2026-08-03T16:32:28Z"
        }
        """;

    private static HttpClient ClientOver(StubHttpMessageHandler handler) =>
        new(handler) { BaseAddress = BaseAddress };

    [Fact]
    public async Task ThePlanIsReadWithEveryFieldTheServerSends()
    {
        var handler = StubHttpMessageHandler.RespondingWith(HttpStatusCode.OK, DeltaPlan);
        var client = new DownloadApiClient(ClientOver(handler));

        DownloadPlan plan = await client.GetPlanAsync(
            "b2", "b1", TestContext.Current.CancellationToken);

        Assert.Equal("/api/v1/builds/b2/download", handler.LastRequest.PathAndQuery);
        Assert.Equal(HttpMethod.Post, handler.LastRequest.Method);
        Assert.Equal("""{"fromBuildId":"b1"}""", handler.LastRequest.Body);

        Assert.Equal(DownloadKind.Delta, plan.Kind);
        Assert.Equal("Game.exe", plan.Entrypoint);
        Assert.Equal(21, plan.DownloadBytes);
        Assert.Equal(77, plan.TotalBytes);
        Assert.Equal(
            new DateTimeOffset(2026, 8, 3, 16, 32, 28, TimeSpan.Zero), plan.UrlsExpireAt);
        Assert.Equal(["old.dll"], plan.Remove);
        Assert.Equal(2, plan.Files.Count);
        Assert.True(plan.Files[0].Executable);
        Assert.Equal(56, Assert.Single(plan.Unchanged).Size);
    }

    // The hint is an optimisation, and the client has to be able to tell it apart from a file
    // that genuinely has to travel — that difference is the whole point of a delta.
    [Fact]
    public async Task AFileThatOnlyMovedCarriesTheLocalPathToCopyFrom()
    {
        var handler = StubHttpMessageHandler.RespondingWith(HttpStatusCode.OK, DeltaPlan);
        var client = new DownloadApiClient(ClientOver(handler));

        DownloadPlan plan = await client.GetPlanAsync(
            "b2", "b1", TestContext.Current.CancellationToken);

        Assert.False(plan.Files[0].CanBeCopiedLocally);
        Assert.Null(plan.Files[0].CopyFrom);
        Assert.True(plan.Files[1].CanBeCopiedLocally);
        Assert.Equal("data/pak", plan.Files[1].CopyFrom);
    }

    [Fact]
    public async Task AFirstInstallNamesNoPreviousBuild()
    {
        var handler = StubHttpMessageHandler.RespondingWith(HttpStatusCode.OK, DeltaPlan);
        var client = new DownloadApiClient(ClientOver(handler));

        await client.GetPlanAsync("b2", cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal("{}", handler.LastRequest.Body);
    }

    [Fact]
    public async Task VerifySendsWhatWasFoundOnDiskAndReadsTheReport()
    {
        const string report = """
            {
              "buildId": "b2", "manifestSha256": "86e1", "intact": false,
              "missing": ["data/pak"], "corrupt": ["Game.exe"], "unexpected": ["saves/slot1"],
              "repair": [
                { "path": "Game.exe", "sha256": "53e5", "size": 21, "executable": true,
                  "url": "http://files.example/files/53/e5/53e5?token=abc&expires=1" }
              ],
              "repairBytes": 77, "urlsExpireAt": "2026-08-03T16:32:28Z"
            }
            """;

        var handler = StubHttpMessageHandler.RespondingWith(HttpStatusCode.OK, report);
        var client = new DownloadApiClient(ClientOver(handler));

        IntegrityReport result = await client.VerifyAsync(
            "b2",
            [new InstalledFile("Game.exe", "beef")],
            TestContext.Current.CancellationToken);

        Assert.Equal("/api/v1/builds/b2/verify", handler.LastRequest.PathAndQuery);
        Assert.Equal(
            """{"files":[{"path":"Game.exe","sha256":"beef"}]}""", handler.LastRequest.Body);

        Assert.False(result.Intact);
        Assert.Equal(["data/pak"], result.Missing);
        Assert.Equal(["Game.exe"], result.Corrupt);
        Assert.Equal(["saves/slot1"], result.Unexpected);
        Assert.Equal(21, Assert.Single(result.Repair).Size);
        Assert.Equal(77, result.RepairBytes);
    }

    [Fact]
    public async Task TheManifestIsAcceptedWhenItHashesToWhatWasPromised()
    {
        const string document =
            """{"schema":1,"entrypoint":"Game.exe","launchArgs":"-w","files":""" +
            """[{"path":"Game.exe","sha256":"53e5","size":21,"executable":true}]}""";

        var handler = StubHttpMessageHandler.RespondingWith(HttpStatusCode.OK, document);
        var client = new DownloadApiClient(ClientOver(handler));

        BuildManifest manifest = await client.GetManifestAsync(
            "b2", Sha256Of(document), TestContext.Current.CancellationToken);

        Assert.Equal("/api/v1/builds/b2/manifest", handler.LastRequest.PathAndQuery);
        Assert.Equal(1, manifest.Schema);
        Assert.Equal("Game.exe", manifest.Entrypoint);
        Assert.Equal("-w", manifest.LaunchArgs);
        Assert.Equal(Sha256Of(document), manifest.Sha256);
        Assert.Equal(21, Assert.Single(manifest.Files).Size);
        Assert.Equal(21, manifest.TotalBytes);
    }

    // The server serves the exact bytes its hash covers, so anything else means the document
    // was altered on the way. Parsing it anyway would install a build nobody published.
    [Fact]
    public async Task AManifestThatHashesToSomethingElseIsRefusedRatherThanParsed()
    {
        const string document = """{"schema":1,"entrypoint":"Game.exe","launchArgs":"","files":[]}""";

        var handler = StubHttpMessageHandler.RespondingWith(HttpStatusCode.OK, document);
        var client = new DownloadApiClient(ClientOver(handler));

        ApiException exception = await Assert.ThrowsAsync<ApiException>(() =>
            client.GetManifestAsync("b2", new string('a', 64), TestContext.Current.CancellationToken));

        Assert.Equal(ApiErrorCode.Integrity, exception.Code);
        Assert.Contains(Sha256Of(document), exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ABuildTheAccountMayNotSeeIsReportedAsNotFound()
    {
        var handler = StubHttpMessageHandler.RespondingWith(
            HttpStatusCode.NotFound,
            """{"code":"not_found","title":"Not found","status":404,"requestId":"01H"}""");
        var client = new DownloadApiClient(ClientOver(handler));

        ApiException exception = await Assert.ThrowsAsync<ApiException>(() =>
            client.GetPlanAsync("b2", cancellationToken: TestContext.Current.CancellationToken));

        Assert.Equal(ApiErrorCode.NotFound, exception.Code);
        Assert.Equal("01H", exception.RequestId);
    }

    private static string Sha256Of(string text) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(text)));
}

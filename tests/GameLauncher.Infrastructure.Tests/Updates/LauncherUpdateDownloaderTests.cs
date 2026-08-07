using System.Net;
using System.Security.Cryptography;
using System.Text;
using GameLauncher.Core.Api;
using GameLauncher.Core.Platform;
using GameLauncher.Core.Updates;
using GameLauncher.Infrastructure.Tests.Progressing;
using GameLauncher.Infrastructure.Updates;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;

namespace GameLauncher.Infrastructure.Tests.Updates;

/// <summary>
/// The third of the five rules the server asks a client to hold: bytes that do not hash to the
/// content address inside the signed document are refused. It is what makes the URL safe to
/// follow at all — an attacker who could rewrite it entirely would still have to produce bytes
/// hashing to a value somebody signed.
/// </summary>
public sealed class LauncherUpdateDownloaderTests : IDisposable
{
    private const string Url = "https://files.example.test/launcher/ab/cd/abcd.zip";

    private static readonly byte[] Artifact =
        Encoding.UTF8.GetBytes("a self-contained launcher, pretend it is 80 MB");

    private readonly TemporaryDirectory _directory = new();

    private readonly IPathProvider _paths = Substitute.For<IPathProvider>();

    public LauncherUpdateDownloaderTests() =>
        _paths.UpdateDirectory.Returns(Path.Combine(_directory.Path, "updates"));

    public void Dispose() => _directory.Dispose();

    private static string HashOf(byte[] bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes));

    private static ReleaseDocument Release(byte[] artifact) => new()
    {
        Version = new ReleaseVersion(0, 2, 0),
        Platform = "windows",
        Arch = "x64",
        Sha256 = HashOf(artifact),
        Size = artifact.Length,
        ReleasedAt = "2026-08-07T10:00:00Z",
    };

    private LauncherUpdateDownloader CreateDownloader(HttpMessageHandler handler) =>
        new(new HttpClient(handler), _paths, NullLogger<LauncherUpdateDownloader>.Instance);

    [Fact]
    public async Task AnArtifactThatHashesToWhatWasSignedLandsOnDisk()
    {
        ByteServingHandler handler = new(Artifact);

        string path = await CreateDownloader(handler).DownloadAsync(
            Release(Artifact), Url, cancellationToken: TestContext.Current.CancellationToken);

        Assert.True(File.Exists(path));
        Assert.Equal(
            Artifact,
            await File.ReadAllBytesAsync(path, TestContext.Current.CancellationToken));

        // Under the user's data directory, and filed by the version it is: the application
        // directory is read-only after install and is the thing an update replaces.
        Assert.Equal("0.2.0", Path.GetFileName(Path.GetDirectoryName(path)));
    }

    // The document and its signature are perfectly intact here. This is the check that says the
    // *file* is the thing the key vouched for.
    [Fact]
    public async Task AnArtifactThatIsNotTheOneNamedIsRefused()
    {
        byte[] tampered = [.. Artifact];
        tampered[0] ^= 0xFF;

        ByteServingHandler handler = new(tampered);

        ApiException exception = await Assert.ThrowsAsync<ApiException>(
            () => CreateDownloader(handler).DownloadAsync(
                Release(Artifact), Url, cancellationToken: TestContext.Current.CancellationToken));

        Assert.Equal(ApiErrorCode.Integrity, exception.Code);

        // Nothing is left behind that a later run could mistake for a verified archive.
        Assert.Empty(Directory.GetFiles(
            Path.Combine(_paths.UpdateDirectory, "0.2.0"), "*", SearchOption.AllDirectories));
    }

    // The signed document says how big the artifact is, so a host that keeps sending is stopped
    // there rather than after however many gigabytes it felt like sending.
    [Fact]
    public async Task AResponseLongerThanTheDocumentDeclaresIsCutOff()
    {
        byte[] longer = [.. Artifact, .. Artifact];
        ByteServingHandler handler = new(longer);

        ApiException exception = await Assert.ThrowsAsync<ApiException>(
            () => CreateDownloader(handler).DownloadAsync(
                Release(Artifact), Url, cancellationToken: TestContext.Current.CancellationToken));

        Assert.Equal(ApiErrorCode.Integrity, exception.Code);
    }

    [Fact]
    public async Task AFileServerThatRefusesIsReportedAsItself()
    {
        ByteServingHandler handler = new([], HttpStatusCode.Forbidden);

        ApiException exception = await Assert.ThrowsAsync<ApiException>(
            () => CreateDownloader(handler).DownloadAsync(
                Release(Artifact), Url, cancellationToken: TestContext.Current.CancellationToken));

        Assert.Equal(ApiErrorCode.Forbidden, exception.Code);
    }

    [Fact]
    public async Task AnUnreachableFileServerIsANetworkFailure()
    {
        ByteServingHandler handler = new([]) { Failure = new HttpRequestException("no route") };

        ApiException exception = await Assert.ThrowsAsync<ApiException>(
            () => CreateDownloader(handler).DownloadAsync(
                Release(Artifact), Url, cancellationToken: TestContext.Current.CancellationToken));

        Assert.Equal(ApiErrorCode.Network, exception.Code);
    }

    // Pressing the button twice does not fetch it twice — and the file is re-hashed rather than
    // trusted, because it has been sitting on a disk somebody else can write to.
    [Fact]
    public async Task AnArchiveAlreadyVerifiedIsNotFetchedAgain()
    {
        ByteServingHandler handler = new(Artifact);
        LauncherUpdateDownloader downloader = CreateDownloader(handler);

        string first = await downloader.DownloadAsync(
            Release(Artifact), Url, cancellationToken: TestContext.Current.CancellationToken);
        string second = await downloader.DownloadAsync(
            Release(Artifact), Url, cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(first, second);
        Assert.Equal(1, handler.Requests);
    }

    [Fact]
    public async Task AnArchiveThatChangedUnderUsIsFetchedAgain()
    {
        ByteServingHandler handler = new(Artifact);
        LauncherUpdateDownloader downloader = CreateDownloader(handler);

        string path = await downloader.DownloadAsync(
            Release(Artifact), Url, cancellationToken: TestContext.Current.CancellationToken);
        await File.WriteAllTextAsync(
            path, "something else entirely", TestContext.Current.CancellationToken);

        await downloader.DownloadAsync(
            Release(Artifact), Url, cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(2, handler.Requests);
        Assert.Equal(
            Artifact,
            await File.ReadAllBytesAsync(path, TestContext.Current.CancellationToken));
    }

    // One version's worth of archive at a time: three offers over a month would otherwise leave
    // three self-contained builds under somebody's data directory, and only one is worth having.
    [Fact]
    public async Task AnOlderDownloadIsSweptAway()
    {
        string stale = Path.Combine(_paths.UpdateDirectory, "0.1.5");
        Directory.CreateDirectory(stale);
        await File.WriteAllTextAsync(
            Path.Combine(stale, "old.zip"), "yesterday", TestContext.Current.CancellationToken);

        await CreateDownloader(new ByteServingHandler(Artifact)).DownloadAsync(
            Release(Artifact), Url, cancellationToken: TestContext.Current.CancellationToken);

        Assert.False(Directory.Exists(stale));
    }

    [Fact]
    public async Task ProgressIsReportedAsBytesOnDisk()
    {
        List<long> reports = [];

        await CreateDownloader(new ByteServingHandler(Artifact)).DownloadAsync(
            Release(Artifact),
            Url,
            new ImmediateProgress<long>(reports.Add),
            TestContext.Current.CancellationToken);

        Assert.NotEmpty(reports);
        Assert.Equal(Artifact.Length, reports[^1]);
    }

    /// <summary>Serves one body, and counts how many times it was asked for it.</summary>
    private sealed class ByteServingHandler(
        byte[] body, HttpStatusCode status = HttpStatusCode.OK) : HttpMessageHandler
    {
        public int Requests { get; private set; }

        public Exception? Failure { get; init; }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            ++Requests;

            if (Failure is not null)
            {
                throw Failure;
            }

            return Task.FromResult(new HttpResponseMessage(status)
            {
                Content = new ByteArrayContent(body),
            });
        }
    }
}

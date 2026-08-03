using System.Net;
using System.Security.Cryptography;
using System.Text;
using GameLauncher.Core.Api;
using GameLauncher.Core.Models;
using GameLauncher.Infrastructure.Downloads;
using Microsoft.Extensions.Logging.Abstractions;

namespace GameLauncher.Infrastructure.Tests.Downloads;

public sealed class BlobFetcherTests
{
    private const string Url = "http://files.example/files/ab/cd/abcd?token=t&expires=1";

    private static readonly byte[] Content =
        Encoding.UTF8.GetBytes("the bytes of a small but perfectly formed game executable");

    private static PlannedFile FileOf(byte[] content, string? sha256 = null) => new()
    {
        Path = "Game.exe",
        Sha256 = sha256 ?? Sha256Of(content),
        Size = content.Length,
        Url = Url,
    };

    private static BlobFetcher FetcherOver(FileServerStub stub, int maxAttempts = 3) =>
        new(new HttpClient(stub), NullLogger<BlobFetcher>.Instance, maxAttempts, TimeSpan.Zero);

    private static string Sha256Of(byte[] content) =>
        Convert.ToHexStringLower(SHA256.HashData(content));

    [Fact]
    public async Task AFetchedBlobLandsAtItsContentAddressWithNoLeftovers()
    {
        using var directory = new TemporaryDirectory();
        string destination = directory.File("abcd");
        var stub = FileServerStub.Serving(Content);

        await FetcherOver(stub).FetchAsync(
            FileOf(Content), destination, cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(Content, await File.ReadAllBytesAsync(
            destination, TestContext.Current.CancellationToken));
        Assert.False(File.Exists(destination + BlobFetcher.PartialSuffix));
        Assert.Null(Assert.Single(stub.Requests).RangeFrom);
    }

    // The signed URL carries its own authorization. Attaching the launcher's bearer token would
    // hand it to whatever host the API named.
    [Fact]
    public async Task NoBearerTokenIsSentToTheFileServer()
    {
        using var directory = new TemporaryDirectory();
        var stub = FileServerStub.Serving(Content);

        await FetcherOver(stub).FetchAsync(
            FileOf(Content),
            directory.File("abcd"),
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.Null(Assert.Single(stub.Requests).Authorization);
    }

    [Fact]
    public async Task AnInterruptedTransferResumesWhereItStopped()
    {
        using var directory = new TemporaryDirectory();
        string destination = directory.File("abcd");
        await File.WriteAllBytesAsync(
            destination + BlobFetcher.PartialSuffix,
            Content[..20],
            TestContext.Current.CancellationToken);

        var stub = FileServerStub.Serving(Content);

        await FetcherOver(stub).FetchAsync(
            FileOf(Content), destination, cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(20, Assert.Single(stub.Requests).RangeFrom);
        Assert.Equal(Content, await File.ReadAllBytesAsync(
            destination, TestContext.Current.CancellationToken));
    }

    // Appending a body the server sent from zero onto what is already there would produce a
    // file made of the same bytes twice, which the hash would then reject forever.
    [Fact]
    public async Task AServerThatIgnoresRangeMakesTheTransferStartOver()
    {
        using var directory = new TemporaryDirectory();
        string destination = directory.File("abcd");
        await File.WriteAllBytesAsync(
            destination + BlobFetcher.PartialSuffix,
            Content[..20],
            TestContext.Current.CancellationToken);

        var stub = FileServerStub.IgnoringRange(Content);

        await FetcherOver(stub).FetchAsync(
            FileOf(Content), destination, cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(Content, await File.ReadAllBytesAsync(
            destination, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task LeftoversLongerThanTheBlobAreThrownAwayRatherThanResumedFrom()
    {
        using var directory = new TemporaryDirectory();
        string destination = directory.File("abcd");
        await File.WriteAllBytesAsync(
            destination + BlobFetcher.PartialSuffix,
            [.. Content, .. Content],
            TestContext.Current.CancellationToken);

        var stub = FileServerStub.Serving(Content);

        await FetcherOver(stub).FetchAsync(
            FileOf(Content), destination, cancellationToken: TestContext.Current.CancellationToken);

        Assert.Null(Assert.Single(stub.Requests).RangeFrom);
        Assert.Equal(Content, await File.ReadAllBytesAsync(
            destination, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task ABlobAlreadyAtItsAddressIsNotFetchedAgain()
    {
        using var directory = new TemporaryDirectory();
        string destination = directory.File("abcd");
        await File.WriteAllBytesAsync(
            destination, Content, TestContext.Current.CancellationToken);

        var stub = FileServerStub.Serving(Content);
        CountingProgress progress = new();

        await FetcherOver(stub).FetchAsync(
            FileOf(Content), destination, progress, TestContext.Current.CancellationToken);

        Assert.Empty(stub.Requests);

        // It still counts towards the download: the caller is showing one bar for the build.
        Assert.Equal(Content.Length, progress.Total);
    }

    [Fact]
    public async Task BytesThatHashToSomethingElseNeverTakeTheContentAddress()
    {
        using var directory = new TemporaryDirectory();
        string destination = directory.File("abcd");
        var stub = FileServerStub.Serving(Content);

        // The plan says this blob is something the server is not sending.
        PlannedFile file = FileOf(Content, sha256: new string('a', 64));

        ApiException exception = await Assert.ThrowsAsync<ApiException>(() =>
            FetcherOver(stub).FetchAsync(
                file, destination, cancellationToken: TestContext.Current.CancellationToken));

        Assert.Equal(ApiErrorCode.Integrity, exception.Code);
        Assert.False(File.Exists(destination));
        Assert.Equal(3, stub.Requests.Count);
    }

    [Fact]
    public async Task ACorruptTransferIsDiscardedAndTheRetrySucceeds()
    {
        using var directory = new TemporaryDirectory();
        string destination = directory.File("abcd");

        var stub = FileServerStub.Answering(
            FileServerStub.Body(Encoding.UTF8.GetBytes("not the blob at all, but the same length")),
            FileServerStub.Body(Content));

        await FetcherOver(stub).FetchAsync(
            FileOf(Content), destination, cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(2, stub.Requests.Count);

        // The second attempt started over rather than resuming onto rejected bytes.
        Assert.Null(stub.Requests[1].RangeFrom);
        Assert.Equal(Content, await File.ReadAllBytesAsync(
            destination, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task AnExpiredUrlIsReportedApartFromABadSignatureAndIsNotRetried()
    {
        using var directory = new TemporaryDirectory();
        var stub = FileServerStub.Refusing(HttpStatusCode.Gone);

        ApiException exception = await Assert.ThrowsAsync<ApiException>(() =>
            FetcherOver(stub).FetchAsync(
                FileOf(Content),
                directory.File("abcd"),
                cancellationToken: TestContext.Current.CancellationToken));

        Assert.Equal(ApiErrorCode.LinkExpired, exception.Code);
        Assert.Single(stub.Requests);
    }

    [Fact]
    public async Task ASignatureTheFileServerRejectsIsForbiddenAndIsNotRetried()
    {
        using var directory = new TemporaryDirectory();
        var stub = FileServerStub.Refusing(HttpStatusCode.Forbidden);

        ApiException exception = await Assert.ThrowsAsync<ApiException>(() =>
            FetcherOver(stub).FetchAsync(
                FileOf(Content),
                directory.File("abcd"),
                cancellationToken: TestContext.Current.CancellationToken));

        Assert.Equal(ApiErrorCode.Forbidden, exception.Code);
        Assert.Single(stub.Requests);
    }

    [Fact]
    public async Task ProgressAddsUpToExactlyTheFileWhateverHappenedOnTheWay()
    {
        using var directory = new TemporaryDirectory();
        string destination = directory.File("abcd");
        await File.WriteAllBytesAsync(
            destination + BlobFetcher.PartialSuffix,
            Content[..20],
            TestContext.Current.CancellationToken);

        // A corrupt first attempt, so the resumed bytes are counted and then given back.
        var stub = FileServerStub.Answering(
            FileServerStub.Body(Encoding.UTF8.GetBytes(new string('x', Content.Length))),
            FileServerStub.Body(Content));

        CountingProgress progress = new();

        await FetcherOver(stub).FetchAsync(
            FileOf(Content), destination, progress, TestContext.Current.CancellationToken);

        Assert.Equal(Content.Length, progress.Total);
    }
}

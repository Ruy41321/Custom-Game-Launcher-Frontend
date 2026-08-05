using System.Security.Cryptography;
using System.Text;
using GameLauncher.Core.Api;
using GameLauncher.Core.Models;
using GameLauncher.Core.Publishing;
using GameLauncher.Infrastructure.Publishing;
using GameLauncher.Infrastructure.Tests.Progressing;
using Microsoft.Extensions.Logging.Abstractions;

namespace GameLauncher.Infrastructure.Tests.Publishing;

public sealed class BuildPublisherTests : IDisposable
{
    private readonly TemporaryDirectory _directory = new();
    private readonly FakePublishingApi _api = new();
    private readonly BuildPublisher _publisher;

    /// <summary>
    /// A deliberately small chunk ceiling: the chunking behaviour is what these tests are
    /// about, and a 4 MiB default would mean writing megabytes to disk to observe it.
    /// </summary>
    private const int TestChunkBytes = 64 * 1024;

    private readonly FixedCapabilities _capabilities =
        FixedCapabilities.WithChunkBytes(TestChunkBytes);

    public BuildPublisherTests() =>
        _publisher = new BuildPublisher(
            _api,
            new DirectoryBuildPackager(_capabilities),
            _capabilities,
            NullLogger<BuildPublisher>.Instance);

    public void Dispose() => _directory.Dispose();

    private void Write(string relativePath, string content)
    {
        string full = Path.Combine(
            _directory.Path, relativePath.Replace('/', Path.DirectorySeparatorChar));

        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        File.WriteAllText(full, content);
    }

    private void WriteLarge(string relativePath, int bytes)
    {
        string full = Path.Combine(_directory.Path, relativePath);
        byte[] content = new byte[bytes];
        Random.Shared.NextBytes(content);
        File.WriteAllBytes(full, content);
    }

    private static string Sha(string content) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(content)));

    private PublishRequest Request => new()
    {
        GameIdOrSlug = "orbital-drift",
        VersionId = "v1",
        Platform = GamePlatform.Windows,
        Directory = _directory.Path,
        Entrypoint = "Game.exe",
        LaunchArgs = "--fullscreen",
    };

    [Fact]
    public async Task EveryBlobArrivesIntactAndTheManifestFinishesTheBuild()
    {
        Write("Game.exe", "the executable");
        Write("data/pak", "an asset");

        PublishResult result = await _publisher.PublishAsync(
            Request, cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(2, _api.Stored.Count);
        Assert.Equal(
            "the executable",
            Encoding.UTF8.GetString(_api.Stored[Sha("the executable")]));
        Assert.Equal("an asset", Encoding.UTF8.GetString(_api.Stored[Sha("an asset")]));

        Assert.Equal(BuildStatus.Ready, result.Build.Status);
        Assert.Equal(2, result.BlobsUploaded);
        Assert.Equal(0, result.BlobsAlreadyPresent);
        Assert.Equal("the executable".Length + "an asset".Length, result.UploadedBytes);

        Assert.NotNull(_api.Submitted);
        Assert.Equal("Game.exe", _api.Submitted.Entrypoint);
        Assert.Equal("--fullscreen", _api.Submitted.LaunchArgs);
        Assert.Equal(2, _api.Submitted.Files.Count);
    }

    // This is the whole point of negotiating before transferring: a second build of the same
    // game re-uploads only what changed.
    [Fact]
    public async Task WhatTheServerAlreadyHoldsIsNeverSentAgain()
    {
        Write("Game.exe", "the executable");
        Write("data/pak", "an asset");
        _api.AlreadyStored.Add(Sha("an asset"));

        PublishResult result = await _publisher.PublishAsync(
            Request, cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(1, result.BlobsUploaded);
        Assert.Equal(1, result.BlobsAlreadyPresent);
        Assert.Equal("the executable".Length, result.UploadedBytes);
        Assert.DoesNotContain(Sha("an asset"), _api.Stored.Keys, StringComparer.OrdinalIgnoreCase);

        // Both files are still in the manifest: the blob is on the server either way.
        Assert.Equal(2, _api.Submitted?.Files.Count);
    }

    [Fact]
    public async Task TwoFilesWithTheSameContentAreOneUpload()
    {
        Write("Game.exe", "the executable");
        Write("data/pak", "shared content");
        Write("assets/pak", "shared content");

        PublishResult result = await _publisher.PublishAsync(
            Request, cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(2, result.BlobsUploaded);
        Assert.Equal(3, _api.Submitted?.Files.Count);
    }

    [Fact]
    public async Task ALargeBlobTravelsInChunksAndArrivesWhole()
    {
        Write("Game.exe", "the executable");
        WriteLarge("big.pak", (TestChunkBytes * 2) + 1024);

        await _publisher.PublishAsync(
            Request, cancellationToken: TestContext.Current.CancellationToken);

        byte[] expected = await File.ReadAllBytesAsync(
            Path.Combine(_directory.Path, "big.pak"), TestContext.Current.CancellationToken);
        string big = Convert.ToHexStringLower(SHA256.HashData(expected));

        List<(string Sha256, long Offset, int Length)> chunks =
            [.. _api.Chunks.Where(chunk => chunk.Sha256 == big)];

        Assert.Equal(3, chunks.Count);
        Assert.Equal(0, chunks[0].Offset);
        Assert.Equal(TestChunkBytes, chunks[1].Offset);
        Assert.Equal(TestChunkBytes * 2L, chunks[2].Offset);

        Assert.Equal(expected, _api.Stored[big]);
    }

    // The server's count is the authority: it is assigned by a conditional UPDATE, so a client
    // that disagrees is the one that is wrong.
    [Fact]
    public async Task AnUploadResumesFromWhereTheServerSaysItIs()
    {
        Write("Game.exe", "the executable");

        // The server already has the first four bytes of exactly this content.
        _api.ResumeWith[Sha("the executable")] =
            Encoding.UTF8.GetBytes("the executable")[..4];

        await _publisher.PublishAsync(
            Request, cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(4, _api.Chunks.Single().Offset);
        Assert.Equal("the executable".Length - 4, _api.Chunks.Single().Length);
        Assert.Equal(
            "the executable", Encoding.UTF8.GetString(_api.Stored[Sha("the executable")]));
    }

    // A refused offset carries the real one, so a confused client recovers from the error
    // instead of corrupting the file.
    [Fact]
    public async Task ARefusedOffsetIsCorrectedByAskingRatherThanGuessing()
    {
        Write("Game.exe", "the executable");
        _api.RefusalsAt[0] = 1;

        await _publisher.PublishAsync(
            Request, cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(
            "the executable", Encoding.UTF8.GetString(_api.Stored[Sha("the executable")]));
    }

    [Fact]
    public async Task AnOffsetThatKeepsBeingRefusedIsReportedRatherThanRetriedForever()
    {
        Write("Game.exe", "the executable");

        // Refused more often than the client is willing to re-aim.
        _api.RefusalsAt[0] = 5;

        ApiException exception = await Assert.ThrowsAsync<ApiException>(() =>
            _publisher.PublishAsync(
                Request, cancellationToken: TestContext.Current.CancellationToken));

        Assert.Equal(ApiErrorCode.Conflict, exception.Code);
    }

    [Fact]
    public async Task ProgressRunsThroughEveryPhaseAndEndsFull()
    {
        Write("Game.exe", "the executable");
        Write("data/pak", "an asset");

        List<PublishProgress> reports = [];
        await _publisher.PublishAsync(
            Request,
            new ImmediateProgress<PublishProgress>(reports.Add),
            TestContext.Current.CancellationToken);

        Assert.Equal(PublishPhase.Packaging, reports[0].Phase);
        Assert.Contains(reports, report => report.Phase == PublishPhase.Negotiating);
        Assert.Contains(reports, report => report.Phase == PublishPhase.Uploading);
        Assert.Contains(reports, report => report.Phase == PublishPhase.Finalizing);

        Assert.Equal(PublishPhase.Done, reports[^1].Phase);
        Assert.Equal(1, reports[^1].Fraction);
        Assert.Equal(2, reports[^1].BlobsUploaded);
    }

    [Fact]
    public async Task NothingIsCreatedWhenTheDirectoryCannotBePackaged()
    {
        Write("data/pak", "an asset");

        PublishingException exception = await Assert.ThrowsAsync<PublishingException>(() =>
            _publisher.PublishAsync(
                Request, cancellationToken: TestContext.Current.CancellationToken));

        Assert.Equal(PublishFailure.EntrypointMissing, exception.Reason);

        // Packaging comes first precisely so a build is not created for something that cannot
        // be published.
        Assert.Null(_api.CreatedBuild);
        Assert.Empty(_api.Stored);
    }
}

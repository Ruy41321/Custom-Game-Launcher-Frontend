using GameLauncher.Core.Api;
using GameLauncher.Core.Models;
using GameLauncher.Core.Publishing;
using Microsoft.Extensions.Logging;

namespace GameLauncher.Infrastructure.Publishing;

/// <summary>
/// The publish flow, in the order the server's protocol defines: package, ask what is missing,
/// upload only that, then submit the manifest. The separation between negotiating and
/// transferring is the whole reason a second build costs what actually changed.
/// </summary>
public sealed class BuildPublisher(
    IPublishingApi api,
    IBuildPackager packager,
    ILogger<BuildPublisher> logger) : IBuildPublisher
{
    /// <summary>
    /// Below the server's <c>uploads.maxChunkBytes</c>, whose default is 8 MiB. There is no
    /// endpoint that advertises the real limit, so this is a guess with headroom rather than
    /// an agreement — see the open debts.
    /// </summary>
    public const int ChunkBytes = 4 * 1024 * 1024;

    /// <summary>
    /// How many times a chunk may be re-aimed at an offset the server corrected. One is the
    /// case this exists for — a resumed session the client had stale knowledge of. More than
    /// two in a row means the two sides disagree about something a retry will not fix.
    /// </summary>
    private const int MaxOffsetCorrections = 2;

    public async Task<PublishResult> PublishAsync(
        PublishRequest request,
        IProgress<PublishProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        PublishProgress state = new() { Phase = PublishPhase.Packaging };
        progress?.Report(state);

        BuildPackage package = await packager.PackageAsync(
            request.Directory,
            request.Entrypoint,
            new Relay<PackagingProgress>(report =>
            {
                state = state with
                {
                    FilesHashed = report.FilesHashed,
                    TotalFiles = report.TotalFiles,
                };
                progress?.Report(state);
            }),
            cancellationToken).ConfigureAwait(false);

        GameBuild build = await api.CreateBuildAsync(
            request.GameIdOrSlug,
            request.VersionId,
            new CreateBuildRequest
            {
                Platform = request.Platform,
                Architecture = request.Architecture,
            },
            cancellationToken).ConfigureAwait(false);

        state = state with { Phase = PublishPhase.Negotiating };
        progress?.Report(state);

        IReadOnlyList<BlobDeclaration> declared = package.DistinctBlobs;
        IReadOnlyList<string> missing = await api
            .MissingBlobsAsync(build.Id, declared, cancellationToken)
            .ConfigureAwait(false);

        HashSet<string> wanted = new(missing, StringComparer.OrdinalIgnoreCase);
        List<BlobDeclaration> toUpload = [.. declared.Where(blob => wanted.Contains(blob.Sha256))];

        logger.LogInformation(
            "Publishing {Count} files as {Blobs} blobs; the server needs {Missing}",
            package.Files.Count, declared.Count, toUpload.Count);

        state = state with
        {
            Phase = PublishPhase.Uploading,
            TotalBlobs = toUpload.Count,
            TotalBytes = toUpload.Sum(blob => blob.Size),
        };
        progress?.Report(state);

        // Sequential on purpose: the server bounds open sessions per user, and staging disk is
        // bounded by that count times the largest blob. Four at once would be four times the
        // scratch space on a machine chosen for being cheap.
        foreach (BlobDeclaration blob in toUpload)
        {
            PackagedFile file = package.Files.First(candidate =>
                string.Equals(candidate.Sha256, blob.Sha256, StringComparison.OrdinalIgnoreCase));

            long sent = await UploadAsync(build.Id, blob, file, state, progress, cancellationToken)
                .ConfigureAwait(false);

            state = state with
            {
                UploadedBytes = state.UploadedBytes + sent,
                BlobsUploaded = state.BlobsUploaded + 1,
            };
            progress?.Report(state);
        }

        state = state with { Phase = PublishPhase.Finalizing };
        progress?.Report(state);

        GameBuild ready = await api.SubmitManifestAsync(
            build.Id, package.ToManifest(request.LaunchArgs), cancellationToken)
            .ConfigureAwait(false);

        progress?.Report(state with { Phase = PublishPhase.Done });

        return new PublishResult
        {
            Build = ready,
            UploadedBytes = state.UploadedBytes,
            BlobsUploaded = toUpload.Count,
            BlobsAlreadyPresent = declared.Count - toUpload.Count,
        };
    }

    /// <summary>
    /// One blob, in chunks, resuming from whatever offset the server says it is at. The
    /// server's count is the authority: it is assigned by a conditional UPDATE, so a client
    /// that disagrees is the one that is wrong.
    /// </summary>
    private async Task<long> UploadAsync(
        string buildId,
        BlobDeclaration blob,
        PackagedFile file,
        PublishProgress state,
        IProgress<PublishProgress>? progress,
        CancellationToken cancellationToken)
    {
        UploadSession session = await api
            .BeginUploadAsync(buildId, blob, cancellationToken)
            .ConfigureAwait(false);

        long startedAt = session.ReceivedBytes;

        await using FileStream source = new(
            file.SourcePath, FileMode.Open, FileAccess.Read, FileShare.Read,
            ChunkBytes, useAsync: true);

        byte[] buffer = new byte[ChunkBytes];
        int corrections = 0;

        while (!session.Complete)
        {
            cancellationToken.ThrowIfCancellationRequested();

            long offset = session.ReceivedBytes;
            source.Seek(offset, SeekOrigin.Begin);

            int read = await source
                .ReadAtLeastAsync(buffer, ChunkBytes, throwOnEndOfStream: false, cancellationToken)
                .ConfigureAwait(false);

            if (read == 0)
            {
                // The file is shorter than the session was told it would be. Nothing further
                // can be sent, and the session would fail its hash check anyway.
                await api.AbortUploadAsync(session.Id, CancellationToken.None)
                    .ConfigureAwait(false);

                throw new PublishingException(
                    PublishFailure.Unreadable,
                    $"{file.Path} ended at {offset} bytes, before the {blob.Size} it declared.");
            }

            try
            {
                session = await api.SendChunkAsync(
                    session.Id, offset, buffer.AsMemory(0, read), cancellationToken)
                    .ConfigureAwait(false);

                corrections = 0;
            }
            catch (ApiException exception) when (
                exception.Code == ApiErrorCode.Conflict && corrections < MaxOffsetCorrections)
            {
                // The offset was refused, and the refusal carries the real one. Asking is what
                // the status endpoint is for; guessing is what corrupts a file.
                corrections++;
                logger.LogWarning(
                    "Chunk at {Offset} of blob {Blob} was refused; asking where the session is",
                    offset, blob.Sha256);

                session = await api.GetUploadAsync(session.Id, cancellationToken)
                    .ConfigureAwait(false);
                continue;
            }

            progress?.Report(state with
            {
                UploadedBytes = state.UploadedBytes + (session.ReceivedBytes - startedAt),
            });
        }

        return session.ReceivedBytes - startedAt;
    }

    /// <summary>Forwards a report on the calling thread, without a captured context.</summary>
    private sealed class Relay<T>(Action<T> report) : IProgress<T>
    {
        public void Report(T value) => report(value);
    }
}

using GameLauncher.Core.Models;

namespace GameLauncher.Core.Publishing;

/// <summary>Which build to publish, and from where.</summary>
public sealed record PublishRequest
{
    public required string GameIdOrSlug { get; init; }

    public required string VersionId { get; init; }

    public required GamePlatform Platform { get; init; }

    public BuildArchitecture Architecture { get; init; } = BuildArchitecture.X64;

    /// <summary>The directory holding the build, exactly as it should be installed.</summary>
    public required string Directory { get; init; }

    /// <summary>The executable, relative to <see cref="Directory"/>.</summary>
    public required string Entrypoint { get; init; }

    public string? LaunchArgs { get; init; }
}

public enum PublishPhase
{
    /// <summary>Reading and hashing the directory. Not instant on a large build.</summary>
    Packaging,

    /// <summary>Asking which blobs the server does not already have.</summary>
    Negotiating,

    Uploading,

    /// <summary>Submitting the manifest, which is what turns the build ready.</summary>
    Finalizing,

    Done,
}

public sealed record PublishProgress
{
    public PublishPhase Phase { get; init; }

    public int FilesHashed { get; init; }

    public int TotalFiles { get; init; }

    public long UploadedBytes { get; init; }

    /// <summary>What the upload will cost: the blobs the server asked for, and only those.</summary>
    public long TotalBytes { get; init; }

    public int BlobsUploaded { get; init; }

    public int TotalBlobs { get; init; }

    public double Fraction => TotalBytes > 0
        ? Math.Clamp((double)UploadedBytes / TotalBytes, 0, 1)
        : Phase == PublishPhase.Done ? 1 : 0;
}

public sealed record PublishResult
{
    public required GameBuild Build { get; init; }

    /// <summary>Bytes that actually travelled.</summary>
    public long UploadedBytes { get; init; }

    public int BlobsUploaded { get; init; }

    /// <summary>
    /// Blobs the server already held. On a second build of the same game this is most of
    /// them, and it is the number that shows why publishing an update is cheap.
    /// </summary>
    public int BlobsAlreadyPresent { get; init; }
}

/// <summary>
/// Publishes a build: package, negotiate, upload, finalize. The four steps of the server's
/// publish flow, in the one order that makes an update cost what changed.
/// </summary>
public interface IBuildPublisher
{
    Task<PublishResult> PublishAsync(
        PublishRequest request,
        IProgress<PublishProgress>? progress = null,
        CancellationToken cancellationToken = default);
}

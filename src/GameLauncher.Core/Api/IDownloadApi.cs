using GameLauncher.Core.Models;

namespace GameLauncher.Core.Api;

/// <summary>
/// The three calls that turn a build into an installation. None of them moves a byte of the
/// build itself: the API answers with signed URLs the file server validates on its own, so a
/// multi-gigabyte transfer never occupies an API worker and <c>Range</c> is handled natively.
/// </summary>
public interface IDownloadApi
{
    /// <summary>
    /// The manifest of a build, verified against <paramref name="expectedSha256"/> — the hash
    /// the catalog or a plan carries. The server serves the exact bytes that hash covers, so
    /// verification is hashing the response; a mismatch is
    /// <see cref="ApiErrorCode.Integrity"/> and never a document this client tries to use.
    /// </summary>
    Task<BuildManifest> GetManifestAsync(
        string buildId, string expectedSha256, CancellationToken cancellationToken = default);

    /// <summary>
    /// What it takes to reach <paramref name="buildId"/>. <paramref name="fromBuildId"/> is the
    /// build currently installed; null asks for a first install. It must be a build of the same
    /// game, or the request is <see cref="ApiErrorCode.InvalidInput"/>.
    /// </summary>
    Task<DownloadPlan> GetPlanAsync(
        string buildId, string? fromBuildId = null, CancellationToken cancellationToken = default);

    /// <summary>
    /// Compares what was found on disk against the manifest. A path the client could not read
    /// is reported by leaving it out, which lands it in
    /// <see cref="IntegrityReport.Missing"/>.
    /// </summary>
    Task<IntegrityReport> VerifyAsync(
        string buildId,
        IReadOnlyList<InstalledFile> files,
        CancellationToken cancellationToken = default);
}

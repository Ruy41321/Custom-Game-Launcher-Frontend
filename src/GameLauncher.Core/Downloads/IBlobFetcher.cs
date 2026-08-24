using GameLauncher.Core.Models;

namespace GameLauncher.Core.Downloads;

/// <summary>
/// Fetches one blob from the file server into the staging area. The unit is the blob and not
/// the file: two paths of a build with identical content are one transfer, which is also why
/// the destination is a content address rather than an install path.
/// </summary>
public interface IBlobFetcher
{
    /// <summary>
    /// Downloads <paramref name="file"/> to <paramref name="destinationPath"/>, resuming an
    /// interrupted transfer with <c>Range</c> and verifying the result against
    /// <see cref="ManifestEntry.Sha256"/> before the bytes are allowed to take that name.
    /// A destination that already exists is left alone: it can only have got there by
    /// matching, so re-fetching it would be work with no possible outcome.
    ///
    /// <paramref name="transferred"/> receives the bytes accounted for since the last report,
    /// so a caller can add them up across concurrent transfers. A resumed download reports
    /// the bytes already on disk first, so the total still adds up to the file.
    /// </summary>
    Task FetchAsync(
        PlannedFile file,
        string destinationPath,
        IProgress<long>? transferred = null,
        CancellationToken cancellationToken = default);
}

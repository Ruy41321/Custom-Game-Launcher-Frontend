using GameLauncher.Core.Api;

namespace GameLauncher.Core.Updates;

/// <summary>
/// Where the old installation goes while the new one takes its place.
///
/// <b>A rename beside the target, never a copy and never a delete.</b> A sibling directory is on
/// the same filesystem, so putting the old installation out of the way is one atomic operation
/// that needs no second copy of a self-contained build — the same reason the download's staging
/// tree lives inside its own root. And it is renamed rather than deleted because a rollback with
/// nothing to roll back to is not a rollback.
/// </summary>
public static class UpdateSwapPaths
{
    private const string PreviousSuffix = ".previous";

    /// <summary>
    /// <c>C:\Apps\Launcher</c> becomes <c>C:\Apps\Launcher.previous</c>.
    /// </summary>
    /// <exception cref="ApiException">
    /// <see cref="ApiErrorCode.Integrity"/> when the target has no parent to put a sibling in —
    /// an installation directly on a volume root cannot be swapped this way, and finding that
    /// out after the rename would be finding it out too late.
    /// </exception>
    public static string PreviousOf(string targetDirectory)
    {
        string full = Path.TrimEndingDirectorySeparator(Path.GetFullPath(targetDirectory));
        string? parent = Path.GetDirectoryName(full);

        if (string.IsNullOrEmpty(parent))
        {
            throw new ApiException(
                ApiErrorCode.Integrity,
                $"An installation at a volume root cannot be replaced: {targetDirectory}");
        }

        return Path.Combine(parent, Path.GetFileName(full) + PreviousSuffix);
    }
}

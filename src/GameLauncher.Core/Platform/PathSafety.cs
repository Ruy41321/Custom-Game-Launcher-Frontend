using GameLauncher.Core.Api;

namespace GameLauncher.Core.Platform;

/// <summary>
/// The one rule about paths that arrived over the network: they say what to call a file, never
/// where the launcher writes or what it runs.
/// </summary>
public static class PathSafety
{
    /// <summary>
    /// Resolves <paramref name="relativePath"/> under <paramref name="root"/>, refusing
    /// anything that would land outside it. The server validates manifest paths on ingestion
    /// and the database enforces it again, and this check is still worth a string comparison:
    /// a client that writes wherever it is told is one compromised server away from writing
    /// into the user's startup folder. This is the check that protects *this* machine.
    /// </summary>
    /// <exception cref="ApiException">
    /// <see cref="ApiErrorCode.Integrity"/> when the path escapes the root.
    /// </exception>
    public static string ResolveInside(string root, string relativePath)
    {
        string rootFull = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
        string full = Path.GetFullPath(
            Path.Combine(rootFull, relativePath.Replace('/', Path.DirectorySeparatorChar)));

        bool inside = full.Length > rootFull.Length
            && full.StartsWith(rootFull, StringComparison.Ordinal)
            && full[rootFull.Length] == Path.DirectorySeparatorChar;

        return inside
            ? full
            : throw new ApiException(
                ApiErrorCode.Integrity,
                $"The build names a file outside its install directory: {relativePath}");
    }
}

using GameLauncher.Core.Platform;

namespace GameLauncher.Infrastructure.Platform;

/// <summary>Reads the free space of the volume a path belongs to.</summary>
public sealed class DiskSpaceProbe : IDiskSpaceProbe
{
    public long AvailableFreeBytes(string path)
    {
        string? existing = NearestExistingDirectory(path);
        if (existing is null)
        {
            return long.MaxValue;
        }

        try
        {
            return new DriveInfo(Path.GetPathRoot(existing) ?? existing).AvailableFreeSpace;
        }
        catch (Exception exception) when (
            exception is ArgumentException or IOException or UnauthorizedAccessException)
        {
            // A network share or a mount point the runtime will not describe. Guessing low
            // would refuse an install that would have worked.
            return long.MaxValue;
        }
    }

    /// <summary>
    /// The install directory usually does not exist yet, and neither may its parent. The volume
    /// is whichever ancestor does.
    /// </summary>
    private static string? NearestExistingDirectory(string path)
    {
        string? candidate = Path.GetFullPath(path);
        while (candidate is not null && !Directory.Exists(candidate))
        {
            candidate = Path.GetDirectoryName(candidate);
        }

        return candidate;
    }
}

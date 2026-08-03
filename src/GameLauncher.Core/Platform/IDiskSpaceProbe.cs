namespace GameLauncher.Core.Platform;

/// <summary>
/// How much room is left where a path lives. Behind an interface because the check that
/// matters is the one that refuses an install, and a test cannot fill a disk to prove it.
/// </summary>
public interface IDiskSpaceProbe
{
    /// <summary>
    /// Free bytes on the volume holding <paramref name="path"/>, whether or not the path
    /// itself exists yet — an install directory is created after the check, not before it.
    /// Returns <see cref="long.MaxValue"/> when the volume cannot be identified, because
    /// refusing to install over a question the launcher cannot answer would be worse than
    /// letting the write fail with the real reason.
    /// </summary>
    long AvailableFreeBytes(string path);
}

namespace GameLauncher.Core.Platform;

/// <summary>
/// Shows a directory in whatever the desktop uses to browse files. Behind an interface for the
/// reason every other shell-out is (D27, D32): starting a process is not something a view-model
/// test can be made to do, while deciding <em>whether</em> to start one is exactly what a test
/// should press.
/// </summary>
public interface IFileBrowser
{
    /// <summary>
    /// Returns whether the directory was handed to the desktop — false for a path that is not
    /// there, and for a platform with nothing to hand it to. It never throws: a page that
    /// stopped working because a folder would not open would be a worse outcome than the
    /// folder not opening.
    /// </summary>
    bool Reveal(string directory);
}

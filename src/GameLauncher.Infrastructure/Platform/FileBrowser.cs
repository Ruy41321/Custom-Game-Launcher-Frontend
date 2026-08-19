using System.Diagnostics;
using GameLauncher.Core.Platform;
using Microsoft.Extensions.Logging;

namespace GameLauncher.Infrastructure.Platform;

/// <summary>
/// Hands an install directory to the desktop's file manager.
///
/// The two platforms are spelled out rather than left to <c>UseShellExecute</c>, which resolves
/// to something different on each of them and to nothing at all on a machine with no desktop
/// session — a difference worth having in the open, since the failure is otherwise a button
/// that does nothing on somebody else's computer.
/// </summary>
public sealed class FileBrowser(ILogger<FileBrowser> logger) : IFileBrowser
{
    public bool Reveal(string directory)
    {
        if (string.IsNullOrWhiteSpace(directory) || !Directory.Exists(directory))
        {
            return false;
        }

        ProcessStartInfo start = OperatingSystem.IsWindows()
            ? new ProcessStartInfo("explorer.exe", [directory])
            : new ProcessStartInfo("xdg-open", [directory]);

        start.UseShellExecute = false;

        try
        {
            using Process? process = Process.Start(start);

            // explorer.exe returns a non-zero exit code on success often enough that waiting
            // for it would report a failure that did not happen. Started is the answer here.
            return process is not null;
        }
        catch (Exception exception) when (
            exception is System.ComponentModel.Win32Exception or InvalidOperationException
                or PlatformNotSupportedException)
        {
            logger.LogWarning(
                exception, "could not open a file browser for {Directory}", directory);
            return false;
        }
    }
}

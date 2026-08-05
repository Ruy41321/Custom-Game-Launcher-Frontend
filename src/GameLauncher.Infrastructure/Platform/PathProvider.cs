using System.Runtime.InteropServices;
using GameLauncher.Core.Platform;

namespace GameLauncher.Infrastructure.Platform;

/// <summary>
/// Resolves the platform-appropriate locations. The three operating systems disagree about
/// every one of these, which is exactly why nothing else is allowed to guess.
/// </summary>
public sealed class PathProvider : IPathProvider
{
    private const string ApplicationFolderName = "CustomGameLauncher";

    private const string DatabaseFileName = "launcher.db";

    public PathProvider(string? applicationDirectory = null, string? userDataDirectory = null)
    {
        ApplicationDirectory = applicationDirectory ?? AppContext.BaseDirectory;
        UserDataDirectory = userDataDirectory
            ?? Path.Combine(ResolveUserDataRoot(), ApplicationFolderName);
        LogDirectory = Path.Combine(UserDataDirectory, "logs");
        DownloadStagingDirectory = Path.Combine(UserDataDirectory, "staging");
        ImageCacheDirectory = Path.Combine(UserDataDirectory, "images");
        DefaultInstallDirectory = Path.Combine(ResolveInstallRoot(), ApplicationFolderName, "Games");
        LocalDatabasePath = Path.Combine(UserDataDirectory, DatabaseFileName);
    }

    public string ApplicationDirectory { get; }

    public string UserDataDirectory { get; }

    public string LogDirectory { get; }

    public string DownloadStagingDirectory { get; }

    public string ImageCacheDirectory { get; }

    public string DefaultInstallDirectory { get; }

    public string LocalDatabasePath { get; }

    public void EnsureDirectoriesExist()
    {
        Directory.CreateDirectory(UserDataDirectory);
        Directory.CreateDirectory(LogDirectory);
        Directory.CreateDirectory(DownloadStagingDirectory);
        Directory.CreateDirectory(ImageCacheDirectory);
    }

    private static string ResolveUserDataRoot()
    {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            // %LOCALAPPDATA%: machine-local, and correctly excluded from roaming profiles —
            // nobody wants gigabytes of game data syncing to a domain server.
            return Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        }

        if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
        {
            return Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                "Library",
                "Application Support");
        }

        // Linux and anything else: XDG Base Directory specification.
        string xdgDataHome = Environment.GetEnvironmentVariable("XDG_DATA_HOME") ?? string.Empty;
        return string.IsNullOrWhiteSpace(xdgDataHome)
            ? Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                ".local",
                "share")
            : xdgDataHome;
    }

    private static string ResolveInstallRoot()
    {
        // Games go beside the user's data rather than into a system-wide location: the
        // launcher must never need elevation to install something.
        return ResolveUserDataRoot();
    }
}

namespace GameLauncher.Core.Platform;

/// <summary>
/// Every filesystem location the launcher uses. Nothing anywhere else may build a path from
/// a literal or call <see cref="Environment.GetFolderPath(Environment.SpecialFolder)"/>
/// directly — Windows, Linux and macOS disagree about all of these.
/// </summary>
public interface IPathProvider
{
    /// <summary>Directory the application was started from (read-only after install).</summary>
    string ApplicationDirectory { get; }

    /// <summary>Writable per-user directory for settings and the local database.</summary>
    string UserDataDirectory { get; }

    /// <summary>Writable directory for rolling log files.</summary>
    string LogDirectory { get; }

    /// <summary>Scratch space for in-flight downloads, cleaned up on a successful install.</summary>
    string DownloadStagingDirectory { get; }

    /// <summary>
    /// Where fetched artwork is kept. Disposable by definition: everything in it can be
    /// downloaded again, and deleting it costs a few thumbnails on the next launch.
    /// </summary>
    string ImageCacheDirectory { get; }

    /// <summary>Default root under which games are installed.</summary>
    string DefaultInstallDirectory { get; }

    /// <summary>The SQLite file holding what is installed on this machine.</summary>
    string LocalDatabasePath { get; }

    /// <summary>Ensures every writable directory above exists.</summary>
    void EnsureDirectoriesExist();
}

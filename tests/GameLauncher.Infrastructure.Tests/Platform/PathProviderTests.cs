using GameLauncher.Infrastructure.Platform;

namespace GameLauncher.Infrastructure.Tests.Platform;

public sealed class PathProviderTests
{
    [Fact]
    public void EveryPathIsAbsolute()
    {
        var paths = new PathProvider();

        Assert.True(Path.IsPathRooted(paths.ApplicationDirectory));
        Assert.True(Path.IsPathRooted(paths.UserDataDirectory));
        Assert.True(Path.IsPathRooted(paths.LogDirectory));
        Assert.True(Path.IsPathRooted(paths.DownloadStagingDirectory));
        Assert.True(Path.IsPathRooted(paths.ImageCacheDirectory));
        Assert.True(Path.IsPathRooted(paths.DefaultInstallDirectory));
        Assert.True(Path.IsPathRooted(paths.LocalDatabasePath));
    }

    // The database holds what is installed, which has to outlive a cleared staging directory
    // and must not be mistaken for a log.
    [Fact]
    public void TheDatabaseSitsDirectlyInTheUserDataDirectory()
    {
        var paths = new PathProvider();

        Assert.Equal(paths.UserDataDirectory, Path.GetDirectoryName(paths.LocalDatabasePath));
    }

    [Fact]
    public void TheUserDataDirectoryCanBeOverriddenForTesting()
    {
        using var directory = new TemporaryDirectory();

        var paths = new PathProvider(userDataDirectory: directory.Path);

        Assert.Equal(directory.Path, paths.UserDataDirectory);
        Assert.StartsWith(directory.Path, paths.LocalDatabasePath, StringComparison.Ordinal);
    }

    // Staging must not sit inside the log directory or vice versa; cleaning one would
    // otherwise take the other with it.
    [Fact]
    public void TheWritableDirectoriesAreDistinct()
    {
        var paths = new PathProvider();

        string[] directories =
        [
            paths.UserDataDirectory,
            paths.LogDirectory,
            paths.DownloadStagingDirectory,
            paths.ImageCacheDirectory,
            paths.DefaultInstallDirectory,
        ];

        Assert.Equal(directories.Length, directories.Distinct(StringComparer.Ordinal).Count());
    }

    [Fact]
    public void LogsAndStagingLiveUnderTheUserDataDirectory()
    {
        var paths = new PathProvider();

        Assert.StartsWith(paths.UserDataDirectory, paths.LogDirectory, StringComparison.Ordinal);
        Assert.StartsWith(
            paths.UserDataDirectory, paths.DownloadStagingDirectory, StringComparison.Ordinal);
    }

    [Fact]
    public void TheApplicationDirectoryCanBeOverriddenForTesting()
    {
        using var directory = new TemporaryDirectory();

        var paths = new PathProvider(directory.Path);

        Assert.Equal(directory.Path, paths.ApplicationDirectory);
    }

    [Fact]
    public void EnsureDirectoriesExistCreatesEveryWritableLocation()
    {
        var paths = new PathProvider();

        paths.EnsureDirectoriesExist();

        Assert.True(Directory.Exists(paths.UserDataDirectory));
        Assert.True(Directory.Exists(paths.LogDirectory));
        Assert.True(Directory.Exists(paths.DownloadStagingDirectory));
        Assert.True(Directory.Exists(paths.ImageCacheDirectory));
    }

    [Fact]
    public void EnsureDirectoriesExistIsIdempotent()
    {
        var paths = new PathProvider();

        paths.EnsureDirectoriesExist();
        paths.EnsureDirectoriesExist();

        Assert.True(Directory.Exists(paths.UserDataDirectory));
    }

    // Installing a game must never need administrator rights.
    [Fact]
    public void TheDefaultInstallDirectoryIsNotASystemLocation()
    {
        var paths = new PathProvider();

        Assert.DoesNotContain("Program Files", paths.DefaultInstallDirectory, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("/usr/", paths.DefaultInstallDirectory, StringComparison.Ordinal);
    }
}


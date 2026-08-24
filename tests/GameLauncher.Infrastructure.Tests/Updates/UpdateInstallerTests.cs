using System.IO.Compression;
using System.Text;
using GameLauncher.Core.Api;
using GameLauncher.Core.Platform;
using GameLauncher.Core.Updates;
using GameLauncher.Infrastructure.Updates;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;

namespace GameLauncher.Infrastructure.Tests.Updates;

/// <summary>
/// Unpacking is the launcher's job rather than the updater's, and this is why it needs its own
/// tests: the hash check already proved the archive is the bytes somebody signed, and says
/// nothing at all about the <b>names</b> inside it. An archive that is correctly signed and
/// hostile — carrying <c>../..</c> or an absolute path — is a real and different case, and it
/// has to be refused with nothing written.
/// </summary>
public sealed class UpdateInstallerTests : IDisposable
{
    private readonly TemporaryDirectory _directory = new();

    private readonly IPathProvider _paths = Substitute.For<IPathProvider>();

    private const string LauncherName = "GameLauncher";

    public UpdateInstallerTests()
    {
        _paths.UpdateDirectory.Returns(Path.Combine(_directory.Path, "updates"));
        _paths.ApplicationDirectory.Returns(Path.Combine(_directory.Path, "install"));
        _paths.ExecutablePath.Returns(
            Path.Combine(_directory.Path, "install", LauncherName));
    }

    public void Dispose() => _directory.Dispose();

    private static readonly ReleaseDocument Release = new()
    {
        Version = new ReleaseVersion(0, 5, 0),
        Platform = "windows",
        Arch = "x64",
        Sha256 = new string('a', 64),
        Size = 1,
        ReleasedAt = "2026-08-07T10:00:00Z",
    };

    private UpdateInstaller CreateInstaller() =>
        new(_paths, NullLogger<UpdateInstaller>.Instance);

    private string ArchiveWith(params string[] entryNames) => ArchiveWithModes(null, entryNames);

    /// <summary>
    /// <paramref name="unixMode"/> is written into the high half of the external attributes,
    /// which is where a zip created on Unix carries a file mode. Null leaves it at zero, which
    /// is what an archive built on Windows looks like.
    /// </summary>
    private string ArchiveWithModes(UnixFileMode? unixMode, params string[] entryNames)
    {
        string path = Path.Combine(_directory.Path, Guid.NewGuid().ToString("N") + ".zip");

        using FileStream file = File.Create(path);
        using ZipArchive archive = new(file, ZipArchiveMode.Create);

        foreach (string name in entryNames)
        {
            ZipArchiveEntry created = archive.CreateEntry(name);
            if (unixMode is { } mode)
            {
                created.ExternalAttributes = (int)mode << 16;
            }

            using Stream entry = created.Open();
            entry.Write(Encoding.UTF8.GetBytes("payload"));
        }

        return path;
    }

    private string StagedDirectory =>
        Path.Combine(_paths.UpdateDirectory, Release.Version.ToString(), "staged");

    [Theory]
    [InlineData("../../evil.exe")]
    [InlineData("/etc/cron.d/evil")]
    [InlineData(@"C:\Windows\System32\evil.dll")]
    public async Task AnArchiveThatNamesAFileOutsideItIsRefusedBeforeAnythingIsWritten(string name)
    {
        // The good entry comes first, so a refusal that arrived too late would leave it behind.
        string archive = ArchiveWith(name, LauncherName);

        ApiException exception = await Assert.ThrowsAsync<ApiException>(
            () => CreateInstaller().StartAsync(
                Release, archive, TestContext.Current.CancellationToken));

        Assert.Equal(ApiErrorCode.Integrity, exception.Code);
        Assert.False(File.Exists(Path.Combine(_directory.Path, "evil.exe")));
        Assert.False(File.Exists(Path.Combine(StagedDirectory, LauncherName)));
    }

    /// <summary>
    /// A build with no <c>updater/</c> beside the launcher cannot swap anything, and finding
    /// that out here rather than after the installation has been renamed is the whole point of
    /// preparing before exiting.
    /// </summary>
    [Fact]
    public async Task ABuildShippingNoUpdaterIsRefusedAfterUnpackingAndBeforeStartingAnything()
    {
        string archive = ArchiveWith(LauncherName, "runtimes/win-x64/native/lib.dll");

        ApiException exception = await Assert.ThrowsAsync<ApiException>(
            () => CreateInstaller().StartAsync(
                Release, archive, TestContext.Current.CancellationToken));

        Assert.Equal(ApiErrorCode.Integrity, exception.Code);

        // Unpacking happened and is harmless: it wrote inside the update directory only.
        Assert.True(File.Exists(Path.Combine(StagedDirectory, LauncherName)));
        Assert.True(File.Exists(
            Path.Combine(StagedDirectory, "runtimes", "win-x64", "native", "lib.dll")));
    }

    [Fact]
    public async Task TheUpdaterIsCopiedOutOfTheDirectoryItIsAboutToReplace()
    {
        string installedUpdater = Path.Combine(_paths.ApplicationDirectory, "updater");
        Directory.CreateDirectory(installedUpdater);

        // Not a runnable executable, so starting it fails — after the copy, which is what this
        // is about: the helper must not still be inside the directory being renamed.
        await File.WriteAllTextAsync(
            Path.Combine(installedUpdater, UpdaterExecutableName), "not really an executable",
            TestContext.Current.CancellationToken);

        string archive = ArchiveWith(LauncherName);

        await Assert.ThrowsAnyAsync<Exception>(
            () => CreateInstaller().StartAsync(
                Release, archive, TestContext.Current.CancellationToken));

        Assert.True(File.Exists(Path.Combine(
            _paths.UpdateDirectory, Release.Version.ToString(), "updater", UpdaterExecutableName)));
    }

    /// <summary>
    /// The launcher's own name is what the swap will start again, so an archive built for a
    /// different runtime identifier — a Linux release reaching a Windows launcher, say — is
    /// refused before the installation is touched rather than discovered as a rollback.
    /// </summary>
    [Fact]
    public async Task AnArchiveWithoutTheLauncherThisOneRunsAsIsRefused()
    {
        string archive = ArchiveWith("SomethingElse", "runtimes/native/lib.dll");

        ApiException exception = await Assert.ThrowsAsync<ApiException>(
            () => CreateInstaller().StartAsync(
                Release, archive, TestContext.Current.CancellationToken));

        Assert.Equal(ApiErrorCode.Integrity, exception.Code);
        Assert.Contains(LauncherName, exception.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// The bit is the difference between an update that installs and one that rolls itself
    /// back on every release: a launcher without it cannot be started, and from the updater
    /// that is indistinguishable from a new version that crashed.
    /// </summary>
    [Fact]
    public async Task OnUnixTheModeInTheArchiveSurvivesTheUnpacking()
    {
        Assert.SkipWhen(
            OperatingSystem.IsWindows(),
            "File modes are a Unix concept; a zip's mode bits mean nothing on Windows.");

        string archive = ArchiveWithModes(
            UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute,
            LauncherName);

        await Assert.ThrowsAnyAsync<Exception>(
            () => CreateInstaller().StartAsync(
                Release, archive, TestContext.Current.CancellationToken));

        // The skip above already decided this; the check is what tells the platform analyzer.
        if (!OperatingSystem.IsWindows())
        {
            Assert.True(File.GetUnixFileMode(Path.Combine(StagedDirectory, LauncherName))
                .HasFlag(UnixFileMode.UserExecute));
        }
    }

    /// <summary>
    /// An archive built on Windows for a Linux runtime identifier carries no mode at all, which
    /// is an ordinary way to cut a release. The launcher is made executable anyway, because
    /// without it nothing in the tree could be started.
    /// </summary>
    [Fact]
    public async Task OnUnixAnArchiveCarryingNoModeStillLeavesALaunchableLauncher()
    {
        Assert.SkipWhen(
            OperatingSystem.IsWindows(),
            "File modes are a Unix concept; a zip's mode bits mean nothing on Windows.");

        string archive = ArchiveWith(LauncherName);

        await Assert.ThrowsAnyAsync<Exception>(
            () => CreateInstaller().StartAsync(
                Release, archive, TestContext.Current.CancellationToken));

        if (!OperatingSystem.IsWindows())
        {
            Assert.True(File.GetUnixFileMode(Path.Combine(StagedDirectory, LauncherName))
                .HasFlag(UnixFileMode.UserExecute));
        }
    }

    private static string UpdaterExecutableName =>
        OperatingSystem.IsWindows() ? "GameLauncher.Updater.exe" : "GameLauncher.Updater";
}

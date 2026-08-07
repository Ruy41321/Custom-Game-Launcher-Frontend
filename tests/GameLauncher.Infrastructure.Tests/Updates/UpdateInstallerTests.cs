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

    public UpdateInstallerTests()
    {
        _paths.UpdateDirectory.Returns(Path.Combine(_directory.Path, "updates"));
        _paths.ApplicationDirectory.Returns(Path.Combine(_directory.Path, "install"));
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

    private string ArchiveWith(params string[] entryNames)
    {
        string path = Path.Combine(_directory.Path, Guid.NewGuid().ToString("N") + ".zip");

        using FileStream file = File.Create(path);
        using ZipArchive archive = new(file, ZipArchiveMode.Create);

        foreach (string name in entryNames)
        {
            using Stream entry = archive.CreateEntry(name).Open();
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
        string archive = ArchiveWith(name, "GameLauncher.exe");

        ApiException exception = await Assert.ThrowsAsync<ApiException>(
            () => CreateInstaller().StartAsync(
                Release, archive, TestContext.Current.CancellationToken));

        Assert.Equal(ApiErrorCode.Integrity, exception.Code);
        Assert.False(File.Exists(Path.Combine(_directory.Path, "evil.exe")));
        Assert.False(File.Exists(Path.Combine(StagedDirectory, "GameLauncher.exe")));
    }

    /// <summary>
    /// A build with no <c>updater/</c> beside the launcher cannot swap anything, and finding
    /// that out here rather than after the installation has been renamed is the whole point of
    /// preparing before exiting.
    /// </summary>
    [Fact]
    public async Task ABuildShippingNoUpdaterIsRefusedAfterUnpackingAndBeforeStartingAnything()
    {
        string archive = ArchiveWith("GameLauncher.exe", "runtimes/win-x64/native/lib.dll");

        ApiException exception = await Assert.ThrowsAsync<ApiException>(
            () => CreateInstaller().StartAsync(
                Release, archive, TestContext.Current.CancellationToken));

        Assert.Equal(ApiErrorCode.Integrity, exception.Code);

        // Unpacking happened and is harmless: it wrote inside the update directory only.
        Assert.True(File.Exists(Path.Combine(StagedDirectory, "GameLauncher.exe")));
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

        string archive = ArchiveWith("GameLauncher.exe");

        await Assert.ThrowsAnyAsync<Exception>(
            () => CreateInstaller().StartAsync(
                Release, archive, TestContext.Current.CancellationToken));

        Assert.True(File.Exists(Path.Combine(
            _paths.UpdateDirectory, Release.Version.ToString(), "updater", UpdaterExecutableName)));
    }

    private static string UpdaterExecutableName =>
        OperatingSystem.IsWindows() ? "GameLauncher.Updater.exe" : "GameLauncher.Updater";
}

using GameLauncher.Core.Installs;
using GameLauncher.Core.Launching;
using GameLauncher.Core.Models;
using GameLauncher.Infrastructure.Installs;
using GameLauncher.Infrastructure.Launching;
using Microsoft.Extensions.Logging.Abstractions;

namespace GameLauncher.Infrastructure.Tests.Launching;

/// <summary>
/// Starts a real process, because the part worth proving here is the part
/// <see cref="LaunchPlanner"/> cannot cover: that the launcher notices the exit and stops
/// claiming the game is running. The "game" is the platform's own shell exiting immediately.
/// </summary>
public sealed class ProcessGameLauncherTests : IDisposable
{
    private static readonly DateTimeOffset Now = new(2026, 8, 3, 12, 0, 0, TimeSpan.Zero);

    private readonly TemporaryDirectory _directory = new();
    private readonly SqliteInstallStore _store;
    private readonly FakeTimeProvider _clock = new(Now);
    private readonly ProcessGameLauncher _launcher;

    public ProcessGameLauncherTests()
    {
        _store = new SqliteInstallStore(_directory.File("launcher.db"));
        _launcher = new ProcessGameLauncher(
            _store, _clock, NullLogger<ProcessGameLauncher>.Instance);
    }

    /// <summary>The shell, which every one of the three platforms has and which exits on demand.</summary>
    private static (string Directory, string Entrypoint, string Args) ShellThatExits =>
        OperatingSystem.IsWindows()
            ? (Environment.GetFolderPath(Environment.SpecialFolder.System), "cmd.exe", "/c exit 0")
            : ("/bin", "sh", "-c \"exit 0\"");

    public void Dispose()
    {
        _launcher.Dispose();
        _store.Dispose();
        _directory.Dispose();
    }

    private async Task<InstalledGame> InstalledShellAsync(
        InstallState state = InstallState.Installed)
    {
        (string directory, string entrypoint, string args) = ShellThatExits;

        InstalledGame install = new()
        {
            GameId = "g1",
            GameSlug = "orbital-drift",
            GameTitle = "Orbital Drift",
            BuildId = "b1",
            VersionId = "v1",
            VersionSemver = "0.1.0",
            Platform = GamePlatform.Windows,
            Architecture = BuildArchitecture.X64,
            InstallDirectory = directory,
            Entrypoint = entrypoint,
            LaunchArgs = args,
            State = state,
            InstalledAt = Now,
            UpdatedAt = Now,
        };

        await _store.SaveAsync(install, TestContext.Current.CancellationToken);
        return install;
    }

    [Fact]
    public async Task StartingAGameRecordsThatItWasPlayedAndThatItIsRunning()
    {
        await InstalledShellAsync();

        TaskCompletionSource exited = new();
        _launcher.GameExited += (_, _) => exited.TrySetResult();

        RunningGame running = await _launcher.LaunchAsync(
            "g1", TestContext.Current.CancellationToken);

        Assert.True(running.ProcessId > 0);
        Assert.Equal(Now, running.StartedAt);

        InstalledGame? stored = await _store.FindAsync(
            "g1", TestContext.Current.CancellationToken);
        Assert.Equal(Now, stored?.LastPlayedAt);

        await exited.Task.WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);

        Assert.False(_launcher.IsRunning("g1"));
        Assert.Empty(_launcher.Running);
    }

    [Fact]
    public async Task TheExitIsReportedWithHowLongItRanFor()
    {
        await InstalledShellAsync();

        TaskCompletionSource<GameExitedEventArgs> exited = new();
        _launcher.GameExited += (_, args) => exited.TrySetResult(args);

        await _launcher.LaunchAsync("g1", TestContext.Current.CancellationToken);
        _clock.Advance(TimeSpan.FromMinutes(90));

        GameExitedEventArgs args = await exited.Task.WaitAsync(
            TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);

        Assert.Equal("g1", args.GameId);
        Assert.Equal(TimeSpan.FromMinutes(90), args.Played);
    }

    [Fact]
    public async Task AGameThatIsNotInstalledIsRefusedRatherThanStarted()
    {
        GameLaunchException exception = await Assert.ThrowsAsync<GameLaunchException>(() =>
            _launcher.LaunchAsync("nothing", TestContext.Current.CancellationToken));

        Assert.Equal(LaunchFailure.NotInstalled, exception.Reason);
    }

    [Fact]
    public async Task AnUnfinishedInstallIsRefused()
    {
        await InstalledShellAsync(InstallState.Applying);

        GameLaunchException exception = await Assert.ThrowsAsync<GameLaunchException>(() =>
            _launcher.LaunchAsync("g1", TestContext.Current.CancellationToken));

        Assert.Equal(LaunchFailure.NotPlayable, exception.Reason);
    }

    // Nothing on this machine is at that path, so the operating system never gets asked.
    [Fact]
    public async Task AMissingExecutableIsRefusedBeforeTheOperatingSystemSeesIt()
    {
        InstalledGame install = await InstalledShellAsync();
        await _store.SaveAsync(
            install with { InstallDirectory = _directory.Path, Entrypoint = "bin/Game.exe" },
            TestContext.Current.CancellationToken);

        GameLaunchException exception = await Assert.ThrowsAsync<GameLaunchException>(() =>
            _launcher.LaunchAsync("g1", TestContext.Current.CancellationToken));

        Assert.Equal(LaunchFailure.EntrypointMissing, exception.Reason);
    }
}

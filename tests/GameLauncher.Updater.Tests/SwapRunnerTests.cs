using GameLauncher.Core.Updates;

namespace GameLauncher.Updater.Tests;

/// <summary>
/// The swap against real directories, with the launcher substituted.
///
/// The rejection path matters at least as much as the happy one: every one of these is an
/// installation that is either replaced or put back, and the only one that can be checked by
/// hand is the one that happens on somebody's machine.
/// </summary>
public sealed class SwapRunnerTests : IDisposable
{
    private const string OldMarker = "old.txt";
    private const string NewMarker = "new.txt";

    private readonly TemporaryDirectory _root = new();
    private readonly FakeTimeProvider _time = new();
    private readonly StringWriter _log = new();

    private string Target => Path.Combine(_root.Path, "Launcher");

    private string Source => Path.Combine(_root.Path, "staged");

    private string Previous => UpdateSwapPaths.PreviousOf(Target);

    private string Relaunch => Path.Combine(Target, "GameLauncher.exe");

    public SwapRunnerTests()
    {
        Write(Target, OldMarker, "the installation that works");
        Write(Source, NewMarker, "the new version");
    }

    public void Dispose()
    {
        _root.Dispose();
        _log.Dispose();
    }

    [Fact]
    public void AnExitOfZeroInsideTheWindowKeepsTheNewVersion()
    {
        FakeProcessStarter starter = Run(TimeSpan.FromSeconds(3), exitCode: 0, out int code);

        Assert.Equal(ExitCodes.Ok, code);
        Assert.True(File.Exists(Path.Combine(Target, NewMarker)));
        Assert.False(Directory.Exists(Previous));
        Assert.Equal([Relaunch], starter.Started);
    }

    [Fact]
    public void StillRunningWhenTheWindowClosesKeepsTheNewVersion()
    {
        Run(RelaunchWatch.Window + TimeSpan.FromSeconds(5), exitCode: 0, out int code);

        Assert.Equal(ExitCodes.Ok, code);
        Assert.True(File.Exists(Path.Combine(Target, NewMarker)));
        Assert.False(Directory.Exists(Previous));
    }

    [Fact]
    public void ANonZeroExitInsideTheWindowRestoresTheOldVersionAndStartsItAgain()
    {
        FakeProcessStarter starter = Run(TimeSpan.FromSeconds(2), exitCode: 1, out int code);

        Assert.Equal(ExitCodes.Restored, code);
        Assert.True(File.Exists(Path.Combine(Target, OldMarker)));
        Assert.False(File.Exists(Path.Combine(Target, NewMarker)));
        Assert.False(Directory.Exists(Previous));

        // Once for the new launcher, once for the old one that was put back. Restoring the
        // files and leaving somebody with no application open would be half a rollback.
        Assert.Equal([Relaunch, Relaunch], starter.Started);
    }

    /// <summary>
    /// The declared hole, tested as intended behaviour rather than left to chance: a launcher
    /// that starts, survives the window and only then fails is not rolled back. Nothing here
    /// can tell that from somebody quitting an application that worked.
    /// </summary>
    [Fact]
    public void ANonZeroExitAfterTheWindowIsNotRolledBack()
    {
        Run(RelaunchWatch.Window + TimeSpan.FromSeconds(1), exitCode: 1, out int code);

        Assert.Equal(ExitCodes.Ok, code);
        Assert.True(File.Exists(Path.Combine(Target, NewMarker)));
        Assert.False(Directory.Exists(Previous));
    }

    [Fact]
    public void ATargetThatDoesNotExistIsRefusedWithoutTouchingAnything()
    {
        int code = Runner(new FakeProcessStarter(Process(TimeSpan.Zero, 0))).Run(
            Request() with { TargetDirectory = Path.Combine(_root.Path, "nowhere") });

        Assert.Equal(ExitCodes.Usage, code);
        Assert.True(File.Exists(Path.Combine(Source, NewMarker)));
        Assert.True(File.Exists(Path.Combine(Target, OldMarker)));
    }

    [Fact]
    public void ASourceThatDoesNotExistIsRefusedWithoutTouchingAnything()
    {
        int code = Runner(new FakeProcessStarter(Process(TimeSpan.Zero, 0))).Run(
            Request() with { SourceDirectory = Path.Combine(_root.Path, "nowhere") });

        Assert.Equal(ExitCodes.Usage, code);
        Assert.True(File.Exists(Path.Combine(Target, OldMarker)));
        Assert.False(Directory.Exists(Previous));
    }

    [Fact]
    public void ALauncherThatIsStillRunningStopsTheSwap()
    {
        FakeProcessWaiter waiter = new(exits: false);
        SwapRunner runner = new(
            new FakeProcessStarter(Process(TimeSpan.Zero, 0)), waiter, _time, _log);

        int code = runner.Run(Request() with { WaitForProcessId = 4242 });

        Assert.Equal(ExitCodes.Usage, code);
        Assert.Equal(4242, waiter.AskedAbout);
        Assert.True(File.Exists(Path.Combine(Target, OldMarker)));
        Assert.False(Directory.Exists(Previous));
    }

    /// <summary>
    /// A leftover from an attempt that never resolved is stale by proof: the launcher that just
    /// asked for this update was running, so whatever is in the target directory works. Keeping
    /// the older copy would make the *next* rollback restore a version two updates behind.
    /// </summary>
    [Fact]
    public void APreviousInstallationLeftByAnEarlierAttemptIsDiscarded()
    {
        Write(Previous, "stale.txt", "from an attempt that never finished");

        Run(TimeSpan.FromSeconds(1), exitCode: 0, out int code);

        Assert.Equal(ExitCodes.Ok, code);
        Assert.True(File.Exists(Path.Combine(Target, NewMarker)));
        Assert.False(Directory.Exists(Previous));
    }

    [Fact]
    public void WithNothingToRelaunchTheSwapIsSimplyDone()
    {
        FakeProcessStarter starter = new(Process(TimeSpan.Zero, 0));
        int code = Runner(starter).Run(Request() with { RelaunchExecutable = null });

        Assert.Equal(ExitCodes.Ok, code);
        Assert.True(File.Exists(Path.Combine(Target, NewMarker)));
        Assert.False(Directory.Exists(Previous));
        Assert.Empty(starter.Started);
    }

    [Fact]
    public void RollbackPutsThePreviousInstallationBackAndStartsIt()
    {
        Directory.Move(Target, Previous);
        Write(Target, NewMarker, "the version somebody wants gone");

        FakeProcessStarter starter = new(Process(TimeSpan.Zero, 0));
        int code = Runner(starter).Run(new UpdateSwapRequest
        {
            SourceDirectory = string.Empty,
            TargetDirectory = Target,
            RelaunchExecutable = Relaunch,
            RollbackOnly = true,
        });

        Assert.Equal(ExitCodes.Ok, code);
        Assert.True(File.Exists(Path.Combine(Target, OldMarker)));
        Assert.False(File.Exists(Path.Combine(Target, NewMarker)));
        Assert.Equal([Relaunch], starter.Started);
    }

    [Fact]
    public void RollbackWithNothingToRollBackToIsRefused()
    {
        int code = Runner(new FakeProcessStarter(Process(TimeSpan.Zero, 0))).Run(
            new UpdateSwapRequest
            {
                SourceDirectory = string.Empty,
                TargetDirectory = Target,
                RollbackOnly = true,
            });

        Assert.Equal(ExitCodes.Usage, code);
        Assert.True(File.Exists(Path.Combine(Target, OldMarker)));
    }

    private FakeProcessStarter Run(TimeSpan runsFor, int exitCode, out int code)
    {
        FakeProcessStarter starter = new(Process(runsFor, exitCode));
        code = Runner(starter).Run(Request());
        return starter;
    }

    private SwapRunner Runner(FakeProcessStarter starter) =>
        new(starter, new FakeProcessWaiter(exits: true), _time, _log);

    private FakeRelaunchedProcess Process(TimeSpan runsFor, int exitCode) =>
        new(_time, runsFor, exitCode);

    private UpdateSwapRequest Request() => new()
    {
        SourceDirectory = Source,
        TargetDirectory = Target,
        RelaunchExecutable = Relaunch,
    };

    private static void Write(string directory, string name, string content)
    {
        Directory.CreateDirectory(directory);
        File.WriteAllText(Path.Combine(directory, name), content);
    }
}

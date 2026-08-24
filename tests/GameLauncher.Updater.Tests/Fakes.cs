using GameLauncher.Core.Updates;

namespace GameLauncher.Updater.Tests;

/// <summary>
/// A launcher that never runs. It answers the two questions the verdict is a function of, and
/// records what it was asked to start — which is how a test sees that a rollback really put the
/// old launcher back rather than merely restoring its files.
/// </summary>
internal sealed class FakeProcessStarter(FakeRelaunchedProcess process) : IProcessStarter
{
    public List<string> Started { get; } = [];

    public IRelaunchedProcess Start(string executable, string workingDirectory)
    {
        Started.Add(executable);
        return process;
    }
}

/// <summary>
/// Waiting consumes time, and this says so: <see cref="WaitForExit"/> advances the clock the
/// verdict reads. A fake whose clock stood still would make every outcome look instantaneous
/// and the thirty-second window untestable.
/// </summary>
internal sealed class FakeRelaunchedProcess(
    FakeTimeProvider time, TimeSpan runsFor, int exitCode) : IRelaunchedProcess
{
    public bool WaitForExit(TimeSpan timeout)
    {
        bool exits = runsFor <= timeout;
        time.Advance(exits ? runsFor : timeout);
        return exits;
    }

    public int ExitCode => exitCode;
}

internal sealed class FakeProcessWaiter(bool exits) : IProcessWaiter
{
    public int? AskedAbout { get; private set; }

    public bool WaitForExit(int processId, TimeSpan timeout)
    {
        AskedAbout = processId;
        return exits;
    }
}

/// <summary>
/// A clock the test moves by hand. Only <see cref="GetTimestamp"/> is overridden because that
/// is what <see cref="TimeProvider.GetElapsedTime(long)"/> reads, and elapsed time is the whole
/// of what the decision needs.
/// </summary>
internal sealed class FakeTimeProvider : TimeProvider
{
    private long _ticks;

    public override long GetTimestamp() => _ticks;

    public override long TimestampFrequency => TimeSpan.TicksPerSecond;

    public void Advance(TimeSpan amount) => _ticks += amount.Ticks;
}

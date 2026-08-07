namespace GameLauncher.Core.Updates;

/// <summary>
/// The launcher the updater just started, seen through the only two questions the decision
/// asks of it. Substituting this is what turns the hardest-to-test piece in the repository
/// into a test: the verdict is a pure function of an exit code and an elapsed time, and both
/// come from here.
/// </summary>
public interface IRelaunchedProcess
{
    /// <summary>
    /// Waits up to <paramref name="timeout"/> and answers whether the process ended inside it.
    /// </summary>
    bool WaitForExit(TimeSpan timeout);

    /// <summary>Only meaningful once <see cref="WaitForExit"/> has answered true.</summary>
    int ExitCode { get; }
}

/// <summary>Starts the relaunched launcher. The updater's one piece of real process work.</summary>
public interface IProcessStarter
{
    IRelaunchedProcess Start(string executable, string workingDirectory);
}

/// <summary>Waits for the launcher that asked for the update to be gone.</summary>
public interface IProcessWaiter
{
    /// <summary>
    /// Returns when the process has exited or the timeout is up; answers whether it is gone.
    /// A process id that names nothing is already gone, which is the ordinary case when the
    /// launcher exits faster than this helper starts.
    /// </summary>
    bool WaitForExit(int processId, TimeSpan timeout);
}

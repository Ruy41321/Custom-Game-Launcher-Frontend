using System.Diagnostics;
using GameLauncher.Core.Updates;

namespace GameLauncher.Updater;

/// <summary>The real launcher, started for real. Substituted in every test.</summary>
internal sealed class SystemProcessStarter : IProcessStarter
{
    public IRelaunchedProcess Start(string executable, string workingDirectory) =>
        new SystemRelaunchedProcess(Process.Start(new ProcessStartInfo
        {
            FileName = executable,
            WorkingDirectory = workingDirectory,
            UseShellExecute = false,
        }) ?? throw new IOException($"The launcher could not be started: {executable}"));
}

internal sealed class SystemRelaunchedProcess(Process process) : IRelaunchedProcess
{
    public bool WaitForExit(TimeSpan timeout) => process.WaitForExit((int)timeout.TotalMilliseconds);

    public int ExitCode => process.ExitCode;
}

/// <summary>
/// Waits for the launcher that asked for the update. A process id that names nothing is already
/// gone, which is the ordinary case: the launcher usually exits before this helper is scheduled.
/// </summary>
internal sealed class SystemProcessWaiter : IProcessWaiter
{
    public bool WaitForExit(int processId, TimeSpan timeout)
    {
        Process process;
        try
        {
            process = Process.GetProcessById(processId);
        }
        catch (ArgumentException)
        {
            return true;
        }

        using (process)
        {
            return process.WaitForExit((int)timeout.TotalMilliseconds);
        }
    }
}

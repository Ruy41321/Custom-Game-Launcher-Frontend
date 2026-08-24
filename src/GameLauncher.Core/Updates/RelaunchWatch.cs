namespace GameLauncher.Core.Updates;

/// <summary>
/// Whether the launcher that was just put in place is the one to keep.
///
/// <b>The decision is a pure function of (exit code, elapsed time)</b>, and that is the whole
/// design. No marker file, no IPC, no watchdog outliving its purpose: anything else would need
/// the two processes to agree on a protocol while one of them is the thing under suspicion, and
/// none of it could be exercised without really replacing an installation.
///
/// The hole is declared rather than hidden: a launcher that starts, survives the window and
/// then crashes is <b>not</b> rolled back. That is what the crash reports are for, and
/// <c>--rollback</c> stays a documented manual flag for as long as the old installation is
/// still on disk.
/// </summary>
public static class RelaunchWatch
{
    /// <summary>
    /// Long enough for a launcher to fail at start-up — a missing runtime file, a broken
    /// configuration, an exception before the first frame — and short enough that nobody is
    /// left looking at nothing. Everything past it is somebody using the application.
    /// </summary>
    public static readonly TimeSpan Window = TimeSpan.FromSeconds(30);

    /// <summary>
    /// <paramref name="exitCode"/> is null when the process is still running.
    /// </summary>
    public static SwapVerdict Judge(int? exitCode, TimeSpan elapsed) => exitCode switch
    {
        // Still alive when the window closed: it started, which is all this can ever prove.
        null => SwapVerdict.Succeeded,

        // Quit cleanly. Somebody closing the new launcher straight away is not a failed update,
        // and treating it as one would roll back an installation that works.
        0 => SwapVerdict.Succeeded,

        // The declared hole lives in this line: only a failure fast enough to be a start-up
        // failure is read as one.
        _ => elapsed < Window ? SwapVerdict.Restore : SwapVerdict.Succeeded,
    };

    /// <summary>
    /// Waits on <paramref name="process"/> for at most <see cref="Window"/> and judges it.
    /// The clock is read rather than assumed, because the wait can return early.
    /// </summary>
    public static SwapVerdict Watch(IRelaunchedProcess process, TimeProvider time)
    {
        long started = time.GetTimestamp();
        bool exited = process.WaitForExit(Window);
        TimeSpan elapsed = time.GetElapsedTime(started);

        return Judge(exited ? process.ExitCode : null, elapsed);
    }
}

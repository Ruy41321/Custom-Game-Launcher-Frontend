namespace GameLauncher.Core.Diagnostics;

/// <summary>What one sweep of the pending reports did. Reported for the log, never to the user.</summary>
public sealed record CrashUploadResult
{
    public int Sent { get; init; }

    /// <summary>Files that were dropped without being sent: unreadable, or refused as
    /// permanently invalid by the server.</summary>
    public int Discarded { get; init; }

    /// <summary>Files left on disk to try again next time — the server was unreachable, or it
    /// asked us to slow down.</summary>
    public int Deferred { get; init; }
}

/// <summary>
/// Sends what previous runs wrote down, if the user has asked for that.
///
/// Called once at startup and never again: a crash report is written by a process that is
/// dying, so the run that could send it is the *next* one. There is no queue and no retry
/// timer — a report that could not be sent is simply still on disk when the launcher next
/// starts, which is the same mechanism that got it there.
/// </summary>
public interface ICrashReportUploader
{
    /// <summary>
    /// Sends every pending report, and deletes each one that was accepted.
    ///
    /// Does nothing at all unless <c>UserSettings.SendCrashReports</c> is true — and when it is
    /// false, does not merely skip sending: the pending files are **deleted**, because a person
    /// who has said no should not have a growing pile of unsent crash reports about them on
    /// their own disk.
    ///
    /// Never throws. A launcher that failed to start because it could not report a previous
    /// failure would be the worst possible outcome of this feature.
    /// </summary>
    Task<CrashUploadResult> UploadPendingAsync(CancellationToken cancellationToken = default);
}

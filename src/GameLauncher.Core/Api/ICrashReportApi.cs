using GameLauncher.Core.Diagnostics;

namespace GameLauncher.Core.Api;

/// <summary>
/// Sending a crash report. Rides on the **tokenless** client, beside <c>/auth</c> and
/// <c>/capabilities</c>, and for a related reason: a launcher crashes on the sign-in screen as
/// readily as anywhere else — more readily, since that is where a broken configuration shows —
/// so a route that needed a session would be missing exactly the failures worth having. The
/// server does not look for a token either, and stores no account against a report.
/// </summary>
public interface ICrashReportApi
{
    /// <summary>
    /// Sends one report. Returns the fingerprint the server grouped it under, which is the one
    /// thing the client did not already know and the thing a person filing a bug can quote.
    /// </summary>
    Task<string> SubmitAsync(CrashReport report, CancellationToken cancellationToken = default);
}

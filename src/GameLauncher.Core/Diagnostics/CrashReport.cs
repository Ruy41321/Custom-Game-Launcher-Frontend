namespace GameLauncher.Core.Diagnostics;

/// <summary>
/// What the launcher wrote down when it died, and exactly what it will send if the user has
/// opted in. One shape for both, so a report on disk is the request body and there is nothing
/// to parse back — a second definition of this document would be a second thing to keep in
/// step with the server.
///
/// **Nothing here identifies the person or the machine.** The server stores no account against
/// a report, and the client sends nothing that would let one be inferred: no user name, no
/// install path, no machine name. What survives is what a developer needs to fix the bug.
/// </summary>
public sealed record CrashReport
{
    /// <summary>Which handler caught it: <c>unhandled</c>, <c>unobserved-task</c>, <c>startup</c>.</summary>
    public required string Kind { get; init; }

    /// <summary>When the launcher died, which is not when the server hears about it: a report
    /// is written to disk and sent on the *next* start, so the two can be days apart.</summary>
    public required DateTimeOffset OccurredAt { get; init; }

    public string LauncherVersion { get; init; } = string.Empty;

    public string Platform { get; init; } = string.Empty;

    public string ExceptionType { get; init; } = string.Empty;

    public string Message { get; init; } = string.Empty;

    public string StackTrace { get; init; } = string.Empty;
}

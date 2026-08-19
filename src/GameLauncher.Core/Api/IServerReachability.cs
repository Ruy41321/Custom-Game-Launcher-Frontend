namespace GameLauncher.Core.Api;

/// <summary>Raised when the last known answer about the server changes, and only then.</summary>
public sealed class ReachabilityChangedEventArgs(bool isOnline) : EventArgs
{
    public bool IsOnline { get; } = isOnline;
}

/// <summary>
/// Whether the API is answering, as one fact shared by everything that talks to it.
///
/// It exists because the alternative is what this launcher did before: every caller
/// discovered a dead server for itself, at the cost of a full connection timeout each, and
/// discovered it again on the very next request. A start-up with an unreachable server spent
/// twenty seconds on a token rotation that could not succeed, showed the sign-in screen for
/// the whole of it, and then spent twenty more on the library — which is a launcher that is
/// unusable offline for no better reason than that nobody wrote the answer down.
/// </summary>
public interface IServerReachability
{
    /// <summary>
    /// What the last completed attempt said. This is the one the UI shows: it stays false
    /// through the retry window and only turns true again when a request actually succeeds,
    /// so the banner never claims a server that is still missing.
    /// </summary>
    bool IsOnline { get; }

    /// <summary>
    /// Whether a request is worth putting on the wire. Unlike <see cref="IsOnline"/> this
    /// turns true again on its own once <see cref="ServerReachability.RetryAfter"/> has
    /// passed — the circuit half-opens, one call finds out, and its outcome decides the next
    /// window.
    /// </summary>
    bool AllowsRequests { get; }

    /// <summary>
    /// Whether this server has actually answered something since the launcher started, with
    /// nothing having failed since. False on the first request of a run, and again after any
    /// failure.
    ///
    /// It exists so that the deadline which catches a *hung* server — one that accepts the
    /// connection and then says nothing — applies only while the launcher is still finding
    /// out. Once a server has answered, its slow routes are given all the time they need: the
    /// download plan diffs two manifests server-side, and refusing that on a deadline would
    /// break a working launcher to fix a broken server.
    /// </summary>
    bool IsProven { get; }

    event EventHandler<ReachabilityChangedEventArgs>? Changed;

    /// <summary>A request completed: the server is there, whatever it answered.</summary>
    void ReportReachable();

    /// <summary>A request never reached a server. Holds the circuit open for a while.</summary>
    void ReportUnreachable();

    /// <summary>
    /// Opens the gate now rather than when the window elapses, for the one case that deserves
    /// it: somebody pressed a button. A deliberate action is always allowed to find out for
    /// itself, and it does not pretend the server is back — only the attempt's outcome does.
    /// </summary>
    void RetryNow();
}

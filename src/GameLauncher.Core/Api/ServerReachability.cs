namespace GameLauncher.Core.Api;

/// <summary>
/// The circuit breaker in front of the API. Pure state over a <see cref="TimeProvider"/> —
/// no HTTP, no timer, nothing to dispose — so the whole policy is unit testable and the one
/// thing it must never do, which is stay open after the server comes back, is provable.
///
/// The window is deliberately short. It is not there to spare the server; it is there to
/// spare the person in front of the window, who would otherwise pay one connection timeout
/// per request for the length of a train journey. Twenty seconds is long enough that a page
/// of a dozen cards costs one failed attempt instead of a dozen, and short enough that a
/// launcher left open recovers by itself shortly after the network does.
/// </summary>
public sealed class ServerReachability(TimeProvider time) : IServerReachability
{
    /// <summary>How long the circuit stays shut before one request is allowed to find out.</summary>
    public static readonly TimeSpan RetryAfter = TimeSpan.FromSeconds(20);

    private readonly Lock _gate = new();

    /// <summary>When the gate opens again. Null while the server is answering.</summary>
    private DateTimeOffset? _closedUntil;

    private bool _isOnline = true;

    /// <summary>False until something comes back, and false again after anything fails.</summary>
    private bool _isProven;

    public bool IsOnline
    {
        get
        {
            lock (_gate)
            {
                return _isOnline;
            }
        }
    }

    public bool IsProven
    {
        get
        {
            lock (_gate)
            {
                return _isProven;
            }
        }
    }

    public bool AllowsRequests
    {
        get
        {
            lock (_gate)
            {
                return _closedUntil is not { } until || time.GetUtcNow() >= until;
            }
        }
    }

    public event EventHandler<ReachabilityChangedEventArgs>? Changed;

    public void ReportReachable()
    {
        bool changed;
        lock (_gate)
        {
            _closedUntil = null;
            changed = !_isOnline;
            _isOnline = true;
            _isProven = true;
        }

        // Outside the lock: a handler runs arbitrary code, and holding a lock across it is how
        // a UI thread ends up waiting on a request thread.
        Announce(changed, isOnline: true);
    }

    public void ReportUnreachable()
    {
        bool changed;
        lock (_gate)
        {
            _closedUntil = time.GetUtcNow() + RetryAfter;
            changed = _isOnline;
            _isOnline = false;
            _isProven = false;
        }

        Announce(changed, isOnline: false);
    }

    public void RetryNow()
    {
        lock (_gate)
        {
            // Only the gate. Whether the server is back is not something a button knows, and
            // announcing it here would put the offline notice away before anything succeeded.
            _closedUntil = null;
        }
    }

    private void Announce(bool changed, bool isOnline)
    {
        if (changed)
        {
            Changed?.Invoke(this, new ReachabilityChangedEventArgs(isOnline));
        }
    }
}

namespace GameLauncher.App.Tests;

/// <summary>
/// A clock the test moves by hand, as the other two test projects have — and, unlike them, one
/// that hands out timers too. A debounce needs a timer rather than the time of day, so a fake
/// that only answers <see cref="GetUtcNow"/> cannot drive one; and a debounce a test has to
/// really wait out is a slow test that fails on a loaded machine rather than on a bug.
/// </summary>
internal sealed class FakeTimeProvider(DateTimeOffset now) : TimeProvider
{
    private readonly List<FakeTimer> _timers = [];

    public DateTimeOffset Now { get; set; } = now;

    public override DateTimeOffset GetUtcNow() => Now;

    public override ITimer CreateTimer(
        TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
    {
        FakeTimer timer = new(callback, state, this);
        _timers.Add(timer);
        timer.Change(dueTime, period);
        return timer;
    }

    /// <summary>
    /// Moves the clock, and with it fires every timer that has come due. The copy is because a
    /// callback is allowed to dispose its own timer, which is what a one-shot does.
    /// </summary>
    public void Advance(TimeSpan amount)
    {
        Now += amount;

        foreach (FakeTimer timer in _timers.ToList())
        {
            timer.FireIfDue(Now);
        }
    }

    private void Forget(FakeTimer timer) => _timers.Remove(timer);

    private sealed class FakeTimer(TimerCallback callback, object? state, FakeTimeProvider clock)
        : ITimer
    {
        private DateTimeOffset? _dueAt;
        private TimeSpan _period = Timeout.InfiniteTimeSpan;

        public bool Change(TimeSpan dueTime, TimeSpan period)
        {
            _dueAt = dueTime == Timeout.InfiniteTimeSpan ? null : clock.Now + dueTime;
            _period = period;
            return true;
        }

        public void FireIfDue(DateTimeOffset now)
        {
            if (_dueAt is not { } due || now < due)
            {
                return;
            }

            // The next due time is set *before* the callback runs, so a callback that rearms
            // the timer — which is exactly what a debounce does — wins rather than being
            // overwritten by the schedule it just replaced.
            _dueAt = _period == Timeout.InfiniteTimeSpan ? null : now + _period;
            callback(state);
        }

        public void Dispose() => clock.Forget(this);

        public ValueTask DisposeAsync()
        {
            Dispose();
            return ValueTask.CompletedTask;
        }
    }
}

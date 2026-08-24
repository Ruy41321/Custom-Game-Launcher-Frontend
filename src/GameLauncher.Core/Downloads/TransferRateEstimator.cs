namespace GameLauncher.Core.Downloads;

/// <summary>
/// Turns a series of byte counts into a speed and an estimate. A sliding window rather than an
/// average since the start: what a person wants to know is how fast it is going *now*, and an
/// average from the beginning takes minutes to notice that the line has come back.
/// </summary>
public sealed class TransferRateEstimator(TimeProvider time, TimeSpan? window = null)
{
    private readonly TimeSpan _window = window ?? TimeSpan.FromSeconds(5);
    private readonly Queue<(DateTimeOffset At, long Bytes)> _samples = new();

    /// <summary>Zero until there are two samples far enough apart to divide by.</summary>
    public double BytesPerSecond { get; private set; }

    /// <summary>Feeds in the cumulative bytes transferred so far.</summary>
    public void Observe(long transferredBytes)
    {
        DateTimeOffset now = time.GetUtcNow();
        _samples.Enqueue((now, transferredBytes));

        while (_samples.Count > 2 && now - _samples.Peek().At > _window)
        {
            _samples.Dequeue();
        }

        (DateTimeOffset at, long bytes) = _samples.Peek();
        double seconds = (now - at).TotalSeconds;

        // Below a tenth of a second the division is mostly measuring the clock's resolution,
        // and the number it produces jumps around far too much to show anyone.
        if (seconds >= 0.1)
        {
            BytesPerSecond = Math.Max(0, (transferredBytes - bytes) / seconds);
        }
    }

    /// <summary>
    /// How long the rest is expected to take, or null when there is nothing to base it on.
    /// A missing estimate is better than a made-up one: a countdown that says four hours and
    /// then twelve seconds is worse than no countdown.
    /// </summary>
    public TimeSpan? Remaining(long transferredBytes, long totalBytes)
    {
        long left = totalBytes - transferredBytes;
        return left > 0 && BytesPerSecond > 0
            ? TimeSpan.FromSeconds(left / BytesPerSecond)
            : null;
    }

    public void Reset()
    {
        _samples.Clear();
        BytesPerSecond = 0;
    }
}

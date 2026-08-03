namespace GameLauncher.Infrastructure.Tests.Downloads;

/// <summary>
/// Adds up progress reports on the thread that made them. <see cref="Progress{T}"/> posts its
/// callback to the thread pool, so a test that asserted on it would be asserting on whether
/// the pool had got round to it yet.
/// </summary>
internal sealed class CountingProgress : IProgress<long>
{
    private long _total;

    public long Total => Interlocked.Read(ref _total);

    public void Report(long value) => Interlocked.Add(ref _total, value);
}

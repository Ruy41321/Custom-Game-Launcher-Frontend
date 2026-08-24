namespace GameLauncher.Infrastructure.Tests.Progressing;

/// <summary>
/// Reports on the calling thread. <see cref="Progress{T}"/> posts to a captured context, which
/// in a test is the thread pool - so a test asserting on what a callback recorded would be
/// asserting on whether the pool had caught up.
/// </summary>
internal sealed class ImmediateProgress<T>(Action<T> report) : IProgress<T>
{
    public void Report(T value) => report(value);
}

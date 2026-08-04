namespace GameLauncher.Infrastructure.Tests;

/// <summary>
/// A clock the test moves by hand. The same one Core.Tests has: a test project cannot
/// reference another test project, and sharing it through the production assembly would put a
/// test double in shipped code.
/// </summary>
internal sealed class FakeTimeProvider(DateTimeOffset now) : TimeProvider
{
    public DateTimeOffset Now { get; set; } = now;

    /// <summary>
    /// How far the clock moves on every read. Zero — the default — is a clock that only moves
    /// when the test says so.
    ///
    /// A non-zero step is for measuring something whose two readings happen on threads the
    /// test does not control: a game's exit is reported by the runtime whenever the process
    /// actually ends, so advancing the clock from the test between those two readings is a
    /// race that passes on a slow machine and fails on a fast one.
    /// </summary>
    public TimeSpan Step { get; set; }

    public override DateTimeOffset GetUtcNow()
    {
        DateTimeOffset reading = Now;
        Now += Step;
        return reading;
    }

    public void Advance(TimeSpan amount) => Now += amount;
}

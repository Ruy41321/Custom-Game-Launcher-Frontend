namespace GameLauncher.Core.Tests;

/// <summary>
/// A clock the test moves by hand. Token expiry is the whole point of the session logic, and
/// a test that has to sleep to reach it is a test that will be flaky on a loaded CI runner.
/// </summary>
internal sealed class FakeTimeProvider(DateTimeOffset now) : TimeProvider
{
    public DateTimeOffset Now { get; set; } = now;

    public override DateTimeOffset GetUtcNow() => Now;

    public void Advance(TimeSpan amount) => Now += amount;
}

namespace GameLauncher.Infrastructure.Tests;

/// <summary>
/// A clock the test moves by hand. The same one Core.Tests has: a test project cannot
/// reference another test project, and sharing it through the production assembly would put a
/// test double in shipped code.
/// </summary>
internal sealed class FakeTimeProvider(DateTimeOffset now) : TimeProvider
{
    public DateTimeOffset Now { get; set; } = now;

    public override DateTimeOffset GetUtcNow() => Now;

    public void Advance(TimeSpan amount) => Now += amount;
}

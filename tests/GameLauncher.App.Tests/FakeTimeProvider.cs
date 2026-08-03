namespace GameLauncher.App.Tests;

/// <summary>A clock the test moves by hand, as the other two test projects have.</summary>
internal sealed class FakeTimeProvider(DateTimeOffset now) : TimeProvider
{
    public DateTimeOffset Now { get; set; } = now;

    public override DateTimeOffset GetUtcNow() => Now;

    public void Advance(TimeSpan amount) => Now += amount;
}

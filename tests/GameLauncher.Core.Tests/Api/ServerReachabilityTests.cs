using GameLauncher.Core.Api;

namespace GameLauncher.Core.Tests.Api;

/// <summary>
/// The circuit breaker in front of the API. The two properties it exposes answer different
/// questions on purpose — one is what the UI says, the other is whether a request is worth
/// sending — and most of these tests are about the moments where they disagree.
/// </summary>
public sealed class ServerReachabilityTests
{
    private static readonly DateTimeOffset Now = new(2026, 8, 18, 21, 0, 0, TimeSpan.Zero);

    private readonly FakeTimeProvider _clock = new(Now);

    private ServerReachability CreateReachability() => new(_clock);

    [Fact]
    public void ALauncherThatHasAskedNothingAssumesTheServerIsThere()
    {
        ServerReachability reachability = CreateReachability();

        Assert.True(reachability.IsOnline);
        Assert.True(reachability.AllowsRequests);
    }

    /// <summary>
    /// Unproven until something comes back: that is what puts the hung-server deadline on the
    /// first request of a run and takes it off the slow routes afterwards.
    /// </summary>
    [Fact]
    public void AServerIsUnprovenUntilItAnswersAndUnprovenAgainAfterItFails()
    {
        ServerReachability reachability = CreateReachability();
        Assert.False(reachability.IsProven);

        reachability.ReportReachable();
        Assert.True(reachability.IsProven);

        reachability.ReportUnreachable();
        Assert.False(reachability.IsProven);
    }

    [Fact]
    public void AFailureShutsTheGateAndSaysSo()
    {
        ServerReachability reachability = CreateReachability();

        reachability.ReportUnreachable();

        Assert.False(reachability.IsOnline);
        Assert.False(reachability.AllowsRequests);
    }

    /// <summary>
    /// The whole point of the window: a page of a dozen cards costs one failed attempt rather
    /// than a dozen, because the eleven that follow are refused without a connection.
    /// </summary>
    [Fact]
    public void WithinTheWindowNothingIsPutOnTheWire()
    {
        ServerReachability reachability = CreateReachability();
        reachability.ReportUnreachable();

        _clock.Advance(ServerReachability.RetryAfter - TimeSpan.FromSeconds(1));

        Assert.False(reachability.AllowsRequests);
    }

    /// <summary>
    /// Half-open, and the reason the two properties are not one: the next call is allowed to
    /// find out, while the banner keeps saying offline until something actually succeeds.
    /// </summary>
    [Fact]
    public void OnceTheWindowHasPassedOneRequestIsAllowedThroughAndTheNoticeStays()
    {
        ServerReachability reachability = CreateReachability();
        reachability.ReportUnreachable();

        _clock.Advance(ServerReachability.RetryAfter);

        Assert.True(reachability.AllowsRequests);
        Assert.False(reachability.IsOnline);
    }

    [Fact]
    public void AnAnswerFromTheServerPutsTheNoticeAway()
    {
        ServerReachability reachability = CreateReachability();
        reachability.ReportUnreachable();

        reachability.ReportReachable();

        Assert.True(reachability.IsOnline);
        Assert.True(reachability.AllowsRequests);
    }

    /// <summary>
    /// A button opens the gate and promises nothing. Announcing a server that is still missing
    /// would take the notice off the screen and put it back a second later.
    /// </summary>
    [Fact]
    public void PressingRetryOpensTheGateWithoutClaimingTheServerIsBack()
    {
        ServerReachability reachability = CreateReachability();
        reachability.ReportUnreachable();

        reachability.RetryNow();

        Assert.True(reachability.AllowsRequests);
        Assert.False(reachability.IsOnline);
    }

    [Fact]
    public void AFailureAfterARetryShutsTheGateForAWholeWindowAgain()
    {
        ServerReachability reachability = CreateReachability();
        reachability.ReportUnreachable();
        reachability.RetryNow();

        reachability.ReportUnreachable();
        _clock.Advance(ServerReachability.RetryAfter - TimeSpan.FromSeconds(1));

        Assert.False(reachability.AllowsRequests);
    }

    /// <summary>
    /// Every request reports its outcome, so the event has to be about the change and not
    /// about the report — a page that reloaded on each of a hundred successes would be a page
    /// that reloads a hundred times.
    /// </summary>
    [Fact]
    public void OnlyAChangeIsAnnounced()
    {
        ServerReachability reachability = CreateReachability();
        List<bool> announced = [];
        reachability.Changed += (_, args) => announced.Add(args.IsOnline);

        reachability.ReportReachable();
        reachability.ReportReachable();
        reachability.ReportUnreachable();
        reachability.ReportUnreachable();
        reachability.ReportReachable();

        Assert.Equal([false, true], announced);
    }
}

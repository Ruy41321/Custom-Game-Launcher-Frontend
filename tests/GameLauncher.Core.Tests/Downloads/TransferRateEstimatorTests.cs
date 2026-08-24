using GameLauncher.Core.Downloads;

namespace GameLauncher.Core.Tests.Downloads;

public sealed class TransferRateEstimatorTests
{
    private static readonly DateTimeOffset Start = new(2026, 8, 3, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void OneSampleIsNotARate()
    {
        var clock = new FakeTimeProvider(Start);
        TransferRateEstimator estimator = new(clock);

        estimator.Observe(1_000);

        Assert.Equal(0, estimator.BytesPerSecond);
        Assert.Null(estimator.Remaining(1_000, 10_000));
    }

    [Fact]
    public void TheRateIsWhatArrivedDividedByHowLongItTook()
    {
        var clock = new FakeTimeProvider(Start);
        TransferRateEstimator estimator = new(clock);

        estimator.Observe(0);
        clock.Advance(TimeSpan.FromSeconds(2));
        estimator.Observe(2_000);

        Assert.Equal(1_000, estimator.BytesPerSecond);
        Assert.Equal(TimeSpan.FromSeconds(8), estimator.Remaining(2_000, 10_000));
    }

    // An average since the start takes minutes to notice that the line has come back, which is
    // exactly when a person is looking at the number.
    [Fact]
    public void OldSamplesFallOutOfTheWindowSoTheRateFollowsTheLine()
    {
        var clock = new FakeTimeProvider(Start);
        TransferRateEstimator estimator = new(clock, TimeSpan.FromSeconds(4));

        estimator.Observe(0);
        clock.Advance(TimeSpan.FromSeconds(10));
        estimator.Observe(100);

        // Ten seconds of near-nothing, then the line comes back.
        clock.Advance(TimeSpan.FromSeconds(1));
        estimator.Observe(10_100);
        clock.Advance(TimeSpan.FromSeconds(1));
        estimator.Observe(20_100);

        Assert.Equal(10_000, estimator.BytesPerSecond);
    }

    [Fact]
    public void NothingLeftToTransferMeansNoEstimateRatherThanZero()
    {
        var clock = new FakeTimeProvider(Start);
        TransferRateEstimator estimator = new(clock);

        estimator.Observe(0);
        clock.Advance(TimeSpan.FromSeconds(1));
        estimator.Observe(10_000);

        Assert.Null(estimator.Remaining(10_000, 10_000));
    }

    [Fact]
    public void ResettingForgetsTheDownloadThatCameBefore()
    {
        var clock = new FakeTimeProvider(Start);
        TransferRateEstimator estimator = new(clock);

        estimator.Observe(0);
        clock.Advance(TimeSpan.FromSeconds(1));
        estimator.Observe(10_000);

        estimator.Reset();

        Assert.Equal(0, estimator.BytesPerSecond);
    }
}

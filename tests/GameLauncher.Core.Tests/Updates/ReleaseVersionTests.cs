using GameLauncher.Core.Updates;

namespace GameLauncher.Core.Tests.Updates;

public sealed class ReleaseVersionTests
{
    [Fact]
    public void AThreeComponentVersionParses()
    {
        Assert.True(ReleaseVersion.TryParse("1.2.3", out ReleaseVersion version));

        Assert.Equal(new ReleaseVersion(1, 2, 3), version);
        Assert.Equal("1.2.3", version.ToString());
    }

    // The full form, always: "0.2" and "0.2.0" are one version written two ways, and the
    // server's unique index cannot see them as one.
    [Theory]
    [InlineData("0.2")]
    [InlineData("1")]
    [InlineData("1.2.3.4")]
    [InlineData("v1.2.3")]
    [InlineData("1.2.3-beta")]
    [InlineData(" 1.2.3")]
    [InlineData("1.2.3 ")]
    [InlineData("-1.2.3")]
    [InlineData("+1.2.3")]
    [InlineData("0.02.0")]
    [InlineData("")]
    [InlineData(null)]
    public void AnythingElseIsRefused(string? text) =>
        Assert.False(ReleaseVersion.TryParse(text, out _));

    // The trap the comparison exists for: compared as text, 0.10.0 sorts *before* 0.9.0.
    [Fact]
    public void TenIsNewerThanNine()
    {
        Assert.True(new ReleaseVersion(0, 10, 0).IsNewerThan(new ReleaseVersion(0, 9, 0)));
        Assert.False(new ReleaseVersion(0, 9, 0).IsNewerThan(new ReleaseVersion(0, 10, 0)));
    }

    // Strictly newer. Equal is not newer, which is what makes a correctly signed *old*
    // document useless to somebody replaying it.
    [Fact]
    public void TheSameVersionIsNotNewerThanItself() =>
        Assert.False(new ReleaseVersion(1, 2, 3).IsNewerThan(new ReleaseVersion(1, 2, 3)));

    [Theory]
    [InlineData(2, 0, 0, 1, 9, 9)]
    [InlineData(1, 3, 0, 1, 2, 9)]
    [InlineData(1, 2, 4, 1, 2, 3)]
    public void EachComponentCountsInOrder(
        int major, int minor, int patch, int otherMajor, int otherMinor, int otherPatch)
    {
        ReleaseVersion candidate = new(major, minor, patch);
        ReleaseVersion installed = new(otherMajor, otherMinor, otherPatch);

        Assert.True(candidate.IsNewerThan(installed));
        Assert.False(installed.IsNewerThan(candidate));
    }
}

using GameLauncher.Core.Models;
using GameLauncher.Core.Updates;

namespace GameLauncher.Core.Tests.Updates;

public sealed class ReleaseTargetsTests
{
    [Theory]
    [InlineData("stable", "stable")]
    [InlineData("beta", "beta")]
    [InlineData("BETA", "beta")]
    // A typo must not be what stops a launcher from opening, and the server refuses a channel
    // it does not know — so an unrecognised one is read as the stream everybody is on rather
    // than spent on a request that would be answered 422 every start.
    [InlineData("nightly", "stable")]
    [InlineData("", "stable")]
    [InlineData(null, "stable")]
    public void AnUnknownChannelIsReadAsStable(string? configured, string expected) =>
        Assert.Equal(expected, ReleaseTargets.Channel(configured));

    [Theory]
    [InlineData(GamePlatform.Windows, "windows")]
    [InlineData(GamePlatform.Linux, "linux")]
    // The server says "macos"; the enum would be spelled "macOs" by the naming policy.
    [InlineData(GamePlatform.MacOs, "macos")]
    public void PlatformsAreSpeltTheWayTheServerParsesThem(GamePlatform platform, string expected) =>
        Assert.Equal(expected, ReleaseTargets.NameOf(platform));

    [Theory]
    [InlineData(BuildArchitecture.X64, "x64")]
    [InlineData(BuildArchitecture.Arm64, "arm64")]
    public void SoAreArchitectures(BuildArchitecture architecture, string expected) =>
        Assert.Equal(expected, ReleaseTargets.NameOf(architecture));
}

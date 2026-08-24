using GameLauncher.Core.Configuration;

namespace GameLauncher.Core.Tests.Configuration;

public sealed class BrandingPathsTests
{
    private static readonly string Root =
        Path.Combine(Path.GetTempPath(), "cgl-branding-root");

    [Fact]
    public void ResolvesAPathUnderTheApplicationDirectory()
    {
        string? resolved = BrandingPaths.Resolve(Root, "assets/Logo.png");

        Assert.Equal(Path.Combine(Root, "assets", "Logo.png"), resolved);
    }

    [Fact]
    public void AcceptsTheOtherPlatformsSeparator()
    {
        Assert.Equal(
            BrandingPaths.Resolve(Root, "assets/Logo.png"),
            BrandingPaths.Resolve(Root, "assets\\Logo.png"));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void NoneConfiguredIsNoAsset(string? configured)
    {
        Assert.Null(BrandingPaths.Resolve(Root, configured));
    }

    /// <summary>
    /// The likeliest way to write one of these by mistake — and the one that would otherwise
    /// resolve to the root of the disk, because <see cref="Path.Combine(string, string)"/>
    /// discards the directory it was given when the second argument is rooted.
    /// </summary>
    [Fact]
    public void AnAbsolutePathIsRefusedRatherThanFollowed()
    {
        Assert.Null(BrandingPaths.Resolve(Root, "/assets/Logo.png"));
    }

    [Fact]
    public void APathThatClimbsOutIsRefused()
    {
        Assert.Null(BrandingPaths.Resolve(Root, "../../secrets/Logo.png"));
    }

    [Fact]
    public void TheApplicationDirectoryItselfIsNotAnAsset()
    {
        Assert.Null(BrandingPaths.Resolve(Root, ".."));
        Assert.Null(BrandingPaths.Resolve(Root, "."));
    }

    /// <summary>
    /// A typo costs the fork its logo and never the launcher, so the refusals are nulls and
    /// this is the case that has no refusal at all: a name the filesystem will reject resolves
    /// perfectly well here, because <see cref="Path.GetFullPath(string)"/> stopped policing
    /// characters, and it is answered by the file simply not being there.
    /// </summary>
    [Fact]
    public void AnUnusableNameIsSomebodyElsesProblem()
    {
        Assert.Null(Record.Exception(() => BrandingPaths.Resolve(Root, "|")));
    }
}

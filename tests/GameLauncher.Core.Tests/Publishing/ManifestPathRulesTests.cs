using GameLauncher.Core.Publishing;

namespace GameLauncher.Core.Tests.Publishing;

/// <summary>
/// A copy of the server's rule, tested against the same cases, so the copy is at least a
/// faithful one. Checking here is what stops a name the server will refuse being discovered
/// after gigabytes have travelled.
/// </summary>
public sealed class ManifestPathRulesTests
{
    [Theory]
    [InlineData("Game.exe")]
    [InlineData("data/pak")]
    [InlineData("a/b/c/d/e.dat")]
    [InlineData("spaces are fine.txt")]
    [InlineData("unicodé.dat")]
    public void AnOrdinaryRelativePathIsAccepted(string path)
    {
        Assert.Null(ManifestPathRules.Reject(path));
    }

    [Theory]
    [InlineData("", "empty")]
    [InlineData("/absolute", "relative")]
    [InlineData("back\\slash", "separator")]
    [InlineData("C:relative", "absolute")]
    [InlineData("double//slash", "empty segments")]
    [InlineData("./here", "'.'")]
    [InlineData("../escape", "'.'")]
    [InlineData("a/../b", "'.'")]
    public void ANameTheServerWouldRefuseIsRefusedHereFirst(string path, string because)
    {
        string? reason = ManifestPathRules.Reject(path);

        Assert.NotNull(reason);
        Assert.Contains(because, reason, StringComparison.Ordinal);
    }

    // Built from the code point rather than written as an escape: a control character in a
    // source file is invisible in every diff it ever appears in.
    [Theory]
    [InlineData(7)]
    [InlineData(10)]
    [InlineData(27)]
    public void ControlCharactersAreRefused(int codePoint)
    {
        Assert.NotNull(ManifestPathRules.Reject("name" + (char)codePoint + ".dat"));
    }

    [Fact]
    public void APathLongerThanTheLimitIsRefused()
    {
        Assert.Null(ManifestPathRules.Reject(new string('a', ManifestPathRules.DefaultMaxPathLength)));
        Assert.NotNull(
            ManifestPathRules.Reject(new string('a', ManifestPathRules.DefaultMaxPathLength + 1)));
    }
}

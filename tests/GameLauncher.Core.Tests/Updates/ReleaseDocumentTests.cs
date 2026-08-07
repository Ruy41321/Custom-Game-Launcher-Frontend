using System.Text;
using GameLauncher.Core.Updates;

namespace GameLauncher.Core.Tests.Updates;

public sealed class ReleaseDocumentTests
{
    private static bool TryParse(string document, out ReleaseDocument? parsed) =>
        ReleaseDocument.TryParse(Encoding.UTF8.GetBytes(document), out parsed, out _);

    [Fact]
    public void TheCanonicalDocumentIsRead()
    {
        Assert.True(TryParse(ReleaseSigningFixture.CanonicalDocument, out ReleaseDocument? release));

        Assert.Equal("stable", release!.Channel);
        Assert.Equal(new ReleaseVersion(0, 2, 0), release.Version);
        Assert.Equal("windows", release.Platform);
        Assert.Equal("x64", release.Arch);
        Assert.Equal(
            "9f86d081884c7d659a2feaa0c55ad015a3bf4f1b2b0b822cd15d6c15b0f00a08", release.Sha256);
        Assert.Equal(83442176, release.Size);
        Assert.Equal("2026-08-07T10:00:00Z", release.ReleasedAt);
        Assert.Equal("Self-update, at last.", release.Notes);
    }

    [Fact]
    public void ADocumentThatIsNotJsonIsRefusedWithAReason()
    {
        Assert.False(ReleaseDocument.TryParse(
            Encoding.UTF8.GetBytes("<html>proxy says no</html>"), out _, out string problem));

        Assert.NotEmpty(problem);
    }

    // A schema this launcher predates is not something to guess at: the fields it does not
    // know about are the ones that would have changed what the known ones mean.
    [Theory]
    [InlineData("\"schema\":1", "\"schema\":2")]
    [InlineData("\"channel\":\"stable\"", "\"channel\":\"nightly\"")]
    [InlineData("\"platform\":\"windows\"", "\"platform\":\"freebsd\"")]
    [InlineData("\"arch\":\"x64\"", "\"arch\":\"ppc\"")]
    [InlineData("\"version\":\"0.2.0\"", "\"version\":\"0.2\"")]
    [InlineData("\"size\":83442176", "\"size\":0")]
    [InlineData("\"size\":83442176", "\"size\":5000000000")]
    [InlineData("\"releasedAt\":\"2026-08-07T10:00:00Z\"", "\"releasedAt\":\"2026-08-07 10:00:00\"")]
    [InlineData("\"releasedAt\":\"2026-08-07T10:00:00Z\"", "\"releasedAt\":\"2026-08-07T10:00:00+00:00\"")]
    [InlineData("\"notes\":\"Self-update, at last.\"", "\"notes\":7")]
    public void AFieldThatCannotBeMeantIsRefused(string original, string replacement)
    {
        string document = ReleaseSigningFixture.CanonicalDocument.Replace(
            original, replacement, StringComparison.Ordinal);

        Assert.NotEqual(ReleaseSigningFixture.CanonicalDocument, document);
        Assert.False(TryParse(document, out _));
    }

    // Uppercase would be a second content address for one file.
    [Theory]
    [InlineData("9F86D081884C7D659A2FEAA0C55AD015A3BF4F1B2B0B822CD15D6C15B0F00A08")]
    [InlineData("9f86d081")]
    [InlineData("zzzzd081884c7d659a2feaa0c55ad015a3bf4f1b2b0b822cd15d6c15b0f00a08")]
    public void OnlySixtyFourLowercaseHexDigitsAreAContentAddress(string sha256)
    {
        string document = ReleaseSigningFixture.CanonicalDocument.Replace(
            "9f86d081884c7d659a2feaa0c55ad015a3bf4f1b2b0b822cd15d6c15b0f00a08",
            sha256,
            StringComparison.Ordinal);

        Assert.False(TryParse(document, out _));
    }

    [Fact]
    public void AMissingFieldIsRefusedRatherThanDefaulted()
    {
        string document = ReleaseSigningFixture.CanonicalDocument.Replace(
            ",\"sha256\":\"9f86d081884c7d659a2feaa0c55ad015a3bf4f1b2b0b822cd15d6c15b0f00a08\"",
            string.Empty,
            StringComparison.Ordinal);

        Assert.False(TryParse(document, out _));
    }

    // Notes are optional: a release does not have to say anything.
    [Fact]
    public void ADocumentWithNoNotesIsStillARelease()
    {
        string document = ReleaseSigningFixture.CanonicalDocument.Replace(
            ",\"notes\":\"Self-update, at last.\"", string.Empty, StringComparison.Ordinal);

        Assert.True(TryParse(document, out ReleaseDocument? release));
        Assert.Empty(release!.Notes);
    }

    // The signature vouches for what the document *says*, which is what stops a server holding
    // real signed releases from handing a Windows launcher the Linux one.
    [Fact]
    public void ADocumentKnowsWhichLauncherItIsFor()
    {
        Assert.True(TryParse(ReleaseSigningFixture.CanonicalDocument, out ReleaseDocument? release));

        Assert.True(release!.Describes("stable", "windows", "x64"));
        Assert.False(release.Describes("beta", "windows", "x64"));
        Assert.False(release.Describes("stable", "linux", "x64"));
        Assert.False(release.Describes("stable", "windows", "arm64"));
    }
}

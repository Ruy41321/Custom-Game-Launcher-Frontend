using GameLauncher.Core.Text;

namespace GameLauncher.Core.Tests.Text;

/// <summary>
/// The half of devlog rendering that can be tested: what the text means. What it looks like
/// needs Avalonia and has no test, exactly as decoding a bitmap does not (D37).
/// </summary>
public sealed class MarkdownParserTests
{
    private static string Flatten(MarkdownBlock block) =>
        string.Concat(block.Spans.Select(span => span.Text));

    [Fact]
    public void NothingAtAllIsNoBlocks()
    {
        Assert.Empty(MarkdownParser.Parse(string.Empty));
        Assert.Empty(MarkdownParser.Parse("   \n\n  "));
    }

    // The complaint that started this: the launcher showed the hashes.
    [Fact]
    public void HeadingsAreHeadingsRatherThanLinesBeginningWithAHash()
    {
        IReadOnlyList<MarkdownBlock> blocks = MarkdownParser.Parse(
            "# Ciao a tutti ragazzi\n## benvenuti\nciao");

        Assert.Equal(3, blocks.Count);

        Assert.Equal(MarkdownBlockKind.Heading, blocks[0].Kind);
        Assert.Equal(1, blocks[0].Level);
        Assert.Equal("Ciao a tutti ragazzi", Flatten(blocks[0]));

        Assert.Equal(MarkdownBlockKind.Heading, blocks[1].Kind);
        Assert.Equal(2, blocks[1].Level);
        Assert.Equal("benvenuti", Flatten(blocks[1]));

        Assert.Equal(MarkdownBlockKind.Paragraph, blocks[2].Kind);
        Assert.Equal("ciao", Flatten(blocks[2]));
    }

    [Theory]
    [InlineData("#nospace")]
    [InlineData("####### seven")]
    [InlineData("a # in the middle")]
    public void SomethingThatIsNotAHeadingStaysAParagraph(string line)
    {
        MarkdownBlock block = Assert.Single(MarkdownParser.Parse(line));

        Assert.Equal(MarkdownBlockKind.Paragraph, block.Kind);
        Assert.Equal(line, Flatten(block));
    }

    // Consecutive lines are one paragraph, which is what a blank line between them means.
    [Fact]
    public void ABlankLineSeparatesParagraphsAndASingleNewlineDoesNot()
    {
        IReadOnlyList<MarkdownBlock> blocks = MarkdownParser.Parse(
            "one\ntwo\n\nthree");

        Assert.Equal(2, blocks.Count);
        Assert.Equal("one two", Flatten(blocks[0]));
        Assert.Equal("three", Flatten(blocks[1]));
    }

    [Fact]
    public void WindowsLineEndingsAreReadTheSameWayAsUnixOnes()
    {
        IReadOnlyList<MarkdownBlock> blocks = MarkdownParser.Parse("one\r\n\r\ntwo");

        Assert.Equal(2, blocks.Count);
        Assert.Equal("one", Flatten(blocks[0]));
    }

    [Theory]
    [InlineData("- first")]
    [InlineData("* first")]
    [InlineData("  + first")]
    public void EachBulletIsItsOwnBlock(string line)
    {
        MarkdownBlock block = Assert.Single(MarkdownParser.Parse(line));

        Assert.Equal(MarkdownBlockKind.BulletItem, block.Kind);
        Assert.Equal("first", Flatten(block));
    }

    [Fact]
    public void EmphasisBecomesStyleRatherThanAsterisks()
    {
        MarkdownBlock block = Assert.Single(
            MarkdownParser.Parse("a **bold** and an *italic* word"));

        Assert.Equal("a bold and an italic word", Flatten(block));
        Assert.Contains(
            block.Spans, span => span.Text == "bold" && span.Style == MarkdownStyle.Bold);
        Assert.Contains(
            block.Spans, span => span.Text == "italic" && span.Style == MarkdownStyle.Italic);
    }

    [Fact]
    public void EmphasisNests()
    {
        MarkdownBlock block = Assert.Single(
            MarkdownParser.Parse("**bold with *italic* inside**"));

        Assert.Equal("bold with italic inside", Flatten(block));
        Assert.Contains(
            block.Spans,
            span => span.Text == "italic"
                && span.Style == (MarkdownStyle.Bold | MarkdownStyle.Italic));
    }

    [Fact]
    public void ThreeMarkersAreBothAtOnce()
    {
        MarkdownBlock block = Assert.Single(MarkdownParser.Parse("***shouting***"));

        Assert.Contains(
            block.Spans,
            span => span.Text == "shouting"
                && span.Style == (MarkdownStyle.Bold | MarkdownStyle.Italic));
    }

    // Half a pair of asterisks is how somebody writes about asterisks.
    [Fact]
    public void AMarkerNobodyClosedIsJustText()
    {
        MarkdownBlock block = Assert.Single(MarkdownParser.Parse("2 * 3 and a **start"));

        Assert.Equal("2 * 3 and a **start", Flatten(block));
        Assert.All(block.Spans, span => Assert.Equal(MarkdownStyle.None, span.Style));
    }

    // The rule that stops a code sample being reformatted by its own contents.
    [Fact]
    public void WhatIsInsideBackticksIsNotMarkup()
    {
        MarkdownBlock block = Assert.Single(MarkdownParser.Parse("run `--flag **x**` now"));

        Assert.Contains(
            block.Spans,
            span => span.Text == "--flag **x**" && span.Style == MarkdownStyle.Code);
    }

    [Fact]
    public void AFencedBlockIsKeptExactlyAsItWasTyped()
    {
        IReadOnlyList<MarkdownBlock> blocks = MarkdownParser.Parse(
            "before\n\n```\n  indented **not bold**\n  second\n```\n\nafter");

        Assert.Equal(3, blocks.Count);
        Assert.Equal(MarkdownBlockKind.Code, blocks[1].Kind);
        Assert.Equal("  indented **not bold**\n  second", Flatten(blocks[1]));
        Assert.Equal("after", Flatten(blocks[2]));
    }

    // A publisher who forgot the closing fence still meant the text they typed.
    [Fact]
    public void AnUnclosedFenceStillYieldsItsContents()
    {
        IReadOnlyList<MarkdownBlock> blocks = MarkdownParser.Parse("```\nnever closed");

        MarkdownBlock block = Assert.Single(blocks);
        Assert.Equal(MarkdownBlockKind.Code, block.Kind);
        Assert.Equal("never closed", Flatten(block));
    }

    // Navigating to an address a publisher wrote is a capability, and this is a text renderer:
    // the URL is shown rather than hidden behind a word that could be pressed.
    [Fact]
    public void ALinkShowsItsAddressAndIsNotSomethingToPress()
    {
        MarkdownBlock block = Assert.Single(
            MarkdownParser.Parse("see [the notes](https://example.test/notes) for more"));

        Assert.Equal(
            "see the notes [https://example.test/notes] for more", Flatten(block));
    }

    [Fact]
    public void SomethingThatOnlyLooksLikeALinkIsLeftAlone()
    {
        MarkdownBlock block = Assert.Single(MarkdownParser.Parse("an [aside] in brackets"));

        Assert.Equal("an [aside] in brackets", Flatten(block));
    }

    // No HTML, no remote images: the two things D38 refused to render, and the reason this
    // parser is allowed to exist. Both come out as the characters they are.
    [Theory]
    [InlineData("<script>alert(1)</script>")]
    [InlineData("<b>not bold</b>")]
    [InlineData("![a picture](https://example.test/tracker.png)")]
    public void MarkupThatWouldReachOutsideTheTextStaysText(string line)
    {
        MarkdownBlock block = Assert.Single(MarkdownParser.Parse(line));

        Assert.Equal(MarkdownBlockKind.Paragraph, block.Kind);
        Assert.Equal(line, Flatten(block));
    }
}

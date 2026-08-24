namespace GameLauncher.Core.Text;

/// <summary>What a line of a devlog turned out to be.</summary>
public enum MarkdownBlockKind
{
    Paragraph,

    /// <summary>A <c>#</c> heading; <see cref="MarkdownBlock.Level"/> says how many.</summary>
    Heading,

    /// <summary>One item of a <c>-</c> list. The list itself is not a block: items stand alone.</summary>
    BulletItem,

    /// <summary>A fenced block, kept exactly as it was typed and never parsed for inline marks.</summary>
    Code,
}

[Flags]
public enum MarkdownStyle
{
    None = 0,
    Bold = 1,
    Italic = 2,
    Code = 4,
}

/// <summary>A run of text with one set of marks on it.</summary>
public sealed record MarkdownSpan(string Text, MarkdownStyle Style = MarkdownStyle.None);

public sealed record MarkdownBlock(
    MarkdownBlockKind Kind, IReadOnlyList<MarkdownSpan> Spans, int Level = 0);

/// <summary>
/// Enough Markdown for a devlog, and deliberately not more.
///
/// D38 refused to render Markdown at all, on the grounds that rendering remote Markdown is
/// rendering remote markup. That reasoning is about a *general* renderer: the danger in Markdown
/// is the parts that reach outside the text — embedded HTML, remote images, and links that
/// navigate — and every one of them is a thing this parser does not produce. What is left is
/// emphasis, headings, lists and code, which is what a publisher was writing anyway and which
/// arrived on screen as literal asterisks.
///
/// So: no HTML (a line of it is text), no images (the syntax stays as typed), and a link becomes
/// its own label followed by its URL in brackets rather than something that can be clicked —
/// a publisher's URL opening a browser is a capability, and this is a text renderer.
///
/// Pure, in Core, with no Avalonia anywhere near it: the App layer turns these blocks into
/// controls, exactly as it turns bytes into a <c>Bitmap</c> (D37).
/// </summary>
public static class MarkdownParser
{
    private const string Fence = "```";

    /// <summary>
    /// Longest first within a family, so <c>***</c> is recognised before the <c>**</c> that
    /// starts it and <c>**</c> before <c>*</c>. Ties are broken by this order because
    /// <see cref="NextMark"/> keeps the first marker found at the earliest position.
    ///
    /// The one shape this does not reach is emphasis whose runs *end* together —
    /// <c>**bold *and italic***</c> — because the closing <c>***</c> is claimed by the outer
    /// pair. CommonMark solves that with delimiter runs, which is a parser several times this
    /// size for a case a devlog can write the other way round.
    /// </summary>
    private static readonly (string Marker, MarkdownStyle Style)[] Markers =
    [
        ("`", MarkdownStyle.Code),
        ("***", MarkdownStyle.Bold | MarkdownStyle.Italic),
        ("___", MarkdownStyle.Bold | MarkdownStyle.Italic),
        ("**", MarkdownStyle.Bold),
        ("__", MarkdownStyle.Bold),
        ("*", MarkdownStyle.Italic),
        ("_", MarkdownStyle.Italic),
    ];

    public static IReadOnlyList<MarkdownBlock> Parse(string markdown)
    {
        if (string.IsNullOrWhiteSpace(markdown))
        {
            return [];
        }

        List<MarkdownBlock> blocks = [];
        List<string> paragraph = [];
        List<string> code = [];
        bool fenced = false;

        foreach (string raw in markdown.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n'))
        {
            string line = raw.TrimEnd();

            if (line.TrimStart().StartsWith(Fence, StringComparison.Ordinal))
            {
                // Closing a fence emits what it held; opening one first flushes whatever
                // paragraph was being built, because a fence ends a paragraph.
                if (fenced)
                {
                    blocks.Add(new MarkdownBlock(
                        MarkdownBlockKind.Code, [new MarkdownSpan(string.Join('\n', code))]));
                    code.Clear();
                }
                else
                {
                    Flush(blocks, paragraph);
                }

                fenced = !fenced;
                continue;
            }

            if (fenced)
            {
                code.Add(raw);
                continue;
            }

            if (line.Trim().Length == 0)
            {
                Flush(blocks, paragraph);
                continue;
            }

            if (HeadingLevel(line) is { } level)
            {
                Flush(blocks, paragraph);
                blocks.Add(new MarkdownBlock(
                    MarkdownBlockKind.Heading,
                    Inline(line[level..].TrimStart(' ', '#')),
                    level));
                continue;
            }

            if (BulletContent(line) is { } item)
            {
                Flush(blocks, paragraph);
                blocks.Add(new MarkdownBlock(MarkdownBlockKind.BulletItem, Inline(item)));
                continue;
            }

            paragraph.Add(line.Trim());
        }

        // An unclosed fence is a publisher who forgot the second one, and the text they typed
        // is worth more than the refusal: it comes out as the code block they meant.
        if (code.Count > 0)
        {
            blocks.Add(new MarkdownBlock(
                MarkdownBlockKind.Code, [new MarkdownSpan(string.Join('\n', code))]));
        }

        Flush(blocks, paragraph);
        return blocks;
    }

    private static void Flush(List<MarkdownBlock> blocks, List<string> paragraph)
    {
        if (paragraph.Count == 0)
        {
            return;
        }

        blocks.Add(new MarkdownBlock(
            MarkdownBlockKind.Paragraph, Inline(string.Join(' ', paragraph))));

        paragraph.Clear();
    }

    private static int? HeadingLevel(string line)
    {
        int hashes = 0;
        while (hashes < line.Length && line[hashes] == '#')
        {
            hashes++;
        }

        // "#nope" is not a heading in any dialect worth following, and "######## x" is not one
        // either: six is where the syntax stops.
        return hashes is > 0 and <= 6 && hashes < line.Length && line[hashes] == ' '
            ? hashes
            : null;
    }

    private static string? BulletContent(string line)
    {
        string trimmed = line.TrimStart();
        return trimmed.Length > 2 && trimmed[0] is '-' or '*' or '+' && trimmed[1] == ' '
            ? trimmed[2..]
            : null;
    }

    /// <summary>
    /// Splits one line into styled runs. Code is taken first and its contents are never looked
    /// at again, which is the rule that stops <c>`**not bold**`</c> coming out bold.
    /// </summary>
    internal static IReadOnlyList<MarkdownSpan> Inline(string text)
    {
        List<MarkdownSpan> spans = [];
        Emit(spans, Links(text), MarkdownStyle.None);

        return spans.Count == 0 ? [new MarkdownSpan(string.Empty)] : spans;
    }

    /// <summary>
    /// <c>[label](url)</c> becomes <c>label [url]</c>. The URL is shown rather than hidden
    /// behind a word, and it is not something that can be pressed: navigating to an address a
    /// publisher wrote is a capability, and this is a text renderer.
    /// </summary>
    private static string Links(string text)
    {
        int at = 0;
        while (at < text.Length)
        {
            int open = text.IndexOf('[', at);
            if (open < 0)
            {
                break;
            }

            int close = text.IndexOf(']', open);

            // `![alt](url)` is an image, and this renderer fetches nothing: the whole thing
            // stays as the characters somebody typed rather than becoming a caption and a URL
            // for a picture that is not going to appear.
            if (close < 0
                || close + 1 >= text.Length
                || text[close + 1] != '('
                || (open > 0 && text[open - 1] == '!'))
            {
                at = open + 1;
                continue;
            }

            int end = text.IndexOf(')', close);
            if (end < 0)
            {
                at = open + 1;
                continue;
            }

            string label = text[(open + 1)..close];
            string url = text[(close + 2)..end];
            string replacement = url.Length == 0 ? label : $"{label} [{url}]";

            text = text[..open] + replacement + text[(end + 1)..];
            at = open + replacement.Length;
        }

        return text;
    }

    private static void Emit(List<MarkdownSpan> spans, string text, MarkdownStyle style)
    {
        int at = 0;
        while (at < text.Length)
        {
            (int start, int length, string marker, MarkdownStyle added) = NextMark(text, at);

            if (start < 0)
            {
                Add(spans, text[at..], style);
                return;
            }

            Add(spans, text[at..start], style);

            string inner = text.Substring(start + marker.Length, length);
            if (added == MarkdownStyle.Code)
            {
                // Verbatim, on purpose: what is inside backticks is not markup.
                Add(spans, inner, style | MarkdownStyle.Code);
            }
            else
            {
                Emit(spans, inner, style | added);
            }

            at = start + (marker.Length * 2) + length;
        }
    }

    /// <summary>
    /// The first opening marker with a closing one after it, or a negative start when there is
    /// none. A marker nobody closed is text: half a pair of asterisks is how somebody writes
    /// about asterisks.
    /// </summary>
    private static (int Start, int Length, string Marker, MarkdownStyle Style) NextMark(
        string text, int from)
    {
        (int Start, int Length, string Marker, MarkdownStyle Style) best = (-1, 0, "", default);

        foreach ((string marker, MarkdownStyle style) in Markers)
        {
            int open = text.IndexOf(marker, from, StringComparison.Ordinal);

            // Code is the exception to the flanking rule below: `a * b` is a code sample and
            // the spaces inside it are part of what somebody is quoting.
            bool code = style == MarkdownStyle.Code;

            while (open >= 0 && !code && !Opens(text, open + marker.Length))
            {
                open = text.IndexOf(marker, open + 1, StringComparison.Ordinal);
            }

            if (open < 0 || (best.Start >= 0 && open >= best.Start))
            {
                continue;
            }

            int close = text.IndexOf(marker, open + marker.Length, StringComparison.Ordinal);
            while (close >= 0 && !code && !Closes(text, close))
            {
                close = text.IndexOf(marker, close + 1, StringComparison.Ordinal);
            }

            if (close < 0 || close == open + marker.Length)
            {
                continue;
            }

            best = (open, close - open - marker.Length, marker, style);
        }

        return best;
    }

    /// <summary>
    /// An opening marker is followed by something to emphasise, never by a space. It is the
    /// rule that keeps <c>2 * 3 and a * b</c> arithmetic instead of italics.
    /// </summary>
    private static bool Opens(string text, int after) =>
        after < text.Length && !char.IsWhiteSpace(text[after]);

    /// <summary>And a closing one is preceded by the thing it emphasised.</summary>
    private static bool Closes(string text, int at) =>
        at > 0 && !char.IsWhiteSpace(text[at - 1]);

    private static void Add(List<MarkdownSpan> spans, string text, MarkdownStyle style)
    {
        if (text.Length > 0)
        {
            spans.Add(new MarkdownSpan(text, style));
        }
    }
}

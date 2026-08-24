namespace GameLauncher.Core.Media;

/// <summary>
/// What a video starts with — the same job <see cref="ImageFormats"/> does for pictures, and
/// held to the same rule (D41): this refuses early, it never vouches. The server decides what a
/// video is from the same bytes and its answer is the one that counts, because that answer
/// becomes the <c>Content-Type</c> of a public URL.
///
/// The launcher recognises what the server stores: MP4 and WebM. The reasoning behind those two
/// belongs to the server (its D64) and is not repeated here; what is repeated, deliberately, is
/// the <em>check</em>, because a publisher who picks a 400 MB <c>.mkv</c> should be told before
/// it travels rather than after.
///
/// Two details that are not arbitrary and would be easy to lose:
///
/// <list type="bullet">
/// <item>The ISO base media <b>brand</b> is read, not just the <c>ftyp</c> box, because HEIC and
/// AVIF are ISO base media files too — a check that stopped at the box would offer a photograph
/// as a trailer.</item>
/// <item>The WebM <b>DocType</b> is looked for only in the first 64 bytes, where the EBML header
/// is. Matroska opens with the same four bytes and says <c>matroska</c> there.</item>
/// </list>
/// </summary>
public static class VideoFormats
{
    /// <summary>How far in the EBML DocType may be. The header is a few dozen bytes long.</summary>
    private const int EbmlHeaderWindow = 64;

    public static bool LooksLikeAVideo(ReadOnlySpan<byte> bytes) =>
        IsMp4(bytes) || IsWebm(bytes);

    /// <summary>
    /// File extensions worth offering in a picker. A convenience for the person choosing a file
    /// and nothing more — what actually gets refused is decided by the bytes above, on both
    /// sides.
    /// </summary>
    public static IReadOnlyList<string> PickerExtensions { get; } = ["mp4", "webm"];

    /// <summary>
    /// The major brands that mean "this is an MP4". QuickTime (<c>qt  </c>) is deliberately
    /// absent: a .mov is not what <c>video/mp4</c> promises, even where a player opens it.
    /// </summary>
    private static readonly string[] Mp4Brands =
        ["isom", "iso2", "iso4", "iso5", "iso6", "mp41", "mp42", "avc1", "dash", "M4V "];

    private static ReadOnlySpan<byte> EbmlSignature => [0x1A, 0x45, 0xDF, 0xA3];

    private static bool IsMp4(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length < 12 || !bytes[4..8].SequenceEqual("ftyp"u8))
        {
            return false;
        }

        Span<char> brand = stackalloc char[4];
        for (int index = 0; index < 4; index++)
        {
            brand[index] = (char)bytes[8 + index];
        }

        foreach (string accepted in Mp4Brands)
        {
            if (brand.SequenceEqual(accepted))
            {
                return true;
            }
        }

        return false;
    }

    private static bool IsWebm(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length < 4 || !bytes[..4].SequenceEqual(EbmlSignature))
        {
            return false;
        }

        ReadOnlySpan<byte> window = bytes[..Math.Min(EbmlHeaderWindow, bytes.Length)];
        return window.IndexOf("webm"u8) >= 0;
    }
}

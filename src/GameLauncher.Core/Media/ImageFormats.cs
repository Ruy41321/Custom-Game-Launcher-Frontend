namespace GameLauncher.Core.Media;

/// <summary>
/// What a picture starts with. Both sides of the launcher need this — the loader before it
/// hands bytes to a decoder, and the uploader before it spends somebody's bandwidth on
/// something the server will refuse — and one rule with two implementations is one rule that
/// will eventually disagree with itself.
///
/// The launcher recognises what the server stores: PNG, JPEG and WebP. **SVG is not one of
/// them, deliberately**: it is a document format that can carry script rather than a picture,
/// and the server refuses it for that reason. Recognising it here would only mean uploading
/// something to be told no.
///
/// A positive answer is not a guarantee, and is not treated as one. The server decides what an
/// image is from the same bytes, and its answer is the one that counts (D28 of the backend);
/// this exists to refuse early, never to vouch.
/// </summary>
public static class ImageFormats
{
    public static bool LooksLikeAnImage(ReadOnlySpan<byte> bytes) =>
        IsPng(bytes) || IsJpeg(bytes) || IsWebp(bytes);

    /// <summary>
    /// File extensions worth offering in a picker. A convenience for the person choosing a
    /// file and nothing more — what actually gets refused is decided by the bytes above, on
    /// both sides.
    /// </summary>
    public static IReadOnlyList<string> PickerExtensions { get; } =
        ["png", "jpg", "jpeg", "webp"];

    private static ReadOnlySpan<byte> PngSignature =>
        [0x89, (byte)'P', (byte)'N', (byte)'G', 0x0D, 0x0A, 0x1A, 0x0A];

    private static bool IsPng(ReadOnlySpan<byte> bytes) =>
        bytes.Length >= 8 && bytes[..8].SequenceEqual(PngSignature);

    private static bool IsJpeg(ReadOnlySpan<byte> bytes) =>
        bytes.Length >= 3 && bytes[0] == 0xFF && bytes[1] == 0xD8 && bytes[2] == 0xFF;

    private static bool IsWebp(ReadOnlySpan<byte> bytes) =>
        bytes.Length >= 12
        && bytes[..4].SequenceEqual("RIFF"u8)
        && bytes[8..12].SequenceEqual("WEBP"u8);
}

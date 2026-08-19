using System.Text;
using GameLauncher.Core.Media;

namespace GameLauncher.Core.Tests.Media;

/// <summary>
/// The client's half of the server's D64. Held to D41's rule: this refuses early, it never
/// vouches — so every assertion here is about what is turned away, plus the two shapes that get
/// through.
/// </summary>
public sealed class VideoFormatsTests
{
    /// <summary>
    /// An ISO base media header: a box length, "ftyp", and the major brand. Nothing parses the
    /// length, so it is deliberately nonsense.
    /// </summary>
    private static byte[] IsoBaseMedia(string brand, int totalBytes = 64)
    {
        byte[] bytes = new byte[totalBytes];
        Encoding.ASCII.GetBytes("....ftyp" + brand).CopyTo(bytes, 0);
        return bytes;
    }

    /// <summary>The magic, some header bytes, and the DocType string where a real file has it.</summary>
    private static byte[] Ebml(string docType, int docTypeOffset = 24, int totalBytes = 96)
    {
        byte[] bytes = new byte[totalBytes];
        ReadOnlySpan<byte> signature = [0x1A, 0x45, 0xDF, 0xA3];
        signature.CopyTo(bytes);
        Encoding.ASCII.GetBytes(docType).CopyTo(bytes, docTypeOffset);
        return bytes;
    }

    [Theory]
    [InlineData("isom")]
    [InlineData("iso2")]
    [InlineData("mp42")]
    [InlineData("avc1")]
    [InlineData("M4V ")]
    public void RecognisesTheOrdinaryMp4Brands(string brand) =>
        Assert.True(VideoFormats.LooksLikeAVideo(IsoBaseMedia(brand)));

    /// <summary>
    /// Why the brand is read at all rather than the box alone: HEIC and AVIF are ISO base media
    /// files, and offering a photograph as a trailer is exactly the mistake that would make.
    /// QuickTime is refused because the server refuses it.
    /// </summary>
    [Theory]
    [InlineData("heic")]
    [InlineData("avif")]
    [InlineData("mif1")]
    [InlineData("qt  ")]
    [InlineData("3gp4")]
    public void RefusesTheIsoBaseMediaFilesThatAreNotMp4(string brand) =>
        Assert.False(VideoFormats.LooksLikeAVideo(IsoBaseMedia(brand)));

    [Fact]
    public void RecognisesWebMByItsDocType() =>
        Assert.True(VideoFormats.LooksLikeAVideo(Ebml("webm")));

    /// <summary>Matroska opens with the same four bytes and is a different container.</summary>
    [Fact]
    public void RefusesMatroskaWhichSharesTheSignature() =>
        Assert.False(VideoFormats.LooksLikeAVideo(Ebml("matroska")));

    /// <summary>
    /// The DocType lives at the front. A file that says "webm" a kilobyte in is telling you
    /// about bytes whose position somebody else chose.
    /// </summary>
    [Fact]
    public void DoesNotLookForTheDocTypePastTheHeader() =>
        Assert.False(VideoFormats.LooksLikeAVideo(Ebml("webm", docTypeOffset: 200, totalBytes: 256)));

    [Fact]
    public void RefusesPicturesAndEmptyAndTruncatedBodies()
    {
        Assert.False(VideoFormats.LooksLikeAVideo([]));
        Assert.False(VideoFormats.LooksLikeAVideo([0x1A, 0x45, 0xDF, 0xA3]));
        Assert.False(VideoFormats.LooksLikeAVideo(Encoding.ASCII.GetBytes("....ftyp")));
        Assert.False(VideoFormats.LooksLikeAVideo(
            [0x89, (byte)'P', (byte)'N', (byte)'G', 0x0D, 0x0A, 0x1A, 0x0A, 0, 0, 0, 0]));
    }

    /// <summary>
    /// The picker is a convenience, and it must not offer what the bytes would then refuse —
    /// a publisher shown "mkv" in a dialog has been invited to waste an upload.
    /// </summary>
    [Fact]
    public void ThePickerOffersOnlyWhatTheBytesWouldAccept() =>
        Assert.Equal(["mp4", "webm"], VideoFormats.PickerExtensions);
}

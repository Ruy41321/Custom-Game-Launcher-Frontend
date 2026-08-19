using GameLauncher.Core.Media;
using GameLauncher.Core.Models;

namespace GameLauncher.Core.Tests.Media;

public sealed class MediaUploadRulesTests
{
    /// <summary>
    /// Deliberately not the defaults. Every assertion below has to come out of *these* numbers,
    /// so a rule that quietly fell back to a constant would fail rather than pass by accident.
    /// </summary>
    private static readonly MediaCapabilities Limits = new()
    {
        MaxBytes = 64,
        MaxScreenshotsPerGame = 3,
        MaxAltTextLength = 10,
    };

    private static byte[] Png(int totalBytes = 16)
    {
        byte[] bytes = new byte[totalBytes];
        ReadOnlySpan<byte> signature = [0x89, (byte)'P', (byte)'N', (byte)'G', 0x0D, 0x0A, 0x1A, 0x0A];
        signature.CopyTo(bytes);
        return bytes;
    }

    [Fact]
    public void APictureWithinEveryLimitIsAccepted() =>
        Assert.Null(MediaUploadRules.Reject(
            Png(), MediaKind.Screenshot, "key art", existing: 0, Limits));

    [Fact]
    public void AnEmptyFileIsRefusedBeforeAnythingElse()
    {
        MediaRejection? rejection = MediaUploadRules.Reject(
            [], MediaKind.Cover, string.Empty, 0, Limits);

        Assert.Equal(MediaFailure.Empty, rejection?.Reason);
    }

    // The point of reading the limit from the server: this number is the deployment's, and a
    // client carrying its own copy would accept something the deployment refuses.
    [Fact]
    public void TheSizeLimitIsTheOneTheServerAnnouncedAndTheRejectionQuotesIt()
    {
        MediaRejection? rejection = MediaUploadRules.Reject(
            Png(totalBytes: 65), MediaKind.Cover, string.Empty, 0, Limits);

        Assert.Equal(MediaFailure.TooLarge, rejection?.Reason);
        Assert.Equal(64, rejection?.Limit);
    }

    [Fact]
    public void AFileExactlyAtTheLimitIsAccepted() =>
        Assert.Null(MediaUploadRules.Reject(
            Png(totalBytes: 64), MediaKind.Cover, string.Empty, 0, Limits));

    [Theory]
    [InlineData("<svg xmlns=\"http://www.w3.org/2000/svg\"><script/></svg>")]
    [InlineData("GIF89a")]
    [InlineData("not a picture at all")]
    public void WhatIsNotOneOfTheServersFormatsIsRefused(string content)
    {
        MediaRejection? rejection = MediaUploadRules.Reject(
            System.Text.Encoding.UTF8.GetBytes(content), MediaKind.Cover, string.Empty, 0, Limits);

        Assert.Equal(MediaFailure.UnsupportedFormat, rejection?.Reason);
    }

    [Fact]
    public void TheGalleryCapIsTheServersAndTheRejectionQuotesIt()
    {
        MediaRejection? rejection = MediaUploadRules.Reject(
            Png(), MediaKind.Screenshot, string.Empty, existing: 3, Limits);

        Assert.Equal(MediaFailure.GalleryFull, rejection?.Reason);
        Assert.Equal(3, rejection?.Limit);
    }

    // A game has one cover, one banner and one logo, and uploading another is how you replace
    // it. Counting the gallery against those kinds would refuse a legitimate replacement.
    [Theory]
    [InlineData(MediaKind.Cover)]
    [InlineData(MediaKind.Banner)]
    [InlineData(MediaKind.Logo)]
    public void TheGalleryCapDoesNotApplyToTheSingletonKinds(MediaKind kind) =>
        Assert.Null(MediaUploadRules.Reject(
            Png(), kind, string.Empty, existing: 99, Limits));

    [Fact]
    public void AltTextLongerThanTheServerAcceptsIsRefused()
    {
        MediaRejection? rejection = MediaUploadRules.Reject(
            Png(), MediaKind.Screenshot, new string('a', 11), 0, Limits);

        Assert.Equal(MediaFailure.AltTextTooLong, rejection?.Reason);
        Assert.Equal(10, rejection?.Limit);
    }

    // PATCH /media/{id} edits alt text without a file, so the rule has to be reachable on its
    // own — otherwise the limit gets copied into a second place and the two drift.
    [Fact]
    public void AltTextCanBeCheckedWithoutAPicture()
    {
        Assert.Null(MediaUploadRules.RejectAltText("short", Limits));
        Assert.Equal(
            MediaFailure.AltTextTooLong,
            MediaUploadRules.RejectAltText(new string('a', 11), Limits)?.Reason);
    }
}

public sealed class ImageFormatsTests
{
    [Fact]
    public void PngJpegAndWebpAreRecognised()
    {
        Assert.True(ImageFormats.LooksLikeAnImage(
            [0x89, (byte)'P', (byte)'N', (byte)'G', 0x0D, 0x0A, 0x1A, 0x0A]));
        Assert.True(ImageFormats.LooksLikeAnImage([0xFF, 0xD8, 0xFF, 0xE0]));
        Assert.True(ImageFormats.LooksLikeAnImage(
            [.. "RIFF"u8, 0x24, 0x00, 0x00, 0x00, .. "WEBP"u8]));
    }

    // Not an oversight: the server refuses it because it is a document that can carry script,
    // and recognising it here would only mean uploading something to be told no.
    [Fact]
    public void SvgIsNotAnImageAsFarAsTheLauncherIsConcerned() =>
        Assert.False(ImageFormats.LooksLikeAnImage(
            System.Text.Encoding.UTF8.GetBytes("<svg xmlns=\"http://www.w3.org/2000/svg\"/>")));

    [Fact]
    public void BytesTooShortToCarryASignatureAreNotAnImage()
    {
        Assert.False(ImageFormats.LooksLikeAnImage([]));
        Assert.False(ImageFormats.LooksLikeAnImage([0x89, (byte)'P']));
        Assert.False(ImageFormats.LooksLikeAnImage([.. "RIFF"u8, 0x24, 0x00]));
    }

    // A RIFF container that is not WebP — a .wav, say — must not pass on the prefix alone.
    [Fact]
    public void ARiffContainerThatIsNotWebpIsRefused() =>
        Assert.False(ImageFormats.LooksLikeAnImage(
            [.. "RIFF"u8, 0x24, 0x00, 0x00, 0x00, .. "WAVE"u8]));
}

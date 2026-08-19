using System.Text;
using GameLauncher.Core.Media;
using GameLauncher.Core.Models;

namespace GameLauncher.Core.Tests.Media;

/// <summary>
/// The video half of <see cref="MediaUploadRules"/>. Its own class because it is measured
/// against its own three numbers, which is the whole point: a client that read
/// <see cref="MediaCapabilities.MaxBytes"/> for a trailer would refuse at the picture limit.
/// </summary>
public sealed class VideoUploadRulesTests
{
    /// <summary>Deliberately not the defaults, and deliberately not the picture ones.</summary>
    private static readonly MediaCapabilities Limits = new()
    {
        MaxBytes = 64,
        MaxScreenshotsPerGame = 3,
        MaxAltTextLength = 10,
        MaxVideoBytes = 512,
        MaxVideosPerGame = 2,
        VideoContentTypes = ["video/mp4", "video/webm"],
    };

    /// <summary>
    /// Small enough to be under the *picture* limit too, so that the test about a video sent as
    /// a screenshot fails on the format rather than on the size.
    /// </summary>
    private static byte[] Mp4(int totalBytes = 32)
    {
        byte[] bytes = new byte[totalBytes];
        Encoding.ASCII.GetBytes("....ftypisom").CopyTo(bytes, 0);
        return bytes;
    }

    private static byte[] Png(int totalBytes = 16)
    {
        byte[] bytes = new byte[totalBytes];
        ReadOnlySpan<byte> signature = [0x89, (byte)'P', (byte)'N', (byte)'G', 0x0D, 0x0A, 0x1A, 0x0A];
        signature.CopyTo(bytes);
        return bytes;
    }

    [Fact]
    public void AVideoWithinEveryLimitIsAccepted() =>
        Assert.Null(MediaUploadRules.Reject(
            Mp4(), MediaKind.Video, "clip", existing: 0, Limits));

    /// <summary>
    /// The one that matters most. A body this size is refused as a picture and accepted as a
    /// video, from the same limits object — so the kind is choosing the budget rather than the
    /// body happening to fit.
    /// </summary>
    [Fact]
    public void AVideoIsMeasuredAgainstTheVideoLimitAndNotThePictureOne()
    {
        Assert.Equal(
            MediaFailure.TooLarge,
            MediaUploadRules.Reject(Png(totalBytes: 200), MediaKind.Screenshot, "", 0, Limits)?.Reason);

        Assert.Null(MediaUploadRules.Reject(Mp4(totalBytes: 200), MediaKind.Video, "", 0, Limits));
    }

    /// <summary>
    /// And this is why the check exists at all: past this size the server never answers with a
    /// sentence — the framework in front of it returns a bare 413 with no problem document — so
    /// a rejection that does not happen here is a failure nothing can explain.
    /// </summary>
    [Fact]
    public void AnOversizedVideoIsRefusedAndTheRejectionQuotesTheLimit()
    {
        MediaRejection? rejection = MediaUploadRules.Reject(
            Mp4(totalBytes: 513), MediaKind.Video, string.Empty, 0, Limits);

        Assert.Equal(MediaFailure.VideoTooLarge, rejection?.Reason);
        Assert.Equal(512, rejection?.Limit);
    }

    [Fact]
    public void APictureSentAsAVideoIsRefused() =>
        Assert.Equal(
            MediaFailure.UnsupportedVideoFormat,
            MediaUploadRules.Reject(Png(), MediaKind.Video, string.Empty, 0, Limits)?.Reason);

    [Fact]
    public void AVideoSentAsAScreenshotIsRefused() =>
        Assert.Equal(
            MediaFailure.UnsupportedFormat,
            MediaUploadRules.Reject(Mp4(), MediaKind.Screenshot, string.Empty, 0, Limits)?.Reason);

    /// <summary>The two galleries are counted separately, as the server counts them.</summary>
    [Fact]
    public void TheVideoCapIsItsOwn()
    {
        MediaRejection? rejection = MediaUploadRules.Reject(
            Mp4(), MediaKind.Video, string.Empty, existing: 2, Limits);

        Assert.Equal(MediaFailure.VideoGalleryFull, rejection?.Reason);
        Assert.Equal(2, rejection?.Limit);

        // Three screenshots is the picture cap, and it says nothing about a video.
        Assert.Null(MediaUploadRules.Reject(Mp4(), MediaKind.Video, string.Empty, 1, Limits));
    }

    /// <summary>
    /// A server that named no video limits is a server that cannot store one, and the client
    /// reads that silence as "no" rather than as "unknown". The upload is not offered in that
    /// case; this is the answer for a caller that asked anyway.
    /// </summary>
    [Fact]
    public void ADeploymentThatNamedNoVideoLimitsRefusesEveryVideo()
    {
        MediaCapabilities silent = new();
        Assert.False(silent.SupportsVideo);

        Assert.Equal(
            MediaFailure.VideoNotSupported,
            MediaUploadRules.Reject(Mp4(), MediaKind.Video, string.Empty, 0, silent)?.Reason);
    }

    /// <summary>
    /// Half an answer is not an answer: a limit with no format list, or formats with no limit,
    /// is a server describing itself incompletely, and the safe reading is the same as silence.
    /// </summary>
    [Theory]
    [InlineData(0, 2, true)]
    [InlineData(512, 0, true)]
    [InlineData(512, 2, false)]
    public void SupportsVideoNeedsEveryHalf(long maxBytes, int maxPerGame, bool withFormats)
    {
        MediaCapabilities limits = new()
        {
            MaxVideoBytes = maxBytes,
            MaxVideosPerGame = maxPerGame,
            VideoContentTypes = withFormats ? ["video/mp4"] : [],
        };

        Assert.False(limits.SupportsVideo);
    }

    [Fact]
    public void AVideoStillObeysTheAltTextLimit() =>
        Assert.Equal(
            MediaFailure.AltTextTooLong,
            MediaUploadRules.Reject(
                Mp4(), MediaKind.Video, new string('x', 11), 0, Limits)?.Reason);

    [Fact]
    public void AnEmptyFileIsRefusedBeforeTheKindIsEvenConsidered() =>
        Assert.Equal(
            MediaFailure.Empty,
            MediaUploadRules.Reject([], MediaKind.Video, string.Empty, 0, Limits)?.Reason);
}

using GameLauncher.Core.Models;

namespace GameLauncher.Core.Media;

/// <summary>
/// Why a picture — or a video — cannot be offered to the server. Each one is its own sentence.
/// </summary>
public enum MediaFailure
{
    /// <summary>A file with no bytes in it.</summary>
    Empty,

    /// <summary>Larger than <see cref="MediaCapabilities.MaxBytes"/>.</summary>
    TooLarge,

    /// <summary>Not one of the formats the server stores — see <see cref="ImageFormats"/>.</summary>
    UnsupportedFormat,

    /// <summary>Alt text longer than <see cref="MediaCapabilities.MaxAltTextLength"/>.</summary>
    AltTextTooLong,

    /// <summary>The gallery already holds <see cref="MediaCapabilities.MaxScreenshotsPerGame"/>.</summary>
    GalleryFull,

    /// <summary>Larger than <see cref="MediaCapabilities.MaxVideoBytes"/>.</summary>
    VideoTooLarge,

    /// <summary>Not MP4 or WebM — see <see cref="VideoFormats"/>.</summary>
    UnsupportedVideoFormat,

    /// <summary>The game already holds <see cref="MediaCapabilities.MaxVideosPerGame"/>.</summary>
    VideoGalleryFull,

    /// <summary>
    /// This deployment does not store videos at all, which is what a server that names no
    /// video limits is saying. The upload is not offered in that case, so this is the answer
    /// for a caller that asked anyway.
    /// </summary>
    VideoNotSupported,
}

/// <summary>What a rejection is, with the number that caused it so a message can quote it.</summary>
public sealed record MediaRejection(MediaFailure Reason, long Limit = 0);

/// <summary>
/// Whether a picture is worth sending. Every limit here comes from
/// <see cref="MediaCapabilities"/> — which the server announces at
/// <c>GET /api/v1/capabilities</c> — and **none of them is a constant in this file** (D39). A
/// deployment that narrows one has to be able to say so, and the previous arrangement, where
/// the client guessed from the server repository's defaults, is exactly what made a narrowed
/// limit produce a refusal that did not name it.
///
/// The checks are here to fail fast and locally, never to vouch: the server applies all of them
/// again, and it is the one that decides what an image is. A client that passed this and is
/// still refused is a client that was wrong, not a server that is.
/// </summary>
public static class MediaUploadRules
{
    /// <summary>Null when the picture may be offered, otherwise why it may not.</summary>
    /// <param name="existing">
    /// How many of <paramref name="kind"/> the game already has. Only the two gallery kinds
    /// read it; a cover is replaced rather than added to.
    /// </param>
    public static MediaRejection? Reject(
        ReadOnlySpan<byte> content,
        MediaKind kind,
        string altText,
        int existing,
        MediaCapabilities limits)
    {
        if (content.Length == 0)
        {
            return new MediaRejection(MediaFailure.Empty);
        }

        return kind == MediaKind.Video
            ? RejectVideo(content, altText, existing, limits)
            : RejectPicture(content, kind, altText, existing, limits);
    }

    private static MediaRejection? RejectPicture(
        ReadOnlySpan<byte> content,
        MediaKind kind,
        string altText,
        int existingScreenshots,
        MediaCapabilities limits)
    {
        if (content.Length > limits.MaxBytes)
        {
            return new MediaRejection(MediaFailure.TooLarge, limits.MaxBytes);
        }

        if (!ImageFormats.LooksLikeAnImage(content))
        {
            return new MediaRejection(MediaFailure.UnsupportedFormat);
        }

        // The gallery cap applies to screenshots alone: a game has one cover, one banner and
        // one logo, and uploading a second replaces nothing — the server's partial unique index
        // refuses it. Counting them here would refuse a replacement that is legitimate.
        if (kind == MediaKind.Screenshot && existingScreenshots >= limits.MaxScreenshotsPerGame)
        {
            return new MediaRejection(MediaFailure.GalleryFull, limits.MaxScreenshotsPerGame);
        }

        return RejectAltText(altText, limits);
    }

    /// <summary>
    /// The same three questions with the other three numbers — and one the pictures do not have,
    /// because a deployment that stores no video is a real answer rather than a limit of zero.
    ///
    /// The size check here matters more than its picture counterpart. An oversized image is
    /// refused by the server with a sentence naming the limit; an oversized <em>video</em> is
    /// refused by the web framework in front of it, before any handler runs, with a bare 413 and
    /// no problem document — so if this check does not catch it, nothing downstream can explain
    /// it.
    /// </summary>
    private static MediaRejection? RejectVideo(
        ReadOnlySpan<byte> content, string altText, int existingVideos, MediaCapabilities limits)
    {
        if (!limits.SupportsVideo)
        {
            return new MediaRejection(MediaFailure.VideoNotSupported);
        }

        if (content.Length > limits.MaxVideoBytes)
        {
            return new MediaRejection(MediaFailure.VideoTooLarge, limits.MaxVideoBytes);
        }

        if (!VideoFormats.LooksLikeAVideo(content))
        {
            return new MediaRejection(MediaFailure.UnsupportedVideoFormat);
        }

        if (existingVideos >= limits.MaxVideosPerGame)
        {
            return new MediaRejection(MediaFailure.VideoGalleryFull, limits.MaxVideosPerGame);
        }

        return RejectAltText(altText, limits);
    }

    /// <summary>
    /// The alt-text rule on its own, because editing a picture's description is a route of its
    /// own (<c>PATCH /media/{id}</c>) and must not have to invent a second copy of the limit.
    /// </summary>
    public static MediaRejection? RejectAltText(string altText, MediaCapabilities limits) =>
        altText.Length > limits.MaxAltTextLength
            ? new MediaRejection(MediaFailure.AltTextTooLong, limits.MaxAltTextLength)
            : null;
}

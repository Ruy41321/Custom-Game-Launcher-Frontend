using GameLauncher.Core.Models;

namespace GameLauncher.Core.Media;

/// <summary>Why a picture cannot be offered to the server. Each one is its own sentence.</summary>
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
    public static MediaRejection? Reject(
        ReadOnlySpan<byte> content,
        MediaKind kind,
        string altText,
        int existingScreenshots,
        MediaCapabilities limits)
    {
        if (content.Length == 0)
        {
            return new MediaRejection(MediaFailure.Empty);
        }

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
    /// The alt-text rule on its own, because editing a picture's description is a route of its
    /// own (<c>PATCH /media/{id}</c>) and must not have to invent a second copy of the limit.
    /// </summary>
    public static MediaRejection? RejectAltText(string altText, MediaCapabilities limits) =>
        altText.Length > limits.MaxAltTextLength
            ? new MediaRejection(MediaFailure.AltTextTooLong, limits.MaxAltTextLength)
            : null;
}

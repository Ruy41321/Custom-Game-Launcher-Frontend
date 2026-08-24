namespace GameLauncher.Core.Models;

/// <summary>
/// Mirrors the server's <c>media_kind</c> enum. The first three are a game's identity and
/// there is at most one of each — the database enforces that, not this client — while
/// screenshots are a gallery ordered by <see cref="GameMedia.SortOrder"/>.
///
/// <see cref="Video"/> is a gallery too, with a cap and a size limit of its own, and it is the
/// one kind whose <see cref="GameMedia.Url"/> is never handed to an image decoder. It is last
/// because that is where the server's <c>ALTER TYPE … ADD VALUE</c> put it.
/// </summary>
public enum MediaKind
{
    Cover,
    Banner,
    Logo,
    Screenshot,
    Video,
}

/// <summary>
/// One picture — or one video — belonging to a game. <see cref="Url"/> is absolute, public and **unsigned**:
/// artwork lives on a root of its own precisely so a cover does not need a signature that
/// would expire while somebody was looking at the page. It is therefore fetched by a client
/// that carries no bearer token (see D35).
/// </summary>
public sealed record GameMedia
{
    public string Id { get; init; } = string.Empty;

    public string GameId { get; init; } = string.Empty;

    public MediaKind Kind { get; init; }

    /// <summary>
    /// Content-addressed: the same picture uploaded twice is the same URL, and editing a
    /// picture is uploading a different one. That is what makes caching it by URL safe.
    /// </summary>
    public string Url { get; init; } = string.Empty;

    public string ContentType { get; init; } = string.Empty;

    public long SizeBytes { get; init; }

    /// <summary>The publisher's description of the image, for screen readers and tooltips.</summary>
    public string AltText { get; init; } = string.Empty;

    public int SortOrder { get; init; }

    public DateTimeOffset CreatedAt { get; init; }
}

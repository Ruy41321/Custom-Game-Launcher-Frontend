namespace GameLauncher.Core.Models;

/// <summary>
/// What the server this launcher is pointed at will accept. Read from
/// <c>GET /api/v1/capabilities</c>, which needs no token.
///
/// Every value here used to be a constant compiled into the client, guessed from the defaults
/// in the server repository. A deployment that narrowed one broke publishing with an error
/// that did not say what the real limit was; one that widened it gained nothing.
///
/// **Every field is optional by design.** A server older than the route answers 404, and an
/// unknown field is ignored, so the defaults below are what the launcher falls back to. They
/// are deliberately the *conservative* reading of the server's own defaults: too small a chunk
/// is slow, too large a chunk is refused.
/// </summary>
public sealed record ServerCapabilities
{
    /// <summary>What a launcher assumes about a server that did not answer.</summary>
    public static ServerCapabilities Fallback { get; } = new();

    public string ApiVersion { get; init; } = "v1";

    public string ServerVersion { get; init; } = string.Empty;

    public UploadCapabilities Uploads { get; init; } = new();

    public ManifestCapabilities Manifest { get; init; } = new();

    public MediaCapabilities Media { get; init; } = new();

    public CatalogCapabilities Catalog { get; init; } = new();
}

public sealed record UploadCapabilities
{
    /// <summary>
    /// The largest body one `PATCH /uploads/{id}` may carry. The client sends less than this,
    /// never more: the server refuses an oversized chunk before the handler runs, so the
    /// failure would look like a routing problem rather than a size one.
    /// </summary>
    public long MaxChunkBytes { get; init; } = 8 * 1024 * 1024;

    /// <summary>The largest single file a build may contain.</summary>
    public long MaxBlobBytes { get; init; } = 2L * 1024 * 1024 * 1024;

    /// <summary>
    /// How many uploads one account may have open. The publisher uploads one blob at a time,
    /// so this is informational — but it is the number that would have to change first if it
    /// ever stopped doing that.
    /// </summary>
    public int MaxOpenSessionsPerUser { get; init; } = 16;

    /// <summary>How long an interrupted upload stays resumable.</summary>
    public long SessionTtlSeconds { get; init; } = 86400;

    /// <summary>What a new account is given. Never what this account has left.</summary>
    public long DefaultQuotaBytes { get; init; } = 5L * 1024 * 1024 * 1024;
}

public sealed record ManifestCapabilities
{
    public int MaxPathLength { get; init; } = 1024;

    public int MaxFiles { get; init; } = 200_000;
}

public sealed record MediaCapabilities
{
    public long MaxBytes { get; init; } = 5 * 1024 * 1024;

    public int MaxScreenshotsPerGame { get; init; } = 12;

    public int MaxAltTextLength { get; init; } = 300;

    /// <summary>
    /// The image types the server stores, in the order it lists them. Named rather than
    /// counted because a client choosing a format for an upload needs the list.
    /// </summary>
    public IReadOnlyList<string> ContentTypes { get; init; } =
        ["image/png", "image/jpeg", "image/webp"];
}

public sealed record CatalogCapabilities
{
    public int MaxPageSize { get; init; } = 100;

    public int DefaultPageSize { get; init; } = 20;

    public int MaxPatchNotePageSize { get; init; } = 100;
}

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

    public CrashReportCapabilities CrashReports { get; init; } = new();

    public MailCapabilities Mail { get; init; } = new();
}

public sealed record MailCapabilities
{
    /// <summary>
    /// Whether this deployment can send a message at all. It decides whether the sign-in screen
    /// offers "forgotten your password?" — the routes that send answer 404 where the transport
    /// is switched off, so without this the offer is a button whose only outcome is an error.
    ///
    /// The fallback is <b>true</b>, unlike <see cref="CrashReportCapabilities.Enabled"/>, and
    /// the asymmetry is the point. That one is permission to send something about the user, so
    /// silence means no. This one is a feature the person needs: a server too old to carry the
    /// key does send mail, and reading its silence as "no mail" would hide the reset link on
    /// every server that predates this field. Guessing wrong in this direction costs one
    /// refusal, which the sign-in screen already explains; guessing wrong in the other hides
    /// the way back into an account.
    /// </summary>
    public bool Enabled { get; init; } = true;
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

    /// <summary>
    /// The largest video this deployment accepts. The fallback is <b>zero</b>, and unlike
    /// <see cref="MailCapabilities.Enabled"/> the silence is read as "no", because the two
    /// silences mean different things: mail is a feature every server older than that field
    /// still had, while video is one that did not exist before this key existed. A server that
    /// does not name it cannot store a video, so offering the upload would spend somebody's
    /// bandwidth to be refused.
    ///
    /// It is also the one limit a client must enforce <em>itself</em>. The server's refusal for
    /// an oversized body comes from the web framework before any handler runs, and it is a bare
    /// <c>413</c> with no problem document in it — nothing to turn into a sentence. The
    /// refusals that carry a message only happen for a body already accepted.
    /// </summary>
    public long MaxVideoBytes { get; init; }

    /// <summary>How many videos one game may have. Zero where the server did not say.</summary>
    public int MaxVideosPerGame { get; init; }

    /// <summary>
    /// The video containers the server stores. Empty where it named none, which is what
    /// <see cref="SupportsVideo"/> reads.
    /// </summary>
    public IReadOnlyList<string> VideoContentTypes { get; init; } = [];

    /// <summary>
    /// Whether this deployment does video at all. Both halves are required: a limit with no
    /// format list, or a list with no limit, is a server describing itself incompletely, and
    /// the safe reading of that is the same as silence.
    /// </summary>
    public bool SupportsVideo =>
        MaxVideoBytes > 0 && MaxVideosPerGame > 0 && VideoContentTypes.Count > 0;
}

public sealed record CrashReportCapabilities
{
    /// <summary>
    /// Whether sending them is worth attempting. The fallback is **false**, unlike every other
    /// default here: the rest are limits on something the launcher is going to do anyway, and
    /// this is permission to send something about the user. A server that did not answer has
    /// not agreed to receive anything, so the launcher keeps the reports on disk rather than
    /// posting into a route that may not exist.
    /// </summary>
    public bool Enabled { get; init; }

    public int MaxMessageLength { get; init; } = 2000;

    public int MaxStackLength { get; init; } = 16000;
}

public sealed record CatalogCapabilities
{
    public int MaxPageSize { get; init; } = 100;

    public int DefaultPageSize { get; init; } = 20;

    public int MaxPatchNotePageSize { get; init; } = 100;
}

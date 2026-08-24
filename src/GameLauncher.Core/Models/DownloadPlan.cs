namespace GameLauncher.Core.Models;

/// <summary>How the server expects this build to be obtained.</summary>
public enum DownloadKind
{
    /// <summary>Every file travels. Also what the server falls back to when a delta stops paying.</summary>
    Full,

    /// <summary>Only what differs from the build the client said it had.</summary>
    Delta,
}

/// <summary>
/// A file the install does not yet hold in the state the target build wants, together with the
/// signed URL that would fetch it.
/// </summary>
public sealed record PlannedFile : ManifestEntry
{
    /// <summary>
    /// Signed URL on the file server. It carries its own authorization, so it is fetched
    /// **without** an <c>Authorization</c> header — see <see cref="DownloadPlan.UrlsExpireAt"/>.
    /// </summary>
    public string Url { get; init; } = string.Empty;

    /// <summary>
    /// A path in the current install that already holds exactly these bytes, or null when they
    /// have to travel. The server only ever names a path the update keeps unchanged, so the
    /// copy is safe whatever order the plan is applied in.
    /// </summary>
    public string? CopyFrom { get; init; }

    /// <summary>An optimisation, never an instruction: the URL is filled in either way.</summary>
    public bool CanBeCopiedLocally => !string.IsNullOrEmpty(CopyFrom);
}

/// <summary>
/// What the client has to do to reach a build, from an older one or from nothing at all.
/// Computed on demand by the server over the two manifests, so any old install reaches the
/// current build in one hop.
/// </summary>
public sealed record DownloadPlan
{
    public string BuildId { get; init; } = string.Empty;

    public string GameId { get; init; } = string.Empty;

    public string VersionId { get; init; } = string.Empty;

    public DownloadKind Kind { get; init; }

    public string ManifestSha256 { get; init; } = string.Empty;

    public string Entrypoint { get; init; } = string.Empty;

    public string LaunchArgs { get; init; } = string.Empty;

    public IReadOnlyList<PlannedFile> Files { get; init; } = [];

    /// <summary>Already correct on disk. Empty on a full download.</summary>
    public IReadOnlyList<ManifestEntry> Unchanged { get; init; } = [];

    /// <summary>Paths the install holds that the target build does not have at all.</summary>
    public IReadOnlyList<string> Remove { get; init; } = [];

    /// <summary>
    /// What the transfer is expected to cost. Lower than the sum of <see cref="Files"/>: paths
    /// are the unit of the plan and blobs the unit of the transfer, so two files with identical
    /// content are two entries and one download.
    /// </summary>
    public long DownloadBytes { get; init; }

    /// <summary>The build as installed, which is what the free-space check is about.</summary>
    public long TotalBytes { get; init; }

    /// <summary>
    /// After this, every URL in the plan is refused. The expiry is checked when a request
    /// starts and not while it runs, but a *resumed* download issues a new request — so a
    /// transfer interrupted for longer than this asks for a fresh plan rather than retrying.
    /// </summary>
    public DateTimeOffset UrlsExpireAt { get; init; }

    /// <summary>Nothing to fetch and nothing to delete: the install is already this build.</summary>
    public bool IsUpToDate => Files.Count == 0 && Remove.Count == 0;

    /// <summary>
    /// Every file of the target build, which is what the install has to look like when the plan
    /// has been applied.
    /// </summary>
    public IReadOnlyList<ManifestEntry> TargetFiles =>
        [.. Files, .. Unchanged];

    /// <summary>
    /// Whether the URLs are too close to expiry to start using. The margin exists because a
    /// plan that expires mid-transfer costs a full round trip and a restarted file.
    /// </summary>
    public bool IsExpiring(DateTimeOffset now, TimeSpan margin) => now + margin >= UrlsExpireAt;
}

/// <summary>What the client found at one path of an install, for the integrity check.</summary>
public sealed record InstalledFile(string Path, string Sha256);

/// <summary>
/// The server's answer to "is what I have actually the build". The manifest is the authority,
/// so this is the only opinion that counts however the install got into its current state.
/// </summary>
public sealed record IntegrityReport
{
    public string BuildId { get; init; } = string.Empty;

    public string ManifestSha256 { get; init; } = string.Empty;

    /// <summary>
    /// Deliberately ignores <see cref="Unexpected"/>: an install directory legitimately
    /// accumulates saves, configuration and logs, and calling that corruption would train
    /// people to ignore the check.
    /// </summary>
    public bool Intact { get; init; }

    /// <summary>In the manifest, not in the install — a file the client could not read lands here.</summary>
    public IReadOnlyList<string> Missing { get; init; } = [];

    /// <summary>Present, but not the content the manifest names.</summary>
    public IReadOnlyList<string> Corrupt { get; init; } = [];

    /// <summary>In the install, not in the manifest. Reported so the client can decide.</summary>
    public IReadOnlyList<string> Unexpected { get; init; } = [];

    /// <summary>The missing and corrupt files with fresh URLs, so a repair needs no second call.</summary>
    public IReadOnlyList<PlannedFile> Repair { get; init; } = [];

    public long RepairBytes { get; init; }

    public DateTimeOffset UrlsExpireAt { get; init; }
}

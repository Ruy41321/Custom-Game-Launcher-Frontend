namespace GameLauncher.Core.Models;

/// <summary>
/// A game together with everything the detail page shows. The server decides which versions
/// are visible — a publisher sees their unreleased ones, everybody else does not — so the
/// client renders the list it is given rather than filtering it again.
/// </summary>
public sealed record GameDetail
{
    public Game Game { get; init; } = new();

    /// <summary>Whether the *calling account* already has this game in its library.</summary>
    public bool InLibrary { get; init; }

    public IReadOnlyList<GameVersion> Versions { get; init; } = [];

    public IReadOnlyList<GameBuild> Builds { get; init; } = [];

    /// <summary>
    /// Everything hanging off the game: at most one cover, banner and logo, plus the screenshot
    /// gallery and the videos.
    /// </summary>
    public IReadOnlyList<GameMedia> Media { get; init; } = [];

    /// <summary>
    /// The one picture of a kind there can only be one of. Null when the publisher has not
    /// uploaded it — which is the ordinary case for a banner and a logo.
    /// </summary>
    public GameMedia? Artwork(MediaKind kind) =>
        kind is MediaKind.Screenshot or MediaKind.Video
            ? null
            : Media.FirstOrDefault(item => item.Kind == kind);

    /// <summary>
    /// The gallery, in the order the publisher arranged it. Ties fall back to upload order so
    /// two screenshots left at the default sort order do not swap places between loads.
    /// </summary>
    public IReadOnlyList<GameMedia> Screenshots =>
    [
        .. Media
            .Where(item => item.Kind == MediaKind.Screenshot)
            .OrderBy(item => item.SortOrder)
            .ThenBy(item => item.CreatedAt),
    ];

    /// <summary>
    /// The videos, ordered the way the screenshots are and for the same reason. Kept separate
    /// rather than filtered at each call site: they are shown in a different place and played
    /// rather than decoded, and a list that mixed them would hand a container to an image
    /// decoder.
    /// </summary>
    public IReadOnlyList<GameMedia> Videos =>
    [
        .. Media
            .Where(item => item.Kind == MediaKind.Video)
            .OrderBy(item => item.SortOrder)
            .ThenBy(item => item.CreatedAt),
    ];

    /// <summary>
    /// The build to install on this machine, or null when the publisher has shipped nothing
    /// for it yet. Newest ready build of a <em>published</em> version for the platform,
    /// preferring the running architecture. A build whose version is not published is not a
    /// candidate for anybody, its publisher included (D71).
    /// </summary>
    public GameBuild? BuildFor(GamePlatform platform, BuildArchitecture architecture) =>
        Builds
            .Where(build =>
                build.Status == BuildStatus.Ready
                && build.Platform == platform
                && IsPublished(build))
            .OrderByDescending(build => build.Architecture == architecture)
            .ThenByDescending(build => build.ReadyAt ?? build.CreatedAt)
            .FirstOrDefault();

    /// <summary>
    /// Whether the version a build belongs to is out. A build the version list says nothing
    /// about counts as unpublished, the same direction the server's <c>versionPublished</c>
    /// defaults in: a listing that forgot to carry the version withholds a build rather than
    /// offering one nobody may download.
    /// </summary>
    private bool IsPublished(GameBuild build) =>
        Versions.FirstOrDefault(version => version.Id == build.VersionId)?.Published ?? false;
}

/// <summary>One page of a paged listing, in the envelope the server sends.</summary>
public sealed record PagedResult<T>
{
    public IReadOnlyList<T> Items { get; init; } = [];

    /// <summary>Total matching rows, not the size of <see cref="Items"/>.</summary>
    public int Total { get; init; }

    public int Limit { get; init; }

    public int Offset { get; init; }

    /// <summary>1-based, matching what <see cref="GameQuery.Page"/> asked for.</summary>
    public int Page => Limit > 0 ? (Offset / Limit) + 1 : 1;

    public int PageCount => Limit > 0 ? Math.Max(1, (Total + Limit - 1) / Limit) : 1;
}

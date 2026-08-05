using System.Text.Json.Serialization;
using GameLauncher.Core.Json;
using GameLauncher.Core.Models;

namespace GameLauncher.Core.Api;

/// <summary>A new game. Everything but the title is optional and can be filled in later.</summary>
public sealed record CreateGameRequest
{
    public required string Title { get; init; }

    /// <summary>Null lets the server derive one from the title, which is what it is for.</summary>
    public string? Slug { get; init; }

    public string? Summary { get; init; }

    public string? Description { get; init; }

    [JsonConverter(typeof(OptionalDateOnlyConverter))]
    public DateOnly? ReleaseDate { get; init; }

    /// <summary>Drafts are visible to their publisher and to nobody else.</summary>
    public GameVisibility Visibility { get; init; } = GameVisibility.Draft;
}

/// <summary>
/// A partial update. An absent field means "leave it alone" — the serializer drops nulls, so
/// null *is* absence here. Clearing a field is done by sending it empty, which is the one
/// thing an absent field cannot express.
/// </summary>
public sealed record GameChanges
{
    public string? Title { get; init; }

    public string? Summary { get; init; }

    public string? Description { get; init; }

    [JsonConverter(typeof(OptionalDateOnlyConverter))]
    public DateOnly? ReleaseDate { get; init; }

    public GameVisibility? Visibility { get; init; }
}

public sealed record CreateVersionRequest
{
    public required string Semver { get; init; }

    public BuildStage Stage { get; init; } = BuildStage.Release;

    public string? ReleaseNotes { get; init; }

    /// <summary>
    /// An unpublished version is a staging area: it and its builds exist, and only the
    /// publisher can see them.
    /// </summary>
    public bool Publish { get; init; }
}

public sealed record CreateBuildRequest
{
    public required GamePlatform Platform { get; init; }

    public BuildArchitecture Architecture { get; init; } = BuildArchitecture.X64;
}

/// <summary>A blob the publisher is offering: its content address and how big it is.</summary>
public sealed record BlobDeclaration(string Sha256, long Size);

/// <summary>
/// A resumable upload of one blob. <see cref="ReceivedBytes"/> is the server's count and the
/// only one that matters: it is assigned by a conditional UPDATE, so it is the authority even
/// when the client thinks it knows better.
/// </summary>
public sealed record UploadSession
{
    public string Id { get; init; } = string.Empty;

    public string BuildId { get; init; } = string.Empty;

    public string Sha256 { get; init; } = string.Empty;

    public long SizeBytes { get; init; }

    public long ReceivedBytes { get; init; }

    public string Status { get; init; } = string.Empty;

    public bool Complete { get; init; }
}

public sealed record ManifestFile
{
    public required string Path { get; init; }

    public required string Sha256 { get; init; }

    public bool Executable { get; init; }
}

/// <summary>
/// What turns a build from <c>uploading</c> into <c>ready</c>. File sizes are deliberately not
/// sent: the server reads them back from the blobs it stored, so a build cannot advertise a
/// download size its content does not have.
/// </summary>
public sealed record ManifestSubmission
{
    public required IReadOnlyList<ManifestFile> Files { get; init; }

    public required string Entrypoint { get; init; }

    public string? LaunchArgs { get; init; }
}

/// <summary>
/// A picture on its way to the server. The bytes are the request body and nothing else travels
/// with them — there is no multipart form, because there is one file and the fields that
/// describe it are query parameters.
///
/// There is deliberately no content type here. The server decides what an image is from its
/// leading bytes and ignores what an uploader declares, because that answer becomes the
/// <c>Content-Type</c> of a public URL (D28 of the server). Carrying a field the server refuses
/// to read would only invite somebody to trust it.
/// </summary>
public sealed record MediaUpload
{
    public required MediaKind Kind { get; init; }

    public required ReadOnlyMemory<byte> Content { get; init; }

    public string AltText { get; init; } = string.Empty;

    /// <summary>Position in the gallery. Meaningless for the singleton kinds.</summary>
    public int SortOrder { get; init; }
}

/// <summary>
/// What can be changed about a picture: how it is described and where it sits. **Not the
/// picture** — there is no route that swaps bytes under an existing id, because the id's whole
/// meaning is the content it points at. Replacing an image is uploading another one.
/// </summary>
public sealed record MediaChanges
{
    public string? AltText { get; init; }

    public int? SortOrder { get; init; }
}

/// <summary>A new devlog entry. <c>publish: false</c> writes a draft.</summary>
public sealed record CreatePatchNoteRequest
{
    public required string Title { get; init; }

    public required string BodyMarkdown { get; init; }

    /// <summary>
    /// Optional, and must name a version of the same game. A post about no version at all —
    /// "what we are working on this month" — is a legitimate entry, which is why this is not
    /// a version's release notes.
    /// </summary>
    public string? VersionId { get; init; }

    public bool Publish { get; init; }
}

/// <summary>
/// A partial edit. Null means "leave it alone"; the serializer drops nulls, so null *is*
/// absence. <see cref="VersionId"/> set to the empty string detaches the entry from its
/// version, which is the one thing an absent field cannot express.
///
/// <see cref="Published"/> is both publishing and withdrawing, because a note that went out by
/// mistake has to be able to come back. Re-publishing does not move the original date: that
/// date is when readers saw it, not when it was last edited.
/// </summary>
public sealed record PatchNoteChanges
{
    public string? Title { get; init; }

    public string? BodyMarkdown { get; init; }

    public string? VersionId { get; init; }

    public bool? Published { get; init; }
}

/// <summary>
/// Everything a publisher does. Separate from <see cref="ICatalogApi"/> because these routes
/// need <c>game.publish</c>, <c>build.upload</c> and <c>patchnote.write</c>, and because a
/// player's launcher never calls any of them — keeping them apart is what makes that visible
/// in the type system rather than in a comment.
///
/// **Every write route belongs here.** Adding one to <see cref="ICatalogApi"/> would put a call
/// a player's account can never make into a player's client (D30).
/// </summary>
public interface IPublishingApi
{
    Task<Game> CreateGameAsync(
        CreateGameRequest request, CancellationToken cancellationToken = default);

    Task<Game> UpdateGameAsync(
        string idOrSlug, GameChanges changes, CancellationToken cancellationToken = default);

    Task<GameVersion> CreateVersionAsync(
        string idOrSlug, CreateVersionRequest request, CancellationToken cancellationToken = default);

    Task<GameBuild> CreateBuildAsync(
        string idOrSlug,
        string versionId,
        CreateBuildRequest request,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Which of these the server does not already hold. This is what keeps a second build cost
    /// only what actually changed, so it is asked before a byte travels.
    /// </summary>
    Task<IReadOnlyList<string>> MissingBlobsAsync(
        string buildId,
        IReadOnlyList<BlobDeclaration> blobs,
        CancellationToken cancellationToken = default);

    Task<UploadSession> BeginUploadAsync(
        string buildId, BlobDeclaration blob, CancellationToken cancellationToken = default);

    /// <summary>Where to resume from. A client that lost its place asks; it does not guess.</summary>
    Task<UploadSession> GetUploadAsync(
        string sessionId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Sends the next chunk. <paramref name="offset"/> is mandatory on the wire and a wrong one
    /// is refused with a <see cref="ApiErrorCode.Conflict"/> naming the real offset, so a
    /// confused client recovers from the error rather than corrupting the file.
    /// </summary>
    Task<UploadSession> SendChunkAsync(
        string sessionId,
        long offset,
        ReadOnlyMemory<byte> chunk,
        CancellationToken cancellationToken = default);

    Task AbortUploadAsync(string sessionId, CancellationToken cancellationToken = default);

    Task<GameBuild> SubmitManifestAsync(
        string buildId, ManifestSubmission manifest, CancellationToken cancellationToken = default);

    // --- artwork -------------------------------------------------------------------------

    /// <summary>
    /// Uploads one picture. The bytes are the body; kind, alt text and sort order are query
    /// parameters. What the image *is* gets decided server-side from those bytes.
    /// </summary>
    Task<GameMedia> UploadMediaAsync(
        string idOrSlug, MediaUpload upload, CancellationToken cancellationToken = default);

    /// <summary>Alt text and position. Never the picture — see <see cref="MediaChanges"/>.</summary>
    Task<GameMedia> UpdateMediaAsync(
        string mediaId, MediaChanges changes, CancellationToken cancellationToken = default);

    /// <summary>
    /// Removes one picture. Irreversible from the client's side: there is no undo route, and
    /// restoring it means uploading the file again.
    /// </summary>
    Task DeleteMediaAsync(string mediaId, CancellationToken cancellationToken = default);

    // --- the devlog ----------------------------------------------------------------------

    Task<PatchNote> CreatePatchNoteAsync(
        string idOrSlug,
        CreatePatchNoteRequest request,
        CancellationToken cancellationToken = default);

    /// <summary>Edits, publishes or withdraws — all of it is one PATCH.</summary>
    Task<PatchNote> UpdatePatchNoteAsync(
        string noteId, PatchNoteChanges changes, CancellationToken cancellationToken = default);

    Task DeletePatchNoteAsync(string noteId, CancellationToken cancellationToken = default);

    // --- taking things back --------------------------------------------------------------

    /// <summary>
    /// Deletes a build. The blobs it referenced are reclaimed by the server's collector once
    /// nothing else points at them, and the account's quota is refunded — so this is how a
    /// publisher gets space back, not merely how they tidy up.
    /// </summary>
    Task DeleteBuildAsync(string buildId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Deletes a version **and every build under it**. Nothing here warns about that; saying
    /// what disappears is the caller's job, before it calls.
    /// </summary>
    Task DeleteVersionAsync(
        string idOrSlug, string versionId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Deletes a game and everything under it: every version and build, its artwork, its devlog,
    /// every library entry pointing at it, and its download history.
    ///
    /// The server allows this **even while other accounts hold the game in their library** — an
    /// entry is a bookmark, not a licence — so nothing refuses it on their behalf and the
    /// caller has to say so first. What those people already installed keeps working; what
    /// stops is updating and verifying it, and the server answers both with
    /// <see cref="ApiErrorCode.NotFound"/> rather than a refusal, because afterwards there
    /// genuinely is no such game.
    ///
    /// A publisher who only wants a title to stop being visible should set
    /// <see cref="GameVisibility.Draft"/> instead, which is reversible.
    /// </summary>
    Task DeleteGameAsync(string idOrSlug, CancellationToken cancellationToken = default);
}

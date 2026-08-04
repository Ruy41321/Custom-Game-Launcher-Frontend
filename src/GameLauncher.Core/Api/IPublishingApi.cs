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
/// Everything a publisher does. Separate from <see cref="ICatalogApi"/> because these routes
/// need <c>game.publish</c> and <c>build.upload</c>, and because a player's launcher never
/// calls any of them — keeping them apart is what makes that visible in the type system.
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
}

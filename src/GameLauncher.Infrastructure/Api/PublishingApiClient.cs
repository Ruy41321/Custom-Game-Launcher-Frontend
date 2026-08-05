using System.Globalization;
using GameLauncher.Core.Api;
using GameLauncher.Core.Models;

namespace GameLauncher.Infrastructure.Api;

/// <summary>
/// The publisher's half of the API. Runs on the authenticated <see cref="HttpClient"/>; every
/// route here needs a permission a player's account does not have, and the server checks each
/// one again whatever the client believed.
/// </summary>
public sealed class PublishingApiClient(HttpClient httpClient) : IPublishingApi
{
    /// <summary>The content type the upload protocol uses for a chunk, as tus does.</summary>
    private const string ChunkContentType = "application/offset+octet-stream";

    /// <summary>
    /// What an image upload declares: nothing. The server sniffs the leading bytes and never
    /// reads this header, so saying <c>image/png</c> would be a guess dressed as a fact.
    /// </summary>
    private const string UnknownContentType = "application/octet-stream";

    private readonly ApiTransport _transport = new(httpClient);

    public Task<Game> CreateGameAsync(
        CreateGameRequest request, CancellationToken cancellationToken = default) =>
        _transport.PostAsync<Game>("games", request, cancellationToken);

    public Task<Game> UpdateGameAsync(
        string idOrSlug, GameChanges changes, CancellationToken cancellationToken = default) =>
        _transport.PatchAsync<Game>(GamePath(idOrSlug), changes, cancellationToken);

    public Task<GameVersion> CreateVersionAsync(
        string idOrSlug,
        CreateVersionRequest request,
        CancellationToken cancellationToken = default) =>
        _transport.PostAsync<GameVersion>(
            GamePath(idOrSlug) + "/versions", request, cancellationToken);

    public Task<GameBuild> CreateBuildAsync(
        string idOrSlug,
        string versionId,
        CreateBuildRequest request,
        CancellationToken cancellationToken = default) =>
        _transport.PostAsync<GameBuild>(
            GamePath(idOrSlug) + "/versions/" + Uri.EscapeDataString(versionId) + "/builds",
            request,
            cancellationToken);

    public async Task<IReadOnlyList<string>> MissingBlobsAsync(
        string buildId,
        IReadOnlyList<BlobDeclaration> blobs,
        CancellationToken cancellationToken = default)
    {
        MissingBlobsResponse response = await _transport
            .PostAsync<MissingBlobsResponse>(
                BuildPath(buildId) + "/blobs/missing",
                new MissingBlobsRequest(blobs),
                cancellationToken)
            .ConfigureAwait(false);

        return response.Missing;
    }

    public Task<UploadSession> BeginUploadAsync(
        string buildId, BlobDeclaration blob, CancellationToken cancellationToken = default) =>
        _transport.PostAsync<UploadSession>(
            BuildPath(buildId) + "/uploads", blob, cancellationToken);

    public Task<UploadSession> GetUploadAsync(
        string sessionId, CancellationToken cancellationToken = default) =>
        _transport.GetAsync<UploadSession>(UploadPath(sessionId), cancellationToken);

    public Task<UploadSession> SendChunkAsync(
        string sessionId,
        long offset,
        ReadOnlyMemory<byte> chunk,
        CancellationToken cancellationToken = default) =>
        _transport.PatchBytesAsync<UploadSession>(
            UploadPath(sessionId),
            chunk,
            ChunkContentType,
            [("Upload-Offset", offset.ToString(CultureInfo.InvariantCulture))],
            cancellationToken);

    public Task AbortUploadAsync(string sessionId, CancellationToken cancellationToken = default) =>
        _transport.DeleteAsync(UploadPath(sessionId), cancellationToken);

    public Task<GameBuild> SubmitManifestAsync(
        string buildId,
        ManifestSubmission manifest,
        CancellationToken cancellationToken = default) =>
        _transport.PostAsync<GameBuild>(
            BuildPath(buildId) + "/manifest", manifest, cancellationToken);

    public Task<GameMedia> UploadMediaAsync(
        string idOrSlug, MediaUpload upload, CancellationToken cancellationToken = default) =>
        _transport.PostBytesAsync<GameMedia>(
            GamePath(idOrSlug) + "/media"
                + "?kind=" + Uri.EscapeDataString(WireName(upload.Kind))
                + "&altText=" + Uri.EscapeDataString(upload.AltText)
                + "&sortOrder=" + upload.SortOrder.ToString(CultureInfo.InvariantCulture),
            upload.Content,
            // Deliberately not an image type. The server decides what these bytes are from the
            // bytes, and a client that named a type would be claiming something it cannot know
            // — and inviting a later reader to believe the claim.
            UnknownContentType,
            cancellationToken);

    public Task<GameMedia> UpdateMediaAsync(
        string mediaId, MediaChanges changes, CancellationToken cancellationToken = default) =>
        _transport.PatchAsync<GameMedia>(MediaPath(mediaId), changes, cancellationToken);

    public Task DeleteMediaAsync(string mediaId, CancellationToken cancellationToken = default) =>
        _transport.DeleteAsync(MediaPath(mediaId), cancellationToken);

    public Task<PatchNote> CreatePatchNoteAsync(
        string idOrSlug,
        CreatePatchNoteRequest request,
        CancellationToken cancellationToken = default) =>
        _transport.PostAsync<PatchNote>(
            GamePath(idOrSlug) + "/patch-notes", request, cancellationToken);

    public Task<PatchNote> UpdatePatchNoteAsync(
        string noteId, PatchNoteChanges changes, CancellationToken cancellationToken = default) =>
        _transport.PatchAsync<PatchNote>(PatchNotePath(noteId), changes, cancellationToken);

    public Task DeletePatchNoteAsync(
        string noteId, CancellationToken cancellationToken = default) =>
        _transport.DeleteAsync(PatchNotePath(noteId), cancellationToken);

    public Task DeleteBuildAsync(string buildId, CancellationToken cancellationToken = default) =>
        _transport.DeleteAsync(BuildPath(buildId), cancellationToken);

    public Task DeleteVersionAsync(
        string idOrSlug, string versionId, CancellationToken cancellationToken = default) =>
        _transport.DeleteAsync(
            GamePath(idOrSlug) + "/versions/" + Uri.EscapeDataString(versionId),
            cancellationToken);

    /// <summary>
    /// The wire spelling of a media kind: the server's enum is lower case, and
    /// <c>ToString()</c> on a C# enum is not.
    /// </summary>
    private static string WireName(MediaKind kind) =>
        kind.ToString().ToLowerInvariant();

    private static string GamePath(string idOrSlug) => "games/" + Uri.EscapeDataString(idOrSlug);

    private static string MediaPath(string mediaId) => "media/" + Uri.EscapeDataString(mediaId);

    private static string PatchNotePath(string noteId) =>
        "patch-notes/" + Uri.EscapeDataString(noteId);

    private static string BuildPath(string buildId) => "builds/" + Uri.EscapeDataString(buildId);

    private static string UploadPath(string sessionId) =>
        "uploads/" + Uri.EscapeDataString(sessionId);

    private sealed record MissingBlobsRequest(IReadOnlyList<BlobDeclaration> Blobs);

    private sealed record MissingBlobsResponse
    {
        public IReadOnlyList<string> Missing { get; init; } = [];
    }
}

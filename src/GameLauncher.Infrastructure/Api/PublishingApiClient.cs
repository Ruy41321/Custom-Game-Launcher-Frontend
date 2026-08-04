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

    private static string GamePath(string idOrSlug) => "games/" + Uri.EscapeDataString(idOrSlug);

    private static string BuildPath(string buildId) => "builds/" + Uri.EscapeDataString(buildId);

    private static string UploadPath(string sessionId) =>
        "uploads/" + Uri.EscapeDataString(sessionId);

    private sealed record MissingBlobsRequest(IReadOnlyList<BlobDeclaration> Blobs);

    private sealed record MissingBlobsResponse
    {
        public IReadOnlyList<string> Missing { get; init; } = [];
    }
}

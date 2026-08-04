using System.Security.Cryptography;
using GameLauncher.Core.Api;
using GameLauncher.Core.Models;

namespace GameLauncher.Infrastructure.Tests.Publishing;

/// <summary>
/// The server's side of the upload protocol, as far as the publisher can tell: it assigns the
/// offset, refuses a chunk aimed at the wrong one, and checks the hash before it accepts a
/// blob. Written by hand rather than substituted because the behaviour under test is a
/// conversation over several calls, not a single answer.
/// </summary>
internal sealed class FakePublishingApi : IPublishingApi
{
    private readonly Dictionary<string, MemoryStream> _sessions = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> _sessionBlob = new(StringComparer.Ordinal);
    private readonly Dictionary<string, long> _sessionSize = new(StringComparer.Ordinal);
    private int _nextSession;

    /// <summary>Content addresses the server already holds, so they are never asked for.</summary>
    public HashSet<string> AlreadyStored { get; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Blobs that arrived, by content address.</summary>
    public Dictionary<string, byte[]> Stored { get; } = new(StringComparer.OrdinalIgnoreCase);

    public List<(string Sha256, long Offset, int Length)> Chunks { get; } = [];

    public ManifestSubmission? Submitted { get; private set; }

    public GameBuild? CreatedBuild { get; private set; }

    /// <summary>
    /// Bytes a session is to start with, as a resumed upload does. Real content rather than
    /// filler, so the hash check at the end still means something.
    /// </summary>
    public Dictionary<string, byte[]> ResumeWith { get; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>How many times a chunk aimed at a given offset is refused before it is taken.</summary>
    public Dictionary<long, int> RefusalsAt { get; } = [];

    public Task<GameBuild> CreateBuildAsync(
        string idOrSlug,
        string versionId,
        CreateBuildRequest request,
        CancellationToken cancellationToken = default)
    {
        CreatedBuild = new GameBuild
        {
            Id = "b1",
            VersionId = versionId,
            Platform = request.Platform,
            Architecture = request.Architecture,
            Status = BuildStatus.Uploading,
        };

        return Task.FromResult(CreatedBuild);
    }

    public Task<IReadOnlyList<string>> MissingBlobsAsync(
        string buildId,
        IReadOnlyList<BlobDeclaration> blobs,
        CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyList<string>>(
        [
            .. blobs
                .Where(blob => !AlreadyStored.Contains(blob.Sha256))
                .Select(blob => blob.Sha256),
        ]);

    public Task<UploadSession> BeginUploadAsync(
        string buildId, BlobDeclaration blob, CancellationToken cancellationToken = default)
    {
        string id = "s" + (++_nextSession).ToString(System.Globalization.CultureInfo.InvariantCulture);
        MemoryStream received = new();

        if (ResumeWith.TryGetValue(blob.Sha256, out byte[]? already))
        {
            received.Write(already);
        }

        _sessions[id] = received;
        _sessionBlob[id] = blob.Sha256;
        _sessionSize[id] = blob.Size;

        return Task.FromResult(SessionFor(id));
    }

    public Task<UploadSession> GetUploadAsync(
        string sessionId, CancellationToken cancellationToken = default) =>
        Task.FromResult(SessionFor(sessionId));

    public Task<UploadSession> SendChunkAsync(
        string sessionId,
        long offset,
        ReadOnlyMemory<byte> chunk,
        CancellationToken cancellationToken = default)
    {
        MemoryStream received = _sessions[sessionId];

        if (RefusalsAt.TryGetValue(offset, out int refusals) && refusals > 0)
        {
            RefusalsAt[offset] = refusals - 1;
            throw new ApiException(
                ApiErrorCode.Conflict,
                $"the session is at offset {received.Length}",
                409);
        }

        if (offset != received.Length)
        {
            throw new ApiException(
                ApiErrorCode.Conflict,
                $"the session is at offset {received.Length}",
                409);
        }

        Chunks.Add((_sessionBlob[sessionId], offset, chunk.Length));
        received.Write(chunk.Span);

        if (received.Length == _sessionSize[sessionId])
        {
            byte[] bytes = received.ToArray();
            string actual = Convert.ToHexStringLower(SHA256.HashData(bytes));

            if (!string.Equals(actual, _sessionBlob[sessionId], StringComparison.OrdinalIgnoreCase))
            {
                throw new ApiException(
                    ApiErrorCode.InvalidInput, "the bytes do not hash to what was declared", 422);
            }

            Stored[_sessionBlob[sessionId]] = bytes;
        }

        return Task.FromResult(SessionFor(sessionId));
    }

    public Task AbortUploadAsync(string sessionId, CancellationToken cancellationToken = default)
    {
        _sessions.Remove(sessionId);
        return Task.CompletedTask;
    }

    public Task<GameBuild> SubmitManifestAsync(
        string buildId, ManifestSubmission manifest, CancellationToken cancellationToken = default)
    {
        Submitted = manifest;

        return Task.FromResult(new GameBuild
        {
            Id = buildId,
            Status = BuildStatus.Ready,
            Entrypoint = manifest.Entrypoint,
            LaunchArgs = manifest.LaunchArgs ?? string.Empty,
            FileCount = manifest.Files.Count,
        });
    }

    public Task<Game> CreateGameAsync(
        CreateGameRequest request, CancellationToken cancellationToken = default) =>
        Task.FromResult(new Game { Id = "g1", Slug = "orbital-drift", Title = request.Title });

    public Task<Game> UpdateGameAsync(
        string idOrSlug, GameChanges changes, CancellationToken cancellationToken = default) =>
        Task.FromResult(new Game { Id = "g1", Slug = idOrSlug });

    public Task<GameVersion> CreateVersionAsync(
        string idOrSlug,
        CreateVersionRequest request,
        CancellationToken cancellationToken = default) =>
        Task.FromResult(new GameVersion { Id = "v1", Semver = request.Semver });

    private UploadSession SessionFor(string id)
    {
        long received = _sessions[id].Length;

        return new UploadSession
        {
            Id = id,
            BuildId = "b1",
            Sha256 = _sessionBlob[id],
            SizeBytes = _sessionSize[id],
            ReceivedBytes = received,
            Complete = received == _sessionSize[id],
            Status = received == _sessionSize[id] ? "completed" : "pending",
        };
    }
}

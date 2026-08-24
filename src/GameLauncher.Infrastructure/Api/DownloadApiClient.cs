using System.Security.Cryptography;
using System.Text.Json;
using GameLauncher.Core.Api;
using GameLauncher.Core.Models;
using GameLauncher.Infrastructure.Configuration;

namespace GameLauncher.Infrastructure.Api;

/// <summary>
/// Download plans, manifests and integrity checks. Runs on the authenticated
/// <see cref="HttpClient"/> — the *plan* needs a bearer token, while the signed URLs it hands
/// back are fetched by <c>BlobFetcher</c> on a client that attaches none.
/// </summary>
public sealed class DownloadApiClient(HttpClient httpClient) : IDownloadApi
{
    private readonly ApiTransport _transport = new(httpClient);

    public async Task<BuildManifest> GetManifestAsync(
        string buildId, string expectedSha256, CancellationToken cancellationToken = default)
    {
        byte[] document = await _transport
            .GetBytesAsync(PathFor(buildId, "manifest"), cancellationToken)
            .ConfigureAwait(false);

        string actualSha256 = Convert.ToHexStringLower(SHA256.HashData(document));
        if (!string.Equals(actualSha256, expectedSha256, StringComparison.OrdinalIgnoreCase))
        {
            throw new ApiException(
                ApiErrorCode.Integrity,
                $"The manifest of build {buildId} hashes to {actualSha256}, not {expectedSha256}.");
        }

        BuildManifest? manifest;
        try
        {
            manifest = JsonSerializer.Deserialize<BuildManifest>(
                document, LauncherJsonSerializer.Compact);
        }
        catch (JsonException exception)
        {
            throw new ApiException(
                ApiErrorCode.Unknown,
                "The manifest could not be understood.",
                innerException: exception);
        }

        if (manifest is null)
        {
            throw new ApiException(ApiErrorCode.Unknown, "The manifest was empty.");
        }

        return manifest with { Sha256 = actualSha256 };
    }

    public Task<DownloadPlan> GetPlanAsync(
        string buildId,
        string? fromBuildId = null,
        CancellationToken cancellationToken = default) =>
        _transport.PostAsync<DownloadPlan>(
            PathFor(buildId, "download"), new PlanRequest(fromBuildId), cancellationToken);

    public Task<IntegrityReport> VerifyAsync(
        string buildId,
        IReadOnlyList<InstalledFile> files,
        CancellationToken cancellationToken = default) =>
        _transport.PostAsync<IntegrityReport>(
            PathFor(buildId, "verify"), new VerifyRequest(files), cancellationToken);

    private static string PathFor(string buildId, string action) =>
        "builds/" + Uri.EscapeDataString(buildId) + "/" + action;

    /// <summary>
    /// Serialises to <c>{}</c> for a first install, because the serializer drops nulls. The
    /// server reads the field as optional, which is exactly what "I have nothing yet" means.
    /// </summary>
    private sealed record PlanRequest(string? FromBuildId);

    private sealed record VerifyRequest(IReadOnlyList<InstalledFile> Files);
}

using GameLauncher.Core.Api;

namespace GameLauncher.Infrastructure.Api;

/// <summary>
/// Runs on the client that never attaches a bearer token — the fourth route to do so, beside
/// <c>/auth</c>, <c>/capabilities</c> and the crash reports, and the one with the sharpest
/// reason: the launcher that most needs an update is the one that cannot sign in.
/// </summary>
public sealed class LauncherReleaseApiClient(HttpClient httpClient) : ILauncherReleaseApi
{
    private readonly ApiTransport _transport = new(httpClient);

    public Task<LauncherReleaseResponse> GetLatestAsync(
        string channel,
        string platform,
        string arch,
        CancellationToken cancellationToken = default)
    {
        string path = "launcher/releases/latest"
            + $"?channel={Uri.EscapeDataString(channel)}"
            + $"&platform={Uri.EscapeDataString(platform)}"
            + $"&arch={Uri.EscapeDataString(arch)}";

        return _transport.GetAsync<LauncherReleaseResponse>(path, cancellationToken);
    }
}

using GameLauncher.Core.Api;
using GameLauncher.Core.Models;

namespace GameLauncher.Infrastructure.Api;

/// <summary>
/// Runs on the client that never attaches a bearer token, alongside <c>/auth</c>: the route
/// needs none, and asking for one would mean a token refresh before the launcher knows whether
/// it can talk to this server at all.
/// </summary>
public sealed class CapabilitiesApiClient(HttpClient httpClient) : ICapabilitiesApi
{
    private readonly ApiTransport _transport = new(httpClient);

    public Task<ServerCapabilities> GetAsync(CancellationToken cancellationToken = default) =>
        _transport.GetAsync<ServerCapabilities>("capabilities", cancellationToken);
}

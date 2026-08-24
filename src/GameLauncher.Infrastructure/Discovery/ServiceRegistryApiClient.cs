using System.Net;
using GameLauncher.Core.Discovery;
using Microsoft.Extensions.Logging;

namespace GameLauncher.Infrastructure.Discovery;

/// <summary>
/// The registry client: one GET, on absolute URLs, with no token and no base address.
///
/// A sixth <see cref="HttpClient"/> and deliberately not one of the five that exist. It is the
/// only one that talks to a host which is <b>not</b> the API — it is what says where the API
/// is — so it must carry no bearer token (D20's reasoning, from the other direction) and it
/// cannot take the base address every API client is configured with, because that address is
/// the thing it is being asked about.
/// </summary>
public sealed class ServiceRegistryApiClient : IServiceRegistryApi
{
    private readonly HttpClient _client;
    private readonly ILogger<ServiceRegistryApiClient> _logger;

    public ServiceRegistryApiClient(HttpClient client, ILogger<ServiceRegistryApiClient> logger)
    {
        _client = client;
        _logger = logger;
    }

    public async Task<string?> GetSignedEndpointAsync(
        Uri registryUrl,
        string serviceKey,
        string environment,
        CancellationToken cancellationToken = default)
    {
        Uri request = new(
            registryUrl,
            $"v1/services/{Uri.EscapeDataString(serviceKey)}" +
            $"?environment={Uri.EscapeDataString(environment)}");

        try
        {
            using HttpResponseMessage response = await _client
                .GetAsync(request, cancellationToken)
                .ConfigureAwait(false);

            if (!response.IsSuccessStatusCode)
            {
                // 404 is the ordinary answer for a service the registry does not carry, or
                // carries switched off. It is a fact about the registry, not a failure of the
                // launcher, and the caller's response to it is the same as to anything else.
                LogRefusal(response.StatusCode, serviceKey);
                return null;
            }

            return await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (HttpRequestException exception)
        {
            _logger.LogDebug(exception, "The service registry could not be reached.");
            return null;
        }
        catch (TaskCanceledException exception) when (!cancellationToken.IsCancellationRequested)
        {
            // The client's own timeout, which arrives as a cancellation nobody asked for.
            _logger.LogDebug(exception, "The service registry did not answer in time.");
            return null;
        }
    }

    private void LogRefusal(HttpStatusCode status, string serviceKey)
    {
        if (_logger.IsEnabled(LogLevel.Debug))
        {
            _logger.LogDebug(
                "The service registry answered {Status} for {ServiceKey}.", (int)status, serviceKey);
        }
    }
}

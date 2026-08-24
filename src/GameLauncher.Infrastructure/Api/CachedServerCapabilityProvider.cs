using GameLauncher.Core.Api;
using GameLauncher.Core.Models;
using Microsoft.Extensions.Logging;

namespace GameLauncher.Infrastructure.Api;

/// <summary>
/// Asks the server once, keeps the answer, and never lets the question fail a publish.
///
/// Cached for <see cref="Lifetime"/> rather than for the process: an operator who reconfigures
/// a limit and restarts the server should not have to make everybody restart their launcher.
/// A failure is *not* cached — a server that was down while the launcher started is asked
/// again the next time somebody publishes.
/// </summary>
public sealed class CachedServerCapabilityProvider(
    ICapabilitiesApi api,
    TimeProvider time,
    ILogger<CachedServerCapabilityProvider> logger) : IServerCapabilityProvider, IDisposable
{
    public static readonly TimeSpan Lifetime = TimeSpan.FromMinutes(15);

    private readonly SemaphoreSlim _gate = new(1, 1);

    private ServerCapabilities? _cached;

    private DateTimeOffset _fetchedAt;

    public async Task<ServerCapabilities> GetAsync(CancellationToken cancellationToken = default)
    {
        if (Fresh() is { } known)
        {
            return known;
        }

        // One request at a time: publishing asks the packager and the publisher in quick
        // succession, and two identical round trips to learn one document is one too many.
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (Fresh() is { } raced)
            {
                return raced;
            }

            ServerCapabilities capabilities = await api
                .GetAsync(cancellationToken)
                .ConfigureAwait(false);

            _cached = capabilities;
            _fetchedAt = time.GetUtcNow();
            return capabilities;
        }
        catch (ApiException exception)
        {
            // Including a 404 from a server older than the route. Refusing to publish because
            // a document *about* publishing could not be read would be worse than the guessing
            // this replaced.
            logger.LogDebug(
                exception,
                "The server did not describe its limits; falling back to the built-in defaults");

            return ServerCapabilities.Fallback;
        }
        finally
        {
            _gate.Release();
        }
    }

    public void Dispose() => _gate.Dispose();

    private ServerCapabilities? Fresh() =>
        _cached is { } cached && time.GetUtcNow() - _fetchedAt < Lifetime ? cached : null;
}

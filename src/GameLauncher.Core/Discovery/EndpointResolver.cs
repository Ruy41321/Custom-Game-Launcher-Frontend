using GameLauncher.Core.Configuration;
using Microsoft.Extensions.Logging;

namespace GameLauncher.Core.Discovery;

/// <summary>
/// Decides which address this run uses, from a stored claim, from the registry, or from the
/// shipped configuration — in that order of preference on the start-up path.
///
/// It is in Core rather than Infrastructure because it performs no I/O of its own: the network
/// is <see cref="IServiceRegistryApi"/> and the disk is <see cref="IEndpointCache"/>, and what
/// is left is the policy, which is the part worth testing.
/// </summary>
public sealed class EndpointResolver : IEndpointResolver
{
    /// <summary>
    /// How long a launcher with nothing cached waits for the registry before starting anyway.
    ///
    /// Short on purpose. This is the only case that blocks a window from opening, and it
    /// happens once per machine: every later run has a stored claim and asks the network
    /// nothing. A registry that has not answered in this long is one whose answer is not worth
    /// the wait, because the shipped address is very probably still correct — it is what the
    /// launcher was built with.
    /// </summary>
    public static readonly TimeSpan FirstRunTimeout = TimeSpan.FromSeconds(3);

    private readonly IServiceRegistryApi _registry;
    private readonly IEndpointCache _cache;
    private readonly ILogger<EndpointResolver> _logger;
    private readonly string _publicKeyBase64;

    public EndpointResolver(
        IServiceRegistryApi registry,
        IEndpointCache cache,
        ILogger<EndpointResolver> logger)
        : this(registry, cache, logger, ServiceRegistryKey.PublicKeyBase64)
    {
    }

    /// <summary>Takes the key explicitly, which is how a test signs anything at all.</summary>
    public EndpointResolver(
        IServiceRegistryApi registry,
        IEndpointCache cache,
        ILogger<EndpointResolver> logger,
        string publicKeyBase64)
    {
        _registry = registry;
        _cache = cache;
        _logger = logger;
        _publicKeyBase64 = publicKeyBase64;
    }

    public async Task<ResolvedEndpoint> ResolveAsync(
        LauncherConfiguration configuration, CancellationToken cancellationToken = default)
    {
        ResolvedEndpoint shipped = new(configuration.ApiBaseUrl, EndpointSource.ShippedConfiguration, null);

        if (!IsEnabled(configuration, out Uri? registryUrl))
        {
            return shipped;
        }

        ServiceRegistryConfiguration settings = configuration.ServiceRegistry;

        EndpointClaim? cached = await ReadCachedAsync(settings, cancellationToken).ConfigureAwait(false);
        if (cached is not null)
        {
            // Used as it stands, with no round trip: the window opens at once, and
            // RefreshAsync picks up a move in time for the next start.
            return new ResolvedEndpoint(cached.BaseUrl, EndpointSource.Cache, cached.IssuedAt);
        }

        using CancellationTokenSource timeout = CancellationTokenSource
            .CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(FirstRunTimeout);

        EndpointClaim? claim = await FetchAsync(settings, registryUrl, timeout.Token)
            .ConfigureAwait(false);

        return claim is null
            ? shipped
            : new ResolvedEndpoint(claim.BaseUrl, EndpointSource.Registry, claim.IssuedAt);
    }

    public async Task<EndpointClaim?> RefreshAsync(
        LauncherConfiguration configuration, CancellationToken cancellationToken = default)
    {
        if (!IsEnabled(configuration, out Uri? registryUrl))
        {
            return null;
        }

        return await FetchAsync(configuration.ServiceRegistry, registryUrl, cancellationToken)
            .ConfigureAwait(false);
    }

    /// <summary>
    /// A registry is consulted only when both halves are present: an address to ask, and a key
    /// to check the answer with. A build with no key would otherwise believe whoever answered.
    /// </summary>
    private bool IsEnabled(LauncherConfiguration configuration, out Uri registryUrl)
    {
        registryUrl = null!;

        if (!configuration.ServiceRegistry.IsConfigured)
        {
            return false;
        }

        if (string.IsNullOrWhiteSpace(_publicKeyBase64))
        {
            _logger.LogInformation(
                "A service registry is configured but this build carries no verification key; " +
                "using the endpoint from the configuration file.");
            return false;
        }

        if (!Uri.TryCreate(configuration.ServiceRegistry.Url, UriKind.Absolute, out Uri? parsed))
        {
            return false;
        }

        registryUrl = parsed;
        return true;
    }

    private async Task<EndpointClaim?> ReadCachedAsync(
        ServiceRegistryConfiguration settings, CancellationToken cancellationToken)
    {
        string? stored = await _cache
            .ReadAsync(settings.ServiceKey, settings.Environment, cancellationToken)
            .ConfigureAwait(false);

        // Re-verified rather than trusted: the file is writable by anything running as this
        // user, and a cache believed on sight would be the way around the signature.
        EndpointClaim? claim = SignedEndpointReader.Read(
            stored, settings.ServiceKey, settings.Environment, _publicKeyBase64);

        if (stored is not null && claim is null)
        {
            _logger.LogWarning(
                "The stored endpoint for {ServiceKey} did not verify and was ignored.",
                settings.ServiceKey);
        }

        return claim;
    }

    private async Task<EndpointClaim?> FetchAsync(
        ServiceRegistryConfiguration settings, Uri registryUrl, CancellationToken cancellationToken)
    {
        string? body;
        try
        {
            body = await _registry
                .GetSignedEndpointAsync(
                    registryUrl, settings.ServiceKey, settings.Environment, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // The deadline above, or a launcher shutting down. Neither is worth a warning:
            // the fallback is the address this build shipped with.
            _logger.LogDebug("The service registry did not answer in time.");
            return null;
        }

        EndpointClaim? claim = SignedEndpointReader.Read(
            body, settings.ServiceKey, settings.Environment, _publicKeyBase64);

        if (claim is null)
        {
            if (body is not null)
            {
                _logger.LogWarning(
                    "The service registry answered for {ServiceKey} with something this " +
                    "launcher could not verify; the answer was discarded.",
                    settings.ServiceKey);
            }

            return null;
        }

        await StoreIfNewerAsync(settings, body!, claim, cancellationToken).ConfigureAwait(false);
        return claim;
    }

    /// <summary>
    /// Stores the envelope unless what is already there is newer.
    ///
    /// The comparison is what stops a replay: somebody able to answer for the registry can
    /// hand back a genuine, correctly signed answer from before the address moved, and
    /// overwriting a newer stored claim with it would move every client back.
    /// </summary>
    private async Task StoreIfNewerAsync(
        ServiceRegistryConfiguration settings,
        string envelope,
        EndpointClaim claim,
        CancellationToken cancellationToken)
    {
        EndpointClaim? stored = await ReadCachedAsync(settings, cancellationToken).ConfigureAwait(false);
        if (stored is not null && stored.IssuedAt > claim.IssuedAt)
        {
            _logger.LogWarning(
                "The service registry answered for {ServiceKey} with a claim older than the " +
                "stored one; it was not kept.",
                settings.ServiceKey);
            return;
        }

        await _cache
            .WriteAsync(settings.ServiceKey, settings.Environment, envelope, cancellationToken)
            .ConfigureAwait(false);
    }
}

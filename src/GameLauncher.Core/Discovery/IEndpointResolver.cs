using GameLauncher.Core.Configuration;

namespace GameLauncher.Core.Discovery;

/// <summary>Where a resolved address came from. Log-and-diagnose material, not a decision.</summary>
public enum EndpointSource
{
    /// <summary>No registry is configured, or nothing better was available.</summary>
    ShippedConfiguration,

    /// <summary>A verified claim stored by an earlier run.</summary>
    Cache,

    /// <summary>A verified claim the registry answered just now.</summary>
    Registry,
}

/// <summary>The address the launcher will use for this run, and where it came from.</summary>
public sealed record ResolvedEndpoint(string BaseUrl, EndpointSource Source, DateTimeOffset? IssuedAt);

/// <summary>
/// Answers "where is the API right now?" before any HTTP client is built.
///
/// <see cref="ResolveAsync"/> is on the start-up path and is therefore biased towards
/// answering <i>fast</i>: a stored claim is used as it is, and the network is only consulted
/// when there is nothing stored at all. <see cref="RefreshAsync"/> is the other half — it
/// always asks, and it runs after the window is up, so a moved backend is picked up for the
/// next start rather than paid for at every one.
/// </summary>
public interface IEndpointResolver
{
    /// <summary>
    /// Resolves the address to use now. Never throws: every failure falls back, ultimately to
    /// <see cref="LauncherConfiguration.ApiBaseUrl"/>, because a launcher that will not start
    /// because a registry is down is worse than one pointed at a stale address.
    /// </summary>
    Task<ResolvedEndpoint> ResolveAsync(
        LauncherConfiguration configuration, CancellationToken cancellationToken = default);

    /// <summary>
    /// Asks the registry and stores a newer verified claim. Never throws, and returns what it
    /// stored, or null when it stored nothing.
    /// </summary>
    Task<EndpointClaim?> RefreshAsync(
        LauncherConfiguration configuration, CancellationToken cancellationToken = default);
}

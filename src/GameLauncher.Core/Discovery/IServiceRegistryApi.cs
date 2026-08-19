namespace GameLauncher.Core.Discovery;

/// <summary>
/// The one call this launcher makes to the registry.
///
/// It returns the response <b>body as text</b> rather than a parsed object, because the bytes
/// are what the signature covers: a client that handed back a deserialised envelope would have
/// re-encoded the payload before anything could check it.
/// </summary>
public interface IServiceRegistryApi
{
    /// <summary>
    /// Fetches the signed answer for one service, or null when the registry could not be
    /// reached or refused. Never throws: the caller's fallback is the same for every failure.
    /// </summary>
    Task<string?> GetSignedEndpointAsync(
        Uri registryUrl,
        string serviceKey,
        string environment,
        CancellationToken cancellationToken = default);
}

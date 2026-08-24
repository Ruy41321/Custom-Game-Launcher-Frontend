namespace GameLauncher.Core.Discovery;

/// <summary>
/// The last verified answer, kept between runs so that a launcher which has reached the
/// registry once never has to wait for it again.
///
/// It stores the <b>signed envelope</b> rather than the address, and reading re-verifies it.
/// That is the whole point of the type: a file under the user's data directory is writable by
/// anything running as that user, and a cache trusted more than the network would be the way
/// around the signature rather than a use of it.
/// </summary>
public interface IEndpointCache
{
    /// <summary>The stored envelope, or null when there is none. Never throws.</summary>
    Task<string?> ReadAsync(
        string serviceKey, string environment, CancellationToken cancellationToken = default);

    /// <summary>Stores an envelope. Never throws: a cache that cannot be written costs a lookup.</summary>
    Task WriteAsync(
        string serviceKey, string environment, string envelope,
        CancellationToken cancellationToken = default);
}

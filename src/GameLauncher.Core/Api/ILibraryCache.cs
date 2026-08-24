using GameLauncher.Core.Models;

namespace GameLauncher.Core.Api;

/// <summary>
/// The account's library as it was last seen, kept on this disk so that the page has
/// something true to show when the server cannot be asked.
///
/// It exists because "what is installed" is not an answer to "what do I own". A launcher that
/// fell back to the install rows showed an empty library to anybody who had not downloaded
/// anything yet, and quietly hid every game an account owns but has not installed on this
/// machine — which is most of them, for most people.
///
/// Keyed by account, because two people share a machine and neither is owed the other's list.
/// Nothing here is authoritative: it is a copy of an answer the server gave, replaced by the
/// next answer it gives, and never consulted while the server is reachable.
/// </summary>
public interface ILibraryCache
{
    /// <summary>
    /// The last stored list, or empty when there is none — a cache miss is not a failure, and
    /// neither is a file this launcher can no longer read.
    /// </summary>
    Task<IReadOnlyList<Game>> ReadAsync(string accountId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Records the server's answer. Never throws: a library that could not be written to disk
    /// is a library that will be fetched again, not a page that failed to load.
    /// </summary>
    Task WriteAsync(
        string accountId, IReadOnlyList<Game> games, CancellationToken cancellationToken = default);

    /// <summary>Forgets one account's list. Used when the account itself is being erased.</summary>
    Task ClearAsync(string accountId, CancellationToken cancellationToken = default);
}

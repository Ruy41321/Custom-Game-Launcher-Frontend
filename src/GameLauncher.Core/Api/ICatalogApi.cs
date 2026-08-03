using GameLauncher.Core.Models;

namespace GameLauncher.Core.Api;

/// <summary>
/// Explore and game detail. Every route here needs a bearer token: the launcher is an online
/// client for everything except starting an installed game, so there is no anonymous catalog.
/// </summary>
public interface ICatalogApi
{
    Task<PagedResult<Game>> ExploreAsync(
        GameQuery query, CancellationToken cancellationToken = default);

    /// <summary>
    /// Accepts an id or a slug. A game the account may not see is reported as
    /// <see cref="ApiErrorCode.NotFound"/>, never Forbidden — the server refuses to confirm
    /// that an unannounced title exists, and the client must not present it as if it did.
    /// </summary>
    Task<GameDetail> GetGameAsync(string idOrSlug, CancellationToken cancellationToken = default);

    /// <summary>The caller's own games, drafts included. Needs <c>game.publish</c>.</summary>
    Task<PagedResult<Game>> GetMyGamesAsync(
        GameQuery query, CancellationToken cancellationToken = default);
}

/// <summary>
/// The account's library. This records ownership only: what is *installed* is per machine and
/// belongs to the client's own database, which is what has to survive a reinstall.
/// </summary>
public interface ILibraryApi
{
    Task<IReadOnlyList<Game>> GetLibraryAsync(CancellationToken cancellationToken = default);

    /// <summary>Idempotent: adding a game the account already has is not an error.</summary>
    Task AddAsync(string idOrSlug, CancellationToken cancellationToken = default);

    /// <summary>
    /// Removing a game that is not in the library is a <see cref="ApiErrorCode.NotFound"/>,
    /// because there the client's model and the server's genuinely disagree.
    /// </summary>
    Task RemoveAsync(string gameId, CancellationToken cancellationToken = default);
}

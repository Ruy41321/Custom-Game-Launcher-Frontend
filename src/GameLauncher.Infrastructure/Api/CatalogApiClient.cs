using System.Globalization;
using GameLauncher.Core.Api;
using GameLauncher.Core.Models;

namespace GameLauncher.Infrastructure.Api;

/// <summary>
/// Explore and game detail. Runs on the authenticated <see cref="HttpClient"/>: every catalog
/// route requires a bearer token, because the launcher is an online client for everything
/// except starting an already installed game.
/// </summary>
public sealed class CatalogApiClient(HttpClient httpClient) : ICatalogApi
{
    private readonly ApiTransport _transport = new(httpClient);

    public Task<PagedResult<Game>> ExploreAsync(
        GameQuery query, CancellationToken cancellationToken = default) =>
        _transport.GetAsync<PagedResult<Game>>(PathFor("games", query), cancellationToken);

    public Task<GameDetail> GetGameAsync(
        string idOrSlug, CancellationToken cancellationToken = default) =>
        _transport.GetAsync<GameDetail>(
            "games/" + Uri.EscapeDataString(idOrSlug), cancellationToken);

    public Task<PagedResult<Game>> GetMyGamesAsync(
        GameQuery query, CancellationToken cancellationToken = default) =>
        _transport.GetAsync<PagedResult<Game>>(PathFor("me/games", query), cancellationToken);

    public Task<PagedResult<PatchNote>> GetPatchNotesAsync(
        string idOrSlug,
        int page = 1,
        int pageSize = ICatalogApi.DefaultPatchNotePageSize,
        CancellationToken cancellationToken = default) =>
        _transport.GetAsync<PagedResult<PatchNote>>(
            string.Create(
                CultureInfo.InvariantCulture,
                $"games/{Uri.EscapeDataString(idOrSlug)}/patch-notes?page={Math.Max(page, 1)}&pageSize={Math.Max(pageSize, 1)}"),
            cancellationToken);

    private static string PathFor(string resource, GameQuery query)
    {
        string parameters = query.ToQueryString();
        return parameters.Length == 0 ? resource : resource + "?" + parameters;
    }
}

/// <summary>The account's library, which records ownership and nothing about installs.</summary>
public sealed class LibraryApiClient(HttpClient httpClient) : ILibraryApi
{
    private readonly ApiTransport _transport = new(httpClient);

    public async Task<IReadOnlyList<Game>> GetLibraryAsync(
        CancellationToken cancellationToken = default)
    {
        PagedResult<Game> page = await _transport
            .GetAsync<PagedResult<Game>>("library", cancellationToken)
            .ConfigureAwait(false);

        return page.Items;
    }

    public Task AddAsync(string idOrSlug, CancellationToken cancellationToken = default) =>
        _transport.PutAsync("library/" + Uri.EscapeDataString(idOrSlug), cancellationToken);

    public Task RemoveAsync(string gameId, CancellationToken cancellationToken = default) =>
        _transport.DeleteAsync("library/" + Uri.EscapeDataString(gameId), cancellationToken);
}

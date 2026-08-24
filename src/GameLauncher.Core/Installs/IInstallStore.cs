namespace GameLauncher.Core.Installs;

/// <summary>
/// What this machine has on disk. Transactional and crash-safe on purpose: a plain JSON file
/// rewritten during an update is corrupt if the process dies mid-write, and the one moment the
/// launcher most needs to know what it was doing is the moment after it died doing it.
/// </summary>
public interface IInstallStore
{
    Task<IReadOnlyList<InstalledGame>> GetAllAsync(CancellationToken cancellationToken = default);

    /// <summary>Null when the game is not installed on this machine.</summary>
    Task<InstalledGame?> FindAsync(string gameId, CancellationToken cancellationToken = default);

    /// <summary>Inserts or replaces the row for <see cref="InstalledGame.GameId"/>.</summary>
    Task SaveAsync(InstalledGame install, CancellationToken cancellationToken = default);

    /// <summary>Removing a game that is not there is not an error — the end state is the same.</summary>
    Task RemoveAsync(string gameId, CancellationToken cancellationToken = default);
}

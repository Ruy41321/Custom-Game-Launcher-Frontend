namespace GameLauncher.Core.Launching;

/// <summary>A game that this launcher started and has not yet seen exit.</summary>
public sealed record RunningGame(string GameId, int ProcessId, DateTimeOffset StartedAt);

public sealed class GameExitedEventArgs(string gameId, int exitCode, TimeSpan played) : EventArgs
{
    public string GameId { get; } = gameId;

    public int ExitCode { get; } = exitCode;

    public TimeSpan Played { get; } = played;
}

/// <summary>
/// Starts installed games. The only part of the launcher that has to work with no server at
/// all: everything it needs is the local install row and the files on disk.
/// </summary>
public interface IGameLauncher
{
    /// <summary>
    /// Starts the game and records that it was played. Refuses with a
    /// <see cref="GameLaunchException"/> rather than failing silently — a Play button that
    /// does nothing is the worst outcome available here.
    /// </summary>
    Task<RunningGame> LaunchAsync(string gameId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Whether this launcher started the game and has not seen it exit. A game started outside
    /// the launcher is invisible to it, which is honest: the launcher can only report what it
    /// is in a position to know.
    /// </summary>
    bool IsRunning(string gameId);

    IReadOnlyList<RunningGame> Running { get; }

    /// <summary>
    /// Raised on a thread that is not the UI's. A view model subscribing to it has to marshal.
    /// </summary>
    event EventHandler<GameExitedEventArgs>? GameExited;
}

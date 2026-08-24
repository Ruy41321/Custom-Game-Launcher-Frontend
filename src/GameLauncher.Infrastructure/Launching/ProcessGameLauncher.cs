using System.Collections.Concurrent;
using System.ComponentModel;
using System.Diagnostics;
using GameLauncher.Core.Installs;
using GameLauncher.Core.Launching;
using Microsoft.Extensions.Logging;

namespace GameLauncher.Infrastructure.Launching;

/// <summary>
/// Starts a game as a child process. Thin on purpose: every decision about what to run lives
/// in <see cref="LaunchPlanner"/>, where it can be tested without starting anything.
///
/// The game is a child rather than a detached process so the launcher can tell whether it is
/// still running and stop offering to start it twice. It is deliberately **not** killed when
/// the launcher closes: a player who quits the launcher has not asked to quit the game.
/// </summary>
public sealed class ProcessGameLauncher(
    IInstallStore installStore,
    TimeProvider time,
    ILogger<ProcessGameLauncher> logger) : IGameLauncher, IDisposable
{
    private readonly ConcurrentDictionary<string, RunningProcess> _running =
        new(StringComparer.Ordinal);

    public event EventHandler<GameExitedEventArgs>? GameExited;

    public IReadOnlyList<RunningGame> Running =>
        [.. _running.Values.Select(entry => entry.Game)];

    public bool IsRunning(string gameId) => _running.ContainsKey(gameId);

    public async Task<RunningGame> LaunchAsync(
        string gameId, CancellationToken cancellationToken = default)
    {
        if (IsRunning(gameId))
        {
            throw new GameLaunchException(
                LaunchFailure.AlreadyRunning, "That game is already running.");
        }

        InstalledGame? install = await installStore
            .FindAsync(gameId, cancellationToken).ConfigureAwait(false);

        LaunchPlan plan = LaunchPlanner.PlanFor(install, install?.LaunchOptions, File.Exists);

        MakeExecutable(plan.FileName);

        Process process = new()
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = plan.FileName,
                Arguments = plan.Arguments,
                WorkingDirectory = plan.WorkingDirectory,

                // The launcher decides the working directory and wants to hear about the exit,
                // and the shell would take both away.
                UseShellExecute = false,
            },
            EnableRaisingEvents = true,
        };

        DateTimeOffset startedAt = time.GetUtcNow();

        try
        {
            if (!process.Start())
            {
                throw new GameLaunchException(
                    LaunchFailure.StartFailed, "The game could not be started.");
            }
        }
        catch (Exception exception) when (
            exception is Win32Exception or InvalidOperationException or PlatformNotSupportedException)
        {
            process.Dispose();
            throw new GameLaunchException(
                LaunchFailure.StartFailed, "The game could not be started.", exception);
        }

        RunningGame running = new(gameId, process.Id, startedAt);
        RunningProcess entry = new(running, process);
        _running[gameId] = entry;

        process.Exited += (_, _) => OnExited(entry);

        // Between Start and the handler being attached the process may already have exited,
        // and Exited would then never fire for it.
        if (process.HasExited)
        {
            OnExited(entry);
        }

        await installStore.SaveAsync(
            install! with { LastPlayedAt = startedAt, UpdatedAt = startedAt },
            cancellationToken).ConfigureAwait(false);

        logger.LogInformation(
            "Started {Game} as process {Pid}", install!.GameSlug, running.ProcessId);

        return running;
    }

    public void Dispose()
    {
        foreach (RunningProcess entry in _running.Values)
        {
            entry.Process.Dispose();
        }

        _running.Clear();
    }

    /// <summary>
    /// A build that forgot to mark its entrypoint executable is unplayable on Unix for a
    /// reason no player can act on. The entrypoint is by definition the thing meant to be run.
    /// </summary>
    private void MakeExecutable(string path)
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        try
        {
            UnixFileMode mode = File.GetUnixFileMode(path);
            if (!mode.HasFlag(UnixFileMode.UserExecute))
            {
                File.SetUnixFileMode(path, mode | UnixFileMode.UserExecute);
            }
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException)
        {
            logger.LogWarning(exception, "Could not mark {Path} executable", path);
        }
    }

    private void OnExited(RunningProcess entry)
    {
        if (!_running.TryRemove(entry.Game.GameId, out _))
        {
            // Already reported: Exited and the HasExited check can both reach here.
            return;
        }

        int exitCode;
        try
        {
            exitCode = entry.Process.ExitCode;
        }
        catch (InvalidOperationException)
        {
            exitCode = 0;
        }

        TimeSpan played = time.GetUtcNow() - entry.Game.StartedAt;
        entry.Process.Dispose();

        logger.LogInformation(
            "{Game} exited with {Code} after {Seconds:0}s",
            entry.Game.GameId, exitCode, played.TotalSeconds);

        GameExited?.Invoke(this, new GameExitedEventArgs(entry.Game.GameId, exitCode, played));
    }

    private sealed record RunningProcess(RunningGame Game, Process Process);
}

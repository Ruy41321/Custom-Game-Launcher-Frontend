using GameLauncher.Core.Api;
using GameLauncher.Core.Installs;
using GameLauncher.Core.Platform;

namespace GameLauncher.Core.Launching;

/// <summary>Why a game could not be started. Each one is a different sentence to the user.</summary>
public enum LaunchFailure
{
    /// <summary>Nothing on this machine for that game.</summary>
    NotInstalled,

    /// <summary>An install that is damaged or half applied. Reinstalling repairs it.</summary>
    NotPlayable,

    /// <summary>The install is recorded as complete but the executable is not there.</summary>
    EntrypointMissing,

    /// <summary>This launcher already started it and has not seen it exit.</summary>
    AlreadyRunning,

    /// <summary>The operating system refused to start the process.</summary>
    StartFailed,
}

public sealed class GameLaunchException(LaunchFailure reason, string message, Exception? inner = null)
    : Exception(message, inner)
{
    public LaunchFailure Reason { get; } = reason;
}

/// <summary>
/// Exactly what to hand the operating system. Separated from the code that starts a process so
/// the decisions — which executable, which arguments, which directory, and when to refuse —
/// are testable without starting anything.
/// </summary>
public sealed record LaunchPlan
{
    public required string FileName { get; init; }

    /// <summary>
    /// A command line, not a list. The publisher wrote a string in the manifest and the user
    /// writes a string in the options; re-tokenising them here would be a second argument
    /// parser to get wrong, and it would disagree with the one the game itself uses.
    /// </summary>
    public required string Arguments { get; init; }

    /// <summary>
    /// The install root. Games resolve their assets relative to the working directory, so
    /// starting one from wherever the launcher happens to live is how a game that works when
    /// double-clicked fails when launched.
    /// </summary>
    public required string WorkingDirectory { get; init; }
}

public static class LaunchPlanner
{
    /// <summary>
    /// Works out how to start <paramref name="install"/>, or refuses with the reason.
    /// <paramref name="fileExists"/> is taken as a parameter so the rules can be exercised
    /// without a disk.
    /// </summary>
    public static LaunchPlan PlanFor(
        InstalledGame? install,
        string? extraArguments,
        Func<string, bool> fileExists)
    {
        if (install is null)
        {
            throw new GameLaunchException(
                LaunchFailure.NotInstalled, "The game is not installed on this machine.");
        }

        if (install.State != InstallState.Installed)
        {
            throw new GameLaunchException(
                LaunchFailure.NotPlayable,
                $"{install.GameTitle} is recorded as {install.State} and cannot be started.");
        }

        // The entrypoint is a manifest path like any other, and it decides what gets executed
        // rather than merely where a byte lands — so it gets the same containment check.
        string executable;
        try
        {
            executable = PathSafety.ResolveInside(install.InstallDirectory, install.Entrypoint);
        }
        catch (ApiException exception)
        {
            throw new GameLaunchException(
                LaunchFailure.EntrypointMissing, exception.Message, exception);
        }

        if (!fileExists(executable))
        {
            throw new GameLaunchException(
                LaunchFailure.EntrypointMissing,
                $"{install.Entrypoint} is missing from the installation.");
        }

        return new LaunchPlan
        {
            FileName = executable,
            Arguments = Join(install.LaunchArgs, extraArguments),
            WorkingDirectory = install.InstallDirectory,
        };
    }

    /// <summary>
    /// The publisher's arguments first, the user's after, so a user can override a switch the
    /// build sets: nearly every command line parser lets the last occurrence win.
    /// </summary>
    private static string Join(string buildArguments, string? userArguments)
    {
        string build = buildArguments.Trim();
        string user = (userArguments ?? string.Empty).Trim();

        return (build.Length, user.Length) switch
        {
            (0, 0) => string.Empty,
            (0, _) => user,
            (_, 0) => build,
            _ => build + " " + user,
        };
    }
}

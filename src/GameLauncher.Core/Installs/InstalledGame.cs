using GameLauncher.Core.Models;

namespace GameLauncher.Core.Installs;

/// <summary>What the launcher believes the install directory currently holds.</summary>
public enum InstallState
{
    /// <summary>Every file of the build is in place, as far as the last operation knows.</summary>
    Installed,

    /// <summary>
    /// An install or update is applying. A row left in this state by a crash is the signal
    /// that the directory is a mixture of two builds and must be repaired before it is run.
    /// </summary>
    Applying,

    /// <summary>An integrity check found the install is not the build it claims to be.</summary>
    Broken,
}

/// <summary>
/// One game installed on this machine. The account's library says what is *owned* and lives on
/// the server; this says what is on this disk, which is exactly the part that must survive a
/// reinstall of the launcher and cannot be re-derived from anywhere else.
///
/// A game is installed at most once per machine — one directory, one build — so the game id is
/// the identity here.
/// </summary>
public sealed record InstalledGame
{
    public string GameId { get; init; } = string.Empty;

    /// <summary>Kept so the library can be listed and launched with no server to ask.</summary>
    public string GameSlug { get; init; } = string.Empty;

    public string GameTitle { get; init; } = string.Empty;

    public string BuildId { get; init; } = string.Empty;

    public string VersionId { get; init; } = string.Empty;

    public string VersionSemver { get; init; } = string.Empty;

    public GamePlatform Platform { get; init; }

    public BuildArchitecture Architecture { get; init; }

    /// <summary>Absolute path of the directory the build was applied into.</summary>
    public string InstallDirectory { get; init; } = string.Empty;

    /// <summary>Executable, relative to <see cref="InstallDirectory"/>, with <c>/</c> separators.</summary>
    public string Entrypoint { get; init; } = string.Empty;

    public string LaunchArgs { get; init; } = string.Empty;

    /// <summary>The manifest this install was applied from — what an update comes *from*.</summary>
    public string ManifestSha256 { get; init; } = string.Empty;

    public long SizeBytes { get; init; }

    public int FileCount { get; init; }

    public InstallState State { get; init; }

    public DateTimeOffset InstalledAt { get; init; }

    /// <summary>Last time the row changed: an update, a repair, a state transition.</summary>
    public DateTimeOffset UpdatedAt { get; init; }

    /// <summary>Null until the install has been checked against the manifest at least once.</summary>
    public DateTimeOffset? LastVerifiedAt { get; init; }

    public DateTimeOffset? LastPlayedAt { get; init; }

    /// <summary>The executable's location on this operating system's terms.</summary>
    public string EntrypointPath => System.IO.Path.Combine(
        InstallDirectory, Entrypoint.Replace('/', System.IO.Path.DirectorySeparatorChar));

    /// <summary>Whether this install already is the given build.</summary>
    public bool Is(string buildId) =>
        State == InstallState.Installed && string.Equals(BuildId, buildId, StringComparison.Ordinal);
}

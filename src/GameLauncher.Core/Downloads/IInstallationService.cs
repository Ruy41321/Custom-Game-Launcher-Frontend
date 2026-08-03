using GameLauncher.Core.Installs;
using GameLauncher.Core.Models;

namespace GameLauncher.Core.Downloads;

/// <summary>Which build of which game to put on this machine, and where.</summary>
public sealed record InstallRequest
{
    public required Game Game { get; init; }

    public required GameVersion Version { get; init; }

    public required GameBuild Build { get; init; }

    /// <summary>Null installs under the launcher's default root, in a directory of its own.</summary>
    public string? InstallDirectory { get; init; }
}

/// <summary>What an install or update turned out to cost.</summary>
public sealed record InstallResult
{
    public required InstalledGame Install { get; init; }

    /// <summary>Bytes that actually travelled: zero when everything was already on disk.</summary>
    public long DownloadedBytes { get; init; }

    public DownloadKind Kind { get; init; }

    /// <summary>False for a first install, true when this replaced an earlier build.</summary>
    public bool WasUpdate { get; init; }
}

public sealed record UninstallResult(string GameId, long FreedBytes);

/// <summary>
/// Installing, updating, repairing and removing a game. Everything that writes to the install
/// directory goes through here, so the rule that a game is never presented as installed until
/// every one of its files is verified in place has one place to hold.
/// </summary>
public interface IInstallationService
{
    /// <summary>
    /// Brings <see cref="InstallRequest.Build"/> onto this machine, as a first install or as an
    /// update from whatever is installed. Safe to call on a game that is already at that build:
    /// the server plans nothing and the call is a round trip.
    /// </summary>
    Task<InstallResult> InstallAsync(
        InstallRequest request,
        IProgress<DownloadProgress>? progress = null,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Hashes what is on disk and asks the server to compare it with the manifest. An install
    /// the server calls broken is recorded as <see cref="InstallState.Broken"/>, which is what
    /// makes the next install a full download rather than a delta from a build that is not
    /// really there.
    /// </summary>
    Task<IntegrityReport> VerifyAsync(string gameId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Deletes the install directory and the row, and reports what that gave back. Removing a
    /// game that is not installed reports zero rather than failing.
    /// </summary>
    Task<UninstallResult> UninstallAsync(
        string gameId, CancellationToken cancellationToken = default);
}

/// <summary>
/// Raised by the space check, before anything has been written. Carrying both numbers is what
/// lets the message say how much is missing instead of only that something is.
/// </summary>
public sealed class InsufficientDiskSpaceException(
    string path, long requiredBytes, long availableBytes)
    : Exception($"{path} has {availableBytes} bytes free, and {requiredBytes} are needed.")
{
    public string Path { get; } = path;

    public long RequiredBytes { get; } = requiredBytes;

    public long AvailableBytes { get; } = availableBytes;
}

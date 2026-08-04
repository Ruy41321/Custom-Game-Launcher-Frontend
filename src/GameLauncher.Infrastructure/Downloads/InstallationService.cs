using System.Security.Cryptography;
using GameLauncher.Core.Api;
using GameLauncher.Core.Configuration;
using GameLauncher.Core.Downloads;
using GameLauncher.Core.Installs;
using GameLauncher.Core.Models;
using GameLauncher.Core.Platform;
using Microsoft.Extensions.Logging;

namespace GameLauncher.Infrastructure.Downloads;

/// <summary>
/// Everything that writes to an install directory. The order of the steps is the design: the
/// space check happens before anything is written, every blob is verified before it is applied,
/// and the row that says a game is installed is the last thing to change — so an interrupted
/// update leaves a directory the launcher knows is unfinished rather than one it will happily
/// run.
/// </summary>
public sealed class InstallationService(
    IDownloadApi downloadApi,
    IBlobFetcher blobFetcher,
    IInstallStore installStore,
    IPathProvider paths,
    IDiskSpaceProbe diskSpace,
    IUserSettingsStore settings,
    TimeProvider time,
    ILogger<InstallationService> logger) : IInstallationService
{
    /// <summary>
    /// Enough connections to fill a domestic line, few enough that a slow server is not being
    /// asked to serve one client from a dozen workers at once.
    /// </summary>
    private const int MaxConcurrentTransfers = 4;

    private const int CopyBufferBytes = 128 * 1024;

    public async Task<InstallResult> InstallAsync(
        InstallRequest request,
        IProgress<DownloadProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        InstalledGame? existing = await installStore
            .FindAsync(request.Game.Id, cancellationToken).ConfigureAwait(false);

        string installDirectory = request.InstallDirectory
            ?? existing?.InstallDirectory
            ?? InstallPaths.DefaultInstallDirectory(
                await InstallRootAsync(cancellationToken).ConfigureAwait(false),
                request.Game.Slug,
                request.Game.Id);

        progress?.Report(new DownloadProgress { Phase = InstallPhase.Planning });

        // Only an install that finished can be updated *from*. A directory left half-applied
        // is not the build its row names, so asking for a delta against it would produce a
        // plan that assumes files that may not be there.
        string? fromBuildId = existing?.State == InstallState.Installed ? existing.BuildId : null;

        DownloadPlan plan = await downloadApi
            .GetPlanAsync(request.Build.Id, fromBuildId, cancellationToken).ConfigureAwait(false);

        if (plan.IsUpToDate && existing?.Is(request.Build.Id) == true)
        {
            progress?.Report(new DownloadProgress { Phase = InstallPhase.Done });
            return new InstallResult
            {
                Install = existing,
                DownloadedBytes = 0,
                Kind = plan.Kind,
                WasUpdate = true,
            };
        }

        progress?.Report(new DownloadProgress
        {
            Phase = InstallPhase.CheckingSpace,
            TotalBytes = plan.DownloadBytes,
            TotalFiles = plan.Files.Count,
        });

        EnsureThereIsRoom(plan, installDirectory, existing);

        Dictionary<string, string> localSources = LocalSources(plan, installDirectory);

        await DownloadAsync(plan, localSources, progress, cancellationToken).ConfigureAwait(false);

        InstalledGame applying = Row(request, plan, installDirectory, existing, InstallState.Applying);
        await installStore.SaveAsync(applying, cancellationToken).ConfigureAwait(false);

        await ApplyAsync(plan, localSources, installDirectory, progress, cancellationToken)
            .ConfigureAwait(false);

        InstalledGame installed = applying with
        {
            State = InstallState.Installed,
            UpdatedAt = time.GetUtcNow(),
        };
        await installStore.SaveAsync(installed, cancellationToken).ConfigureAwait(false);

        // Only now: staging is what makes a crash during apply recoverable without downloading
        // the build a second time.
        DiscardStaging(request.Build.Id);

        progress?.Report(new DownloadProgress
        {
            Phase = InstallPhase.Done,
            TransferredBytes = plan.DownloadBytes,
            TotalBytes = plan.DownloadBytes,
            FilesApplied = plan.Files.Count,
            TotalFiles = plan.Files.Count,
        });

        logger.LogInformation(
            "Installed {Game} build {Build} ({Kind}, {Bytes} bytes) into {Directory}",
            request.Game.Slug, request.Build.Id, plan.Kind, plan.DownloadBytes, installDirectory);

        return new InstallResult
        {
            Install = installed,
            DownloadedBytes = plan.DownloadBytes,
            Kind = plan.Kind,
            WasUpdate = existing is not null,
        };
    }

    public async Task<IntegrityReport> VerifyAsync(
        string gameId, CancellationToken cancellationToken = default)
    {
        InstalledGame install = await installStore
            .FindAsync(gameId, cancellationToken).ConfigureAwait(false)
            ?? throw new ApiException(
                ApiErrorCode.NotFound, $"{gameId} is not installed on this machine.");

        IReadOnlyList<InstalledFile> found = await HashInstallAsync(
            install.InstallDirectory, cancellationToken).ConfigureAwait(false);

        IntegrityReport report = await downloadApi
            .VerifyAsync(install.BuildId, found, cancellationToken).ConfigureAwait(false);

        await installStore.SaveAsync(
            install with
            {
                State = report.Intact ? InstallState.Installed : InstallState.Broken,
                LastVerifiedAt = time.GetUtcNow(),
                UpdatedAt = time.GetUtcNow(),
            },
            cancellationToken).ConfigureAwait(false);

        return report;
    }

    public async Task<UninstallResult> UninstallAsync(
        string gameId, CancellationToken cancellationToken = default)
    {
        InstalledGame? install = await installStore
            .FindAsync(gameId, cancellationToken).ConfigureAwait(false);

        if (install is null)
        {
            return new UninstallResult(gameId, 0);
        }

        long freed = DirectorySize(install.InstallDirectory);

        if (Directory.Exists(install.InstallDirectory))
        {
            Directory.Delete(install.InstallDirectory, recursive: true);
        }

        DiscardStaging(install.BuildId);

        // The row goes last: a deletion interrupted half way leaves a row pointing at a
        // directory that is partly gone, which reinstalling repairs. A row deleted first would
        // leave the files with nothing that knows they are there.
        await installStore.RemoveAsync(gameId, cancellationToken).ConfigureAwait(false);

        logger.LogInformation("Uninstalled {Game}, freeing {Bytes} bytes", gameId, freed);
        return new UninstallResult(gameId, freed);
    }

    /// <summary>
    /// Where new games go. The user's choice wins over the platform default, and an install
    /// that already exists is never moved by changing it — the setting decides where the
    /// *next* game lands, not where the ones already on disk live.
    /// </summary>
    private async Task<string> InstallRootAsync(CancellationToken cancellationToken)
    {
        UserSettings preferences = await settings
            .LoadAsync(cancellationToken).ConfigureAwait(false);

        string? chosen = preferences.InstallDirectory;
        if (string.IsNullOrWhiteSpace(chosen))
        {
            return paths.DefaultInstallDirectory;
        }

        try
        {
            // A directory that cannot be created is a preference the user can no longer act
            // on — an unplugged drive, a folder they deleted. Refusing to install would be a
            // worse answer than quietly using the place that always works.
            Directory.CreateDirectory(chosen);
            return chosen;
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException or ArgumentException)
        {
            logger.LogWarning(
                exception,
                "The configured install directory {Directory} is unusable; falling back to {Default}",
                chosen,
                paths.DefaultInstallDirectory);

            return paths.DefaultInstallDirectory;
        }
    }

    /// <summary>
    /// Blob content the install already holds, from the <c>copyFrom</c> hints. The server only
    /// offers paths the update keeps unchanged, so these files are neither deleted nor
    /// overwritten by any other step and the copy is safe whenever it happens.
    /// </summary>
    private static Dictionary<string, string> LocalSources(
        DownloadPlan plan, string installDirectory)
    {
        Dictionary<string, string> sources = new(StringComparer.OrdinalIgnoreCase);

        foreach (PlannedFile file in plan.Files)
        {
            if (!file.CanBeCopiedLocally || sources.ContainsKey(file.Sha256))
            {
                continue;
            }

            string source = InstallPaths.Inside(installDirectory, file.CopyFrom!);
            if (File.Exists(source))
            {
                sources[file.Sha256] = source;
            }
        }

        return sources;
    }

    private void EnsureThereIsRoom(
        DownloadPlan plan, string installDirectory, InstalledGame? existing)
    {
        // Staging holds what travels; the install directory holds the build. They are checked
        // separately because they are usually on the same volume and sometimes are not, and
        // the sum is only the right answer in the first case.
        long stagingNeeded = plan.DownloadBytes;
        long installNeeded = Math.Max(0, plan.TotalBytes - (existing?.SizeBytes ?? 0));

        long stagingFree = diskSpace.AvailableFreeBytes(paths.DownloadStagingDirectory);
        long installFree = diskSpace.AvailableFreeBytes(installDirectory);

        if (stagingFree == installFree)
        {
            Require(installDirectory, stagingNeeded + installNeeded, installFree);
            return;
        }

        Require(paths.DownloadStagingDirectory, stagingNeeded, stagingFree);
        Require(installDirectory, installNeeded, installFree);

        static void Require(string path, long needed, long free)
        {
            if (free < needed)
            {
                throw new InsufficientDiskSpaceException(path, needed, free);
            }
        }
    }

    private async Task DownloadAsync(
        DownloadPlan plan,
        Dictionary<string, string> localSources,
        IProgress<DownloadProgress>? progress,
        CancellationToken cancellationToken)
    {
        // Blobs, not files: two paths with identical content are one transfer, which is the
        // same reason the plan's downloadBytes counts distinct blobs.
        List<PlannedFile> blobs =
        [
            .. plan.Files
                .Where(file => !localSources.ContainsKey(file.Sha256))
                .GroupBy(file => file.Sha256, StringComparer.OrdinalIgnoreCase)
                .Select(group => group.First()),
        ];

        if (blobs.Count == 0)
        {
            return;
        }

        long transferred = 0;
        Progress report = new(bytes =>
        {
            long total = Interlocked.Add(ref transferred, bytes);
            progress?.Report(new DownloadProgress
            {
                Phase = InstallPhase.Downloading,
                TransferredBytes = total,
                TotalBytes = plan.DownloadBytes,
                TotalFiles = plan.Files.Count,
            });
        });

        await Parallel.ForEachAsync(
            blobs,
            new ParallelOptions
            {
                MaxDegreeOfParallelism = MaxConcurrentTransfers,
                CancellationToken = cancellationToken,
            },
            async (blob, token) => await blobFetcher.FetchAsync(
                blob,
                InstallPaths.StagedBlob(
                    paths.DownloadStagingDirectory, plan.BuildId, blob.Sha256),
                report,
                token).ConfigureAwait(false)).ConfigureAwait(false);
    }

    private async Task ApplyAsync(
        DownloadPlan plan,
        Dictionary<string, string> localSources,
        string installDirectory,
        IProgress<DownloadProgress>? progress,
        CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(installDirectory);

        int applied = 0;
        foreach (PlannedFile file in plan.Files)
        {
            cancellationToken.ThrowIfCancellationRequested();

            string source = localSources.TryGetValue(file.Sha256, out string? local)
                ? local
                : InstallPaths.StagedBlob(
                    paths.DownloadStagingDirectory, plan.BuildId, file.Sha256);

            string destination = InstallPaths.Inside(installDirectory, file.Path);
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);

            await CopyVerifiedAsync(source, destination, file.Sha256, cancellationToken)
                .ConfigureAwait(false);

            MakeExecutable(destination, file.Executable);

            applied++;
            progress?.Report(new DownloadProgress
            {
                Phase = InstallPhase.Applying,
                TransferredBytes = plan.DownloadBytes,
                TotalBytes = plan.DownloadBytes,
                FilesApplied = applied,
                TotalFiles = plan.Files.Count,
            });
        }

        foreach (string path in plan.Remove)
        {
            string target = InstallPaths.Inside(installDirectory, path);
            if (File.Exists(target))
            {
                File.Delete(target);
            }
        }

        PruneEmptyDirectories(installDirectory);
    }

    /// <summary>
    /// Copies and hashes in the same pass. The bytes are being read anyway, so the check that
    /// the copy is what the manifest names costs nothing — and it is the one step of the
    /// install that a per-blob download check cannot cover.
    /// </summary>
    private static async Task CopyVerifiedAsync(
        string source, string destination, string sha256, CancellationToken cancellationToken)
    {
        string partial = destination + BlobFetcher.PartialSuffix;

        await using (FileStream input = new(
            source, FileMode.Open, FileAccess.Read, FileShare.Read, CopyBufferBytes, useAsync: true))
        await using (FileStream output = new(
            partial, FileMode.Create, FileAccess.Write, FileShare.None, CopyBufferBytes,
            useAsync: true))
        {
            using IncrementalHash hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            byte[] buffer = new byte[CopyBufferBytes];

            while (true)
            {
                int read = await input.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
                if (read == 0)
                {
                    break;
                }

                hash.AppendData(buffer, 0, read);
                await output.WriteAsync(buffer.AsMemory(0, read), cancellationToken)
                    .ConfigureAwait(false);
            }

            string actual = Convert.ToHexStringLower(hash.GetCurrentHash());
            if (!string.Equals(actual, sha256, StringComparison.OrdinalIgnoreCase))
            {
                throw new ApiException(
                    ApiErrorCode.Integrity,
                    $"{Path.GetFileName(destination)} was copied as {actual}, not {sha256}.");
            }
        }

        File.Move(partial, destination, overwrite: true);
    }

    /// <summary>The bit only exists on Unix, and Windows has nothing to say about it.</summary>
    private static void MakeExecutable(string path, bool executable)
    {
        if (!executable || OperatingSystem.IsWindows())
        {
            return;
        }

        File.SetUnixFileMode(
            path,
            File.GetUnixFileMode(path)
                | UnixFileMode.UserExecute
                | UnixFileMode.GroupExecute
                | UnixFileMode.OtherExecute);
    }

    /// <summary>
    /// A build that dropped its last file from a directory should not leave the directory
    /// behind. The install root itself is never removed: it is where the game lives.
    /// </summary>
    private static void PruneEmptyDirectories(string root)
    {
        foreach (string directory in Directory.EnumerateDirectories(root))
        {
            Prune(directory);
        }

        static void Prune(string directory)
        {
            foreach (string child in Directory.EnumerateDirectories(directory))
            {
                Prune(child);
            }

            if (!Directory.EnumerateFileSystemEntries(directory).Any())
            {
                Directory.Delete(directory);
            }
        }
    }

    private static async Task<IReadOnlyList<InstalledFile>> HashInstallAsync(
        string installDirectory, CancellationToken cancellationToken)
    {
        if (!Directory.Exists(installDirectory))
        {
            return [];
        }

        List<InstalledFile> found = [];
        foreach (string path in Directory.EnumerateFiles(
            installDirectory, "*", SearchOption.AllDirectories))
        {
            cancellationToken.ThrowIfCancellationRequested();

            string relative = Path
                .GetRelativePath(installDirectory, path)
                .Replace(Path.DirectorySeparatorChar, '/');

            try
            {
                await using FileStream stream = new(
                    path, FileMode.Open, FileAccess.Read, FileShare.Read, CopyBufferBytes,
                    useAsync: true);

                byte[] hash = await SHA256.HashDataAsync(stream, cancellationToken)
                    .ConfigureAwait(false);
                found.Add(new InstalledFile(relative, Convert.ToHexStringLower(hash)));
            }
            catch (Exception exception) when (
                exception is IOException or UnauthorizedAccessException)
            {
                // Leaving it out is how a file the client could not read is reported: the
                // server lists it as missing, which is what it is as far as the install goes.
            }
        }

        return found;
    }

    private InstalledGame Row(
        InstallRequest request,
        DownloadPlan plan,
        string installDirectory,
        InstalledGame? existing,
        InstallState state) => new()
        {
            GameId = request.Game.Id,
            GameSlug = request.Game.Slug,
            GameTitle = request.Game.Title,
            BuildId = plan.BuildId,
            VersionId = request.Version.Id,
            VersionSemver = request.Version.Semver,
            Platform = request.Build.Platform,
            Architecture = request.Build.Architecture,
            InstallDirectory = installDirectory,
            Entrypoint = plan.Entrypoint,
            LaunchArgs = plan.LaunchArgs,

            // The manifest owns LaunchArgs and an update rewrites it; the player owns this one
            // and an update must not quietly discard it.
            LaunchOptions = existing?.LaunchOptions ?? string.Empty,
            ManifestSha256 = plan.ManifestSha256,
            SizeBytes = plan.TotalBytes,
            FileCount = plan.Files.Count + plan.Unchanged.Count,
            State = state,
            InstalledAt = existing?.InstalledAt ?? time.GetUtcNow(),
            UpdatedAt = time.GetUtcNow(),
            LastVerifiedAt = existing?.LastVerifiedAt,
            LastPlayedAt = existing?.LastPlayedAt,
        };

    private void DiscardStaging(string buildId)
    {
        string directory = InstallPaths.StagingForBuild(
            paths.DownloadStagingDirectory, buildId);

        try
        {
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, recursive: true);
            }
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException)
        {
            // Staging is scratch space. Failing an install that has already succeeded because
            // its leftovers could not be swept would be the wrong trade.
            logger.LogWarning(exception, "Could not clear staging at {Directory}", directory);
        }
    }

    private static long DirectorySize(string directory) =>
        Directory.Exists(directory)
            ? Directory
                .EnumerateFiles(directory, "*", SearchOption.AllDirectories)
                .Sum(path => new FileInfo(path).Length)
            : 0;

    /// <summary>
    /// Reports on the calling thread. <see cref="System.Progress{T}"/> posts to a captured
    /// context, and four transfers reporting through the thread pool would deliver a byte
    /// count that arrives after the one that supersedes it.
    /// </summary>
    private sealed class Progress(Action<long> report) : IProgress<long>
    {
        public void Report(long value) => report(value);
    }
}

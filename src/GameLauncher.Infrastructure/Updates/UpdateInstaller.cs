using System.Diagnostics;
using System.IO.Compression;
using GameLauncher.Core.Api;
using GameLauncher.Core.Platform;
using GameLauncher.Core.Updates;
using Microsoft.Extensions.Logging;

namespace GameLauncher.Infrastructure.Updates;

/// <summary>
/// The last thing the launcher does about an update, and the only thing between the verified
/// archive and a helper that is waiting for this process to exit.
///
/// Three steps, and the middle one is the trap. Unpack into
/// <c>&lt;user data&gt;/updates/&lt;version&gt;/staged/</c>, refusing an entry name that would
/// land outside it. <b>Copy the helper out of the installation before starting it</b>: on
/// Windows a running executable can be neither renamed nor deleted, so an updater left inside
/// the directory it is about to rename makes that rename fail for a reason nothing reports.
/// Then start it, and let the caller exit.
/// </summary>
public sealed class UpdateInstaller(
    IPathProvider paths,
    ILogger<UpdateInstaller> logger) : IUpdateInstaller
{
    /// <summary>
    /// Published beside the launcher, self-contained for the same runtime identifier: a machine
    /// running a self-contained launcher is a machine that may have no .NET at all, and an
    /// updater that needed one would be missing exactly when it is needed.
    /// </summary>
    private const string UpdaterDirectoryName = "updater";

    private const string StagedDirectoryName = "staged";

    private static string UpdaterExecutableName =>
        OperatingSystem.IsWindows() ? "GameLauncher.Updater.exe" : "GameLauncher.Updater";

    public async Task<int> StartAsync(
        ReleaseDocument release,
        string archivePath,
        CancellationToken cancellationToken = default)
    {
        string versionDirectory =
            Path.Combine(paths.UpdateDirectory, release.Version.ToString());

        string staged = Path.Combine(versionDirectory, StagedDirectoryName);
        await ExtractAsync(archivePath, staged, cancellationToken).ConfigureAwait(false);

        string helper = CopyHelperOut(versionDirectory);

        UpdateSwapRequest request = new()
        {
            SourceDirectory = staged,
            TargetDirectory = paths.ApplicationDirectory,
            WaitForProcessId = Environment.ProcessId,
            RelaunchExecutable = Environment.ProcessPath,
        };

        return Start(helper, request);
    }

    /// <summary>
    /// Every entry is resolved through <see cref="UpdateArchiveRules"/> before a byte is
    /// written, so an archive carrying <c>../</c> or an absolute path is refused with nothing
    /// on disk to undo.
    /// </summary>
    private static async Task ExtractAsync(
        string archivePath, string destination, CancellationToken cancellationToken)
    {
        if (Directory.Exists(destination))
        {
            // A previous attempt that never got as far as starting the helper. Its contents are
            // whatever that attempt managed to write, which is not a version of anything.
            Directory.Delete(destination, recursive: true);
        }

        Directory.CreateDirectory(destination);

        using ZipArchive archive = ZipFile.OpenRead(archivePath);

        foreach (ZipArchiveEntry entry in archive.Entries)
        {
            if (UpdateArchiveRules.IsDirectoryEntry(entry.FullName))
            {
                continue;
            }

            string path = UpdateArchiveRules.ResolveInside(destination, entry.FullName);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);

            await using Stream source = entry.Open();
            await using FileStream file = new(
                path, FileMode.Create, FileAccess.Write, FileShare.None, 128 * 1024, useAsync: true);

            await source.CopyToAsync(file, cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Beside the archive rather than in the system temporary directory, for two reasons: the
    /// user's data directory is known to be writable because everything else the launcher keeps
    /// is already there, and the sweep that keeps one downloaded version at a time removes this
    /// copy along with it. The helper cannot delete its own running image, so somebody else has
    /// to, and that somebody already exists.
    /// </summary>
    private string CopyHelperOut(string versionDirectory)
    {
        string installed = Path.Combine(paths.ApplicationDirectory, UpdaterDirectoryName);
        if (!Directory.Exists(installed))
        {
            throw new ApiException(
                ApiErrorCode.Integrity,
                $"This build ships no updater: {installed} does not exist.");
        }

        string copy = Path.Combine(versionDirectory, UpdaterDirectoryName);
        if (Directory.Exists(copy))
        {
            Directory.Delete(copy, recursive: true);
        }

        CopyTree(installed, copy);

        string executable = Path.Combine(copy, UpdaterExecutableName);
        if (!File.Exists(executable))
        {
            throw new ApiException(
                ApiErrorCode.Integrity,
                $"The updater is missing from this build: {executable}");
        }

        return executable;
    }

    private static void CopyTree(string source, string destination)
    {
        Directory.CreateDirectory(destination);

        foreach (string directory in Directory.GetDirectories(source, "*", SearchOption.AllDirectories))
        {
            Directory.CreateDirectory(
                Path.Combine(destination, Path.GetRelativePath(source, directory)));
        }

        foreach (string file in Directory.GetFiles(source, "*", SearchOption.AllDirectories))
        {
            File.Copy(file, Path.Combine(destination, Path.GetRelativePath(source, file)), overwrite: true);
        }
    }

    private int Start(string executable, UpdateSwapRequest request)
    {
        ProcessStartInfo start = new()
        {
            FileName = executable,
            WorkingDirectory = Path.GetDirectoryName(executable)!,
            UseShellExecute = false,
            CreateNoWindow = true,
        };

        foreach (string argument in request.ToArguments())
        {
            start.ArgumentList.Add(argument);
        }

        // Not a child that dies with this process: on every platform the launcher targets a
        // started process outlives its parent unless something deliberately ties them together,
        // and the whole point of this one is to still be running after the exit it is waiting
        // for. Nothing here puts it in a job object or a process group.
        using Process? process = Process.Start(start);

        if (process is null)
        {
            throw new ApiException(
                ApiErrorCode.Unknown, $"The updater could not be started: {executable}");
        }

        logger.LogInformation(
            "Updater {ProcessId} started; this launcher must now exit for the swap to begin.",
            process.Id);

        return process.Id;
    }
}

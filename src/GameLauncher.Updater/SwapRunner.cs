using GameLauncher.Core.Updates;

namespace GameLauncher.Updater;

/// <summary>
/// The swap itself: wait, rename the old installation out of the way, put the new one in place,
/// start it, and watch it for long enough to tell a launcher that starts from one that does not.
///
/// Everything that can be decided without moving a file is decided in Core — the command line
/// (<see cref="UpdateSwapRequest"/>), where the old installation goes
/// (<see cref="UpdateSwapPaths"/>) and what the exit code means
/// (<see cref="RelaunchWatch"/>). What is left here is the filesystem and two substituted
/// interfaces, which is what lets a test drive a real installation directory through both the
/// happy path and the rollback without a launcher existing.
/// </summary>
public sealed class SwapRunner(
    IProcessStarter starter,
    IProcessWaiter waiter,
    TimeProvider time,
    TextWriter log)
{
    /// <summary>
    /// How long to wait for the launcher that asked for this. It has already decided to exit,
    /// so anything longer means it is hung — and swapping the files of a running launcher is
    /// the one thing this helper must never do.
    /// </summary>
    private static readonly TimeSpan LauncherExitTimeout = TimeSpan.FromSeconds(30);

    /// <summary>
    /// Windows releases the handles of a process that has exited a moment after it exits, so
    /// the first rename can fail for a reason that fixes itself.
    /// </summary>
    private const int RenameAttempts = 20;

    private static readonly TimeSpan RenameRetryDelay = TimeSpan.FromMilliseconds(250);

    public int Run(UpdateSwapRequest request)
    {
        if (!Directory.Exists(request.TargetDirectory))
        {
            log.WriteLine($"--target does not exist: {request.TargetDirectory}");
            return ExitCodes.Usage;
        }

        string previous;
        try
        {
            previous = UpdateSwapPaths.PreviousOf(request.TargetDirectory);
        }
        catch (Core.Api.ApiException exception)
        {
            log.WriteLine(exception.Message);
            return ExitCodes.Usage;
        }

        return request.RollbackOnly
            ? Rollback(request, previous)
            : Swap(request, previous);
    }

    private int Swap(UpdateSwapRequest request, string previous)
    {
        if (!Directory.Exists(request.SourceDirectory))
        {
            log.WriteLine($"--source does not exist: {request.SourceDirectory}");
            return ExitCodes.Usage;
        }

        if (request.WaitForProcessId != 0
            && !waiter.WaitForExit(request.WaitForProcessId, LauncherExitTimeout))
        {
            // Refusing is the whole point: replacing the files of a launcher that is still
            // running is how an installation ends up half of one build and half of another.
            log.WriteLine(
                $"The launcher {request.WaitForProcessId} is still running; nothing was changed.");
            return ExitCodes.Usage;
        }

        // A leftover from an attempt that never resolved. It is stale by proof rather than by
        // assumption: the launcher that just asked for this update was running, so whatever is
        // in the target directory works, and keeping an older copy would only make the *next*
        // rollback restore a version two updates behind.
        if (Directory.Exists(previous))
        {
            log.WriteLine($"Discarding a previous installation left by an earlier attempt: {previous}");
            Delete(previous);
        }

        try
        {
            Rename(request.TargetDirectory, previous);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // Nothing has moved, so there is nothing to undo.
            log.WriteLine($"The installation could not be moved aside: {exception.Message}");
            return ExitCodes.Usage;
        }

        try
        {
            PutInPlace(request.SourceDirectory, request.TargetDirectory);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            log.WriteLine($"The new version could not be put in place: {exception.Message}");
            return Restore(request, previous, ExitCodes.Restored);
        }

        if (string.IsNullOrEmpty(request.RelaunchExecutable))
        {
            // Nothing was asked to be started, so there is nothing to judge. The swap is done.
            Delete(previous);
            return ExitCodes.Ok;
        }

        SwapVerdict verdict = RelaunchWatch.Watch(
            starter.Start(request.RelaunchExecutable, request.TargetDirectory), time);

        if (verdict == SwapVerdict.Succeeded)
        {
            log.WriteLine("The new launcher is running; the previous installation is going away.");
            Delete(previous);
            return ExitCodes.Ok;
        }

        log.WriteLine("The new launcher failed on start-up; putting the previous one back.");
        return Restore(request, previous, ExitCodes.Restored);
    }

    private int Rollback(UpdateSwapRequest request, string previous)
    {
        if (!Directory.Exists(previous))
        {
            log.WriteLine($"There is nothing to roll back to: {previous} does not exist.");
            return ExitCodes.Usage;
        }

        if (request.WaitForProcessId != 0
            && !waiter.WaitForExit(request.WaitForProcessId, LauncherExitTimeout))
        {
            log.WriteLine(
                $"The launcher {request.WaitForProcessId} is still running; nothing was changed.");
            return ExitCodes.Usage;
        }

        return Restore(request, previous, ExitCodes.Ok);
    }

    /// <summary>
    /// Puts <paramref name="previous"/> back where the installation belongs and starts it again.
    /// The launcher that comes back is the one that was working an update ago, which is the
    /// property the rename bought and a delete would have thrown away.
    /// </summary>
    private int Restore(UpdateSwapRequest request, string previous, int successCode)
    {
        try
        {
            if (Directory.Exists(request.TargetDirectory))
            {
                Delete(request.TargetDirectory);
            }

            Rename(previous, request.TargetDirectory);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            log.WriteLine(
                $"The previous installation could not be put back: {exception.Message}. "
                + $"It is still on disk at {previous}.");
            return ExitCodes.Broken;
        }

        if (!string.IsNullOrEmpty(request.RelaunchExecutable))
        {
            // The same path as before: it named an executable inside the installation
            // directory, and that directory is the old one again.
            starter.Start(request.RelaunchExecutable, request.TargetDirectory);
        }

        return successCode;
    }

    /// <summary>
    /// A move when the two are on one filesystem, which is the ordinary case and is atomic;
    /// a copy when they are not, which the download directory and the installation can be.
    /// </summary>
    private static void PutInPlace(string source, string destination)
    {
        try
        {
            Directory.Move(source, destination);
            return;
        }
        catch (IOException)
        {
            // Different volumes. Fall through: nothing has been written to the destination.
        }

        CopyTree(source, destination);
        Delete(source);
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

    private static void Rename(string from, string to)
    {
        for (int attempt = 1; ; attempt++)
        {
            try
            {
                Directory.Move(from, to);
                return;
            }
            catch (IOException) when (attempt < RenameAttempts)
            {
                Thread.Sleep(RenameRetryDelay);
            }
        }
    }

    private static void Delete(string directory)
    {
        for (int attempt = 1; ; attempt++)
        {
            try
            {
                Directory.Delete(directory, recursive: true);
                return;
            }
            catch (IOException) when (attempt < RenameAttempts)
            {
                Thread.Sleep(RenameRetryDelay);
            }
        }
    }
}

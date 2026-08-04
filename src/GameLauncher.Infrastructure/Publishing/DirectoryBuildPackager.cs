using System.Security.Cryptography;
using GameLauncher.Core.Publishing;

namespace GameLauncher.Infrastructure.Publishing;

/// <summary>
/// Reads a build off the disk. Every file is hashed once, here, and nothing downstream reads
/// the directory again — which is what keeps the publish flow's cost linear in the build's
/// size rather than in the number of steps it takes.
/// </summary>
public sealed class DirectoryBuildPackager : IBuildPackager
{
    /// <summary>Matches the server's <c>uploads.maxBlobBytes</c> default of 2 GiB.</summary>
    public const long MaxBlobBytes = 2L * 1024 * 1024 * 1024;

    private const int BufferBytes = 128 * 1024;

    public async Task<BuildPackage> PackageAsync(
        string directory,
        string entrypoint,
        IProgress<PackagingProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        string root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(directory));
        if (!Directory.Exists(root))
        {
            throw new PublishingException(
                PublishFailure.NothingToPublish, $"{directory} is not a directory.");
        }

        string[] paths = [.. Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories)];
        if (paths.Length == 0)
        {
            throw new PublishingException(
                PublishFailure.NothingToPublish, "A build must contain at least one file.");
        }

        if (paths.Length > ManifestPathRules.MaxFiles)
        {
            throw new PublishingException(
                PublishFailure.TooManyFiles,
                $"A build may contain at most {ManifestPathRules.MaxFiles} files.");
        }

        List<PackagedFile> files = new(paths.Length);
        long hashed = 0;

        foreach (string path in paths)
        {
            cancellationToken.ThrowIfCancellationRequested();

            string relative = Path
                .GetRelativePath(root, path)
                .Replace(Path.DirectorySeparatorChar, '/');

            if (ManifestPathRules.Reject(relative) is { } reason)
            {
                throw new PublishingException(PublishFailure.InvalidPath, reason);
            }

            FileInfo info = new(path);
            if (info.Length > MaxBlobBytes)
            {
                throw new PublishingException(
                    PublishFailure.FileTooLarge,
                    $"{relative} is larger than the {MaxBlobBytes} bytes one upload may carry.");
            }

            files.Add(new PackagedFile
            {
                Path = relative,
                SourcePath = path,
                Sha256 = await HashAsync(path, cancellationToken).ConfigureAwait(false),
                Size = info.Length,
                Executable = IsExecutable(path),
            });

            hashed += info.Length;
            progress?.Report(new PackagingProgress(files.Count, paths.Length, hashed));
        }

        string entrypointPath = entrypoint.Replace('\\', '/').TrimStart('/');
        if (!files.Any(file => string.Equals(file.Path, entrypointPath, StringComparison.Ordinal)))
        {
            throw new PublishingException(
                PublishFailure.EntrypointMissing,
                $"{entrypoint} is not one of the files being published.");
        }

        return new BuildPackage { Files = files, Entrypoint = entrypointPath };
    }

    private static async Task<string> HashAsync(string path, CancellationToken cancellationToken)
    {
        try
        {
            await using FileStream stream = new(
                path, FileMode.Open, FileAccess.Read, FileShare.Read, BufferBytes, useAsync: true);

            byte[] hash = await SHA256.HashDataAsync(stream, cancellationToken)
                .ConfigureAwait(false);
            return Convert.ToHexStringLower(hash);
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException)
        {
            throw new PublishingException(
                PublishFailure.Unreadable,
                $"{Path.GetFileName(path)} could not be read.",
                exception);
        }
    }

    /// <summary>
    /// Only meaningful on Unix. On Windows every file would look executable or none would, and
    /// the flag would say more about the publisher's machine than about the build — so it is
    /// left false and the publisher's Unix build is the one that carries it.
    /// </summary>
    private static bool IsExecutable(string path)
    {
        if (OperatingSystem.IsWindows())
        {
            return false;
        }

        return File.GetUnixFileMode(path).HasFlag(UnixFileMode.UserExecute);
    }
}

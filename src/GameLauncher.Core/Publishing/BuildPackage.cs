using GameLauncher.Core.Api;

namespace GameLauncher.Core.Publishing;

/// <summary>Why a directory could not be turned into a build.</summary>
public enum PublishFailure
{
    /// <summary>Nothing to publish: a build must contain at least one file.</summary>
    NothingToPublish,

    /// <summary>More files than the server will accept in one manifest.</summary>
    TooManyFiles,

    /// <summary>A name the manifest format cannot carry — see <see cref="ManifestPathRules"/>.</summary>
    InvalidPath,

    /// <summary>The chosen executable is not one of the files being published.</summary>
    EntrypointMissing,

    /// <summary>A blob larger than the server accepts in one upload.</summary>
    FileTooLarge,

    /// <summary>A file that is there but could not be read.</summary>
    Unreadable,
}

public sealed class PublishingException(PublishFailure reason, string message, Exception? inner = null)
    : Exception(message, inner)
{
    public PublishFailure Reason { get; } = reason;
}

/// <summary>
/// The manifest path rules, mirrored from the server's <c>domain::validateRelativePath</c> and
/// its <c>build_files_relative_path_safe</c> constraint.
///
/// Checked here so a name the server will refuse is caught before gigabytes travel rather than
/// at the last call of the publish flow. It is a copy of somebody else's rule, which is a debt:
/// if the server ever loosens one of these, this says no for a reason that no longer exists.
/// </summary>
public static class ManifestPathRules
{
    public const int MaxPathLength = 1024;

    public const int MaxFiles = 200_000;

    /// <summary>Null when the path is acceptable, otherwise why it is not.</summary>
    public static string? Reject(string path)
    {
        if (path.Length == 0)
        {
            return "a file path must not be empty";
        }

        if (path.Length > MaxPathLength)
        {
            return $"file path is longer than {MaxPathLength} characters: {path}";
        }

        if (path[0] == '/')
        {
            return $"file paths must be relative to the install directory: {path}";
        }

        if (path.Contains('\\', StringComparison.Ordinal))
        {
            return $"file paths must use '/' as the separator: {path}";
        }

        // "C:relative" is absolute on Windows and is not caught by the leading-slash rule.
        if (path.Length >= 2 && path[1] == ':' && char.IsAsciiLetter(path[0]))
        {
            return $"file paths must not be absolute: {path}";
        }

        foreach (string segment in path.Split('/'))
        {
            if (segment.Length == 0)
            {
                return $"file paths must not contain empty segments: {path}";
            }

            if (segment is "." or "..")
            {
                return $"file paths must not contain '.' or '..' segments: {path}";
            }

            if (segment.Any(char.IsControl))
            {
                return "file paths must not contain control characters";
            }
        }

        return null;
    }
}

/// <summary>One file of a build, ready to be declared and uploaded.</summary>
public sealed record PackagedFile
{
    /// <summary>Relative to the build root, always with <c>/</c> as the separator.</summary>
    public required string Path { get; init; }

    /// <summary>Where the bytes actually are on this machine.</summary>
    public required string SourcePath { get; init; }

    public required string Sha256 { get; init; }

    public required long Size { get; init; }

    public bool Executable { get; init; }
}

/// <summary>
/// A directory, hashed and described. Everything the publish flow needs after this point comes
/// from here, so the disk is read once rather than once per step.
/// </summary>
public sealed record BuildPackage
{
    public required IReadOnlyList<PackagedFile> Files { get; init; }

    public required string Entrypoint { get; init; }

    /// <summary>The build as installed. Paths are the unit here.</summary>
    public long TotalBytes => Files.Sum(file => file.Size);

    /// <summary>
    /// What could actually travel, before the server is asked what it already has. Blobs are
    /// the unit: two files with identical content are one upload.
    /// </summary>
    public IReadOnlyList<BlobDeclaration> DistinctBlobs =>
    [
        .. Files
            .GroupBy(file => file.Sha256, StringComparer.OrdinalIgnoreCase)
            .Select(group => new BlobDeclaration(group.Key, group.First().Size)),
    ];

    /// <summary>The submission that turns the build ready, once every blob is up.</summary>
    public ManifestSubmission ToManifest(string? launchArgs) => new()
    {
        Files =
        [
            .. Files
                .OrderBy(file => file.Path, StringComparer.Ordinal)
                .Select(file => new ManifestFile
                {
                    Path = file.Path,
                    Sha256 = file.Sha256,
                    Executable = file.Executable,
                }),
        ],
        Entrypoint = Entrypoint,
        LaunchArgs = launchArgs,
    };
}

/// <summary>How far the packaging has got. Hashing a large build is not instant.</summary>
public sealed record PackagingProgress(int FilesHashed, int TotalFiles, long BytesHashed);

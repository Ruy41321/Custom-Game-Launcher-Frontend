namespace GameLauncher.Core.Publishing;

/// <summary>
/// Turns a directory into a <see cref="BuildPackage"/>: every file hashed, every path checked
/// against the rules the server will apply anyway.
/// </summary>
public interface IBuildPackager
{
    /// <summary>
    /// <paramref name="entrypoint"/> is relative to <paramref name="directory"/> and must be
    /// one of the files in it — the server refuses a manifest whose entrypoint it cannot find,
    /// and finding that out after the upload would be an expensive way to learn it.
    /// </summary>
    Task<BuildPackage> PackageAsync(
        string directory,
        string entrypoint,
        IProgress<PackagingProgress>? progress = null,
        CancellationToken cancellationToken = default);
}

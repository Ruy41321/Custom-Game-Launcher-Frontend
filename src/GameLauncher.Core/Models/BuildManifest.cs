namespace GameLauncher.Core.Models;

/// <summary>
/// One file of a build: where it goes, and the content address of the bytes that belong there.
/// The manifest is the authority on what an install must look like, however it got that way.
/// </summary>
public record ManifestEntry
{
    /// <summary>Relative to the install root, always with <c>/</c> as the separator.</summary>
    public string Path { get; init; } = string.Empty;

    /// <summary>Lowercase hex SHA-256 of the file's content.</summary>
    public string Sha256 { get; init; } = string.Empty;

    public long Size { get; init; }

    /// <summary>Meaningful on Unix only; Windows has no executable bit to set.</summary>
    public bool Executable { get; init; }
}

/// <summary>
/// The document <c>GET /builds/{id}/manifest</c> serves. The server sends the exact bytes its
/// <c>manifestSha256</c> covers, so <see cref="Sha256"/> is the hash of what arrived rather
/// than of a canonical form this client rebuilt — reproducing that form would put a second
/// definition of the contract in a second language.
/// </summary>
public sealed record BuildManifest
{
    /// <summary>Version of the document format, so an older client can refuse a newer one.</summary>
    public int Schema { get; init; } = 1;

    /// <summary>Path of the executable, relative to the install root.</summary>
    public string Entrypoint { get; init; } = string.Empty;

    public string LaunchArgs { get; init; } = string.Empty;

    public IReadOnlyList<ManifestEntry> Files { get; init; } = [];

    /// <summary>
    /// Hash of the bytes this document was parsed from. Not part of the document itself —
    /// nothing can contain its own hash — so it is filled in by whoever read the response.
    /// </summary>
    public string Sha256 { get; init; } = string.Empty;

    public long TotalBytes => Files.Sum(file => file.Size);
}

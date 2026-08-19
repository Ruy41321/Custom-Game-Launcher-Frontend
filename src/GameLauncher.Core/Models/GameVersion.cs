using System.Text.Json.Serialization;
using GameLauncher.Core.Json;

namespace GameLauncher.Core.Models;

/// <summary>
/// A released version of a game. A version groups the builds that carry the same content for
/// different platforms, which is why the platform lives on <see cref="GameBuild"/> and not here.
/// </summary>
public sealed record GameVersion
{
    public string Id { get; init; } = string.Empty;

    public string GameId { get; init; } = string.Empty;

    /// <summary>Text form of the version; ordering is the server's job, never the client's.</summary>
    public string Semver { get; init; } = string.Empty;

    public BuildStage Stage { get; init; }

    public string ReleaseNotes { get; init; } = string.Empty;

    /// <summary>Null on an unpublished version, which only its publisher ever sees.</summary>
    [JsonConverter(typeof(OptionalDateTimeOffsetConverter))]
    public DateTimeOffset? PublishedAt { get; init; }

    public bool Published { get; init; }

    public DateTimeOffset CreatedAt { get; init; }
}

/// <summary>Mirrors the server's <c>build_status</c> enum.</summary>
public enum BuildStatus
{
    Uploading,
    Ready,
    Failed,
}

/// <summary>
/// One platform/architecture pair of a version. Only a <see cref="BuildStatus.Ready"/> build
/// can be downloaded — the others are uploads in flight or abandoned.
/// </summary>
public sealed record GameBuild
{
    public string Id { get; init; } = string.Empty;

    public string VersionId { get; init; } = string.Empty;

    /// <summary>
    /// The publisher's own label, empty when they gave none — which is also what a build
    /// published before the server grew the column looks like. Not an identifier: two builds of
    /// one version may share a name.
    /// </summary>
    public string Name { get; init; } = string.Empty;

    public GamePlatform Platform { get; init; }

    public BuildArchitecture Architecture { get; init; }

    public BuildStatus Status { get; init; }

    /// <summary>
    /// Hash of the exact bytes <c>GET /builds/{id}/manifest</c> serves. The download engine
    /// verifies against this rather than re-canonicalising the document itself.
    /// </summary>
    public string ManifestSha256 { get; init; } = string.Empty;

    public long TotalSizeBytes { get; init; }

    public int FileCount { get; init; }

    /// <summary>Path of the executable, relative to the install root.</summary>
    public string Entrypoint { get; init; } = string.Empty;

    public string LaunchArgs { get; init; } = string.Empty;

    public DateTimeOffset CreatedAt { get; init; }

    /// <summary>Null until the manifest lands and the build becomes downloadable.</summary>
    [JsonConverter(typeof(OptionalDateTimeOffsetConverter))]
    public DateTimeOffset? ReadyAt { get; init; }
}

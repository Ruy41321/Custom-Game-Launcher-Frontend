using System.Text.Json.Serialization;
using GameLauncher.Core.Json;

namespace GameLauncher.Core.Models;

/// <summary>
/// One devlog entry. Deliberately not a version's release notes: an entry may name a version
/// or none at all, and it carries a publication state of its own so a draft can exist before
/// the build it talks about does. Only a publisher ever receives an unpublished one.
/// </summary>
public sealed record PatchNote
{
    public string Id { get; init; } = string.Empty;

    public string GameId { get; init; } = string.Empty;

    /// <summary>Empty when the entry is about the game rather than about one version.</summary>
    public string VersionId { get; init; } = string.Empty;

    public string Title { get; init; } = string.Empty;

    /// <summary>
    /// Markdown as the publisher wrote it. The launcher renders it as text: a devlog is not
    /// worth a Markdown engine, and rendering remote markup is a decision with consequences.
    /// </summary>
    public string BodyMarkdown { get; init; } = string.Empty;

    /// <summary>When readers first saw it, and null while it is a draft.</summary>
    [JsonConverter(typeof(OptionalDateTimeOffsetConverter))]
    public DateTimeOffset? PublishedAt { get; init; }

    public bool Published { get; init; }

    public DateTimeOffset CreatedAt { get; init; }

    public DateTimeOffset UpdatedAt { get; init; }

    /// <summary>
    /// Who wrote it. Same shape as a game's publisher — an id and a display name, never an
    /// address — so it reuses the type rather than declaring an identical one.
    /// </summary>
    public Publisher Author { get; init; } = new();

    public bool HasVersion => VersionId.Length > 0;
}

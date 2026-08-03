using System.Text.Json.Serialization;
using GameLauncher.Core.Json;

namespace GameLauncher.Core.Models;

/// <summary>Who published a game. The server never exposes a publisher's address.</summary>
public sealed record Publisher
{
    public string Id { get; init; } = string.Empty;

    public string DisplayName { get; init; } = string.Empty;
}

/// <summary>
/// Mirrors the server's <c>game_visibility</c> enum. The client uses it only to label a
/// publisher's own drafts: what Explore lists is decided server-side (see D8).
/// </summary>
public enum GameVisibility
{
    Draft,
    Unlisted,
    Public,
}

/// <summary>A catalog entry as returned by Explore, game detail and the library.</summary>
public sealed record Game
{
    public string Id { get; init; } = string.Empty;

    /// <summary>Human-readable identifier; accepted anywhere an id is, on every route.</summary>
    public string Slug { get; init; } = string.Empty;

    public string Title { get; init; } = string.Empty;

    public string Summary { get; init; } = string.Empty;

    public string Description { get; init; } = string.Empty;

    /// <summary>Null when the publisher has not announced one.</summary>
    [JsonConverter(typeof(OptionalDateOnlyConverter))]
    public DateOnly? ReleaseDate { get; init; }

    public GameVisibility Visibility { get; init; }

    public DateTimeOffset CreatedAt { get; init; }

    public DateTimeOffset UpdatedAt { get; init; }

    public Publisher Publisher { get; init; } = new();
}

using System.Text.Json;
using System.Text.Json.Serialization;
using GameLauncher.Core.Models;

namespace GameLauncher.Core.Tests.Json;

/// <summary>
/// The server spells "no date" as <c>""</c>. Deserializing that with the stock converter
/// throws, which would turn "this game has no announced release date" into a failed request.
/// </summary>
public sealed class OptionalDateConvertersTests
{
    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
    };

    [Fact]
    public void AnEmptyReleaseDateBecomesNull()
    {
        Game? game = JsonSerializer.Deserialize<Game>(
            """{"id":"g1","title":"Orbit","releaseDate":""}""", Options);

        Assert.NotNull(game);
        Assert.Null(game.ReleaseDate);
    }

    [Fact]
    public void ARealReleaseDateIsParsed()
    {
        Game? game = JsonSerializer.Deserialize<Game>(
            """{"id":"g1","releaseDate":"2026-08-03"}""", Options);

        Assert.Equal(new DateOnly(2026, 8, 3), game?.ReleaseDate);
    }

    [Fact]
    public void ANullReleaseDateIsAlsoAccepted()
    {
        Game? game = JsonSerializer.Deserialize<Game>("""{"releaseDate":null}""", Options);

        Assert.Null(game?.ReleaseDate);
    }

    [Fact]
    public void AnEmptyReadyAtBecomesNull()
    {
        GameBuild? build = JsonSerializer.Deserialize<GameBuild>(
            """{"id":"b1","readyAt":"","status":"uploading"}""", Options);

        Assert.NotNull(build);
        Assert.Null(build.ReadyAt);
        Assert.Equal(BuildStatus.Uploading, build.Status);
    }

    // The server formats timestamps as YYYY-MM-DDTHH:MM:SSZ; anything else is a contract change.
    [Fact]
    public void TimestampsAreReadAsUtc()
    {
        GameBuild? build = JsonSerializer.Deserialize<GameBuild>(
            """{"readyAt":"2026-08-03T14:22:01Z"}""", Options);

        Assert.Equal(
            new DateTimeOffset(2026, 8, 3, 14, 22, 1, TimeSpan.Zero), build?.ReadyAt);
    }

    [Fact]
    public void AbsenceSurvivesARoundTripAsTheEmptyStringTheServerUses()
    {
        string json = JsonSerializer.Serialize(new Game(), Options);

        Assert.Contains("\"releaseDate\":\"\"", json, StringComparison.Ordinal);
    }

    // Both directions matter: the platform enum is the one whose C# spelling and wire
    // spelling disagree, and a camelCase policy alone would send "macOs".
    [Fact]
    public void MacOsKeepsTheServersSpelling()
    {
        string json = JsonSerializer.Serialize(
            new GameBuild { Platform = GamePlatform.MacOs }, Options);

        Assert.Contains("\"platform\":\"macos\"", json, StringComparison.Ordinal);
        Assert.Equal(
            GamePlatform.MacOs,
            JsonSerializer.Deserialize<GameBuild>("""{"platform":"macos"}""", Options)?.Platform);
    }
}

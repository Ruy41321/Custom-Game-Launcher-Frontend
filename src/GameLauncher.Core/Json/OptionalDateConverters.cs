using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace GameLauncher.Core.Json;

/// <summary>
/// The server spells "no date" as an empty string rather than as <c>null</c>, because its
/// SQL formats every timestamp through <c>COALESCE(to_char(…), '')</c>. Without this the
/// deserializer throws on the perfectly ordinary case of a game with no announced release
/// date, so the emptiness is translated here once instead of at every call site.
/// </summary>
public sealed class OptionalDateOnlyConverter : JsonConverter<DateOnly?>
{
    private const string Format = "yyyy-MM-dd";

    /// <summary>Without this the framework short-circuits null and never calls the converter.</summary>
    public override bool HandleNull => true;

    public override DateOnly? Read(
        ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType == JsonTokenType.Null)
        {
            return null;
        }

        string? text = reader.GetString();
        return string.IsNullOrWhiteSpace(text)
            ? null
            : DateOnly.ParseExact(text, Format, CultureInfo.InvariantCulture);
    }

    public override void Write(Utf8JsonWriter writer, DateOnly? value, JsonSerializerOptions options)
    {
        if (value is null)
        {
            writer.WriteStringValue(string.Empty);
            return;
        }

        writer.WriteStringValue(value.Value.ToString(Format, CultureInfo.InvariantCulture));
    }
}

/// <summary>Timestamp counterpart of <see cref="OptionalDateOnlyConverter"/>.</summary>
public sealed class OptionalDateTimeOffsetConverter : JsonConverter<DateTimeOffset?>
{
    public override bool HandleNull => true;

    public override DateTimeOffset? Read(
        ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType == JsonTokenType.Null)
        {
            return null;
        }

        string? text = reader.GetString();
        return string.IsNullOrWhiteSpace(text)
            ? null
            : DateTimeOffset.Parse(text, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);
    }

    public override void Write(
        Utf8JsonWriter writer, DateTimeOffset? value, JsonSerializerOptions options)
    {
        if (value is null)
        {
            writer.WriteStringValue(string.Empty);
            return;
        }

        writer.WriteStringValue(value.Value.UtcDateTime.ToString(
            "yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture));
    }
}

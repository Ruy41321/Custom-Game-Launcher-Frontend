using System.Text.Json;
using System.Text.Json.Serialization;

namespace GameLauncher.Infrastructure.Configuration;

/// <summary>Shared JSON conventions, so config files and API payloads never disagree.</summary>
internal static class LauncherJsonSerializer
{
    public static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
    };

    /// <summary>
    /// The same conventions without the indentation. A config file on disk is read by people
    /// and is worth the whitespace; a request body is not.
    /// </summary>
    public static readonly JsonSerializerOptions Compact =
        new(Options) { WriteIndented = false };
}

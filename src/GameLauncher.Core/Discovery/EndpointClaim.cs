using System.Diagnostics.CodeAnalysis;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace GameLauncher.Core.Discovery;

/// <summary>
/// What the registry says about one service: the payload of a signed envelope, parsed only
/// after its signature has been checked.
/// </summary>
public sealed record EndpointClaim
{
    [JsonPropertyName("key")]
    public string Key { get; init; } = string.Empty;

    [JsonPropertyName("environment")]
    public string Environment { get; init; } = string.Empty;

    [JsonPropertyName("displayName")]
    public string DisplayName { get; init; } = string.Empty;

    [JsonPropertyName("baseUrl")]
    public string BaseUrl { get; init; } = string.Empty;

    /// <summary>When an operator last changed the address.</summary>
    [JsonPropertyName("updatedAt")]
    public DateTimeOffset UpdatedAt { get; init; }

    /// <summary>
    /// When the registry made this claim. It orders two claims about the same service, which
    /// is what stops a replayed old answer from displacing a newer cached one.
    /// </summary>
    [JsonPropertyName("issuedAt")]
    public DateTimeOffset IssuedAt { get; init; }

    /// <summary>
    /// Parses the bytes that were signed. Nothing here re-serialises anything: the caller has
    /// already verified <b>these bytes</b>, and a claim rebuilt from a re-serialised form
    /// would be a second definition of a wire contract in a second language.
    /// </summary>
    public static bool TryParse(ReadOnlySpan<byte> payload, [NotNullWhen(true)] out EndpointClaim? claim)
    {
        claim = null;
        try
        {
            claim = JsonSerializer.Deserialize<EndpointClaim>(payload);
        }
        catch (JsonException)
        {
            return false;
        }

        if (claim is null || !claim.IsUsable())
        {
            claim = null;
            return false;
        }

        return true;
    }

    /// <summary>
    /// Whether this claim names an address the launcher may actually use. An absolute http or
    /// https URL and nothing else: the client refuses what it will not talk to rather than
    /// handing an arbitrary scheme to <see cref="Uri"/> and finding out later.
    /// </summary>
    public bool IsUsable() =>
        !string.IsNullOrWhiteSpace(Key)
        && Uri.TryCreate(BaseUrl, UriKind.Absolute, out Uri? uri)
        && (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps);

    /// <summary>
    /// Whether this claim answers the question that was asked. A registry key signs every
    /// record it holds, so a claim about another service — or another environment — is
    /// perfectly signed and still not an answer to this launcher's question.
    /// </summary>
    public bool Answers(string serviceKey, string environment) =>
        string.Equals(Key, serviceKey, StringComparison.OrdinalIgnoreCase)
        && string.Equals(Environment, environment, StringComparison.OrdinalIgnoreCase);
}

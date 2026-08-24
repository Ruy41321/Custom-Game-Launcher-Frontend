using System.Text.Json;
using System.Text.Json.Serialization;
using GameLauncher.Core.Updates;

namespace GameLauncher.Core.Discovery;

/// <summary>
/// Turns the registry's response into a claim this launcher may act on, or into nothing.
///
/// The order is the rule, and it is <see cref="UpdateChecker"/>'s (D19, D55) applied to the
/// document that says where the server is: the signature is checked over the payload bytes
/// <b>as they arrived</b>, and only bytes that verified are ever parsed. A registry that has
/// been replaced by somebody else answers with something perfectly well-formed; what it cannot
/// do is sign it.
///
/// Nothing here throws, and nothing distinguishes a malformed body from a bad signature from a
/// claim about another service. They are one answer — no — because a caller that had to tell
/// them apart is a caller that could get it wrong, and the action is the same either way: keep
/// using the address already in hand.
/// </summary>
public static class SignedEndpointReader
{
    /// <summary>
    /// Reads a response body. <paramref name="publicKeyBase64"/> is the compiled-in key; an
    /// empty one means this build trusts no registry and every answer is refused.
    /// </summary>
    public static EndpointClaim? Read(
        string? body, string serviceKey, string environment, string? publicKeyBase64)
    {
        if (string.IsNullOrWhiteSpace(body) || string.IsNullOrWhiteSpace(publicKeyBase64))
        {
            return null;
        }

        Envelope? envelope;
        try
        {
            envelope = JsonSerializer.Deserialize<Envelope>(body);
        }
        catch (JsonException)
        {
            return null;
        }

        if (envelope is null
            || string.IsNullOrWhiteSpace(envelope.Payload)
            || string.IsNullOrWhiteSpace(envelope.Signature))
        {
            return null;
        }

        byte[] payload;
        try
        {
            payload = Convert.FromBase64String(envelope.Payload);
        }
        catch (FormatException)
        {
            return null;
        }

        // The algorithm is pinned here as it is for a release: reading it out of the message
        // would let whoever wrote the message choose it.
        if (!ReleaseSignature.Verify(payload, envelope.Signature, publicKeyBase64))
        {
            return null;
        }

        if (!EndpointClaim.TryParse(payload, out EndpointClaim? claim))
        {
            return null;
        }

        return claim.Answers(serviceKey, environment) ? claim : null;
    }

    /// <summary>The envelope as it travels. See the registry's <c>api-contract.md</c>.</summary>
    private sealed record Envelope
    {
        [JsonPropertyName("payload")]
        public string? Payload { get; init; }

        [JsonPropertyName("signature")]
        public string? Signature { get; init; }

        [JsonPropertyName("keyId")]
        public string? KeyId { get; init; }

        [JsonPropertyName("algorithm")]
        public string? Algorithm { get; init; }
    }
}

using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace GameLauncher.Core.Tests.Discovery;

/// <summary>
/// Envelopes signed the way the registry signs them, plus one the registry really produced.
///
/// The golden vector is the point of this file. Everything else here is .NET signing and .NET
/// verifying, which would pass just as happily if both sides agreed on the wrong thing; the
/// captured envelope was made by the Go service — a different language, a different crypto
/// library — and it proves the two ends actually interoperate. It was taken from a running
/// registry on 2026-08-19.
/// </summary>
internal static class RegistrySigningFixture
{
    /// <summary>The public key of the registry that produced <see cref="GoldenEnvelope"/>.</summary>
    public const string GoldenPublicKeyBase64 =
        "MFkwEwYHKoZIzj0CAQYIKoZIzj0DAQcDQgAEi4cggYmQPJKquGhqwmUhfuWnRBn3UYoM0uuU135c"
        + "d3zZh7FNFp9Zrw4brnbgF3L+T58YaLX0N79a16t/gyoV9A==";

    /// <summary>
    /// One answer from the real service, byte for byte. Its payload decodes to the record
    /// <c>game-launcher-api</c> / <c>production</c> pointing at
    /// <c>http://localhost:8080/api/v1/</c>.
    /// </summary>
    public const string GoldenEnvelope =
        """
        {"payload":"eyJrZXkiOiJnYW1lLWxhdW5jaGVyLWFwaSIsImVudmlyb25tZW50IjoicHJvZHVjdGlvbiIsImRpc3BsYXlOYW1lIjoiR2FtZSBMYXVuY2hlciBBUEkiLCJiYXNlVXJsIjoiaHR0cDovL2xvY2FsaG9zdDo4MDgwL2FwaS92MS8iLCJ1cGRhdGVkQXQiOiIyMDI2LTA4LTE5VDE0OjA3OjM3WiIsImlzc3VlZEF0IjoiMjAyNi0wOC0xOVQxNDowNzozMFoifQ==","signature":"MEYCIQD5aE5NJhgJBo8IB6Urpucb+6gkS7vi3hqzBbIL6ZvvaAIhALouVNGjwGLPI/z8WH0Bn90itVsmojsxEowzO7fGOM4G","keyId":"b6514e5ab1841e1b","algorithm":"ecdsa-p256-sha256"}
        """;

    /// <summary>A throwaway P-256 pair, so a test can sign whatever it likes.</summary>
    public static (string PublicKeyBase64, ECDsa Key) NewKey()
    {
        ECDsa key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        string publicKey = Convert.ToBase64String(key.ExportSubjectPublicKeyInfo());
        return (publicKey, key);
    }

    /// <summary>Builds the payload the registry signs, with the fields it sends.</summary>
    public static string Payload(
        string key = "game-launcher-api",
        string environment = "production",
        string baseUrl = "https://api.example.com/api/v1/",
        string? issuedAt = null,
        string? updatedAt = null) =>
        $$"""
        {"key":"{{key}}","environment":"{{environment}}","displayName":"Game Launcher API","baseUrl":"{{baseUrl}}","updatedAt":"{{updatedAt ?? "2026-08-19T14:07:37Z"}}","issuedAt":"{{issuedAt ?? "2026-08-19T14:07:30Z"}}"}
        """;

    /// <summary>Signs a payload and wraps it exactly as the service does.</summary>
    public static string Envelope(ECDsa key, string payload)
    {
        byte[] bytes = Encoding.UTF8.GetBytes(payload);
        byte[] signature = key.SignData(
            bytes, HashAlgorithmName.SHA256, DSASignatureFormat.Rfc3279DerSequence);

        return JsonSerializer.Serialize(new
        {
            payload = Convert.ToBase64String(bytes),
            signature = Convert.ToBase64String(signature),
            keyId = "0123456789abcdef",
            algorithm = "ecdsa-p256-sha256",
        });
    }

    /// <summary>The common case: a signed answer for the service the launcher asks about.</summary>
    public static string SignedEndpoint(
        ECDsa key,
        string baseUrl = "https://api.example.com/api/v1/",
        string serviceKey = "game-launcher-api",
        string environment = "production",
        string? issuedAt = null) =>
        Envelope(key, Payload(serviceKey, environment, baseUrl, issuedAt));
}

using System.Security.Cryptography;
using GameLauncher.Core.Discovery;

namespace GameLauncher.Core.Tests.Discovery;

public sealed class SignedEndpointReaderTests
{
    /// <summary>
    /// The one test here that proves anything about the *other* repository: this envelope was
    /// signed by the Go service, and it is read by .NET's own ECDsa. Everything else in this
    /// file signs with the same runtime that verifies.
    /// </summary>
    [Fact]
    public void ReadsAnEnvelopeTheRegistryReallyProduced()
    {
        EndpointClaim? claim = SignedEndpointReader.Read(
            RegistrySigningFixture.GoldenEnvelope,
            "game-launcher-api",
            "production",
            RegistrySigningFixture.GoldenPublicKeyBase64);

        Assert.NotNull(claim);
        Assert.Equal("http://localhost:8080/api/v1/", claim.BaseUrl);
        Assert.Equal("Game Launcher API", claim.DisplayName);
        Assert.Equal(
            DateTimeOffset.Parse("2026-08-19T14:07:30Z", System.Globalization.CultureInfo.InvariantCulture),
            claim.IssuedAt);
    }

    [Fact]
    public void ReadsASignedAnswer()
    {
        (string publicKey, ECDsa key) = RegistrySigningFixture.NewKey();
        using (key)
        {
            string envelope = RegistrySigningFixture.SignedEndpoint(
                key, "https://api.example.com/api/v1/");

            EndpointClaim? claim = SignedEndpointReader.Read(
                envelope, "game-launcher-api", "production", publicKey);

            Assert.NotNull(claim);
            Assert.Equal("https://api.example.com/api/v1/", claim.BaseUrl);
        }
    }

    [Fact]
    public void RefusesAnAnswerSignedWithAnotherKey()
    {
        (string publicKey, ECDsa honest) = RegistrySigningFixture.NewKey();
        (_, ECDsa attacker) = RegistrySigningFixture.NewKey();

        using (honest)
        using (attacker)
        {
            string envelope = RegistrySigningFixture.SignedEndpoint(
                attacker, "https://evil.example.com/api/v1/");

            Assert.Null(SignedEndpointReader.Read(
                envelope, "game-launcher-api", "production", publicKey));
        }
    }

    [Fact]
    public void RefusesAPayloadChangedAfterSigning()
    {
        (string publicKey, ECDsa key) = RegistrySigningFixture.NewKey();
        using (key)
        {
            string envelope = RegistrySigningFixture.SignedEndpoint(key);
            string tampered = TamperWithPayload(envelope);

            Assert.Null(SignedEndpointReader.Read(
                tampered, "game-launcher-api", "production", publicKey));
        }
    }

    /// <summary>
    /// A registry signs every record it holds, so an answer about another service is perfectly
    /// valid and still not an answer to the question this launcher asked.
    /// </summary>
    [Fact]
    public void RefusesAValidAnswerAboutAnotherService()
    {
        (string publicKey, ECDsa key) = RegistrySigningFixture.NewKey();
        using (key)
        {
            string envelope = RegistrySigningFixture.SignedEndpoint(
                key, serviceKey: "some-other-service");

            Assert.Null(SignedEndpointReader.Read(
                envelope, "game-launcher-api", "production", publicKey));
        }
    }

    [Fact]
    public void RefusesAValidAnswerAboutAnotherEnvironment()
    {
        (string publicKey, ECDsa key) = RegistrySigningFixture.NewKey();
        using (key)
        {
            string envelope = RegistrySigningFixture.SignedEndpoint(key, environment: "staging");

            Assert.Null(SignedEndpointReader.Read(
                envelope, "game-launcher-api", "production", publicKey));
        }
    }

    /// <summary>
    /// A build with no key trusts no registry: the answer is refused rather than believed,
    /// which is what makes an empty <c>ServiceRegistryKey</c> a safe default.
    /// </summary>
    [Fact]
    public void RefusesEverythingWithoutAKey()
    {
        (_, ECDsa key) = RegistrySigningFixture.NewKey();
        using (key)
        {
            string envelope = RegistrySigningFixture.SignedEndpoint(key);

            Assert.Null(SignedEndpointReader.Read(envelope, "game-launcher-api", "production", null));
            Assert.Null(SignedEndpointReader.Read(envelope, "game-launcher-api", "production", ""));
        }
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("not json")]
    [InlineData("{}")]
    [InlineData("""{"payload":"not base64!","signature":"MEUCIQ=="}""")]
    [InlineData("""{"payload":"eyJhIjoxfQ=="}""")]
    [InlineData("""{"signature":"MEUCIQ=="}""")]
    public void RefusesAnythingThatIsNotAnEnvelope(string? body)
    {
        (string publicKey, ECDsa key) = RegistrySigningFixture.NewKey();
        using (key)
        {
            Assert.Null(SignedEndpointReader.Read(body, "game-launcher-api", "production", publicKey));
        }
    }

    /// <summary>
    /// A correctly signed claim naming an address the launcher will not talk to is refused
    /// here rather than handed to <see cref="Uri"/> and discovered later.
    /// </summary>
    [Theory]
    [InlineData("file:///c:/windows")]
    [InlineData("javascript:alert(1)")]
    [InlineData("/relative/path")]
    [InlineData("")]
    public void RefusesASignedClaimNamingAnUnusableAddress(string baseUrl)
    {
        (string publicKey, ECDsa key) = RegistrySigningFixture.NewKey();
        using (key)
        {
            string envelope = RegistrySigningFixture.SignedEndpoint(key, baseUrl);

            Assert.Null(SignedEndpointReader.Read(
                envelope, "game-launcher-api", "production", publicKey));
        }
    }

    private static string TamperWithPayload(string envelope)
    {
        using System.Text.Json.JsonDocument document = System.Text.Json.JsonDocument.Parse(envelope);
        byte[] payload = Convert.FromBase64String(
            document.RootElement.GetProperty("payload").GetString()!);
        payload[^3] ^= 0x01;

        string signature = document.RootElement.GetProperty("signature").GetString()!;
        return System.Text.Json.JsonSerializer.Serialize(new
        {
            payload = Convert.ToBase64String(payload),
            signature,
        });
    }
}

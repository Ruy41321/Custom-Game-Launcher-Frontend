using System.Security.Cryptography;
using System.Text;

namespace GameLauncher.Core.Tests.Updates;

/// <summary>
/// Two throwaway P-256 key pairs and one signature that <c>openssl</c> really produced.
///
/// Two keys, because half of what matters is that a signature which is perfectly valid
/// <i>under another key</i> is not a valid signature here — a check that only ever saw the
/// right key would pass whether or not it looked at the key at all.
///
/// The golden signature is here for a narrower reason: it was made by
/// <c>openssl dgst -sha256 -sign</c>, which is what the machine that cuts a release actually
/// runs, so it proves the client accepts the DER shape that tool emits rather than only the one
/// .NET emits. Everything else is signed in-process, where a test can tamper with the input.
/// </summary>
internal static class ReleaseSigningFixture
{
    /// <summary>PKCS#8, base64 DER. A test key: it signs nothing outside this assembly.</summary>
    public const string PrivateKeyBase64 =
        "MIGHAgEAMBMGByqGSM49AgEGCCqGSM49AwEHBG0wawIBAQQgNT6IMWroL6pEBV3XqOdMoaSQRw/0"
        + "vRThPZxLdie5dQChRANCAAS8vBHvNiGgLNSX1SjdvCaUKmDm0nlt4Kr3tMUupqo4hv3oDYb7pVhu"
        + "TqXc9jH0F/W9HSoD8nZWdCwyHtouQniM";

    /// <summary>SubjectPublicKeyInfo, base64 DER — the form both repositories configure.</summary>
    public const string PublicKeyBase64 =
        "MFkwEwYHKoZIzj0CAQYIKoZIzj0DAQcDQgAEvLwR7zYhoCzUl9Uo3bwmlCpg5tJ5beCq97TFLqaq"
        + "OIb96A2G+6VYbk6l3PYx9Bf1vR0qA/J2VnQsMh7aLkJ4jA==";

    /// <summary>The attacker's key: a different, equally real P-256 pair.</summary>
    public const string OtherPrivateKeyBase64 =
        "MIGHAgEAMBMGByqGSM49AgEGCCqGSM49AwEHBG0wawIBAQQgEA0Gos8B5u9BVvYLVTg+YfYj3WhP"
        + "IXK4Tp/azvOPzyWhRANCAAT61SVBlh9wRBnd7+gzqYdlnB1cfB9UrTK1AVIDhjUKOBQM3oDIdzsd"
        + "9O5v8raZJGjgcs4U6BDRdaEjyqXIDagF";

    public const string OtherPublicKeyBase64 =
        "MFkwEwYHKoZIzj0CAQYIKoZIzj0DAQcDQgAE+tUlQZYfcEQZ3e/oM6mHZZwdXHwfVK0ytQFSA4Y1"
        + "CjgUDN6AyHc7HfTub/K2mSRo4HLOFOgQ0XWhI8qlyA2oBQ==";

    /// <summary>
    /// Exactly the bytes <see cref="OpenSslSignatureBase64"/> covers, written with no trailing
    /// newline — which is the shape the server refuses a release for not having.
    /// </summary>
    public const string CanonicalDocument =
        """{"schema":1,"channel":"stable","version":"0.2.0","platform":"windows","arch":"x64","sha256":"9f86d081884c7d659a2feaa0c55ad015a3bf4f1b2b0b822cd15d6c15b0f00a08","size":83442176,"releasedAt":"2026-08-07T10:00:00Z","notes":"Self-update, at last."}""";

    /// <summary>Produced by <c>openssl dgst -sha256 -sign</c> over the bytes above.</summary>
    public const string OpenSslSignatureBase64 =
        "MEYCIQDH0tnXNXQ605T/AfxKDqfjkWWoqLGPbmu6rmOnUhDsawIhANesQ7Re/B2ijYCO5rkJWZ9y"
        + "wtMZpUrhY3ErPVuBukh0";

    public static string Sign(string document, string privateKeyBase64 = PrivateKeyBase64)
    {
        using ECDsa key = ECDsa.Create();
        key.ImportPkcs8PrivateKey(Convert.FromBase64String(privateKeyBase64), out _);

        return Convert.ToBase64String(
            key.SignData(
                Encoding.UTF8.GetBytes(document),
                HashAlgorithmName.SHA256,
                DSASignatureFormat.Rfc3279DerSequence));
    }

    /// <summary>A canonical document for any version, so a test can say what it means.</summary>
    public static string DocumentFor(
        string version,
        string channel = "stable",
        string platform = "windows",
        string arch = "x64",
        string notes = "Self-update, at last.") =>
        $$"""
        {"schema":1,"channel":"{{channel}}","version":"{{version}}","platform":"{{platform}}","arch":"{{arch}}","sha256":"9f86d081884c7d659a2feaa0c55ad015a3bf4f1b2b0b822cd15d6c15b0f00a08","size":83442176,"releasedAt":"2026-08-07T10:00:00Z","notes":"{{notes}}"}
        """;
}

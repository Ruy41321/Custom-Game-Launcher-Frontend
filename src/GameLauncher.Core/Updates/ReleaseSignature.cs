using System.Security.Cryptography;

namespace GameLauncher.Core.Updates;

/// <summary>
/// ECDSA over P-256 with SHA-256, checked against the key compiled into this binary.
///
/// The algorithm is <b>pinned</b> rather than read out of whatever key is configured, exactly
/// as the server pins it: an algorithm taken from the key would let a deployment be given an
/// RSA key that one side verifies happily and the other cannot read at all — a launcher that
/// stops updating for a reason nothing reports.
///
/// Ed25519 would be the better modern choice and is not in .NET 9's base class library, so
/// taking it would mean a native binding across four self-contained runtime identifiers or a
/// managed crypto library. <see cref="ECDsa"/> is already in the runtime, which is why the
/// server chose P-256 for the client's sake.
///
/// Nothing here throws. A malformed key, a malformed signature and a signature that simply
/// does not match are one answer — no — because a caller that had to tell them apart would be
/// a caller that could get it wrong.
/// </summary>
public static class ReleaseSignature
{
    /// <summary>The curve, by its object identifier: <c>prime256v1</c> / <c>secp256r1</c>.</summary>
    private const string P256Oid = "1.2.840.10045.3.1.7";

    /// <summary>
    /// Windows and Linux disagree about which half of an <see cref="Oid"/> a named curve comes
    /// back in — one populates the value, the other only the friendly name — so both are
    /// accepted rather than the check silently passing on one platform and failing on another.
    /// </summary>
    private static readonly string[] P256Names =
        ["nistP256", "ECDSA_P256", "secp256r1", "prime256v1"];

    /// <summary>
    /// Whether the key baked into this build is one this launcher can check a signature with.
    /// An empty key is the honest default for a fork that has not set up signing, and it means
    /// the launcher asks for nothing at all — rather than asking and trusting whoever answers.
    /// </summary>
    public static bool IsUsableKey(string? publicKeyBase64) =>
        TryImport(publicKeyBase64, out ECDsa? key) && Dispose(key);

    /// <summary>
    /// Verifies a detached signature over <paramref name="document"/> <b>as those bytes
    /// arrived</b>. Nothing is parsed, re-serialised or normalised first: a document that is
    /// not the one that was published must never become the one that gets installed.
    /// </summary>
    public static bool Verify(
        ReadOnlySpan<byte> document, string? signatureBase64, string? publicKeyBase64)
    {
        if (!TryDecode(signatureBase64, out byte[]? signature))
        {
            return false;
        }

        if (!TryImport(publicKeyBase64, out ECDsa? key))
        {
            return false;
        }

        using (key)
        {
            try
            {
                // Rfc3279DerSequence is the shape `openssl dgst -sha256 -sign` produces, which
                // is how a release is signed on the machine that cut it.
                return key.VerifyData(
                    document,
                    signature,
                    HashAlgorithmName.SHA256,
                    DSASignatureFormat.Rfc3279DerSequence);
            }
            catch (CryptographicException)
            {
                return false;
            }
        }
    }

    private static bool TryImport(string? publicKeyBase64, out ECDsa key)
    {
        key = null!;
        if (!TryDecode(publicKeyBase64, out byte[]? der))
        {
            return false;
        }

        ECDsa candidate = ECDsa.Create();
        try
        {
            candidate.ImportSubjectPublicKeyInfo(der, out int read);
            if (read != der.Length || candidate.KeySize != 256 || !IsP256(candidate))
            {
                candidate.Dispose();
                return false;
            }
        }
        catch (Exception exception) when (
            exception is CryptographicException or ArgumentException or NotSupportedException)
        {
            candidate.Dispose();
            return false;
        }

        key = candidate;
        return true;
    }

    private static bool IsP256(ECDsa key)
    {
        Oid curve = key.ExportParameters(includePrivateParameters: false).Curve.Oid;

        return string.Equals(curve.Value, P256Oid, StringComparison.Ordinal)
            || (curve.FriendlyName is { Length: > 0 } name
                && P256Names.Contains(name, StringComparer.OrdinalIgnoreCase));
    }

    private static bool TryDecode(string? base64, out byte[] bytes)
    {
        bytes = [];
        if (string.IsNullOrWhiteSpace(base64))
        {
            return false;
        }

        Span<byte> buffer = new byte[((base64.Length + 3) / 4) * 3];
        if (!Convert.TryFromBase64String(base64, buffer, out int written) || written == 0)
        {
            return false;
        }

        bytes = buffer[..written].ToArray();
        return true;
    }

    private static bool Dispose(ECDsa key)
    {
        key.Dispose();
        return true;
    }
}

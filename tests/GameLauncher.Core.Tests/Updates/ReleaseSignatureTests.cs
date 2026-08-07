using System.Security.Cryptography;
using System.Text;
using GameLauncher.Core.Updates;

namespace GameLauncher.Core.Tests.Updates;

public sealed class ReleaseSignatureTests
{
    private static byte[] Utf8(string text) => Encoding.UTF8.GetBytes(text);

    /// <summary>
    /// The interop check, and the only one here that would catch a disagreement between the
    /// tool a release is signed with and the runtime that checks it: this signature was made by
    /// <c>openssl dgst -sha256 -sign</c>, not by .NET.
    /// </summary>
    [Fact]
    public void ASignatureOpenSslProducedIsAccepted() =>
        Assert.True(ReleaseSignature.Verify(
            Utf8(ReleaseSigningFixture.CanonicalDocument),
            ReleaseSigningFixture.OpenSslSignatureBase64,
            ReleaseSigningFixture.PublicKeyBase64));

    [Fact]
    public void ASignatureThisRuntimeProducedIsAcceptedToo()
    {
        string signature = ReleaseSigningFixture.Sign(ReleaseSigningFixture.CanonicalDocument);

        Assert.True(ReleaseSignature.Verify(
            Utf8(ReleaseSigningFixture.CanonicalDocument),
            signature,
            ReleaseSigningFixture.PublicKeyBase64));
    }

    // One byte. A document that is not the one that was published must never become the one
    // that gets installed.
    [Fact]
    public void ADocumentChangedByOneByteIsRefused()
    {
        string tampered = ReleaseSigningFixture.CanonicalDocument.Replace(
            "\"version\":\"0.2.0\"", "\"version\":\"9.2.0\"", StringComparison.Ordinal);

        Assert.False(ReleaseSignature.Verify(
            Utf8(tampered),
            ReleaseSigningFixture.OpenSslSignatureBase64,
            ReleaseSigningFixture.PublicKeyBase64));
    }

    // Even a trailing newline an editor added: the signature covers bytes.
    [Fact]
    public void ATrailingNewlineIsADifferentDocument() =>
        Assert.False(ReleaseSignature.Verify(
            Utf8(ReleaseSigningFixture.CanonicalDocument + "\n"),
            ReleaseSigningFixture.OpenSslSignatureBase64,
            ReleaseSigningFixture.PublicKeyBase64));

    // Half of what matters: a perfectly valid signature *under another key* is not a valid
    // signature here. Without this a check that never looked at the key would pass.
    [Fact]
    public void AValidSignatureFromAnotherKeyIsNotAValidSignature()
    {
        string signature = ReleaseSigningFixture.Sign(
            ReleaseSigningFixture.CanonicalDocument, ReleaseSigningFixture.OtherPrivateKeyBase64);

        Assert.True(ReleaseSignature.Verify(
            Utf8(ReleaseSigningFixture.CanonicalDocument),
            signature,
            ReleaseSigningFixture.OtherPublicKeyBase64));

        Assert.False(ReleaseSignature.Verify(
            Utf8(ReleaseSigningFixture.CanonicalDocument),
            signature,
            ReleaseSigningFixture.PublicKeyBase64));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("not base64 at all!!")]
    [InlineData("YWJjZGVm")]
    public void ASignatureThatIsNotOneIsRefusedRatherThanThrown(string signature) =>
        Assert.False(ReleaseSignature.Verify(
            Utf8(ReleaseSigningFixture.CanonicalDocument),
            signature,
            ReleaseSigningFixture.PublicKeyBase64));

    [Fact]
    public void AUsableKeyIsAP256One()
    {
        Assert.True(ReleaseSignature.IsUsableKey(ReleaseSigningFixture.PublicKeyBase64));
        Assert.True(ReleaseSignature.IsUsableKey(ReleaseSigningFixture.OtherPublicKeyBase64));
    }

    // Empty is the default, and it is what makes a fork that has not set up signing check for
    // no updates at all rather than check and trust whoever answers.
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("this is not a key")]
    public void AnAbsentOrUnreadableKeyIsNotUsable(string? key)
    {
        Assert.False(ReleaseSignature.IsUsableKey(key));
        Assert.False(ReleaseSignature.Verify(
            Utf8(ReleaseSigningFixture.CanonicalDocument),
            ReleaseSigningFixture.OpenSslSignatureBase64,
            key));
    }

    // The algorithm is pinned, so a deployment given an RSA key is a launcher that checks for
    // nothing — never one that verifies with an algorithm nobody chose.
    [Fact]
    public void AKeyThatIsNotP256IsNotUsable()
    {
        using RSA rsa = RSA.Create(2048);
        string key = Convert.ToBase64String(rsa.ExportSubjectPublicKeyInfo());

        Assert.False(ReleaseSignature.IsUsableKey(key));
    }

    [Fact]
    public void AnEcKeyOnAnotherCurveIsNotUsable()
    {
        using ECDsa key = ECDsa.Create(ECCurve.NamedCurves.nistP384);

        Assert.False(ReleaseSignature.IsUsableKey(
            Convert.ToBase64String(key.ExportSubjectPublicKeyInfo())));
    }

    // The shipped repository carries no key on purpose: this launcher is the fork template,
    // and the key is the one thing a fork changes in code.
    [Fact]
    public void TheKeyShippedInThisRepositoryIsEmpty() =>
        Assert.Empty(LauncherReleaseKey.PublicKeyBase64);
}

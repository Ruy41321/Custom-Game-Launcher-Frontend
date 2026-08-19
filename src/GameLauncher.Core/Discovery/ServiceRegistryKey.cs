namespace GameLauncher.Core.Discovery;

/// <summary>
/// The public key a launcher checks a registry answer against.
///
/// Compiled in rather than configured, for the same reason
/// <see cref="Updates.LauncherReleaseKey"/> is: <c>launcher.config.json</c> ships inside the
/// directory a self-update replaces, so a key kept there would be replaced by whatever the
/// update brought with it. The registry <i>URL</i> may live in that file safely — pointing a
/// launcher at a hostile registry gains an attacker nothing, because the answer it returns
/// will not carry a signature this key accepts.
///
/// <b>Empty is the default, and it means this build asks no registry anything</b> — rather
/// than asking and believing whoever answers. A fork that has not set up a registry keeps the
/// endpoint in <c>launcher.config.json</c> and behaves exactly as it always has.
///
/// To set it, put the base64 DER <c>SubjectPublicKeyInfo</c> of the registry's P-256 public
/// key here — the string <c>servicereg keygen</c> prints, and the one the admin panel shows
/// under "Signing key":
///
/// <code>
/// docker compose run --rm registry keygen
/// </code>
///
/// The private half never leaves the registry host.
/// </summary>
public static class ServiceRegistryKey
{
    /// <summary>
    /// A property rather than a <c>const</c> on purpose: a constant empty string would let the
    /// compiler fold every check against it, so a fork setting the key would be changing a
    /// value the surrounding code had already been compiled around.
    /// </summary>
    public static string PublicKeyBase64 => string.Empty;
}

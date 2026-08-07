namespace GameLauncher.Core.Updates;

/// <summary>
/// The public key a launcher checks a release signature against.
///
/// <b>This is the one thing a fork has to change in code</b>, and it is deliberately not a
/// field of <c>launcher.config.json</c>. The reason is not that a file is easier to edit than
/// a binary: it is that <b>the file the updater overwrites must not be the file that authorizes
/// the update.</b> <c>launcher.config.json</c> ships inside the directory a swap replaces, so a
/// key kept there would be replaced by whatever the update brought with it. A constant compiled
/// into the launcher lives inside the artifact whose replacement is itself protected by the
/// signature.
///
/// <b>Empty is the default, and it means this build checks for no updates at all</b> — rather
/// than checking and trusting whoever answers. That is the correct state for a fork that has
/// not set up signing yet, and it is the same answer the server arrives at from the other end:
/// with no key configured there, the release surface is off.
///
/// To set it, put the base64 DER <c>SubjectPublicKeyInfo</c> of the P-256 public key here — the
/// same string the server takes in <c>LAUNCHER_RELEASE_PUBLIC_KEY</c>:
///
/// <code>
/// openssl ec -in release-signing.key -pubout -outform DER | openssl base64 -A
/// </code>
///
/// The private half never comes near either repository. See
/// <c>Documentation/self-update.md</c>.
/// </summary>
public static class LauncherReleaseKey
{
    /// <summary>
    /// A property rather than a <c>const</c> on purpose: a constant empty string would let the
    /// compiler fold every check against it, so a fork setting the key would be changing a
    /// value the surrounding code had already been compiled around.
    /// </summary>
    public static string PublicKeyBase64 => string.Empty;
}

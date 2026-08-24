using GameLauncher.Core.Api;
using GameLauncher.Core.Platform;

namespace GameLauncher.Core.Configuration;

/// <summary>
/// Turns the two paths in <see cref="BrandingConfiguration"/> into absolute ones, or into
/// nothing at all.
///
/// <b>A path that does not resolve is answered with null rather than an exception</b>, which is
/// the same reasoning <c>updates.channel</c> follows: these strings come out of a file the
/// person running the launcher edited by hand, so a typo must cost them a logo and never the
/// program. A missing asset is already "simply not shown", and an unusable one is the same
/// situation reached a different way.
///
/// Resolution goes through <see cref="PathSafety.ResolveInside"/> rather than a second
/// containment check written here, so there is one implementation of "inside this directory"
/// in the client. Its refusal is a throw, because the paths it was written for arrive over the
/// network; here the throw is turned into a null, and the two cases it catches are worth
/// naming — an absolute path, which <see cref="Path.Combine(string, string)"/> resolves to
/// itself and which is the likeliest way to write one of these by mistake, and a
/// <c>..</c> that climbs out of the installation.
/// </summary>
public static class BrandingPaths
{
    /// <summary>
    /// The absolute path of a branding asset, or null when none is configured or the
    /// configured one does not name a file inside the application directory.
    /// </summary>
    /// <remarks>
    /// Whether the file is really there is the caller's question: this project keeps Core free
    /// of I/O, and the answer would be stale by the time it were used anyway.
    /// </remarks>
    public static string? Resolve(string applicationDirectory, string? configuredPath)
    {
        if (string.IsNullOrWhiteSpace(configuredPath))
        {
            return null;
        }

        try
        {
            // Both separators, because this string comes out of a file that ships to both
            // platforms unchanged: `PathSafety` maps `/` to the platform's own, and on Linux
            // a `\` is an ordinary character in a filename rather than a separator, so a
            // path written on Windows would name one file that does not exist instead of a
            // file inside a directory.
            string normalised = configuredPath.Trim().Replace('\\', '/');

            return PathSafety.ResolveInside(applicationDirectory, normalised);
        }
        catch (ApiException)
        {
            return null;
        }
        catch (ArgumentException)
        {
            // An invalid character for this platform — a path written on the other one.
            return null;
        }
    }
}

using System.Security.Cryptography;
using System.Text;
using GameLauncher.Core.Platform;

namespace GameLauncher.Infrastructure.Downloads;

/// <summary>
/// Turns the names in a manifest into paths on this machine. Every one of them arrived over the
/// network, so none of them is allowed to decide where the launcher writes.
/// </summary>
internal static class InstallPaths
{
    /// <summary>
    /// Resolves a manifest path under <paramref name="root"/>. The rule itself lives in Core
    /// because launching a game applies it to the entrypoint, and one rule with two
    /// implementations is one rule that will eventually disagree with itself.
    /// </summary>
    public static string Inside(string root, string relativePath) =>
        PathSafety.ResolveInside(root, relativePath);

    /// <summary>
    /// Where a blob waits between arriving and being applied. Content-addressed with the same
    /// two-level fan-out the server uses, so a directory never fills up and a transfer that
    /// was interrupted is found again by name rather than by remembering it.
    /// </summary>
    public static string StagedBlob(string stagingRoot, string buildId, string sha256) =>
        Path.Combine(
            stagingRoot, Token(buildId), Prefix(sha256, 0), Prefix(sha256, 2), Safe(sha256));

    public static string StagingForBuild(string stagingRoot, string buildId) =>
        Path.Combine(stagingRoot, Token(buildId));

    /// <summary>A directory of its own, under the default root, named after the game.</summary>
    public static string DefaultInstallDirectory(string root, string slug, string gameId) =>
        Path.Combine(root, Readable(slug) ?? Token(gameId));

    /// <summary>
    /// A server-supplied identifier reduced to something that is certainly a single directory
    /// name. Deriving it rather than validating it means there is no shape to get wrong, and it
    /// is stable across restarts, which is what lets an interrupted transfer be found again.
    /// </summary>
    private static string Token(string value) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(value)))[..16];

    private static string? Readable(string slug)
    {
        string kept = new([.. slug.Where(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_')]);
        return kept.Length is > 0 and <= 64 ? kept : null;
    }

    private static string Prefix(string sha256, int start) =>
        sha256.Length >= start + 2 ? Safe(sha256[start..(start + 2)]) : "00";

    private static string Safe(string value) =>
        new([.. value.Where(char.IsAsciiLetterOrDigit)]);
}

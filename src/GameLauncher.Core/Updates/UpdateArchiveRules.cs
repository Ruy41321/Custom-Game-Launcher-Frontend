using GameLauncher.Core.Api;
using GameLauncher.Core.Platform;
using GameLauncher.Core.Publishing;

namespace GameLauncher.Core.Updates;

/// <summary>
/// Which names an update archive is allowed to carry.
///
/// The hash check already happened: an archive that reaches this point holds the exact bytes a
/// signed document named. What is left is a different and entirely real case — an archive that
/// is <b>correctly signed and hostile in its entry names</b>, carrying <c>../..</c> or an
/// absolute path so that unpacking it writes outside the directory it was unpacked into.
///
/// Nothing new is invented for it. The rules are <see cref="ManifestPathRules"/> and
/// <see cref="PathSafety"/>, which exist for D24's reason — <i>a client that writes wherever it
/// is told is one compromised server away from writing into the user's startup folder</i> — and
/// which the install path already applies to every file of every build. A second implementation
/// of a security rule is a rule that will eventually disagree with itself.
/// </summary>
public static class UpdateArchiveRules
{
    /// <summary>
    /// Zip entries for directories carry no content and end in a separator, which the path
    /// rules refuse as an empty segment. They are skipped rather than refused: the directories
    /// are created by the files inside them.
    /// </summary>
    public static bool IsDirectoryEntry(string entryName) =>
        entryName.Length == 0 || entryName[^1] is '/' or '\\';

    /// <summary>Where <paramref name="entryName"/> may be written, or a refusal.</summary>
    /// <exception cref="ApiException">
    /// <see cref="ApiErrorCode.Integrity"/> for a name this launcher will not write.
    /// </exception>
    public static string ResolveInside(string root, string entryName)
    {
        string? refusal = ManifestPathRules.Reject(entryName);
        if (refusal is not null)
        {
            throw new ApiException(
                ApiErrorCode.Integrity,
                $"The update archive names a file this launcher will not write: {refusal}");
        }

        return PathSafety.ResolveInside(root, entryName);
    }
}

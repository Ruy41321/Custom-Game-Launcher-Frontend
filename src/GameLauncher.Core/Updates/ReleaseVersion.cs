using System.Globalization;

namespace GameLauncher.Core.Updates;

/// <summary>
/// The three-component version a launcher release carries.
///
/// Comparison is <b>numeric on all three components</b>, because compared as text
/// <c>0.10.0</c> sorts before <c>0.9.0</c> — and the whole defence against a replayed but
/// correctly signed old document is refusing anything that is not strictly newer than what is
/// running. A signature cannot answer that question by itself.
/// </summary>
public readonly record struct ReleaseVersion(int Major, int Minor, int Patch)
{
    /// <summary>
    /// Accepts only the full <c>major.minor.patch</c> form, spelled exactly as it round-trips.
    /// <c>0.2</c> and <c>0.2.0</c> are one version written two ways, and the server refuses the
    /// short spelling at publish time for the same reason: two spellings of one number are two
    /// rows racing to be the newest.
    /// </summary>
    public static bool TryParse(string? text, out ReleaseVersion version)
    {
        version = default;
        if (string.IsNullOrEmpty(text))
        {
            return false;
        }

        string[] parts = text.Split('.');
        if (parts.Length != 3)
        {
            return false;
        }

        int[] numbers = new int[3];
        for (int index = 0; index < 3; index++)
        {
            // NumberStyles.None refuses a sign, a thousands separator and surrounding
            // whitespace, all of which int.Parse would otherwise accept into a version.
            if (!int.TryParse(
                    parts[index], NumberStyles.None, CultureInfo.InvariantCulture, out numbers[index]))
            {
                return false;
            }
        }

        ReleaseVersion parsed = new(numbers[0], numbers[1], numbers[2]);

        // The round trip is what rejects a leading zero: "0.02.0" parses and is not the text
        // anybody published.
        if (!string.Equals(parsed.ToString(), text, StringComparison.Ordinal))
        {
            return false;
        }

        version = parsed;
        return true;
    }

    /// <summary>Strictly newer. Equal is not newer, which is the point.</summary>
    public bool IsNewerThan(ReleaseVersion other) =>
        (Major, Minor, Patch).CompareTo((other.Major, other.Minor, other.Patch)) > 0;

    public override string ToString() =>
        string.Create(CultureInfo.InvariantCulture, $"{Major}.{Minor}.{Patch}");
}

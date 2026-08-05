using System.Text.Json;
using GameLauncher.Core.Diagnostics;
using GameLauncher.Infrastructure.Configuration;

namespace GameLauncher.Infrastructure.Logging;

/// <summary>
/// Where a pending crash report lives on disk, and how it is written and read back.
///
/// The file **is** the request body: one JSON document, in the shape the server accepts, so
/// there is nothing to parse into and nothing to keep in step. A previous version wrote a
/// human-readable block of text, which would have needed a parser here and a second definition
/// of the same facts — and the two would have drifted the first time a field was added.
///
/// It is still readable by a person: the document is indented, and the rolling log beside it
/// carries the same exception in full and unredacted, because that copy never leaves the
/// machine.
/// </summary>
public static class CrashReportFiles
{
    /// <summary>Matches every pending report and nothing else in the log directory.</summary>
    public const string SearchPattern = "crash-*.json";

    /// <summary>
    /// Names a file for the moment it describes. The timestamp is first so that a directory
    /// listing is in the order the crashes happened, and the kind is in the name so somebody
    /// looking at the folder can tell them apart without opening one.
    /// </summary>
    public static string NameFor(DateTimeOffset occurredAt, string kind) =>
        $"crash-{occurredAt.UtcDateTime:yyyyMMdd-HHmmssfff}-{Sanitized(kind)}.json";

    public static string Serialize(CrashReport report) =>
        JsonSerializer.Serialize(report, LauncherJsonSerializer.Options);

    /// <summary>
    /// Reads one back. Null for anything that is not a report this launcher wrote — a truncated
    /// file from a crash *during* the crash handler, or something else that happens to match
    /// the pattern. The caller discards those rather than retrying them forever.
    /// </summary>
    public static CrashReport? Deserialize(string json)
    {
        try
        {
            CrashReport? report =
                JsonSerializer.Deserialize<CrashReport>(json, LauncherJsonSerializer.Options);

            return string.IsNullOrWhiteSpace(report?.Kind) ? null : report;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>Keeps the kind to something that is safe in a file name whatever a caller passed.</summary>
    private static string Sanitized(string kind)
    {
        string trimmed = new(kind.Where(char.IsAsciiLetterOrDigit).ToArray());
        return trimmed.Length == 0 ? "crash" : trimmed;
    }
}

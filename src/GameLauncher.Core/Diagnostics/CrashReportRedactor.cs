using System.Text.RegularExpressions;

namespace GameLauncher.Core.Diagnostics;

/// <summary>
/// Takes the machine's owner back out of a crash report.
///
/// A .NET exception carries paths, and the ones that matter are the user's: an
/// <c>IOException</c> reads "could not open C:\Users\luigi\Games\...", and a person's name in
/// their home directory is the single most likely way for a crash report to carry a person.
/// The install directory is the same problem with a different prefix, because it is somewhere
/// they chose.
///
/// **The replacement happens where the report is written, not where it is uploaded.** The file
/// on disk is what gets sent, so redacting at upload time would leave the unredacted copy
/// sitting in the log directory of a machine whose owner asked for the opposite — and would
/// mean the thing that was reviewed was not the thing that was sent. The full exception is
/// still in the rolling log beside it, which never leaves the machine.
///
/// This is a *reduction* of risk, not a guarantee: a message can carry anything a caller put in
/// it. That is why the server stores no account against a report either — two partial measures
/// that fail differently, rather than one that is trusted.
/// </summary>
public static partial class CrashReportRedactor
{
    /// <summary>
    /// What a redacted path is replaced with. Deliberately recognisable: somebody reading the
    /// report should be able to tell that something was removed rather than wonder why a path
    /// looks odd.
    ///
    /// Square brackets rather than angle ones, and not for taste: <c>System.Text.Json</c>
    /// escapes <c>&lt;</c> and <c>&gt;</c> by default, so an angled placeholder lands in every
    /// stored report as <c><redacted></c> — correct on the wire and unreadable in the
    /// file, which is half of what the file is for.
    /// </summary>
    public const string Placeholder = "[redacted]";

    /// <summary>
    /// Replaces every occurrence of the paths this machine knows are the user's. Longest first,
    /// so an install directory *inside* the home directory is replaced as a whole rather than
    /// leaving a half-substituted path behind.
    /// </summary>
    public static string Redact(string text, params string?[] paths)
    {
        if (string.IsNullOrEmpty(text))
        {
            return text;
        }

        foreach (string path in paths
            .Where(candidate => !string.IsNullOrWhiteSpace(candidate))
            .Select(candidate => candidate!.TrimEnd('/', '\\'))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderByDescending(candidate => candidate.Length))
        {
            // Case-insensitive because Windows paths compare that way and a message may carry
            // either casing; ordinal because a path is not language.
            text = text.Replace(path, Placeholder, StringComparison.OrdinalIgnoreCase);
        }

        return RedactRemainingUserDirectories(text);
    }

    /// <summary>
    /// Applies <see cref="Redact(string, string?[])"/> to every free-text field of a report.
    /// The rest — the kind, the version, the platform — is fixed vocabulary.
    /// </summary>
    public static CrashReport Redact(CrashReport report, params string?[] paths) => report with
    {
        Message = Redact(report.Message, paths),
        StackTrace = Redact(report.StackTrace, paths),
        ExceptionType = Redact(report.ExceptionType, paths),
    };

    /// <summary>
    /// The backstop: a home directory that is not *this* machine's.
    ///
    /// It happens — a path baked into a build by whoever compiled it, or a second profile on
    /// the same box — and the pattern is narrow enough to be safe: only the name immediately
    /// under a known home root goes, never the rest of the path, so the report still says which
    /// file it was.
    /// </summary>
    private static string RedactRemainingUserDirectories(string text) =>
        HomeDirectoryPattern().Replace(text, match => match.Groups[1].Value + Placeholder);

    [GeneratedRegex(
        @"([A-Za-z]:\\Users\\|/home/|/Users/)[^\\/\r\n""']+",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex HomeDirectoryPattern();
}

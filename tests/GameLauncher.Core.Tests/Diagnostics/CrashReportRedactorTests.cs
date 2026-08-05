using GameLauncher.Core.Diagnostics;

namespace GameLauncher.Core.Tests.Diagnostics;

public sealed class CrashReportRedactorTests
{
    private const string Home = @"C:\Users\luigi";

    /// <summary>
    /// The case this exists for: an IOException carries the path it failed on, and a person's
    /// name in their home directory is the likeliest way a crash report carries a person.
    /// </summary>
    [Fact]
    public void TakesTheUsersHomeDirectoryOutOfAMessage()
    {
        string redacted = CrashReportRedactor.Redact(
            $@"Could not open {Home}\Games\Orbital\data.pak", Home);

        Assert.DoesNotContain("luigi", redacted, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(CrashReportRedactor.Placeholder, redacted, StringComparison.Ordinal);
    }

    // What is left has to still say which file it was, or the report stops being diagnostic.
    [Fact]
    public void KeepsEverythingBelowTheRedactedPart()
    {
        string redacted = CrashReportRedactor.Redact(
            $@"Could not open {Home}\Games\Orbital\data.pak", Home);

        Assert.Contains(@"\Games\Orbital\data.pak", redacted, StringComparison.Ordinal);
    }

    // Windows paths compare case-insensitively, and a message may carry either casing.
    [Fact]
    public void MatchesWhateverCasingThePathArrivedIn()
    {
        string redacted = CrashReportRedactor.Redact(@"at C:\USERS\LUIGI\app.dll", Home);

        Assert.DoesNotContain("LUIGI", redacted, StringComparison.Ordinal);
    }

    /// <summary>
    /// Longest first, so an install directory *inside* the home directory is replaced whole
    /// rather than leaving half a substituted path behind.
    /// </summary>
    [Fact]
    public void ReplacesTheLongestPathFirst()
    {
        string install = $@"{Home}\Games";

        string redacted = CrashReportRedactor.Redact($@"{install}\Orbital\a.pak", Home, install);

        Assert.Equal($@"{CrashReportRedactor.Placeholder}\Orbital\a.pak", redacted);
    }

    /// <summary>
    /// The backstop: a home directory that is not this machine's — baked into a build by
    /// whoever compiled it, or a second profile on the same box.
    /// </summary>
    [Theory]
    [InlineData(@"at D:\Users\someone\src\File.cs:line 4", "someone")]
    [InlineData("at /home/builder/src/File.cs:line 4", "builder")]
    [InlineData("at /Users/ci-runner/src/File.cs:line 4", "ci-runner")]
    public void RemovesAUserDirectoryThisMachineDoesNotKnowAbout(string text, string name)
    {
        string redacted = CrashReportRedactor.Redact(text, Home);

        Assert.DoesNotContain(name, redacted, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("File.cs", redacted, StringComparison.Ordinal);
    }

    [Fact]
    public void LeavesTextWithNothingToRedactAlone()
    {
        const string text = "System.InvalidOperationException: the collection was modified";

        Assert.Equal(text, CrashReportRedactor.Redact(text, Home));
    }

    [Fact]
    public void SurvivesAnEmptyOrAbsentPath()
    {
        Assert.Equal("anything", CrashReportRedactor.Redact("anything", null, "", "   "));
        Assert.Equal(string.Empty, CrashReportRedactor.Redact(string.Empty, Home));
    }

    [Fact]
    public void RedactsEveryFreeTextFieldOfAReport()
    {
        CrashReport report = new()
        {
            Kind = "unhandled",
            OccurredAt = DateTimeOffset.UnixEpoch,
            Message = $@"could not open {Home}\a.pak",
            StackTrace = $@"at Loader.Load() in {Home}\src\Loader.cs:line 9",
        };

        CrashReport redacted = CrashReportRedactor.Redact(report, Home);

        Assert.DoesNotContain("luigi", redacted.Message, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("luigi", redacted.StackTrace, StringComparison.OrdinalIgnoreCase);
        // The fixed vocabulary is left alone: it cannot carry a person.
        Assert.Equal("unhandled", redacted.Kind);
        Assert.Equal(DateTimeOffset.UnixEpoch, redacted.OccurredAt);
    }
}

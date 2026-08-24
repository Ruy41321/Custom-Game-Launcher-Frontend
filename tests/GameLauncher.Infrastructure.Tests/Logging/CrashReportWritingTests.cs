using GameLauncher.Core.Diagnostics;
using GameLauncher.Core.Platform;
using GameLauncher.Infrastructure.Logging;
using NSubstitute;

namespace GameLauncher.Infrastructure.Tests.Logging;

/// <summary>
/// What actually lands on disk when the launcher dies. This is the privacy guarantee rather
/// than a description of one: the file written here is the request body sent later, so what it
/// contains is what leaves the machine.
/// </summary>
public sealed class CrashReportWritingTests : IDisposable
{
    private readonly TemporaryDirectory _root = new();
    private readonly IPathProvider _paths = Substitute.For<IPathProvider>();

    public CrashReportWritingTests()
    {
        _paths.LogDirectory.Returns(_root.Path);
        _paths.UserDataDirectory.Returns(Path.Combine(_root.Path, "userdata"));
        _paths.DefaultInstallDirectory.Returns(Path.Combine(_root.Path, "games"));
    }

    public void Dispose() => _root.Dispose();

    private CrashReport WriteAndRead(Exception exception, string kind = "unhandled")
    {
        LauncherLogging.WriteCrashReport(_paths, exception, kind);

        string[] files = Directory.GetFiles(_root.Path, CrashReportFiles.SearchPattern);
        Assert.Single(files);

        CrashReport? report = CrashReportFiles.Deserialize(File.ReadAllText(files[0]));
        Assert.NotNull(report);
        return report;
    }

    [Fact]
    public void WritesTheDocumentTheServerAccepts()
    {
        CrashReport report = WriteAndRead(new InvalidOperationException("the thing broke"));

        Assert.Equal("unhandled", report.Kind);
        Assert.Equal("System.InvalidOperationException", report.ExceptionType);
        Assert.Equal("the thing broke", report.Message);
        Assert.NotEqual(default, report.OccurredAt);
    }

    // ToString() rather than StackTrace, because the inner exception is usually the one that
    // says what actually went wrong.
    [Fact]
    public void KeepsTheInnerExceptionThatSaysWhatWentWrong()
    {
        CrashReport report = WriteAndRead(
            new InvalidOperationException("outer", new IOException("the disk is full")));

        Assert.Contains("the disk is full", report.StackTrace, StringComparison.Ordinal);
    }

    /// <summary>
    /// Redacted where it is written, not where it is uploaded: otherwise the unredacted copy
    /// would sit in the log directory of a machine whose owner asked for the opposite.
    /// </summary>
    [Fact]
    public void TakesTheUsersOwnDirectoriesOutBeforeAnythingIsStored()
    {
        string userData = Path.Combine(_root.Path, "userdata");

        CrashReport report = WriteAndRead(
            new IOException($"could not open {userData}{Path.DirectorySeparatorChar}session.json"));

        Assert.DoesNotContain(userData, report.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(
            CrashReportRedactor.Placeholder, report.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void NamesTheFileSoADirectoryListingIsInTheOrderTheCrashesHappened()
    {
        LauncherLogging.WriteCrashReport(_paths, new IOException("first"), "startup");
        Thread.Sleep(2);
        LauncherLogging.WriteCrashReport(_paths, new IOException("second"), "unhandled");

        string[] files = Directory.GetFiles(_root.Path, CrashReportFiles.SearchPattern);
        Array.Sort(files, StringComparer.Ordinal);

        Assert.Equal(2, files.Length);
        Assert.Contains("startup", files[0], StringComparison.Ordinal);
        Assert.Contains("unhandled", files[1], StringComparison.Ordinal);
    }

    // A kind is used in a file name, so whatever a caller passes has to be safe there.
    [Fact]
    public void SurvivesAKindThatWouldNotBeAValidFileName()
    {
        LauncherLogging.WriteCrashReport(_paths, new IOException("x"), "un/handled:*");

        Assert.Single(Directory.GetFiles(_root.Path, CrashReportFiles.SearchPattern));
    }
}

using GameLauncher.Core.Api;
using GameLauncher.Core.Updates;

namespace GameLauncher.Core.Tests.Updates;

public sealed class UpdateSwapRequestTests
{
    private static readonly UpdateSwapRequest Swap = new()
    {
        SourceDirectory = @"C:\Users\somebody\AppData\Local\CustomGameLauncher\updates\0.5.0\staged",
        TargetDirectory = @"C:\Program Files\Custom Game Launcher",
        WaitForProcessId = 4242,
        RelaunchExecutable = @"C:\Program Files\Custom Game Launcher\GameLauncher.exe",
    };

    /// <summary>
    /// The launcher builds this command line and the updater parses it, in two processes that
    /// only meet on somebody's machine. One definition, and a test that really goes round.
    /// </summary>
    [Fact]
    public void WhatTheLauncherBuildsIsWhatTheUpdaterParses()
    {
        UpdateSwapRequest? parsed = UpdateSwapRequest.TryParse(
            [.. Swap.ToArguments()], out string? error);

        Assert.Null(error);
        Assert.Equal(Swap, parsed);
    }

    [Fact]
    public void ARollbackRoundTripsToo()
    {
        UpdateSwapRequest rollback = new()
        {
            SourceDirectory = string.Empty,
            TargetDirectory = Swap.TargetDirectory,
            RelaunchExecutable = Swap.RelaunchExecutable,
            RollbackOnly = true,
        };

        UpdateSwapRequest? parsed = UpdateSwapRequest.TryParse(
            [.. rollback.ToArguments()], out string? error);

        Assert.Null(error);
        Assert.Equal(rollback, parsed);
    }

    [Theory]
    [InlineData("--target")]
    [InlineData("--source", "one")]
    [InlineData("--source", "one", "--target")]
    [InlineData("--source", "one", "--target", "two", "--wait-for-pid", "not-a-pid")]
    [InlineData("--source", "one", "--target", "two", "--wait-for-pid", "-1")]
    [InlineData("--rollback", "--source", "one", "--target", "two")]
    [InlineData("--swap-everything", "now")]
    public void AnIncompleteOrUnknownCommandLineIsRefusedWithAReason(params string[] arguments)
    {
        Assert.Null(UpdateSwapRequest.TryParse(arguments, out string? error));
        Assert.False(string.IsNullOrWhiteSpace(error));
    }

    [Fact]
    public void WaitingForNobodyIsAllowed()
    {
        UpdateSwapRequest? parsed = UpdateSwapRequest.TryParse(
            ["--source", "one", "--target", "two"], out string? error);

        Assert.Null(error);
        Assert.Equal(0, parsed!.WaitForProcessId);
        Assert.Null(parsed.RelaunchExecutable);
    }
}

public sealed class UpdateArchiveRulesTests
{
    [Theory]
    [InlineData("../evil.dll")]
    [InlineData("../../Startup/evil.exe")]
    [InlineData("/etc/cron.d/evil")]
    [InlineData(@"C:\Windows\System32\evil.dll")]
    [InlineData(@"subdir\evil.dll")]
    [InlineData("")]
    public void AnArchiveThatNamesAFileOutsideTheDirectoryIsRefused(string entryName)
    {
        ApiException exception = Assert.Throws<ApiException>(
            () => UpdateArchiveRules.ResolveInside(Path.GetTempPath(), entryName));

        Assert.Equal(ApiErrorCode.Integrity, exception.Code);
    }

    [Fact]
    public void AnOrdinaryEntryResolvesInsideTheDirectory()
    {
        string root = Path.Combine(Path.GetTempPath(), "staged");

        Assert.Equal(
            Path.Combine(root, "runtimes", "win-x64", "native", "av_libglesv2.dll"),
            UpdateArchiveRules.ResolveInside(root, "runtimes/win-x64/native/av_libglesv2.dll"));
    }

    [Theory]
    [InlineData("runtimes/")]
    [InlineData("")]
    public void ADirectoryEntryIsSkippedRatherThanRefused(string entryName) =>
        Assert.True(UpdateArchiveRules.IsDirectoryEntry(entryName));
}

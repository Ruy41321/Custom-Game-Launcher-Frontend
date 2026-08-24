using GameLauncher.Core.Api;
using GameLauncher.Core.Updates;

namespace GameLauncher.Core.Tests.Updates;

/// <summary>
/// Everything about the swap that can be decided without moving a file. It is a deliberate
/// amount: the decision is a pure function of (exit code, elapsed time), which is the only shape
/// in which the hardest-to-test piece in the repository becomes a test.
/// </summary>
public sealed class SwapDecisionTests
{
    [Fact]
    public void StillRunningWhenTheWindowClosesIsASuccess() =>
        Assert.Equal(
            SwapVerdict.Succeeded,
            RelaunchWatch.Judge(exitCode: null, RelaunchWatch.Window));

    [Fact]
    public void AnExitOfZeroIsASuccessAtAnyPoint() =>
        Assert.Equal(
            SwapVerdict.Succeeded,
            RelaunchWatch.Judge(exitCode: 0, TimeSpan.FromSeconds(2)));

    [Fact]
    public void ANonZeroExitInsideTheWindowIsARestore() =>
        Assert.Equal(
            SwapVerdict.Restore,
            RelaunchWatch.Judge(exitCode: 1, TimeSpan.FromSeconds(2)));

    /// <summary>
    /// The declared hole, asserted as intended behaviour. From here a launcher that ran for
    /// half a minute and then failed is indistinguishable from one somebody used and closed,
    /// and rolling that back would undo working updates.
    /// </summary>
    [Fact]
    public void ANonZeroExitAfterTheWindowIsNotARestore() =>
        Assert.Equal(
            SwapVerdict.Succeeded,
            RelaunchWatch.Judge(exitCode: 1, RelaunchWatch.Window + TimeSpan.FromSeconds(1)));

    [Fact]
    public void TheOldInstallationIsRenamedBesideTheTargetSoTheMoveStaysOnOneFilesystem()
    {
        // Rooted at the platform's own temporary directory rather than at a literal `C:`,
        // which is a drive on one of the two supported platforms and an ordinary relative
        // segment on the other: there `PreviousOf` resolved it against the working directory
        // and the assertion compared an absolute path with a relative one. It failed only on
        // Linux, where this suite does not run — see CLAUDE.md §9.
        string parent = Path.Combine(Path.GetTempPath(), "Apps");

        string previous = UpdateSwapPaths.PreviousOf(Path.Combine(parent, "Launcher"));

        Assert.Equal(Path.Combine(parent, "Launcher.previous"), previous);
    }

    [Fact]
    public void AnInstallationAtAVolumeRootIsRefusedBeforeAnythingMoves()
    {
        ApiException exception = Assert.Throws<ApiException>(
            () => UpdateSwapPaths.PreviousOf(Path.GetPathRoot(Path.GetTempPath())!));

        Assert.Equal(ApiErrorCode.Integrity, exception.Code);
    }
}

using GameLauncher.Core.Installs;
using GameLauncher.Core.Launching;
using GameLauncher.Core.Models;

namespace GameLauncher.Core.Tests.Launching;

public sealed class LaunchPlannerTests
{
    private static readonly string Root = Path.Combine(Path.GetTempPath(), "orbital-drift");

    private static InstalledGame Install(
        string entrypoint = "bin/Game.exe",
        string launchArgs = "",
        string launchOptions = "",
        InstallState state = InstallState.Installed) => new()
        {
            GameId = "g1",
            GameTitle = "Orbital Drift",
            InstallDirectory = Root,
            Entrypoint = entrypoint,
            LaunchArgs = launchArgs,
            LaunchOptions = launchOptions,
            Platform = GamePlatform.Windows,
            State = state,
        };

    private static LaunchPlan PlanFor(InstalledGame? install, string? extra = null) =>
        LaunchPlanner.PlanFor(install, extra ?? install?.LaunchOptions, _ => true);

    [Fact]
    public void TheGameStartsFromItsOwnDirectory()
    {
        LaunchPlan plan = PlanFor(Install());

        Assert.Equal(Path.Combine(Root, "bin", "Game.exe"), plan.FileName);

        // Games resolve their assets relative to the working directory: starting one from
        // wherever the launcher lives is how a game that works when double-clicked fails here.
        Assert.Equal(Root, plan.WorkingDirectory);
    }

    // The publisher's switches first, the player's after, because nearly every command line
    // parser lets the last occurrence win — which is what makes an override an override.
    [Theory]
    [InlineData("", "", "")]
    [InlineData("--fullscreen", "", "--fullscreen")]
    [InlineData("", "-windowed", "-windowed")]
    [InlineData("--fullscreen", "-windowed", "--fullscreen -windowed")]
    [InlineData("  --fullscreen  ", "  -windowed  ", "--fullscreen -windowed")]
    public void ThePlayersArgumentsComeAfterTheBuilds(
        string buildArgs, string playerArgs, string expected)
    {
        LaunchPlan plan = PlanFor(Install(launchArgs: buildArgs, launchOptions: playerArgs));

        Assert.Equal(expected, plan.Arguments);
    }

    [Fact]
    public void AGameThatIsNotInstalledCannotBeStarted()
    {
        GameLaunchException exception =
            Assert.Throws<GameLaunchException>(() => PlanFor(null));

        Assert.Equal(LaunchFailure.NotInstalled, exception.Reason);
    }

    // A directory left half-applied is not the build its row names, and running it would run
    // a mixture of two versions.
    [Theory]
    [InlineData(InstallState.Applying)]
    [InlineData(InstallState.Broken)]
    public void AnInstallThatIsNotFinishedCannotBeStarted(InstallState state)
    {
        GameLaunchException exception = Assert.Throws<GameLaunchException>(
            () => PlanFor(Install(state: state)));

        Assert.Equal(LaunchFailure.NotPlayable, exception.Reason);
    }

    [Fact]
    public void AnEntrypointThatIsNotThereIsReportedRatherThanHandedToTheOperatingSystem()
    {
        GameLaunchException exception = Assert.Throws<GameLaunchException>(
            () => LaunchPlanner.PlanFor(Install(), null, _ => false));

        Assert.Equal(LaunchFailure.EntrypointMissing, exception.Reason);
    }

    // The entrypoint decides what gets *executed*, not merely where a byte lands, so it gets
    // the same containment check as every other path the server sent.
    [Theory]
    [InlineData("../elsewhere/Game.exe")]
    [InlineData("bin/../../Game.exe")]
    public void AnEntrypointOutsideTheInstallDirectoryIsRefused(string entrypoint)
    {
        GameLaunchException exception = Assert.Throws<GameLaunchException>(
            () => PlanFor(Install(entrypoint: entrypoint)));

        Assert.Equal(LaunchFailure.EntrypointMissing, exception.Reason);
    }
}

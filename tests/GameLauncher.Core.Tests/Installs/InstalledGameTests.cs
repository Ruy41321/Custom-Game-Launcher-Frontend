using GameLauncher.Core.Installs;

namespace GameLauncher.Core.Tests.Installs;

public sealed class InstalledGameTests
{
    // The manifest speaks in '/' whatever the machine is, and the launcher has to hand the
    // operating system a path it recognises.
    [Fact]
    public void TheEntrypointIsResolvedInThisPlatformsSeparators()
    {
        InstalledGame install = new()
        {
            InstallDirectory = Path.Combine("games", "orbital-drift"),
            Entrypoint = "bin/Game.exe",
        };

        Assert.Equal(
            Path.Combine("games", "orbital-drift", "bin", "Game.exe"), install.EntrypointPath);
    }

    [Fact]
    public void OnlyACompleteInstallCountsAsBeingABuild()
    {
        InstalledGame install = new() { BuildId = "b1", State = InstallState.Installed };

        Assert.True(install.Is("b1"));
        Assert.False(install.Is("b2"));
        Assert.False((install with { State = InstallState.Applying }).Is("b1"));
        Assert.False((install with { State = InstallState.Broken }).Is("b1"));
    }
}

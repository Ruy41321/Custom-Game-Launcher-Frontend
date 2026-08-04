using GameLauncher.App.Services;
using GameLauncher.App.ViewModels;
using GameLauncher.Core.Configuration;
using GameLauncher.Core.Installs;
using GameLauncher.Core.Localization;
using GameLauncher.Core.Platform;
using NSubstitute;

namespace GameLauncher.App.Tests.ViewModels;

public sealed class SettingsViewModelTests
{
    private readonly IUserSettingsStore _store = Substitute.For<IUserSettingsStore>();
    private readonly IPathProvider _paths = Substitute.For<IPathProvider>();
    private readonly IInstallStore _installs = Substitute.For<IInstallStore>();
    private readonly IFolderPicker _folders = Substitute.For<IFolderPicker>();
    private readonly IThemeSwitcher _theme = Substitute.For<IThemeSwitcher>();
    private readonly ResourceManagerLocalizationService _localization = new("en");

    private SettingsViewModel CreateViewModel()
    {
        _paths.DefaultInstallDirectory.Returns("/home/luigi/Games");
        return new SettingsViewModel(
            _store, _paths, _installs, _folders, _theme, _localization);
    }

    private void Stored(UserSettings settings) =>
        _store.LoadAsync(Arg.Any<CancellationToken>()).Returns(settings);

    [Fact]
    public async Task AnUnsetInstallDirectoryShowsTheDefaultRatherThanNothing()
    {
        Stored(new UserSettings());

        SettingsViewModel model = CreateViewModel();
        await model.LoadAsync(TestContext.Current.CancellationToken);

        Assert.Empty(model.InstallDirectory);
        Assert.Equal("/home/luigi/Games", model.DefaultInstallDirectory);
    }

    [Fact]
    public async Task ChoosingADirectorySavesItStraightAway()
    {
        Stored(new UserSettings());
        _folders.PickAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns("/mnt/games");

        UserSettings? saved = null;
        _store.SaveAsync(Arg.Do<UserSettings>(settings => saved = settings), Arg.Any<CancellationToken>())
            .Returns(Task.CompletedTask);

        SettingsViewModel model = CreateViewModel();
        await model.LoadAsync(TestContext.Current.CancellationToken);
        await model.ChooseInstallDirectoryCommand.ExecuteAsync(null);

        Assert.Equal("/mnt/games", model.InstallDirectory);
        Assert.Equal("/mnt/games", saved?.InstallDirectory);
        Assert.Equal("Saved.", model.StatusMessage);
    }

    // An empty setting is how "use the platform default" is spelled, so resetting has to write
    // null rather than an empty string that would later be treated as a real path.
    [Fact]
    public async Task ResettingWritesNoDirectoryAtAll()
    {
        Stored(new UserSettings { InstallDirectory = "/mnt/games" });

        UserSettings? saved = null;
        _store.SaveAsync(Arg.Do<UserSettings>(settings => saved = settings), Arg.Any<CancellationToken>())
            .Returns(Task.CompletedTask);

        SettingsViewModel model = CreateViewModel();
        await model.LoadAsync(TestContext.Current.CancellationToken);
        Assert.Equal("/mnt/games", model.InstallDirectory);

        await model.ResetInstallDirectoryCommand.ExecuteAsync(null);

        Assert.NotNull(saved);
        Assert.Null(saved.InstallDirectory);
    }

    // Changing where new games go must not be read as a promise to move the old ones.
    [Fact]
    public async Task ThePageSaysThatInstalledGamesStayWhereTheyAre()
    {
        Stored(new UserSettings());
        _installs.GetAllAsync(Arg.Any<CancellationToken>()).Returns(
        [
            new InstalledGame { GameId = "g1" },
            new InstalledGame { GameId = "g2" },
        ]);

        SettingsViewModel model = CreateViewModel();
        await model.LoadAsync(TestContext.Current.CancellationToken);

        Assert.True(model.HasInstalledElsewhere);
        Assert.Contains("2", model.InstalledElsewhereNotice, StringComparison.Ordinal);
    }

    [Fact]
    public async Task WithNothingInstalledThereIsNothingToWarnAbout()
    {
        Stored(new UserSettings());
        _installs.GetAllAsync(Arg.Any<CancellationToken>()).Returns([]);

        SettingsViewModel model = CreateViewModel();
        await model.LoadAsync(TestContext.Current.CancellationToken);

        Assert.False(model.HasInstalledElsewhere);
    }

    // A theme is judged by looking at it, and a preview that needs a button first is not one.
    [Fact]
    public async Task PickingAThemeAppliesItImmediately()
    {
        Stored(new UserSettings());

        SettingsViewModel model = CreateViewModel();
        await model.LoadAsync(TestContext.Current.CancellationToken);

        model.ThemeVariant = "light";

        _theme.Received().Apply("light");
    }

    [Fact]
    public async Task TheStoredThemeIsWhatTheBoxShows()
    {
        Stored(new UserSettings { ThemeVariant = "system" });

        SettingsViewModel model = CreateViewModel();
        await model.LoadAsync(TestContext.Current.CancellationToken);

        Assert.Equal("system", model.ThemeVariant);
        Assert.Contains("system", model.Themes);
    }
}

using GameLauncher.App.Services;
using GameLauncher.App.ViewModels;
using GameLauncher.Core.Api;
using GameLauncher.Core.Authentication;
using GameLauncher.Core.Configuration;
using GameLauncher.Core.Installs;
using GameLauncher.Core.Localization;
using GameLauncher.Core.Platform;
using NSubstitute;
using NSubstitute.ExceptionExtensions;

namespace GameLauncher.App.Tests.ViewModels;

public sealed class SettingsViewModelTests
{
    private readonly IUserSettingsStore _store = Substitute.For<IUserSettingsStore>();
    private readonly IPathProvider _paths = Substitute.For<IPathProvider>();
    private readonly IInstallStore _installs = Substitute.For<IInstallStore>();
    private readonly IFolderPicker _folders = Substitute.For<IFolderPicker>();
    private readonly IThemeSwitcher _theme = Substitute.For<IThemeSwitcher>();
    private readonly IAccountService _account = Substitute.For<IAccountService>();
    private readonly IAuthenticationService _authentication =
        Substitute.For<IAuthenticationService>();

    private readonly ResourceManagerLocalizationService _localization = new("en");
    private readonly ApiErrorPresenter _errors;

    public SettingsViewModelTests() => _errors = new ApiErrorPresenter(_localization);

    private SettingsViewModel CreateViewModel()
    {
        _paths.DefaultInstallDirectory.Returns("/home/luigi/Games");
        return new SettingsViewModel(
            _store, _paths, _installs, _folders, _theme, _account, _authentication,
            _errors, _localization);
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

    // --- crash reports ----------------------------------------------------------------------

    // It appears on the page now because it finally does something: until the uploader existed,
    // an inert checkbox would have been a promise the launcher did not keep.
    [Fact]
    public async Task TheStoredCrashReportChoiceIsWhatTheBoxShows()
    {
        Stored(new UserSettings { SendCrashReports = true });

        SettingsViewModel model = CreateViewModel();
        await model.LoadAsync(TestContext.Current.CancellationToken);

        Assert.True(model.SendCrashReports);
    }

    [Fact]
    public async Task ConsentIsOffUntilSomebodyTurnsItOn()
    {
        Stored(new UserSettings());

        SettingsViewModel model = CreateViewModel();
        await model.LoadAsync(TestContext.Current.CancellationToken);

        Assert.False(model.SendCrashReports);
    }

    /// <summary>
    /// Saved as soon as it is toggled rather than behind a button: a consent checkbox that needs
    /// a second press to take effect is one somebody will believe they set.
    /// </summary>
    [Fact]
    public async Task TogglingConsentSavesItStraightAway()
    {
        Stored(new UserSettings());
        UserSettings? saved = null;
        _store.SaveAsync(Arg.Do<UserSettings>(settings => saved = settings), Arg.Any<CancellationToken>())
            .Returns(Task.CompletedTask);

        SettingsViewModel model = CreateViewModel();
        await model.LoadAsync(TestContext.Current.CancellationToken);

        model.SendCrashReports = true;

        Assert.True(saved?.SendCrashReports);
    }

    // Turning it off matters more than turning it on, and it has to reach the file too.
    [Fact]
    public async Task WithdrawingConsentIsSavedAsWell()
    {
        Stored(new UserSettings { SendCrashReports = true });
        UserSettings? saved = null;
        _store.SaveAsync(Arg.Do<UserSettings>(settings => saved = settings), Arg.Any<CancellationToken>())
            .Returns(Task.CompletedTask);

        SettingsViewModel model = CreateViewModel();
        await model.LoadAsync(TestContext.Current.CancellationToken);

        model.SendCrashReports = false;

        Assert.False(saved?.SendCrashReports);
    }

    // --- erasing the account ---------------------------------------------------------------

    private async Task<SettingsViewModel> LoadedPage()
    {
        Stored(new UserSettings());
        SettingsViewModel model = CreateViewModel();
        await model.LoadAsync(TestContext.Current.CancellationToken);
        return model;
    }

    [Fact]
    public async Task NothingIsSentUntilTheSecondPress()
    {
        SettingsViewModel model = await LoadedPage();
        model.DeletePassword = "hunter2";

        model.AskToDeleteAccountCommand.Execute(null);

        Assert.True(model.HasPendingDeletion);
        await _account.DidNotReceive().DeleteAsync(
            Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ConfirmingSendsThePasswordAndTheReason()
    {
        SettingsViewModel model = await LoadedPage();
        model.DeletePassword = "hunter2";
        model.DeleteReason = "moving on";

        model.AskToDeleteAccountCommand.Execute(null);
        await model.ConfirmDeletionCommand.ExecuteAsync(null);

        await _account.Received(1).DeleteAsync(
            "hunter2", "moving on", Arg.Any<CancellationToken>());
        Assert.False(model.HasPendingDeletion);
        Assert.Empty(model.DeletePassword);
    }

    /// <summary>
    /// The prompt is the safety, not the button, so what it says is asserted on. This is the
    /// part somebody in this position does not expect: the account goes and the games do not.
    /// </summary>
    [Fact]
    public async Task APublisherIsToldTheirGamesSurviveTheirAccount()
    {
        _authentication.HasPermission(Permissions.GamePublish).Returns(true);
        SettingsViewModel model = await LoadedPage();
        model.DeletePassword = "hunter2";

        model.AskToDeleteAccountCommand.Execute(null);

        Assert.NotNull(model.PendingDeletion);
        Assert.Contains(
            "stays online", model.PendingDeletion.Prompt, StringComparison.Ordinal);
        Assert.Contains(
            "delete those games first",
            model.PendingDeletion.Prompt,
            StringComparison.Ordinal);
    }

    // A player has nothing published, so the sentence about published games would be noise.
    [Fact]
    public async Task APlayerIsNotToldAboutGamesTheyNeverPublished()
    {
        _authentication.HasPermission(Permissions.GamePublish).Returns(false);
        SettingsViewModel model = await LoadedPage();
        model.DeletePassword = "hunter2";

        model.AskToDeleteAccountCommand.Execute(null);

        Assert.NotNull(model.PendingDeletion);
        Assert.DoesNotContain(
            "stays online", model.PendingDeletion.Prompt, StringComparison.Ordinal);
    }

    [Fact]
    public async Task CancellingSendsNothingAndForgetsThePassword()
    {
        SettingsViewModel model = await LoadedPage();
        model.DeletePassword = "hunter2";
        model.AskToDeleteAccountCommand.Execute(null);

        model.CancelDeletionCommand.Execute(null);

        Assert.False(model.HasPendingDeletion);
        Assert.Empty(model.DeletePassword);
        await _account.DidNotReceive().DeleteAsync(
            Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<CancellationToken>());
    }

    // An empty password is a round trip the server can only refuse.
    [Fact]
    public async Task TheButtonIsOffUntilAPasswordIsTyped()
    {
        SettingsViewModel model = await LoadedPage();

        Assert.False(model.CanDeleteAccount);
        model.DeletePassword = "hunter2";
        Assert.True(model.CanDeleteAccount);
    }

    /// <summary>
    /// A mistyped password is the likeliest reason to be here, so the box keeps what was typed
    /// and the refusal is shown as itself rather than as a generic failure.
    /// </summary>
    [Fact]
    public async Task AWrongPasswordIsReportedAndTheBoxKeepsWhatWasTyped()
    {
        _account.DeleteAsync(Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<CancellationToken>())
            .ThrowsAsync(new ApiException(ApiErrorCode.Unauthenticated, "the password is incorrect"));

        SettingsViewModel model = await LoadedPage();
        model.DeletePassword = "wrong";
        model.AskToDeleteAccountCommand.Execute(null);

        await model.ConfirmDeletionCommand.ExecuteAsync(null);

        Assert.NotNull(model.ErrorMessage);
        Assert.Equal("wrong", model.DeletePassword);
        Assert.False(model.IsDeleting);
    }

    // Reopening the page must not present a confirmation somebody could walk into.
    [Fact]
    public async Task ReloadingThePageDisarmsAnythingLeftArmed()
    {
        SettingsViewModel model = await LoadedPage();
        model.DeletePassword = "hunter2";
        model.AskToDeleteAccountCommand.Execute(null);
        Assert.True(model.HasPendingDeletion);

        await model.LoadAsync(TestContext.Current.CancellationToken);

        Assert.False(model.HasPendingDeletion);
        Assert.Empty(model.DeletePassword);
    }
}

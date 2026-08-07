using GameLauncher.App.Services;
using GameLauncher.App.ViewModels;
using GameLauncher.Core.Api;
using GameLauncher.Core.Authentication;
using GameLauncher.Core.Configuration;
using GameLauncher.Core.Diagnostics;
using GameLauncher.Core.Downloads;
using GameLauncher.Core.Installs;
using GameLauncher.Core.Launching;
using GameLauncher.Core.Localization;
using GameLauncher.Core.Models;
using GameLauncher.Core.Platform;
using GameLauncher.Core.Publishing;
using GameLauncher.Core.Updates;
using NSubstitute;
using NSubstitute.ExceptionExtensions;

namespace GameLauncher.App.Tests.ViewModels;

/// <summary>
/// The shell decides what is on screen. These tests are the reason navigation is built on
/// events: a view model that needed a window to exist could not be exercised at all.
/// </summary>
public sealed class MainWindowViewModelTests
{
    private readonly IAuthenticationService _authentication =
        Substitute.For<IAuthenticationService>();
    private readonly ICatalogApi _catalog = Substitute.For<ICatalogApi>();
    private readonly ILibraryApi _library = Substitute.For<ILibraryApi>();
    private readonly IUserSettingsStore _settings = Substitute.For<IUserSettingsStore>();
    private readonly ICrashReportUploader _crashReports =
        Substitute.For<ICrashReportUploader>();

    private readonly IInstallationService _installations =
        Substitute.For<IInstallationService>();
    private readonly ResourceManagerLocalizationService _localization =
        new("en");

    private readonly IImageProvider _images = Substitute.For<IImageProvider>();

    private readonly IUpdateChecker _updates = Substitute.For<IUpdateChecker>();

    private readonly ILauncherUpdateDownloader _updateDownloader =
        Substitute.For<ILauncherUpdateDownloader>();

    /// <summary>
    /// Every start asks about updates, so the answer is arranged here rather than in the
    /// factory: an unconfigured substitute hands back a null result, and the failure would show
    /// up inside the banner instead of in whatever the test was actually about.
    /// </summary>
    public MainWindowViewModelTests() =>
        _updates.CheckAsync(Arg.Any<CancellationToken>())
            .Returns(UpdateCheckResult.NotConfigured);

    private MainWindowViewModel CreateShell()
    {
        _settings.LoadAsync(Arg.Any<CancellationToken>()).Returns(new UserSettings());
        _library.GetLibraryAsync(Arg.Any<CancellationToken>()).Returns([]);
        _catalog.ExploreAsync(Arg.Any<GameQuery>(), Arg.Any<CancellationToken>())
            .Returns(new PagedResult<Game>());

        var errors = new ApiErrorPresenter(_localization);
        var runtime = Substitute.For<IRuntimePlatform>();
        runtime.Platform.Returns(GamePlatform.Windows);
        runtime.Architecture.Returns(BuildArchitecture.X64);

        _catalog
            .GetPatchNotesAsync(
                Arg.Any<string>(), Arg.Any<int>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(new PagedResult<PatchNote>());

        return new MainWindowViewModel(
            _localization,
            _settings,
            new LauncherConfiguration { AppName = "Test Launcher" },
            _authentication,
            _installations,
            _crashReports,
            _updates,
            _updateDownloader,
            errors,
            new LoginViewModel(_authentication, errors, _localization),
            new ExploreViewModel(
                _catalog,
                _library,
                errors,
                _localization,
                _images,
                new FakeTimeProvider(DateTimeOffset.UnixEpoch)),
            new LibraryViewModel(
                _library,
                Substitute.For<IInstallStore>(),
                Substitute.For<IGameLauncher>(),
                errors,
                _localization,
                _images),
            new GameDetailViewModel(
                _catalog,
                _library,
                errors,
                _localization,
                runtime,
                _authentication,
                Substitute.For<IInstallationService>(),
                Substitute.For<IInstallStore>(),
                Substitute.For<IGameLauncher>(),
                _images,
                TimeProvider.System),
            DeveloperPage(errors, runtime),
            new SettingsViewModel(
                _settings,
                Substitute.For<IPathProvider>(),
                Substitute.For<IInstallStore>(),
                Substitute.For<IFolderPicker>(),
                Substitute.For<IThemeSwitcher>(),
                Substitute.For<IAccountService>(),
                _authentication,
                errors,
                _localization));
    }

    /// <summary>
    /// The dashboard and its three children. Assembled here because the shell only needs it to
    /// exist — what it does is covered by <see cref="DeveloperViewModelTests"/> and the three
    /// child test classes.
    /// </summary>
    private DeveloperViewModel DeveloperPage(
        IApiErrorPresenter errors, IRuntimePlatform runtime)
    {
        var publishing = Substitute.For<IPublishingApi>();
        var capabilities = Substitute.For<IServerCapabilityProvider>();
        capabilities.GetAsync(Arg.Any<CancellationToken>()).Returns(ServerCapabilities.Fallback);

        return new DeveloperViewModel(
            _catalog,
            publishing,
            Substitute.For<IBuildPublisher>(),
            errors,
            _localization,
            runtime,
            Substitute.For<IFolderPicker>(),
            new GameEditorViewModel(publishing, errors, _localization),
            new GameMediaViewModel(
                _catalog,
                publishing,
                capabilities,
                errors,
                _localization,
                Substitute.For<IFilePicker>()),
            new GameDevlogViewModel(_catalog, publishing, errors, _localization));
    }

    [Fact]
    public void TheLauncherOpensOnTheSignInScreen()
    {
        MainWindowViewModel shell = CreateShell();

        Assert.Same(shell.Login, shell.CurrentPage);
        Assert.False(shell.IsSignedIn);
    }

    // A launcher that asked for a password on every start would not be worth signing into.
    [Fact]
    public async Task ARestoredSessionLandsInTheLibrary()
    {
        _authentication.RestoreAsync(Arg.Any<CancellationToken>()).Returns(true);
        MainWindowViewModel shell = CreateShell();

        await shell.InitializeAsync(TestContext.Current.CancellationToken);

        Assert.Same(shell.Library, shell.CurrentPage);
        await _library.Received(1).GetLibraryAsync(Arg.Any<CancellationToken>());
    }

    // What a previous run left half done is recorded before anything is shown, so the pages
    // describe what is really on disk rather than what the launcher was last told.
    [Fact]
    public async Task StartupRecordsWhatAPreviousRunLeftUnfinished()
    {
        _authentication.RestoreAsync(Arg.Any<CancellationToken>()).Returns(false);
        MainWindowViewModel shell = CreateShell();

        await shell.InitializeAsync(TestContext.Current.CancellationToken);

        await _installations.Received(1).RecoverAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task AFailedRecoveryIsNotWorthABlankWindow()
    {
        _installations.RecoverAsync(Arg.Any<CancellationToken>())
            .ThrowsAsync(new IOException("the disk went away"));
        _authentication.RestoreAsync(Arg.Any<CancellationToken>()).Returns(false);

        MainWindowViewModel shell = CreateShell();

        await shell.InitializeAsync(TestContext.Current.CancellationToken);

        Assert.Same(shell.Login, shell.CurrentPage);
    }

    [Fact]
    public async Task NothingToRestoreLeavesTheSignInScreenUp()
    {
        _authentication.RestoreAsync(Arg.Any<CancellationToken>()).Returns(false);
        MainWindowViewModel shell = CreateShell();

        await shell.InitializeAsync(TestContext.Current.CancellationToken);

        Assert.Same(shell.Login, shell.CurrentPage);
    }

    // Not the offline path — the real service keeps the session and reports success there.
    // This is the guard for a failure nobody has a story for.
    [Fact]
    public async Task AnUnexpectedFailureWhileRestoringLeavesTheSignInScreenUp()
    {
        _authentication.RestoreAsync(Arg.Any<CancellationToken>())
            .Throws(new ApiException(ApiErrorCode.Network, "offline"));
        MainWindowViewModel shell = CreateShell();

        await shell.InitializeAsync(TestContext.Current.CancellationToken);

        Assert.Same(shell.Login, shell.CurrentPage);
    }

    [Fact]
    public async Task SigningInMovesToTheLibrary()
    {
        _authentication.SignInAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(new AuthSession());
        MainWindowViewModel shell = CreateShell();
        shell.Login.Email = "luigi@example.com";
        shell.Login.Password = "correct horse";

        await shell.Login.SubmitCommand.ExecuteAsync(null);

        Assert.Same(shell.Library, shell.CurrentPage);
    }

    [Fact]
    public async Task SigningOutReturnsToTheSignInScreen()
    {
        MainWindowViewModel shell = CreateShell();
        await shell.ShowExploreCommand.ExecuteAsync(null);

        await shell.SignOutCommand.ExecuteAsync(null);

        Assert.Same(shell.Login, shell.CurrentPage);
        await _authentication.Received(1).SignOutAsync(Arg.Any<CancellationToken>());
    }

    // A session revoked mid-use — its family was wiped — must not leave a dead page up.
    [Fact]
    public void ASessionEndingElsewhereAlsoReturnsToSignIn()
    {
        MainWindowViewModel shell = CreateShell();
        shell.ShowExploreCommand.Execute(null);

        _authentication.SessionChanged += Raise.EventWith(new SessionChangedEventArgs(null));

        Assert.Same(shell.Login, shell.CurrentPage);
    }

    // Advisory, like every client-side permission check. Hiding the tab keeps a player from
    // finding a page that only says no.
    [Fact]
    public void ThePublishTabIsOfferedOnlyToAnAccountThatMay()
    {
        MainWindowViewModel shell = CreateShell();

        _authentication.IsAuthenticated.Returns(true);
        _authentication.HasPermission(Permissions.GamePublish).Returns(false);
        _authentication.SessionChanged += Raise.EventWith(new SessionChangedEventArgs(new AuthSession()));
        Assert.False(shell.CanPublish);

        _authentication.HasPermission(Permissions.GamePublish).Returns(true);
        _authentication.SessionChanged += Raise.EventWith(new SessionChangedEventArgs(new AuthSession()));
        Assert.True(shell.CanPublish);
    }

    [Fact]
    public async Task ThePublishTabOpensTheDeveloperPage()
    {
        _catalog.GetMyGamesAsync(Arg.Any<GameQuery>(), Arg.Any<CancellationToken>())
            .Returns(new PagedResult<Game>());

        MainWindowViewModel shell = CreateShell();
        await shell.ShowDeveloperCommand.ExecuteAsync(null);

        Assert.Same(shell.Developer, shell.CurrentPage);
        await _catalog.Received(1).GetMyGamesAsync(
            Arg.Any<GameQuery>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public void TheAccountNameFollowsTheSession()
    {
        MainWindowViewModel shell = CreateShell();

        var session = new AuthSession { User = new AuthenticatedUser { DisplayName = "Luigi" } };
        _authentication.SessionChanged += Raise.EventWith(new SessionChangedEventArgs(session));

        Assert.Equal("Luigi", shell.AccountName);
    }

    [Fact]
    public async Task OpeningAGameFromExploreShowsItsPage()
    {
        _catalog.GetGameAsync("orbital-drift", Arg.Any<CancellationToken>())
            .Returns(new GameDetail { Game = new Game { Title = "Orbital Drift" } });
        MainWindowViewModel shell = CreateShell();
        await shell.ShowExploreCommand.ExecuteAsync(null);

        shell.Explore.OpenGameCommand.Execute(
            new StoreCardViewModel(new Game { Id = "g1", Slug = "orbital-drift" }));

        Assert.Same(shell.GameDetail, shell.CurrentPage);
    }

    // Opening a game from Explore and going back must not land in the library.
    [Fact]
    public async Task GoingBackReturnsToTheListItCameFrom()
    {
        _catalog.GetGameAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(new GameDetail());
        MainWindowViewModel shell = CreateShell();
        await shell.ShowExploreCommand.ExecuteAsync(null);
        shell.Explore.OpenGameCommand.Execute(
            new StoreCardViewModel(new Game { Id = "g1", Slug = "orbital-drift" }));

        shell.GameDetail.BackCommand.Execute(null);

        Assert.Same(shell.Explore, shell.CurrentPage);
    }

    [Fact]
    public async Task GoingBackFromTheLibraryReturnsToTheLibrary()
    {
        _catalog.GetGameAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(new GameDetail());
        MainWindowViewModel shell = CreateShell();

        // After CreateShell, which stubs an empty library of its own: the last stub wins.
        _library.GetLibraryAsync(Arg.Any<CancellationToken>())
            .Returns([new Game { Id = "g1", Slug = "orbital-drift", Title = "Orbital Drift" }]);

        await shell.ShowLibraryCommand.ExecuteAsync(null);
        shell.Library.OpenGameCommand.Execute(shell.Library.Games[0]);

        shell.GameDetail.BackCommand.Execute(null);

        Assert.Same(shell.Library, shell.CurrentPage);
    }

    private static ReleaseDocument Release(int major, int minor, int patch, string notes = "") =>
        new()
        {
            Version = new ReleaseVersion(major, minor, patch),
            Platform = "windows",
            Arch = "x64",
            Sha256 = new string('a', 64),
            Size = 400,
            ReleasedAt = "2026-08-07T10:00:00Z",
            Notes = notes,
        };

    // Announced, never applied on its own: a swap needs this process to exit, so a silent
    // update is an application closing under the hands of somebody using it.
    [Fact]
    public async Task AnAvailableUpdateIsAnnouncedAndNothingIsFetchedYet()
    {
        _updates.CheckAsync(Arg.Any<CancellationToken>()).Returns(
            UpdateCheckResult.Available(
                Release(0, 2, 0, "Self-update, at last."), "https://files.example.test/l.zip"));

        MainWindowViewModel shell = CreateShell();
        await shell.InitializeAsync(TestContext.Current.CancellationToken);

        Assert.True(shell.UpdateAvailable);
        Assert.Contains("0.2.0", shell.UpdateHeadline, StringComparison.Ordinal);
        Assert.Contains("Test Launcher", shell.UpdateHeadline, StringComparison.Ordinal);
        Assert.Equal("Self-update, at last.", shell.UpdateNotes);
        Assert.Empty(shell.UpdateStatus);

        await _updateDownloader.DidNotReceiveWithAnyArgs().DownloadAsync(
            default!, default!, default, TestContext.Current.CancellationToken);
    }

    [Theory]
    [InlineData(UpdateAvailability.NotConfigured)]
    [InlineData(UpdateAvailability.UpToDate)]
    [InlineData(UpdateAvailability.Undetermined)]
    public async Task AnythingButAnAvailableUpdateIsSilence(UpdateAvailability availability)
    {
        _updates.CheckAsync(Arg.Any<CancellationToken>())
            .Returns(new UpdateCheckResult { Availability = availability });

        MainWindowViewModel shell = CreateShell();
        await shell.InitializeAsync(TestContext.Current.CancellationToken);

        Assert.False(shell.UpdateAvailable);
        Assert.Empty(shell.UpdateStatus);
        Assert.Same(shell.Login, shell.CurrentPage);
    }

    // What the person is told when the download succeeds is where the verified archive is —
    // not that the launcher is about to replace itself, which it cannot do yet.
    [Fact]
    public async Task AVerifiedDownloadSaysWhereTheFileIsAndPromisesNothingMore()
    {
        string directory = Path.Combine(Path.GetTempPath(), "updates", "0.2.0");
        _updates.CheckAsync(Arg.Any<CancellationToken>()).Returns(
            UpdateCheckResult.Available(Release(0, 2, 0), "https://files.example.test/l.zip"));
        _updateDownloader
            .DownloadAsync(
                Arg.Any<ReleaseDocument>(),
                Arg.Any<string>(),
                Arg.Any<IProgress<long>>(),
                Arg.Any<CancellationToken>())
            .Returns(Path.Combine(directory, "launcher.zip"));

        MainWindowViewModel shell = CreateShell();
        await shell.InitializeAsync(TestContext.Current.CancellationToken);
        await shell.DownloadUpdateCommand.ExecuteAsync(null);

        Assert.Contains(directory, shell.UpdateStatus, StringComparison.Ordinal);
        Assert.Contains("not available yet", shell.UpdateStatus, StringComparison.Ordinal);
        Assert.False(shell.CanDownloadUpdate);
    }

    // Bytes that are not the ones the signed document named are refused, and the offer stands:
    // an interrupted transfer and a host serving the wrong file are both worth one more press.
    [Fact]
    public async Task ARefusedDownloadIsSaidAndTheOfferStands()
    {
        _updates.CheckAsync(Arg.Any<CancellationToken>()).Returns(
            UpdateCheckResult.Available(Release(0, 2, 0), "https://files.example.test/l.zip"));
        _updateDownloader
            .DownloadAsync(
                Arg.Any<ReleaseDocument>(),
                Arg.Any<string>(),
                Arg.Any<IProgress<long>>(),
                Arg.Any<CancellationToken>())
            .ThrowsAsync(new ApiException(ApiErrorCode.Integrity, "hashes to something else"));

        MainWindowViewModel shell = CreateShell();
        await shell.InitializeAsync(TestContext.Current.CancellationToken);
        await shell.DownloadUpdateCommand.ExecuteAsync(null);

        Assert.Equal(_localization.Translate("Error.Integrity"), shell.UpdateStatus);
        Assert.True(shell.CanDownloadUpdate);
        Assert.True(shell.UpdateAvailable);
    }

    // The banner's sentences are built in code rather than bound through {loc:Tr}, so without
    // this they would stay in the language they were first written in. Found by looking at the
    // window, where the headline was French and the line under it Italian.
    [Fact]
    public async Task TheUpdateLineFollowsALanguageChange()
    {
        _updates.CheckAsync(Arg.Any<CancellationToken>()).Returns(
            UpdateCheckResult.Available(Release(0, 2, 0), "https://files.example.test/l.zip"));
        _updateDownloader
            .DownloadAsync(
                Arg.Any<ReleaseDocument>(),
                Arg.Any<string>(),
                Arg.Any<IProgress<long>>(),
                Arg.Any<CancellationToken>())
            .Returns(Path.Combine(Path.GetTempPath(), "updates", "0.2.0", "launcher.zip"));

        MainWindowViewModel shell = CreateShell();
        await shell.InitializeAsync(TestContext.Current.CancellationToken);
        await shell.DownloadUpdateCommand.ExecuteAsync(null);

        await shell.ChangeLanguageCommand.ExecuteAsync(
            shell.Languages.Single(language => language.CultureName == "it"));

        Assert.Equal(
            _localization.Translate("Update.Available", "0.2.0", "Test Launcher"),
            shell.UpdateHeadline);
        Assert.Contains("non è ancora possibile", shell.UpdateStatus, StringComparison.Ordinal);
    }

    [Fact]
    public async Task TheUpdateLineCanBePutAwayForThisRun()
    {
        _updates.CheckAsync(Arg.Any<CancellationToken>()).Returns(
            UpdateCheckResult.Available(Release(0, 2, 0), "https://files.example.test/l.zip"));

        MainWindowViewModel shell = CreateShell();
        await shell.InitializeAsync(TestContext.Current.CancellationToken);

        shell.DismissUpdateCommand.Execute(null);

        Assert.False(shell.UpdateAvailable);
    }

    [Fact]
    public async Task ChoosingALanguageSwitchesAndRemembersIt()
    {
        MainWindowViewModel shell = CreateShell();
        LanguageOption italian = shell.Languages.Single(language => language.CultureName == "it");

        await shell.ChangeLanguageCommand.ExecuteAsync(italian);

        Assert.Equal("it", _localization.CurrentCulture.TwoLetterISOLanguageName);
        await _settings.Received(1).SaveAsync(
            Arg.Is<UserSettings>(saved => saved!.Language == "it"), Arg.Any<CancellationToken>());
    }
}

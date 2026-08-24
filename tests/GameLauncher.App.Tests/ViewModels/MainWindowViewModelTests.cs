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
using Microsoft.Extensions.Logging.Abstractions;
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
    private readonly ILibraryCache _libraryCache = Substitute.For<ILibraryCache>();
    private readonly ServerReachability _reachability = new(TimeProvider.System);
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

    private readonly IUpdateInstaller _updateInstaller = Substitute.For<IUpdateInstaller>();

    private readonly IApplicationShutdown _shutdown = Substitute.For<IApplicationShutdown>();

    private readonly IServerCapabilityProvider _capabilities =
        Substitute.For<IServerCapabilityProvider>();

    private readonly IAccountService _account = Substitute.For<IAccountService>();

    /// <summary>
    /// Every start asks about updates, so the answer is arranged here rather than in the
    /// factory: an unconfigured substitute hands back a null result, and the failure would show
    /// up inside the banner instead of in whatever the test was actually about.
    /// </summary>
    public MainWindowViewModelTests()
    {
        _updates.CheckAsync(Arg.Any<CancellationToken>())
            .Returns(UpdateCheckResult.NotConfigured);

        // The sign-in screen reads this on every load, and an unconfigured Task<T> member
        // yields null — the same row of §7 that keeps costing cycles.
        _capabilities.GetAsync(Arg.Any<CancellationToken>())
            .Returns(ServerCapabilities.Fallback);
    }

    private MainWindowViewModel CreateShell()
    {
        _settings.LoadAsync(Arg.Any<CancellationToken>()).Returns(new UserSettings());
        _library.GetLibraryAsync(Arg.Any<CancellationToken>()).Returns([]);
        _catalog.ExploreAsync(Arg.Any<GameQuery>(), Arg.Any<CancellationToken>())
            .Returns(new PagedResult<Game>());

        var errors = new ApiErrorPresenter(_localization, NullLogger<ApiErrorPresenter>.Instance);
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
            _updateInstaller,
            _shutdown,
            errors,
            new LoginViewModel(
                _authentication,
                _capabilities,
                _reachability,
                Substitute.For<IInstallStore>(),
                errors,
                _localization),
            new ExploreViewModel(
                _catalog,
                _library,
                errors,
                _localization,
                _images,
                new FakeTimeProvider(DateTimeOffset.UnixEpoch)),
            new LibraryViewModel(
                _library,
                _libraryCache,
                _authentication,
                _reachability,
                _catalog,
                runtime,
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
                Substitute.For<IVideoPlayback>(),
                Substitute.For<IFileBrowser>(),
                Substitute.For<IFolderPicker>(),
                _settings,
                TimeProvider.System),
            DeveloperPage(errors, runtime),
            new SettingsViewModel(
                _settings,
                Substitute.For<IPathProvider>(),
                Substitute.For<IInstallStore>(),
                Substitute.For<IFolderPicker>(),
                Substitute.For<IThemeSwitcher>(),
                _account,
                _authentication,
                errors,
                _localization),
            new ChangePasswordViewModel(_account, errors, _localization));
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
                Substitute.For<IFilePicker>(),
                _images),
            new GameDevlogViewModel(_catalog, publishing, errors, _localization));
    }

    [Fact]
    public void TheLauncherOpensOnTheSignInScreen()
    {
        MainWindowViewModel shell = CreateShell();

        Assert.Same(shell.Login, shell.CurrentPage);
        Assert.False(shell.IsSignedIn);
    }

    // Signing in needs a server; playing what is already on this disk does not. The sign-in
    // screen offers the way in and the shell is what does the navigating (D17).
    [Fact]
    public async Task ContinuingOfflineOpensTheLibraryWithNoSession()
    {
        MainWindowViewModel shell = CreateShell();

        await shell.ContinueOfflineCommand.ExecuteAsync(null);

        Assert.Same(shell.Library, shell.CurrentPage);
        Assert.True(shell.IsOfflineGuest);
        Assert.False(shell.IsSignedIn);

        // No session, so none of the account's surfaces are offered.
        Assert.False(shell.CanNavigate);
        Assert.False(shell.CanPublish);
    }

    // The only way out of an offline visit: there is no session to sign out of.
    [Fact]
    public async Task TheOfflineVisitLeadsBackToTheSignInScreen()
    {
        MainWindowViewModel shell = CreateShell();
        await shell.ContinueOfflineCommand.ExecuteAsync(null);

        await shell.ShowLoginCommand.ExecuteAsync(null);

        Assert.Same(shell.Login, shell.CurrentPage);
        Assert.False(shell.IsOfflineGuest);
    }

    [Fact]
    public async Task SigningInAfterAnOfflineVisitEndsIt()
    {
        MainWindowViewModel shell = CreateShell();
        await shell.ContinueOfflineCommand.ExecuteAsync(null);
        Assert.True(shell.IsOfflineGuest);

        _authentication.IsAuthenticated.Returns(true);
        _authentication.SessionChanged += Raise.EventWith(
            new SessionChangedEventArgs(SessionFor("user-1")));

        Assert.False(shell.IsOfflineGuest);
    }

    // A launcher that asked for a password on every start would not be worth signing into.
    [Fact]
    public async Task ARestoredSessionLandsInTheLibrary()
    {
        _authentication.RestoreAsync(Arg.Any<CancellationToken>()).Returns(true);
        _authentication.IsAuthenticated.Returns(true);
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

    // A publisher looking at their own dashboard can open the page a player lands on, and going
    // back returns to the dashboard rather than to the library — which works because showing the
    // dashboard already records it as the list to return to.
    [Fact]
    public async Task OpeningTheGamePageFromTheDashboardAndComingBack()
    {
        var draft = new Game { Id = "g1", Slug = "orbital-drift", Title = "Orbital Drift" };
        _catalog.GetMyGamesAsync(Arg.Any<GameQuery>(), Arg.Any<CancellationToken>())
            .Returns(new PagedResult<Game> { Items = [draft], Total = 1 });
        _catalog.GetGameAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(new GameDetail { Game = draft });

        MainWindowViewModel shell = CreateShell();
        await shell.ShowDeveloperCommand.ExecuteAsync(null);
        shell.Developer.SelectedGame = draft;

        shell.Developer.OpenGamePageCommand.Execute(null);
        Assert.Same(shell.GameDetail, shell.CurrentPage);

        shell.GameDetail.BackCommand.Execute(null);
        Assert.Same(shell.Developer, shell.CurrentPage);
    }

    [Fact]
    public async Task GoingBackFromTheLibraryReturnsToTheLibrary()
    {
        _catalog.GetGameAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(new GameDetail());
        MainWindowViewModel shell = CreateShell();

        // After CreateShell, which stubs an empty library of its own: the last stub wins.
        _authentication.IsAuthenticated.Returns(true);
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
    public async Task AVerifiedDownloadStartsTheUpdaterAndThenClosesTheLauncher()
    {
        string archive = Path.Combine(Path.GetTempPath(), "updates", "0.2.0", "launcher.zip");
        _updates.CheckAsync(Arg.Any<CancellationToken>()).Returns(
            UpdateCheckResult.Available(Release(0, 2, 0), "https://files.example.test/l.zip"));
        _updateDownloader
            .DownloadAsync(
                Arg.Any<ReleaseDocument>(),
                Arg.Any<string>(),
                Arg.Any<IProgress<long>>(),
                Arg.Any<CancellationToken>())
            .Returns(archive);

        MainWindowViewModel shell = CreateShell();
        await shell.InitializeAsync(TestContext.Current.CancellationToken);
        await shell.DownloadUpdateCommand.ExecuteAsync(null);

        await _updateInstaller.Received(1).StartAsync(
            Arg.Any<ReleaseDocument>(), archive, Arg.Any<CancellationToken>());

        // The order is the substance: the helper is waiting for this process id to be gone
        // before it touches a single file, so the exit is the last step and not the first.
        _shutdown.Received(1).Shutdown();
        Assert.Contains("start again", shell.UpdateStatus, StringComparison.Ordinal);
        Assert.False(shell.CanDownloadUpdate);
    }

    // An archive that is correctly signed and hostile in its entry names is the one thing the
    // hash cannot catch, and the launcher refuses it before writing anything. From here that is
    // an ordinary refusal: the line says so, nothing closes, and the offer stands.
    [Fact]
    public async Task AnArchiveRefusedForWhatItNamesClosesNothing()
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
        _updateInstaller
            .StartAsync(Arg.Any<ReleaseDocument>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .ThrowsAsync(new ApiException(ApiErrorCode.Integrity, "names a file outside"));

        MainWindowViewModel shell = CreateShell();
        await shell.InitializeAsync(TestContext.Current.CancellationToken);
        await shell.DownloadUpdateCommand.ExecuteAsync(null);

        _shutdown.DidNotReceive().Shutdown();
        Assert.Equal(_localization.Translate("Error.Integrity"), shell.UpdateStatus);
        Assert.True(shell.CanDownloadUpdate);
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
        Assert.Contains("si chiude e riparte", shell.UpdateStatus, StringComparison.Ordinal);
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

    // --- what a page keeps across accounts (D70) ---------------------------------------------

    // The complaint this comes from: sign out, sign in as somebody else, and the dashboard was
    // still showing the previous publisher's game. Every page holds an account's data the same
    // way, so every page is asserted here rather than only the one it was noticed on.
    [Fact]
    public async Task NothingOnScreenSurvivesADifferentAccount()
    {
        MainWindowViewModel shell = await SignedInShellShowingSomething();

        _authentication.SessionChanged += Raise.EventWith(
            new SessionChangedEventArgs(SessionFor("user-2")));

        Assert.Empty(shell.Library.Games);
        Assert.Empty(shell.Explore.Games);
        Assert.Empty(shell.Developer.Games);
        Assert.Null(shell.Developer.Selected);
        Assert.Null(shell.Developer.SelectedGame);
        Assert.Empty(shell.Developer.Devlog.Entries);
        Assert.Null(shell.GameDetail.Detail);
        Assert.Empty(shell.Settings.DeletePassword);
        Assert.Empty(shell.Login.Password);
        Assert.Empty(shell.Login.Email);
    }

    // Signing out is the same rule: the next person at this computer is a different account.
    [Fact]
    public async Task NothingOnScreenSurvivesASignOut()
    {
        MainWindowViewModel shell = await SignedInShellShowingSomething();

        _authentication.SessionChanged += Raise.EventWith(new SessionChangedEventArgs(null));

        Assert.Empty(shell.Library.Games);
        Assert.Empty(shell.Developer.Games);
        Assert.Same(shell.Login, shell.CurrentPage);
    }

    // The trap underneath the fix: the same event announces a rotated access token, several
    // times an hour, with the same person behind it. Resetting there empties the library under
    // somebody's hands. It is the account that is compared, not the event that is trusted.
    [Fact]
    public async Task ARotatedTokenChangesNothingOnScreen()
    {
        MainWindowViewModel shell = await SignedInShellShowingSomething();

        _authentication.SessionChanged += Raise.EventWith(
            new SessionChangedEventArgs(SessionFor("user-1")));

        Assert.NotEmpty(shell.Library.Games);
        Assert.NotEmpty(shell.Developer.Games);
        Assert.NotNull(shell.Developer.Selected);
    }

    // The list is what gets reset, so a page missing from it is a page that keeps somebody
    // else's data — which is the bug, not a smaller version of it.
    [Fact]
    public void EveryPageTheShellOwnsIsOneTheAccountChangeResets()
    {
        MainWindowViewModel shell = CreateShell();

        IEnumerable<System.Reflection.PropertyInfo> pages = typeof(MainWindowViewModel)
            .GetProperties()
            .Where(property => typeof(ViewModelBase).IsAssignableFrom(property.PropertyType))

            // Not a page of its own: it is whichever of the pages below is showing.
            .Where(property => property.Name != nameof(MainWindowViewModel.CurrentPage));

        foreach (System.Reflection.PropertyInfo property in pages)
        {
            object? page = property.GetValue(shell);
            Assert.True(
                page is IAccountScopedPage scoped && shell.Pages.Contains(scoped),
                property.Name + " is not reset when the account changes");
        }
    }

    // --- a session on somebody else's password ------------------------------------------

    // The server refuses every route but the change with `password_change_required`, so the
    // library would be a page that only says no.
    [Fact]
    public async Task ARestoredSessionOnATemporaryPasswordLandsOnTheChangeScreen()
    {
        _authentication.RestoreAsync(Arg.Any<CancellationToken>()).Returns(true);
        _authentication.IsAuthenticated.Returns(true);
        _authentication.CurrentSession.Returns(ForcedSessionFor("user-1"));
        MainWindowViewModel shell = CreateShell();

        await shell.InitializeAsync(TestContext.Current.CancellationToken);

        Assert.Same(shell.ChangePassword, shell.CurrentPage);
        Assert.True(shell.ChangePassword.IsForced);
        Assert.True(shell.MustChangePassword);

        // And nothing was fetched with a token that reaches nothing.
        await _library.DidNotReceive().GetLibraryAsync(Arg.Any<CancellationToken>());
    }

    // The same rule from the other entry point, which is the reason there is only one of them.
    [Fact]
    public async Task SigningInOnATemporaryPasswordLandsOnTheChangeScreenToo()
    {
        MainWindowViewModel shell = CreateShell();
        _authentication.CurrentSession.Returns(ForcedSessionFor("user-1"));
        _authentication
            .SignInAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(ForcedSessionFor("user-1"));

        shell.Login.Email = "locked@example.com";
        shell.Login.Password = "the temporary one";
        await shell.Login.SubmitCommand.ExecuteAsync(null);
        await Task.Yield();

        Assert.Same(shell.ChangePassword, shell.CurrentPage);
        Assert.True(shell.ChangePassword.IsForced);
    }

    [Fact]
    public async Task ChangingThePasswordLandsInTheLibrary()
    {
        _authentication.RestoreAsync(Arg.Any<CancellationToken>()).Returns(true);
        _authentication.IsAuthenticated.Returns(true);
        _authentication.CurrentSession.Returns(ForcedSessionFor("user-1"));
        MainWindowViewModel shell = CreateShell();
        await shell.InitializeAsync(TestContext.Current.CancellationToken);

        _authentication.CurrentSession.Returns(SessionFor("user-1"));
        shell.ChangePassword.CurrentPassword = "the temporary one";
        shell.ChangePassword.NewPassword = "a brand new passphrase";
        shell.ChangePassword.ConfirmPassword = "a brand new passphrase";
        await shell.ChangePassword.SubmitCommand.ExecuteAsync(null);
        await Task.Yield();

        Assert.Same(shell.Library, shell.CurrentPage);
    }

    // The publish tab is hidden as well: it is a page whose every button the server refuses.
    [Fact]
    public async Task NothingElseIsOfferedWhileThePasswordIsSomebodyElsesChoice()
    {
        _authentication.RestoreAsync(Arg.Any<CancellationToken>()).Returns(true);
        _authentication.IsAuthenticated.Returns(true);
        _authentication.IsAuthenticated.Returns(true);
        _authentication.HasPermission(Permissions.GamePublish).Returns(true);
        _authentication.CurrentSession.Returns(ForcedSessionFor("user-1"));
        MainWindowViewModel shell = CreateShell();

        await shell.InitializeAsync(TestContext.Current.CancellationToken);

        Assert.False(shell.CanPublish);

        // And the tabs with it: each of them is a page whose first request answers 403.
        Assert.True(shell.IsSignedIn);
        Assert.False(shell.CanNavigate);
    }

    // Found by signing in through the real window, which is the only place it showed. The
    // event does not arrive on the UI thread — `AuthenticationService` awaits its token store
    // with ConfigureAwait(false), so a sign-in publishes from the thread pool — and everything
    // the handler touches is bound. `Button.get_Command()` threw "the calling thread cannot
    // access this object" on a pool thread, which does not surface as an error message: the
    // launcher closed. Every sign-in, since the pages started being reset.
    [Fact]
    public void ASessionPublishedOffTheUiThreadIsAppliedOnIt()
    {
        RecordingSynchronizationContext context = new();
        SynchronizationContext? previous = SynchronizationContext.Current;
        SynchronizationContext.SetSynchronizationContext(context);

        MainWindowViewModel shell;
        try
        {
            // Captured in the constructor, exactly as the running application captures Avalonia's.
            shell = CreateShell();
        }
        finally
        {
            SynchronizationContext.SetSynchronizationContext(previous);
        }

        // Raised with no context current, which is what a thread-pool continuation looks like.
        _authentication.SessionChanged += Raise.EventWith(
            new SessionChangedEventArgs(SessionFor("user-1")));

        Assert.True(
            context.Posts > 0,
            "the session change reached the bound properties without going through the UI thread");
        Assert.Equal("user-1", shell.AccountName);
    }

    /// <summary>
    /// Runs what is posted to it, immediately and inline, and counts. Inline because these
    /// tests assert on the outcome straight after the event; the count is what says the work
    /// went *through* it rather than round it.
    /// </summary>
    private sealed class RecordingSynchronizationContext : SynchronizationContext
    {
        public int Posts { get; private set; }

        public override void Post(SendOrPostCallback callback, object? state)
        {
            Posts++;
            callback(state);
        }
    }

    private static AuthSession SessionFor(string userId) =>
        new() { User = new AuthenticatedUser { Id = userId, DisplayName = userId } };

    private static AuthSession ForcedSessionFor(string userId) =>
        new()
        {
            User = new AuthenticatedUser
            {
                Id = userId,
                DisplayName = userId,
                PasswordChangeRequired = true,
            },
        };

    /// <summary>
    /// A shell signed in as <c>user-1</c> with something loaded on every page: a library, a
    /// search, a game page, a dashboard with a game selected, a password typed into the
    /// account-deletion box and an address left in the sign-in form.
    /// </summary>
    private async Task<MainWindowViewModel> SignedInShellShowingSomething()
    {
        Game game = new() { Id = "g1", Slug = "orbital-drift", Title = "Orbital Drift" };

        // Built before the answers are arranged: `CreateShell` stubs an empty library and an
        // empty Explore of its own, and NSubstitute's last stub wins.
        MainWindowViewModel shell = CreateShell();

        // Signed in, which the library asks about before it asks the server at all: with
        // nobody signed in it shows the disk and makes no request (the offline visit).
        _authentication.IsAuthenticated.Returns(true);

        _library.GetLibraryAsync(Arg.Any<CancellationToken>()).Returns([game]);
        _catalog.ExploreAsync(Arg.Any<GameQuery>(), Arg.Any<CancellationToken>())
            .Returns(new PagedResult<Game> { Items = [game], Total = 1, Limit = 24 });
        _catalog.GetMyGamesAsync(Arg.Any<GameQuery>(), Arg.Any<CancellationToken>())
            .Returns(new PagedResult<Game> { Items = [game], Total = 1, Limit = 24 });
        _catalog.GetGameAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(new GameDetail { Game = game });

        _authentication.SessionChanged += Raise.EventWith(
            new SessionChangedEventArgs(SessionFor("user-1")));

        await shell.ShowLibraryCommand.ExecuteAsync(null);
        await shell.ShowExploreCommand.ExecuteAsync(null);
        await shell.ShowDeveloperCommand.ExecuteAsync(null);
        shell.Developer.SelectedGame = shell.Developer.Games[0];
        await Task.Yield();

        shell.Explore.OpenGameCommand.Execute(new StoreCardViewModel(game));
        await Task.Yield();

        shell.Settings.DeletePassword = "correct horse battery staple";
        shell.Login.Email = "harness-dev@example.test";
        shell.Login.Password = "correct horse battery staple";

        Assert.NotEmpty(shell.Library.Games);
        Assert.NotEmpty(shell.Explore.Games);
        Assert.NotEmpty(shell.Developer.Games);
        Assert.NotNull(shell.Developer.Selected);
        Assert.NotNull(shell.GameDetail.Detail);

        return shell;
    }
}

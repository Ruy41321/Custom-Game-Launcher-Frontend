using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using GameLauncher.App.Services;
using GameLauncher.Core.Api;
using GameLauncher.Core.Authentication;
using GameLauncher.Core.Configuration;
using GameLauncher.Core.Diagnostics;
using GameLauncher.Core.Downloads;
using GameLauncher.Core.Localization;
using GameLauncher.Core.Updates;

namespace GameLauncher.App.ViewModels;

/// <summary>
/// Shell view model. Owns the window title, the language picker, the account state and which
/// page is showing.
///
/// Navigation runs one way: the shell knows its children and the children raise events. That
/// keeps the graph acyclic — a child holding a navigator that holds the child is the shape
/// that makes a view model impossible to construct in a test.
/// </summary>
public sealed partial class MainWindowViewModel : ViewModelBase
{
    private readonly ILocalizationService _localization;
    private readonly IUserSettingsStore _settingsStore;
    private readonly IAuthenticationService _authentication;
    private readonly IInstallationService _installations;
    private readonly ICrashReportUploader _crashReports;
    private readonly IUpdateChecker _updateChecker;
    private readonly ILauncherUpdateDownloader _updateDownloader;
    private readonly IUpdateInstaller _updateInstaller;
    private readonly IApplicationShutdown _shutdown;
    private readonly IApiErrorPresenter _errors;

    /// <summary>
    /// Whose data is on the pages. Compared rather than the event trusted: the session changes
    /// on every token rotation as well, with the same account behind it, and resetting there
    /// would empty the library under somebody's hands roughly once an hour.
    /// </summary>
    private string? _accountId;

    /// <summary>The verified release the banner is about; null when there is nothing to offer.</summary>
    private UpdateCheckResult? _availableUpdate;

    /// <summary>
    /// How to say the update line again. The banner's sentences are built in code rather than
    /// bound through <c>{loc:Tr}</c>, so — like <see cref="WelcomeMessage"/> — they would
    /// otherwise stay in the language they were first written in when somebody switches.
    /// </summary>
    private Func<string>? _updateStatusText;

    [ObservableProperty]
    private string _welcomeMessage = string.Empty;

    [ObservableProperty]
    private LanguageOption _selectedLanguage;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsSignedIn))]
    [NotifyPropertyChangedFor(nameof(CanPublish))]
    [NotifyPropertyChangedFor(nameof(MustChangePassword))]
    [NotifyPropertyChangedFor(nameof(CanNavigate))]
    private ViewModelBase _currentPage;

    [ObservableProperty]
    private string _accountName = string.Empty;

    /// <summary>
    /// In the launcher with no session, because the server could not be reached and somebody
    /// asked to carry on anyway. The games on this disk are theirs and play perfectly well; the
    /// account's own surfaces — Explore, publishing, settings that talk to a server — are not
    /// offered, because there is nobody to offer them to.
    /// </summary>
    [ObservableProperty]
    private bool _isOfflineGuest;

    /// <summary>
    /// Whether the update line is showing. An update is never applied silently: a swap needs
    /// the launcher to exit, so a silent update is an application closing under the hands of
    /// somebody using it.
    /// </summary>
    [ObservableProperty]
    private bool _updateAvailable;

    [ObservableProperty]
    private string _updateHeadline = string.Empty;

    /// <summary>The release notes, which are a paragraph and not a changelog.</summary>
    [ObservableProperty]
    private string _updateNotes = string.Empty;

    /// <summary>Empty until the download starts; then progress, then the outcome.</summary>
    [ObservableProperty]
    private string _updateStatus = string.Empty;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(DownloadUpdateCommand))]
    private bool _canDownloadUpdate;

    public MainWindowViewModel(
        ILocalizationService localization,
        IUserSettingsStore settingsStore,
        LauncherConfiguration configuration,
        IAuthenticationService authentication,
        IInstallationService installations,
        ICrashReportUploader crashReports,
        IUpdateChecker updateChecker,
        ILauncherUpdateDownloader updateDownloader,
        IUpdateInstaller updateInstaller,
        IApplicationShutdown shutdown,
        IApiErrorPresenter errors,
        LoginViewModel login,
        ExploreViewModel explore,
        LibraryViewModel library,
        GameDetailViewModel gameDetail,
        DeveloperViewModel developer,
        SettingsViewModel settings,
        ChangePasswordViewModel changePassword)
    {
        _localization = localization;
        _settingsStore = settingsStore;
        _authentication = authentication;
        _installations = installations;
        _crashReports = crashReports;
        _updateChecker = updateChecker;
        _updateDownloader = updateDownloader;
        _updateInstaller = updateInstaller;
        _shutdown = shutdown;
        _errors = errors;

        Login = login;
        Explore = explore;
        Library = library;
        GameDetail = gameDetail;
        Developer = developer;
        Settings = settings;
        ChangePassword = changePassword;
        _currentPage = login;

        AppName = configuration.AppName;
        Languages = localization.AvailableLanguages;
        _selectedLanguage =
            Languages.FirstOrDefault(
                language => language.CultureName == localization.CurrentCulture.TwoLetterISOLanguageName)
            ?? Languages[0];

        // Every page the shell owns, in one list, because the reset below must not be able to
        // miss one — a page that keeps an account's data after somebody else signs in is the
        // bug this exists for, and the next page added is the one that would forget.
        Pages = [Login, Explore, Library, GameDetail, Developer, Settings, ChangePassword];

        Login.SignedIn += (_, _) => AfterSignInCommand.Execute(null);

        // Not a sign-in and never treated as one: no session exists, and the library will say
        // so. The shell navigates rather than the page, as everywhere else (D17).
        Login.ContinueOfflineRequested += (_, _) => ContinueOfflineCommand.Execute(null);

        // A password change ends with a live session and nothing else pending, so it lands
        // exactly where a sign-in does; cancelling is only ever possible when it was not
        // forced, and then the library is where somebody was on their way to anyway.
        ChangePassword.Changed += (_, _) => ShowLibraryCommand.Execute(null);
        ChangePassword.Cancelled += (_, _) => ShowLibraryCommand.Execute(null);
        Explore.GameSelected += async (_, idOrSlug) => await ShowGameAsync(idOrSlug).ConfigureAwait(true);
        Library.GameSelected += async (_, idOrSlug) => await ShowGameAsync(idOrSlug).ConfigureAwait(true);

        // The publisher's dashboard opens the same page for the same reason, and "back" already
        // works: showing the dashboard records it as the list page to return to.
        Developer.GameSelected += async (_, idOrSlug) => await ShowGameAsync(idOrSlug).ConfigureAwait(true);

        GameDetail.BackRequested += (_, _) => CurrentPage = _lastListPage ?? Library;

        _authentication.SessionChanged += (_, args) => OnSessionChanged(args.Session);

        _localization.LanguageChanged += (_, _) => RefreshLocalizedText();
        RefreshLocalizedText();
    }

    public string AppName { get; }

    /// <summary>
    /// The pages, as the thing that gets reset. Public so a test can hold this list against
    /// the properties below and fail when they disagree.
    /// </summary>
    public IReadOnlyList<IAccountScopedPage> Pages { get; }

    public IReadOnlyList<LanguageOption> Languages { get; }

    public LoginViewModel Login { get; }

    public ExploreViewModel Explore { get; }

    public LibraryViewModel Library { get; }

    public GameDetailViewModel GameDetail { get; }

    public DeveloperViewModel Developer { get; }

    public SettingsViewModel Settings { get; }

    public ChangePasswordViewModel ChangePassword { get; }

    public bool IsSignedIn => _authentication.IsAuthenticated;

    /// <summary>
    /// Whether the tabs are worth offering: signed in, and not held on the one screen the
    /// server will answer for. A tab that only ever produces a 403 is the dead end this rule
    /// exists to remove, and hiding it is the same answer D61 gives about a button.
    /// </summary>
    public bool CanNavigate => IsSignedIn && !MustChangePassword;

    /// <summary>
    /// Whether the account may reach anything but the password screen. The server refuses every
    /// other route with `password_change_required` until it is done, so the tabs are hidden
    /// rather than left to lead somewhere that only answers no.
    /// </summary>
    public bool MustChangePassword =>
        _authentication.CurrentSession?.User.PasswordChangeRequired ?? false;

    /// <summary>
    /// Advisory, like every client-side permission check: the publish routes refuse the same
    /// account again. Hiding the tab keeps a player from finding a page that only says no.
    /// </summary>
    public bool CanPublish =>
        IsSignedIn
        && !MustChangePassword
        && _authentication.HasPermission(Permissions.GamePublish);

    /// <summary>Where "back" returns to, so opening a game from Explore does not land in Library.</summary>
    private ViewModelBase? _lastListPage;

    /// <summary>
    /// Restores the stored session before the first frame is meaningful. A launcher that
    /// asked for a password every time it started would not be worth signing into.
    /// </summary>
    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        // Before anything else, and never a reason to fail startup: what a previous run left
        // half done is recorded now, so the library and the game page describe what is really
        // on disk rather than what the launcher was told last time it was alive.
        try
        {
            await _installations.RecoverAsync(cancellationToken).ConfigureAwait(true);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // Nothing here is worth a blank window.
        }

        // Before signing in, because a report is sent without a session and the crashes worth
        // having are often the ones that happened on the sign-in screen. Never awaited for its
        // result and never able to fail the start: it swallows everything itself.
        await _crashReports.UploadPendingAsync(cancellationToken).ConfigureAwait(true);

        bool restored;
        try
        {
            restored = await _authentication.RestoreAsync(cancellationToken).ConfigureAwait(true);
        }
        catch (Core.Api.ApiException)
        {
            // A last resort rather than the offline path: an unreachable server keeps the
            // stored session and reports success, and the library falls back to what is on
            // disk. Anything that still throws here is a failure nobody has a story for, and
            // the sign-in screen is the only honest thing left to show.
            restored = false;
        }

        if (restored)
        {
            await AfterSignInAsync(cancellationToken).ConfigureAwait(true);
        }
        else
        {
            // Only when the sign-in screen is what somebody is about to look at: it decides
            // whether to offer "forgotten your password?", and asking on a start-up that goes
            // straight to the library would be a request for a button nobody will see.
            await Login.LoadAsync(cancellationToken).ConfigureAwait(true);
        }

        // Last, and for the same reason the crash uploader can never throw: a launcher that
        // would not open because it could not reach the update route would be the worst
        // possible outcome of this feature. The checker swallows everything itself.
        await CheckForUpdatesAsync(cancellationToken).ConfigureAwait(true);
    }

    /// <summary>
    /// Announces a newer launcher, and announces nothing else. A check that found nothing, one
    /// this build never made because no signing key is compiled in, and one that failed are all
    /// silence: none of the three is something the person in front of the window can act on.
    /// </summary>
    private async Task CheckForUpdatesAsync(CancellationToken cancellationToken)
    {
        UpdateCheckResult result = await _updateChecker.CheckAsync(cancellationToken)
            .ConfigureAwait(true);

        if (!result.IsAvailable)
        {
            return;
        }

        _availableUpdate = result;
        UpdateNotes = result.Release!.Notes;
        _updateStatusText = null;
        CanDownloadUpdate = true;
        UpdateAvailable = true;
        RefreshLocalizedText();
    }

    /// <summary>
    /// Fetches the archive, refuses bytes that are not the ones the signed document named,
    /// unpacks it, starts the updater and closes this launcher — which is the last of those
    /// steps and the one the helper is waiting for.
    ///
    /// It is a button and never a timer. A swap requires this process to exit, so a silent
    /// update is an application closing under the hands of somebody using it.
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanDownloadUpdate))]
    private async Task DownloadUpdateAsync(CancellationToken cancellationToken)
    {
        if (_availableUpdate is not { Release: { } release })
        {
            return;
        }

        CanDownloadUpdate = false;
        SetUpdateStatus(() => _localization.Translate("Update.Downloading", 0));

        try
        {
            string archive = await _updateDownloader
                .DownloadAsync(
                    release,
                    _availableUpdate.ArtifactUrl,
                    new Progress<long>(OnUpdateTransferred),
                    cancellationToken)
                .ConfigureAwait(true);

            SetUpdateStatus(() => _localization.Translate(
                "Update.Restarting", release.Version.ToString(), AppName));

            await _updateInstaller.StartAsync(release, archive, cancellationToken)
                .ConfigureAwait(true);

            // Nothing after this line runs for long: the updater is waiting for this process
            // id to be gone before it touches a single file.
            _shutdown.Shutdown();
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            // Offered again rather than disarmed: an interrupted transfer, a host serving the
            // wrong bytes and an archive refused for the names inside it are all worth one more
            // press, and every one of those refusals happens again identically.
            SetUpdateStatus(() => _errors.Describe(exception));
            CanDownloadUpdate = true;
        }
    }

    private void SetUpdateStatus(Func<string> render)
    {
        _updateStatusText = render;
        UpdateStatus = render();
    }

    /// <summary>Puts the line away for this run. The next start asks again.</summary>
    [RelayCommand]
    private void DismissUpdate() => UpdateAvailable = false;

    private void OnUpdateTransferred(long transferred)
    {
        if (_availableUpdate?.Release is not { Size: > 0 } release)
        {
            return;
        }

        int percent = (int)Math.Clamp(transferred * 100 / release.Size, 0, 100);
        OnUiThread(() => SetUpdateStatus(
            () => _localization.Translate("Update.Downloading", percent)));
    }

    /// <summary>
    /// Where a launcher goes once a session exists — the library, unless the session says the
    /// password is somebody else's choice, in which case there is exactly one screen the
    /// server will answer for.
    ///
    /// One place rather than two: the restore at start-up and the sign-in button both arrive
    /// here, and a rule enforced in one of them is a rule the other forgets.
    /// </summary>
    [RelayCommand]
    private async Task AfterSignInAsync(CancellationToken cancellationToken)
    {
        if (MustChangePassword)
        {
            ShowForcedPasswordChange();
            return;
        }

        await ShowLibraryAsync(cancellationToken).ConfigureAwait(true);
    }

    private void ShowForcedPasswordChange()
    {
        ChangePassword.IsForced = true;
        CurrentPage = ChangePassword;

        // Deliberately not remembered as a list page: "back" from anywhere must never land on
        // a screen that only exists until it is finished.
        _lastListPage = null;
    }

    /// <summary>
    /// Into the library with nobody signed in. It is the same page: it reads the install store
    /// first and unconditionally (D29), so what it shows is what this machine can play, under
    /// the banner that says the rest of the library is not here.
    /// </summary>
    [RelayCommand]
    private async Task ContinueOfflineAsync(CancellationToken cancellationToken)
    {
        IsOfflineGuest = true;
        await ShowLibraryAsync(cancellationToken).ConfigureAwait(true);
    }

    /// <summary>
    /// Back to the sign-in screen from the offline library, which is the only way out of it —
    /// there is no session to sign out of. It asks the server its one question again, which is
    /// also how somebody finds out the network came back.
    /// </summary>
    [RelayCommand]
    private async Task ShowLoginAsync(CancellationToken cancellationToken)
    {
        IsOfflineGuest = false;
        CurrentPage = Login;
        _lastListPage = null;
        await Login.LoadAsync(cancellationToken).ConfigureAwait(true);
    }

    [RelayCommand]
    private async Task ShowLibraryAsync(CancellationToken cancellationToken)
    {
        CurrentPage = Library;
        _lastListPage = Library;
        await Library.LoadAsync(cancellationToken).ConfigureAwait(true);
    }

    [RelayCommand]
    private async Task ShowExploreAsync(CancellationToken cancellationToken)
    {
        CurrentPage = Explore;
        _lastListPage = Explore;
        await Explore.LoadAsync(cancellationToken).ConfigureAwait(true);
    }

    [RelayCommand]
    private async Task ShowDeveloperAsync(CancellationToken cancellationToken)
    {
        CurrentPage = Developer;
        _lastListPage = Developer;
        await Developer.LoadAsync(cancellationToken).ConfigureAwait(true);
    }

    /// <summary>The ordinary way in, for somebody who simply wants a new password.</summary>
    [RelayCommand]
    private void ShowChangePassword()
    {
        ChangePassword.IsForced = false;
        CurrentPage = ChangePassword;
    }

    [RelayCommand]
    private async Task ShowSettingsAsync(CancellationToken cancellationToken)
    {
        // Not remembered as the list page: "back" from a game should never land in settings.
        CurrentPage = Settings;
        await Settings.LoadAsync(cancellationToken).ConfigureAwait(true);
    }

    [RelayCommand]
    private async Task SignOutAsync(CancellationToken cancellationToken)
    {
        await _authentication.SignOutAsync(cancellationToken).ConfigureAwait(true);
        CurrentPage = Login;
        _lastListPage = null;

        // The sign-in screen is on screen again and a different server may be behind the next
        // password, so its one question is asked again rather than answered from a run ago.
        await Login.LoadAsync(cancellationToken).ConfigureAwait(true);
    }

    /// <summary>
    /// Persists the choice so the launcher opens in the same language next time. Failing to
    /// save a preference must never surface as an error to the user.
    /// </summary>
    [RelayCommand]
    private async Task ChangeLanguageAsync(LanguageOption? language)
    {
        if (language is null || !_localization.TrySetLanguage(language.CultureName))
        {
            return;
        }

        SelectedLanguage = language;

        UserSettings settings = await _settingsStore.LoadAsync().ConfigureAwait(false);
        await _settingsStore.SaveAsync(settings with { Language = language.CultureName })
            .ConfigureAwait(false);
    }

    private async Task ShowGameAsync(string idOrSlug)
    {
        CurrentPage = GameDetail;
        await GameDetail.LoadAsync(idOrSlug).ConfigureAwait(true);
    }

    /// <summary>
    /// Everything that follows touches bound properties and re-evaluates bound commands, and
    /// the event that starts it does **not** arrive on the UI thread: `AuthenticationService`
    /// awaits its token store with `ConfigureAwait(false)`, so a sign-in publishes from the
    /// thread pool. `Button.get_Command()` then throws "the calling thread cannot access this
    /// object", which is an unhandled exception on a pool thread — the launcher does not show
    /// an error, it closes. Marshalled here, once, rather than at each of the callees.
    /// </summary>
    private void OnSessionChanged(AuthSession? session) =>
        OnUiThread(() => ApplySessionChange(session));

    private void ApplySessionChange(AuthSession? session)
    {
        AccountName = session?.User.DisplayName ?? string.Empty;

        // A session ends the offline visit, whichever way it arrived.
        if (session is not null)
        {
            IsOfflineGuest = false;
        }
        OnPropertyChanged(nameof(IsSignedIn));
        OnPropertyChanged(nameof(CanPublish));
        OnPropertyChanged(nameof(MustChangePassword));
        OnPropertyChanged(nameof(CanNavigate));

        // The pages live as long as the window and keep what they were shown, which is right
        // for one account and wrong for the next: the dashboard was still showing the previous
        // publisher's game after a sign-out and a sign-in. Keyed on *who*, not on the event —
        // a rotated token is the same person and must change nothing on screen.
        string? accountId = session?.User.Id;
        if (!string.Equals(accountId, _accountId, StringComparison.Ordinal))
        {
            _accountId = accountId;

            foreach (IAccountScopedPage page in Pages)
            {
                page.ResetForAccountChange();
            }
        }

        if (session is null)
        {
            IsOfflineGuest = false;
            CurrentPage = Login;
        }
    }

    partial void OnSelectedLanguageChanged(LanguageOption value)
    {
        if (value.CultureName != _localization.CurrentCulture.TwoLetterISOLanguageName)
        {
            ChangeLanguageCommand.Execute(value);
        }
    }

    private void RefreshLocalizedText()
    {
        WelcomeMessage = _localization.Translate("Shell.Welcome", AppName);

        if (_availableUpdate?.Release is { } release)
        {
            UpdateHeadline = _localization.Translate(
                "Update.Available", release.Version.ToString(), AppName);
            UpdateStatus = _updateStatusText?.Invoke() ?? string.Empty;
        }
    }
}

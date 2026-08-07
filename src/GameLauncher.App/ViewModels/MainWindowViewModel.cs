using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
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
    private readonly IApiErrorPresenter _errors;

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
    private ViewModelBase _currentPage;

    [ObservableProperty]
    private string _accountName = string.Empty;

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
        IApiErrorPresenter errors,
        LoginViewModel login,
        ExploreViewModel explore,
        LibraryViewModel library,
        GameDetailViewModel gameDetail,
        DeveloperViewModel developer,
        SettingsViewModel settings)
    {
        _localization = localization;
        _settingsStore = settingsStore;
        _authentication = authentication;
        _installations = installations;
        _crashReports = crashReports;
        _updateChecker = updateChecker;
        _updateDownloader = updateDownloader;
        _errors = errors;

        Login = login;
        Explore = explore;
        Library = library;
        GameDetail = gameDetail;
        Developer = developer;
        Settings = settings;
        _currentPage = login;

        AppName = configuration.AppName;
        Languages = localization.AvailableLanguages;
        _selectedLanguage =
            Languages.FirstOrDefault(
                language => language.CultureName == localization.CurrentCulture.TwoLetterISOLanguageName)
            ?? Languages[0];

        Login.SignedIn += (_, _) => ShowLibraryCommand.Execute(null);
        Explore.GameSelected += async (_, idOrSlug) => await ShowGameAsync(idOrSlug).ConfigureAwait(true);
        Library.GameSelected += async (_, idOrSlug) => await ShowGameAsync(idOrSlug).ConfigureAwait(true);
        GameDetail.BackRequested += (_, _) => CurrentPage = _lastListPage ?? Library;

        _authentication.SessionChanged += (_, args) => OnSessionChanged(args.Session);

        _localization.LanguageChanged += (_, _) => RefreshLocalizedText();
        RefreshLocalizedText();
    }

    public string AppName { get; }

    public IReadOnlyList<LanguageOption> Languages { get; }

    public LoginViewModel Login { get; }

    public ExploreViewModel Explore { get; }

    public LibraryViewModel Library { get; }

    public GameDetailViewModel GameDetail { get; }

    public DeveloperViewModel Developer { get; }

    public SettingsViewModel Settings { get; }

    public bool IsSignedIn => _authentication.IsAuthenticated;

    /// <summary>
    /// Advisory, like every client-side permission check: the publish routes refuse the same
    /// account again. Hiding the tab keeps a player from finding a page that only says no.
    /// </summary>
    public bool CanPublish =>
        IsSignedIn && _authentication.HasPermission(Permissions.GamePublish);

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
            await ShowLibraryAsync(cancellationToken).ConfigureAwait(true);
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
    /// Fetches the archive and refuses bytes that are not the ones the signed document named.
    ///
    /// It stops there, because <c>GameLauncher.Updater</c> still moves no files: what the user
    /// is told when this succeeds is where the verified archive is, not that the launcher is
    /// about to replace itself. Promising the second would be the one kind of lie this feature
    /// cannot afford.
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

            string directory = Path.GetDirectoryName(archive) ?? archive;
            SetUpdateStatus(() => _localization.Translate(
                "Update.Ready", release.Version.ToString(), directory));
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            // Offered again rather than disarmed: an interrupted transfer and a host serving
            // the wrong bytes are both worth one more press, and the hash refuses the second
            // one exactly as it refused the first.
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

    private void OnSessionChanged(AuthSession? session)
    {
        AccountName = session?.User.DisplayName ?? string.Empty;
        OnPropertyChanged(nameof(IsSignedIn));
        OnPropertyChanged(nameof(CanPublish));

        if (session is null)
        {
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

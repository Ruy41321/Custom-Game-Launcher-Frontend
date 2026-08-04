using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using GameLauncher.Core.Authentication;
using GameLauncher.Core.Configuration;
using GameLauncher.Core.Localization;

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

    public MainWindowViewModel(
        ILocalizationService localization,
        IUserSettingsStore settingsStore,
        LauncherConfiguration configuration,
        IAuthenticationService authentication,
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

    private void RefreshLocalizedText() =>
        WelcomeMessage = _localization.Translate("Shell.Welcome", AppName);
}

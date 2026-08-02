using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using GameLauncher.Core.Configuration;
using GameLauncher.Core.Localization;

namespace GameLauncher.App.ViewModels;

/// <summary>
/// Shell view model. Owns the window title, the connection banner and the language picker;
/// navigation between Library, Explore and Settings arrives in milestone 6.
/// </summary>
public sealed partial class MainWindowViewModel : ViewModelBase
{
    private readonly ILocalizationService _localization;
    private readonly IUserSettingsStore _settingsStore;

    [ObservableProperty]
    private string _welcomeMessage = string.Empty;

    [ObservableProperty]
    private LanguageOption _selectedLanguage;

    public MainWindowViewModel(
        ILocalizationService localization,
        IUserSettingsStore settingsStore,
        LauncherConfiguration configuration)
    {
        _localization = localization;
        _settingsStore = settingsStore;

        AppName = configuration.AppName;
        Languages = localization.AvailableLanguages;
        _selectedLanguage =
            Languages.FirstOrDefault(
                language => language.CultureName == localization.CurrentCulture.TwoLetterISOLanguageName)
            ?? Languages[0];

        _localization.LanguageChanged += (_, _) => RefreshLocalizedText();
        RefreshLocalizedText();
    }

    public string AppName { get; }

    public IReadOnlyList<LanguageOption> Languages { get; }

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

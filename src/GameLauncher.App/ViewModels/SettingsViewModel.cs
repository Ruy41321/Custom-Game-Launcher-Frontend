using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using GameLauncher.App.Services;
using GameLauncher.Core.Configuration;
using GameLauncher.Core.Installs;
using GameLauncher.Core.Localization;
using GameLauncher.Core.Models;
using GameLauncher.Core.Platform;

namespace GameLauncher.App.ViewModels;

/// <summary>
/// The preferences a user can change. Deliberately short: a setting that does nothing is worse
/// than an absent one, so only what is actually honoured appears here.
/// </summary>
public sealed partial class SettingsViewModel : ViewModelBase
{
    private readonly IUserSettingsStore _store;
    private readonly IPathProvider _paths;
    private readonly IInstallStore _installs;
    private readonly IFolderPicker _folders;
    private readonly IThemeSwitcher _theme;
    private readonly ILocalizationService _localization;

    private UserSettings _settings = new();

    [ObservableProperty]
    private string? _statusMessage;

    /// <summary>Empty means "wherever the platform puts things", which is shown alongside.</summary>
    [ObservableProperty]
    private string _installDirectory = string.Empty;

    [ObservableProperty]
    private string _themeVariant = DefaultTheme;

    private const string DefaultTheme = "dark";

    public SettingsViewModel(
        IUserSettingsStore store,
        IPathProvider paths,
        IInstallStore installs,
        IFolderPicker folders,
        IThemeSwitcher theme,
        ILocalizationService localization)
    {
        _store = store;
        _paths = paths;
        _installs = installs;
        _folders = folders;
        _theme = theme;
        _localization = localization;
    }

    public IReadOnlyList<string> Themes { get; } = ["dark", "light", "system"];

    /// <summary>Shown under the box, so an empty setting is not a mystery.</summary>
    public string DefaultInstallDirectory => _paths.DefaultInstallDirectory;

    /// <summary>
    /// What is already installed does not move when this changes, and saying so on the page is
    /// cheaper than a support question.
    /// </summary>
    public string InstalledElsewhereNotice { get; private set; } = string.Empty;

    public bool HasInstalledElsewhere => InstalledElsewhereNotice.Length > 0;

    public async Task LoadAsync(CancellationToken cancellationToken = default)
    {
        _settings = await _store.LoadAsync(cancellationToken).ConfigureAwait(true);

        InstallDirectory = _settings.InstallDirectory ?? string.Empty;
        ThemeVariant = _settings.ThemeVariant ?? DefaultTheme;
        StatusMessage = null;

        IReadOnlyList<InstalledGame> installed = await _installs
            .GetAllAsync(cancellationToken).ConfigureAwait(true);

        InstalledElsewhereNotice = installed.Count > 0
            ? _localization.Translate(
                "Settings.InstalledElsewhere",
                installed.Count.ToString(System.Globalization.CultureInfo.CurrentCulture))
            : string.Empty;

        OnPropertyChanged(nameof(DefaultInstallDirectory));
        OnPropertyChanged(nameof(InstalledElsewhereNotice));
        OnPropertyChanged(nameof(HasInstalledElsewhere));
    }

    [RelayCommand]
    private async Task ChooseInstallDirectoryAsync()
    {
        string? chosen = await _folders
            .PickAsync(_localization.Translate("Settings.ChooseInstallDirectory"))
            .ConfigureAwait(true);

        if (chosen is not null)
        {
            InstallDirectory = chosen;
            await SaveAsync().ConfigureAwait(true);
        }
    }

    /// <summary>Back to the platform default, which is what an empty setting means.</summary>
    [RelayCommand]
    private async Task ResetInstallDirectoryAsync()
    {
        InstallDirectory = string.Empty;
        await SaveAsync().ConfigureAwait(true);
    }

    [RelayCommand]
    private async Task SaveAsync()
    {
        string trimmed = InstallDirectory.Trim();

        _settings = _settings with
        {
            InstallDirectory = trimmed.Length == 0 ? null : trimmed,
            ThemeVariant = ThemeVariant,
        };

        await _store.SaveAsync(_settings).ConfigureAwait(true);
        StatusMessage = _localization.Translate("Settings.Saved");
    }

    /// <summary>
    /// Applied as soon as it is picked rather than on save: a theme is judged by looking at
    /// it, and a preview that needs a button first is not a preview.
    /// </summary>
    partial void OnThemeVariantChanged(string value)
    {
        _theme.Apply(value);

        if (_settings.ThemeVariant is not null || value != DefaultTheme)
        {
            _ = SaveAsync();
        }
    }
}

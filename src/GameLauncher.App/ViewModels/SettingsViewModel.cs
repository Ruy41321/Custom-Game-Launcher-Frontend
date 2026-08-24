using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using GameLauncher.App.Services;
using GameLauncher.Core.Api;
using GameLauncher.Core.Authentication;
using GameLauncher.Core.Configuration;
using GameLauncher.Core.Installs;
using GameLauncher.Core.Localization;
using GameLauncher.Core.Models;
using GameLauncher.Core.Platform;

namespace GameLauncher.App.ViewModels;

/// <summary>
/// The preferences a user can change, and the one action that ends the account.
///
/// Deliberately short: a setting that does nothing is worse than an absent one, so only what is
/// actually honoured appears here. Erasure lives on this page rather than on a page of its own
/// because it is where somebody goes looking for it, and because a page whose only purpose is
/// to delete an account is a page that only ever gets opened by mistake.
/// </summary>
public sealed partial class SettingsViewModel : ViewModelBase, IAccountScopedPage
{
    private readonly IUserSettingsStore _store;
    private readonly IPathProvider _paths;
    private readonly IInstallStore _installs;
    private readonly IFolderPicker _folders;
    private readonly IThemeSwitcher _theme;
    private readonly IAccountService _account;
    private readonly IAuthenticationService _authentication;
    private readonly IApiErrorPresenter _errors;
    private readonly ILocalizationService _localization;

    private UserSettings _settings = new();

    [ObservableProperty]
    private string? _statusMessage;

    [ObservableProperty]
    private string? _errorMessage;

    /// <summary>
    /// Re-authentication, and the server requires it. Never persisted, and cleared the moment
    /// the deletion is armed, confirmed or abandoned.
    /// </summary>
    [ObservableProperty]
    private string _deletePassword = string.Empty;

    /// <summary>Optional, and read by nobody but a server operator.</summary>
    [ObservableProperty]
    private string _deleteReason = string.Empty;

    [ObservableProperty]
    private PendingDeletion? _pendingDeletion;

    [ObservableProperty]
    private bool _isDeleting;

    /// <summary>Empty means "wherever the platform puts things", which is shown alongside.</summary>
    [ObservableProperty]
    private string _installDirectory = string.Empty;

    /// <summary>
    /// Turns the setting above from an answer into a starting point: every install of a game
    /// that is not already here asks where it should go. Off by default — a folder dialog in
    /// front of every install is one people turn off after the second game.
    /// </summary>
    [ObservableProperty]
    private bool _askWhereToInstall;

    [ObservableProperty]
    private string _themeVariant = DefaultTheme;

    /// <summary>
    /// Opt-in, and off until somebody says otherwise. It appears here now because it finally
    /// does something: until the uploader existed, an inert checkbox would have been a promise
    /// the launcher did not keep.
    /// </summary>
    [ObservableProperty]
    private bool _sendCrashReports;

    private const string DefaultTheme = "dark";

    public SettingsViewModel(
        IUserSettingsStore store,
        IPathProvider paths,
        IInstallStore installs,
        IFolderPicker folders,
        IThemeSwitcher theme,
        IAccountService account,
        IAuthenticationService authentication,
        IApiErrorPresenter errors,
        ILocalizationService localization)
    {
        _store = store;
        _paths = paths;
        _installs = installs;
        _folders = folders;
        _theme = theme;
        _account = account;
        _authentication = authentication;
        _errors = errors;
        _localization = localization;
    }

    public IReadOnlyList<string> Themes { get; } = ["dark", "light", "system"];

    public bool HasPendingDeletion => PendingDeletion is not null;

    /// <summary>
    /// The password is what the server checks, so an empty box is a round trip that can only be
    /// refused. Advisory like every client-side check (D8): the server asks again regardless.
    /// </summary>
    public bool CanDeleteAccount => !IsDeleting && DeletePassword.Length > 0;

    /// <summary>Shown under the box, so an empty setting is not a mystery.</summary>
    public string DefaultInstallDirectory => _paths.DefaultInstallDirectory;

    /// <summary>
    /// The whole sentence, built here rather than by a <c>StringFormat</c> in the view. The
    /// view had one, and its format string was a <c>{loc:Tr}</c> — which is a binding, not a
    /// string (D3), so constructing the page threw and the ContentControl showed **nothing**:
    /// every setting on it was invisible. A sentence assembled in a view model is the shape the
    /// page already used next door for <see cref="InstalledElsewhereNotice"/>, and a test can
    /// read it.
    /// </summary>
    public string DefaultInstallDirectoryNotice =>
        _localization.Translate("Settings.InstallDirectoryDefault", DefaultInstallDirectory);

    /// <summary>
    /// What is already installed does not move when this changes, and saying so on the page is
    /// cheaper than a support question.
    /// </summary>
    public string InstalledElsewhereNotice { get; private set; } = string.Empty;

    public bool HasInstalledElsewhere => InstalledElsewhereNotice.Length > 0;

    /// <summary>
    /// The page where the line between an account and a machine is visible, so it is the page
    /// that draws it: the install directory, the theme, the language and the crash-report
    /// consent are this computer's and survive, because none of them changes when somebody
    /// else signs in on it. What goes is the account deletion — a typed password above all,
    /// which must never be sitting in a box for the next person, plus the confirmation it
    /// arms and whatever the last attempt said.
    /// </summary>
    public void ResetForAccountChange()
    {
        DeletePassword = string.Empty;
        DeleteReason = string.Empty;
        PendingDeletion = null;
        IsDeleting = false;
        StatusMessage = null;
        ErrorMessage = null;
        OnPropertyChanged(nameof(HasPendingDeletion));
        OnPropertyChanged(nameof(CanDeleteAccount));
    }

    public async Task LoadAsync(CancellationToken cancellationToken = default)
    {
        _settings = await _store.LoadAsync(cancellationToken).ConfigureAwait(true);

        InstallDirectory = _settings.InstallDirectory ?? string.Empty;
        AskWhereToInstall = _settings.AskWhereToInstall;
        ThemeVariant = _settings.ThemeVariant ?? DefaultTheme;
        SendCrashReports = _settings.SendCrashReports;
        StatusMessage = null;
        ErrorMessage = null;

        // Leaving a typed password and an armed deletion behind when the page is reopened would
        // be a confirmation somebody could walk into.
        CancelAccountDeletion();

        IReadOnlyList<InstalledGame> installed = await _installs
            .GetAllAsync(cancellationToken).ConfigureAwait(true);

        InstalledElsewhereNotice = installed.Count > 0
            ? _localization.Translate(
                "Settings.InstalledElsewhere",
                installed.Count.ToString(System.Globalization.CultureInfo.CurrentCulture))
            : string.Empty;

        OnPropertyChanged(nameof(DefaultInstallDirectory));
        OnPropertyChanged(nameof(DefaultInstallDirectoryNotice));
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
            AskWhereToInstall = AskWhereToInstall,
            ThemeVariant = ThemeVariant,
            SendCrashReports = SendCrashReports,
        };

        await _store.SaveAsync(_settings).ConfigureAwait(true);
        StatusMessage = _localization.Translate("Settings.Saved");
    }

    // --- erasing the account ---------------------------------------------------------------

    /// <summary>
    /// Arms the erasure. Nothing is sent until <see cref="ConfirmDeletionCommand"/> runs.
    ///
    /// The prompt says **what survives**, not only what goes, because that is the part nobody
    /// expects: the server anonymises the account rather than deleting it, so anything a
    /// publisher released stays online under a deleted name. Somebody who wanted their games
    /// gone has to delete them first, and being told that after the fact is being told too late.
    /// </summary>
    [RelayCommand]
    private void AskToDeleteAccount()
    {
        StatusMessage = null;
        ErrorMessage = null;

        // The publisher wording is chosen on the permission rather than on a count of games:
        // asking the server how many this account published would be a request made for a
        // sentence, and a publisher with none is told something harmlessly true anyway.
        string prompt = _authentication.HasPermission(Permissions.GamePublish)
            ? _localization.Translate("Settings.ConfirmDeleteAccountPublisher")
            : _localization.Translate("Settings.ConfirmDeleteAccount");

        PendingDeletion = new PendingDeletion(prompt, DeleteAccountAsync);
        RaiseDeletionDerived();
    }

    [RelayCommand]
    private async Task ConfirmDeletionAsync(CancellationToken cancellationToken)
    {
        if (PendingDeletion is not { } deletion)
        {
            return;
        }

        PendingDeletion = null;
        IsDeleting = true;
        RaiseDeletionDerived();

        try
        {
            await deletion.ConfirmAsync(cancellationToken).ConfigureAwait(true);
        }
        catch (ApiException exception)
        {
            // The password survives a refusal on purpose: a mistyped one is the likeliest
            // reason to be here, and clearing the box would mean typing it again to retry.
            ErrorMessage = _errors.Describe(exception);
        }
        finally
        {
            IsDeleting = false;
            RaiseDeletionDerived();
        }
    }

    [RelayCommand]
    private void CancelDeletion() => CancelAccountDeletion();

    /// <summary>
    /// The account itself. Nothing is shown afterwards: the sign-out inside this raises a
    /// session change, and the shell answers that by showing the sign-in screen — so a status
    /// message here would be written onto a page nobody is looking at any more.
    /// </summary>
    private async Task DeleteAccountAsync(CancellationToken cancellationToken)
    {
        await _account
            .DeleteAsync(DeletePassword, DeleteReason, cancellationToken)
            .ConfigureAwait(true);

        DeletePassword = string.Empty;
        DeleteReason = string.Empty;
    }

    private void CancelAccountDeletion()
    {
        PendingDeletion = null;
        DeletePassword = string.Empty;
        DeleteReason = string.Empty;
        RaiseDeletionDerived();
    }

    /// <summary>
    /// Saved as soon as it is toggled rather than on a button, like the theme: a consent
    /// checkbox that needs a second press to take effect is one somebody will believe they set.
    /// Turning it *off* matters more than turning it on, and it takes effect at once — the
    /// uploader deletes the pending reports on the next start rather than sending them.
    /// </summary>
    partial void OnSendCrashReportsChanged(bool value)
    {
        if (value != _settings.SendCrashReports)
        {
            _ = SaveAsync();
        }
    }

    /// <summary>
    /// Saved on the toggle, like the theme and the crash-report consent: a checkbox that needs
    /// a second press to take effect is one somebody will believe they set.
    /// </summary>
    partial void OnAskWhereToInstallChanged(bool value)
    {
        if (value != _settings.AskWhereToInstall)
        {
            _ = SaveAsync();
        }
    }

    partial void OnDeletePasswordChanged(string value) => RaiseDeletionDerived();

    private void RaiseDeletionDerived()
    {
        OnPropertyChanged(nameof(HasPendingDeletion));
        OnPropertyChanged(nameof(CanDeleteAccount));
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

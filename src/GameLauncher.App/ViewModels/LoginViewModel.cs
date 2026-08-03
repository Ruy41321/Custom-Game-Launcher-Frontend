using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using GameLauncher.Core.Api;
using GameLauncher.Core.Authentication;
using GameLauncher.Core.Localization;

namespace GameLauncher.App.ViewModels;

/// <summary>
/// Sign-in and registration. One view model for both because they share every field but the
/// display name, and a user who mistypes an address should not lose what they already typed
/// when they switch.
/// </summary>
public sealed partial class LoginViewModel : ViewModelBase
{
    private readonly IAuthenticationService _authentication;
    private readonly IApiErrorPresenter _errors;
    private readonly ILocalizationService _localization;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SubmitCommand))]
    private string _email = string.Empty;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SubmitCommand))]
    private string _password = string.Empty;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SubmitCommand))]
    private string _displayName = string.Empty;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SubmitCommand))]
    private bool _isRegistering;

    [ObservableProperty]
    private bool _isBusy;

    [ObservableProperty]
    private string? _errorMessage;

    [ObservableProperty]
    private string? _infoMessage;

    public LoginViewModel(
        IAuthenticationService authentication,
        IApiErrorPresenter errors,
        ILocalizationService localization)
    {
        _authentication = authentication;
        _errors = errors;
        _localization = localization;
    }

    /// <summary>Raised once a session exists, so the shell can leave this screen.</summary>
    public event EventHandler? SignedIn;

    /// <summary>
    /// Only shape is checked here, and only to keep the button from being pressable on an
    /// obviously empty form. The server owns the real rules and re-applies all of them.
    /// </summary>
    private bool CanSubmit =>
        !IsBusy
        && Email.Contains('@', StringComparison.Ordinal)
        && Password.Length > 0
        && (!IsRegistering || DisplayName.Trim().Length > 0);

    [RelayCommand]
    private void ToggleMode()
    {
        IsRegistering = !IsRegistering;
        ErrorMessage = null;
        InfoMessage = null;
    }

    [RelayCommand(CanExecute = nameof(CanSubmit))]
    private async Task SubmitAsync(CancellationToken cancellationToken)
    {
        IsBusy = true;
        ErrorMessage = null;
        InfoMessage = null;

        try
        {
            if (IsRegistering)
            {
                await RegisterAsync(cancellationToken).ConfigureAwait(true);
            }
            else
            {
                await SignInAsync(cancellationToken).ConfigureAwait(true);
            }
        }
        catch (ApiException exception)
        {
            // On this screen a 401 means the credentials were wrong, not that a session aged
            // out — there is no session yet.
            ErrorMessage = _errors.Describe(exception, "Auth.InvalidCredentials");
        }
        finally
        {
            IsBusy = false;
        }
    }

    private async Task SignInAsync(CancellationToken cancellationToken)
    {
        await _authentication
            .SignInAsync(Email.Trim(), Password, cancellationToken)
            .ConfigureAwait(true);

        Password = string.Empty;
        SignedIn?.Invoke(this, EventArgs.Empty);
    }

    private async Task RegisterAsync(CancellationToken cancellationToken)
    {
        RegistrationResult result = await _authentication
            .RegisterAsync(Email.Trim(), Password, DisplayName.Trim(), cancellationToken)
            .ConfigureAwait(true);

        if (result.EmailVerificationRequired)
        {
            // Signing in now would only earn a rejection, so the form goes back to sign-in
            // mode and says what has to happen first.
            IsRegistering = false;
            InfoMessage = _localization.Translate("Auth.VerifyEmailNotice", Email.Trim());
            return;
        }

        await SignInAsync(cancellationToken).ConfigureAwait(true);
    }

    partial void OnIsBusyChanged(bool value) => SubmitCommand.NotifyCanExecuteChanged();
}

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

    /// <summary>
    /// The address a reset link was successfully asked for, so the same press cannot be
    /// repeated by accident. Editing the field clears the guard, which is what a person who
    /// mistyped their address does next.
    /// </summary>
    private string? _resetRequestedFor;

    /// <summary>The address a verification link was successfully asked for. Same guard.</summary>
    private string? _verificationRequestedFor;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SubmitCommand))]
    [NotifyCanExecuteChangedFor(nameof(RequestPasswordResetCommand))]
    [NotifyCanExecuteChangedFor(nameof(ResendVerificationEmailCommand))]
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

    /// <summary>
    /// Whether the screen is in the state where asking for another verification link makes
    /// sense. Not always: on a plain sign-in form it is a button that invites somebody to
    /// spend a rate limit they have no use for.
    /// </summary>
    [ObservableProperty]
    private bool _canResendVerification;

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
        && HasAddress
        && Password.Length > 0
        && (!IsRegistering || DisplayName.Trim().Length > 0);

    private bool HasAddress => Email.Contains('@', StringComparison.Ordinal);

    /// <summary>
    /// Live until the address it was asked for is asked for again. A refusal deliberately
    /// does not arm the guard: after a 429 the right thing to do is wait and press again.
    /// </summary>
    private bool CanRequestPasswordReset =>
        !IsBusy
        && HasAddress
        && !string.Equals(Email.Trim(), _resetRequestedFor, StringComparison.OrdinalIgnoreCase);

    private bool CanSendVerificationLink =>
        !IsBusy
        && CanResendVerification
        && HasAddress
        && !string.Equals(
            Email.Trim(), _verificationRequestedFor, StringComparison.OrdinalIgnoreCase);

    [RelayCommand]
    private void ToggleMode()
    {
        IsRegistering = !IsRegistering;
        ErrorMessage = null;
        InfoMessage = null;
        CanResendVerification = false;
    }

    /// <summary>
    /// Asks for a reset link for whatever address is in the box. Nothing about the answer is
    /// presented as confirmation that the address exists — the server answers identically
    /// either way on purpose, and undoing that here would be undoing it from the wrong side.
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanRequestPasswordReset))]
    private async Task RequestPasswordResetAsync(CancellationToken cancellationToken)
    {
        string address = Email.Trim();
        IsBusy = true;
        ErrorMessage = null;
        InfoMessage = null;

        try
        {
            await _authentication
                .RequestPasswordResetAsync(address, cancellationToken)
                .ConfigureAwait(true);

            InfoMessage = _localization.Translate("Auth.PasswordResetRequested", address);
            _resetRequestedFor = address;
        }
        catch (ApiException exception)
        {
            ReportMailFailure(exception);
        }
        finally
        {
            IsBusy = false;
            RequestPasswordResetCommand.NotifyCanExecuteChanged();
        }
    }

    [RelayCommand(CanExecute = nameof(CanSendVerificationLink))]
    private async Task ResendVerificationEmailAsync(CancellationToken cancellationToken)
    {
        string address = Email.Trim();
        IsBusy = true;
        ErrorMessage = null;
        InfoMessage = null;

        try
        {
            await _authentication
                .ResendVerificationEmailAsync(address, cancellationToken)
                .ConfigureAwait(true);

            InfoMessage = _localization.Translate("Auth.VerificationLinkRequested", address);
            _verificationRequestedFor = address;
        }
        catch (ApiException exception)
        {
            ReportMailFailure(exception);
        }
        finally
        {
            IsBusy = false;
            ResendVerificationEmailCommand.NotifyCanExecuteChanged();
        }
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
            // A 403 from the sign-in route has exactly two causes: an address that has not
            // been confirmed, and a disabled account. The server answers both with the same
            // code, so this client cannot tell them apart either — it says the one a person
            // can act on and offers the resend. For a disabled account that button is
            // harmless: the route answers identically and sends nothing.
            if (!IsRegistering && exception.Code == ApiErrorCode.Forbidden)
            {
                ErrorMessage = _localization.Translate("Auth.ConfirmAddressFirst");
                OfferVerificationResend();
                return;
            }

            // On this screen a 401 means the credentials were wrong, not that a session aged
            // out — there is no session yet.
            ErrorMessage = _errors.Describe(exception, "Auth.InvalidCredentials");
        }
        finally
        {
            IsBusy = false;
        }
    }

    /// <summary>
    /// Puts the screen in the state where another verification link can be asked for, and
    /// clears the "already asked" guard: reaching this state again is new evidence that the
    /// previous message never arrived.
    /// </summary>
    private void OfferVerificationResend()
    {
        CanResendVerification = true;
        _verificationRequestedFor = null;
        ResendVerificationEmailCommand.NotifyCanExecuteChanged();
    }

    private void ReportMailFailure(ApiException exception)
    {
        switch (exception.Code)
        {
            case ApiErrorCode.RateLimited:
                // Not an error on this surface. Three messages in fifteen minutes is tight on
                // purpose, and the person most likely to reach the limit is the one whose
                // message genuinely never arrived — telling them "too many attempts" in red
                // reads as a refusal of something they did wrong. It goes on the info line,
                // and the button stays live, because waiting and pressing again is the answer.
                InfoMessage = ThrottleMessage(exception.RetryAfter);
                break;

            case ApiErrorCode.NotFound:
                // Both routes answer 404 where the deployment is configured to send no mail
                // at all. "That is not available" is true and says nothing; this names the
                // thing that is missing, because no amount of retrying will fix it.
                ErrorMessage = _localization.Translate("Auth.MailUnavailable");
                break;

            default:
                ErrorMessage = _errors.Describe(exception);
                break;
        }
    }

    /// <summary>
    /// The wait is named only when it is worth naming. A minute rounded up reads as "1
    /// minutes" in a resx that cannot decline a number in three languages, and a wrong plural
    /// on the one sentence somebody reads while already annoyed is not worth the precision.
    /// </summary>
    private string ThrottleMessage(TimeSpan? retryAfter)
    {
        int minutes = retryAfter is { } wait ? (int)Math.Ceiling(wait.TotalMinutes) : 0;

        return minutes >= 2
            ? _localization.Translate("Auth.MailThrottled", minutes)
            : _localization.Translate("Auth.MailThrottledSoon");
    }

    private async Task SignInAsync(CancellationToken cancellationToken)
    {
        await _authentication
            .SignInAsync(Email.Trim(), Password, cancellationToken)
            .ConfigureAwait(true);

        Password = string.Empty;
        CanResendVerification = false;
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
            // mode and says what has to happen first. Which sentence depends on whether the
            // message actually left: the account exists either way, and telling somebody to
            // watch an inbox nothing was sent to is a wait with no end.
            IsRegistering = false;
            InfoMessage = result.VerificationEmailSent
                ? _localization.Translate("Auth.VerifyEmailNotice", Email.Trim())
                : _localization.Translate("Auth.VerifyEmailNotSent", Email.Trim());

            // Offered in both cases, not only when the relay failed. A message that left and
            // never arrived — greylisted, filtered, deleted with the junk — leaves somebody in
            // exactly the same place as one that never left, and they have done nothing wrong.
            OfferVerificationResend();
            return;
        }

        await SignInAsync(cancellationToken).ConfigureAwait(true);
    }

    partial void OnIsBusyChanged(bool value)
    {
        SubmitCommand.NotifyCanExecuteChanged();
        RequestPasswordResetCommand.NotifyCanExecuteChanged();
        ResendVerificationEmailCommand.NotifyCanExecuteChanged();
    }

    partial void OnCanResendVerificationChanged(bool value) =>
        ResendVerificationEmailCommand.NotifyCanExecuteChanged();
}

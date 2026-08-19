using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using GameLauncher.Core.Api;
using GameLauncher.Core.Authentication;
using GameLauncher.Core.Installs;
using GameLauncher.Core.Localization;
using GameLauncher.Core.Models;

namespace GameLauncher.App.ViewModels;

/// <summary>
/// Sign-in and registration. One view model for both because they share every field but the
/// display name, and a user who mistypes an address should not lose what they already typed
/// when they switch.
/// </summary>
public sealed partial class LoginViewModel : ViewModelBase, IAccountScopedPage
{
    private readonly IAuthenticationService _authentication;
    private readonly IServerCapabilityProvider _capabilities;
    private readonly IServerReachability _reachability;
    private readonly IInstallStore _installs;
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

    /// <summary>
    /// Whether the password is shown as typed. It survives a switch between signing in and
    /// registering for the same reason the typed password does: somebody who turned the
    /// masking off is checking what they wrote, and turning it back on under them would
    /// undo exactly the thing they asked for.
    /// </summary>
    [ObservableProperty]
    private bool _showPassword;

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
    /// Whether this deployment can send a message at all, read from <c>/capabilities</c> (D40).
    /// True until the answer arrives, which is also what a server too old to say reads as —
    /// the offer is the recoverable guess, and hiding the way back into an account is not.
    /// </summary>
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(RequestPasswordResetCommand))]
    [NotifyCanExecuteChangedFor(nameof(ResendVerificationEmailCommand))]
    private bool _mailAvailable = true;

    /// <summary>
    /// Whether the server is missing rather than refusing. Signing in offline cannot work —
    /// only a server knows whether a password is right — so the screen says so plainly instead
    /// of leaving somebody to discover it by typing a password and waiting for a timeout. It
    /// is a notice with a button beside it, not a disabled form: the network can come back
    /// between reading the sentence and pressing Sign in.
    /// </summary>
    [ObservableProperty]
    private bool _isServerUnreachable;

    /// <summary>
    /// Whether there is anywhere to go without signing in: the server is missing **and** this
    /// machine has a game on it. Both halves matter — a way into an empty library is an offer
    /// to look at nothing, and the offer has no business being on screen at all while the
    /// server is answering, because then signing in works and is what somebody wants.
    /// </summary>
    [ObservableProperty]
    private bool _canContinueOffline;

    /// <summary>
    /// Whether the screen is in the state where asking for another verification link makes
    /// sense. Not always: on a plain sign-in form it is a button that invites somebody to
    /// spend a rate limit they have no use for.
    /// </summary>
    [ObservableProperty]
    private bool _canResendVerification;

    public LoginViewModel(
        IAuthenticationService authentication,
        IServerCapabilityProvider capabilities,
        IServerReachability reachability,
        IInstallStore installs,
        IApiErrorPresenter errors,
        ILocalizationService localization)
    {
        _authentication = authentication;
        _capabilities = capabilities;
        _reachability = reachability;
        _installs = installs;
        _errors = errors;
        _localization = localization;

        // Said as soon as it is known, and unsaid as soon as it stops being true — a sign-in
        // that fails on an unreachable server updates this screen through the same event, from
        // whatever thread the request finished on (D73).
        _reachability.Changed += (_, args) => OnUiThread(() =>
        {
            IsServerUnreachable = !args.IsOnline;

            // The offer goes with it. A server that has just answered is one to sign in to.
            CanContinueOffline = IsServerUnreachable && _hasInstalledGames;
        });
    }

    /// <summary>
    /// Whether this disk holds a game at all, read once per visit to this screen. Kept rather
    /// than re-read on every reachability change: what is installed does not change while
    /// somebody is looking at a sign-in form, and the alternative is a database read on an
    /// event raised from a request thread.
    /// </summary>
    private bool _hasInstalledGames;

    /// <summary>
    /// Asks the server whether it sends mail, and never fails over it. The provider answers
    /// from its cache after the first call and falls back rather than throwing (D39), so this
    /// costs one request per launcher run at most and cannot keep somebody off the sign-in
    /// screen — a launcher that would not let you type a password because it could not read a
    /// capabilities document would be the worst possible outcome of hiding one button.
    /// </summary>
    public async Task LoadAsync(CancellationToken cancellationToken = default)
    {
        ServerCapabilities capabilities = await _capabilities
            .GetAsync(cancellationToken)
            .ConfigureAwait(true);

        MailAvailable = capabilities.Mail.Enabled;

        // The same request answers both questions: one that came back proves the server is
        // there, and one that did not is exactly what the notice is for.
        IsServerUnreachable = !_reachability.IsOnline;

        // Only worth asking the disk when the answer could change something on screen.
        _hasInstalledGames = IsServerUnreachable && await HasSomethingToPlayAsync(cancellationToken)
            .ConfigureAwait(true);

        CanContinueOffline = IsServerUnreachable && _hasInstalledGames;
    }

    /// <summary>
    /// Never a reason to fail the sign-in screen. A database this launcher cannot read is one
    /// fewer offer on a page whose main purpose still works.
    /// </summary>
    private async Task<bool> HasSomethingToPlayAsync(CancellationToken cancellationToken)
    {
        try
        {
            IReadOnlyList<InstalledGame> installed = await _installs
                .GetAllAsync(cancellationToken)
                .ConfigureAwait(true);

            return installed.Count > 0;
        }
        catch (Exception exception) when (exception is IOException or InvalidOperationException)
        {
            return false;
        }
    }

    /// <summary>
    /// Into the launcher with no session at all. What is on this disk was paid for and
    /// downloaded already, and a server that cannot be reached is no reason to hold it: the
    /// library shows what is installed and plays it, and says in its own banner that this is
    /// not the whole library.
    /// </summary>
    [RelayCommand]
    private void ContinueOffline() => ContinueOfflineRequested?.Invoke(this, EventArgs.Empty);

    /// <summary>
    /// Asks the server whether it is back. Nothing else: it does not sign anybody in, because
    /// the password is the user's to submit, and it reuses <see cref="LoadAsync"/> because the
    /// capabilities request is the cheapest question this screen already knows how to ask.
    /// </summary>
    [RelayCommand]
    private async Task RetryAsync(CancellationToken cancellationToken)
    {
        _reachability.RetryNow();
        await LoadAsync(cancellationToken).ConfigureAwait(true);
    }

    /// <summary>Raised once a session exists, so the shell can leave this screen.</summary>
    public event EventHandler? SignedIn;

    /// <summary>
    /// Raised when somebody asks to use the launcher without signing in. The shell does the
    /// navigating, as it does for every other page (D17): this screen only says what happened.
    /// </summary>
    public event EventHandler? ContinueOfflineRequested;

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
        && MailAvailable
        && HasAddress
        && !string.Equals(Email.Trim(), _resetRequestedFor, StringComparison.OrdinalIgnoreCase);

    private bool CanSendVerificationLink =>
        !IsBusy
        && MailAvailable
        && CanResendVerification
        && HasAddress
        && !string.Equals(
            Email.Trim(), _verificationRequestedFor, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// The screen somebody arrives at, which makes it the one screen where "the previous
    /// account's state" is a password still sitting in a box. It is emptied on the way *in*
    /// as well as on the way out — a successful sign-in changes the account too, and a
    /// launcher that keeps credentials in a form nobody is looking at keeps them for no
    /// reason. The "already asked for a link" guards go with them: the next person's address
    /// has never been asked for anything.
    /// </summary>
    public void ResetForAccountChange()
    {
        _resetRequestedFor = null;
        _verificationRequestedFor = null;

        Email = string.Empty;
        Password = string.Empty;
        DisplayName = string.Empty;
        ShowPassword = false;
        IsRegistering = false;
        IsBusy = false;
        ErrorMessage = null;
        InfoMessage = null;
        CanResendVerification = false;

        // Not a preference and not the previous account's: it is what this machine knows about
        // the server right now, and the next load asks again.
        IsServerUnreachable = !_reachability.IsOnline;
        CanContinueOffline = false;
    }

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
        // Not where nothing can be sent: the button would be an invitation to press something
        // whose only outcome is the 404 below. The sentence on the page already says so.
        CanResendVerification = MailAvailable;
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

using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using GameLauncher.Core.Api;
using GameLauncher.Core.Authentication;
using GameLauncher.Core.Localization;

namespace GameLauncher.App.ViewModels;

/// <summary>
/// The screen somebody lands on when their account is holding a password an operator chose for
/// it, and the screen anybody can use to change their password.
///
/// It is one page for both because the flow is identical — the current password, a new one,
/// and the confirmation that the new one was typed the way it was meant — and the only thing
/// the forced case adds is that nothing else is reachable until it is done.
/// <see cref="IsForced"/> is what the shell sets, and all it changes is the sentence at the top
/// and whether there is a way out.
///
/// The password rules are **not** copied here. The server owns them, its refusal names the rule
/// that failed and carries the limit (D64), and `ApiErrorPresenter` turns that into a sentence
/// with the number in it — so a deployment that lowers its minimum needs no client release. What
/// is checked locally is only what would make the request meaningless: an empty box, and two
/// new passwords that do not match, which is the one mistake the server cannot see because only
/// one of the two is sent.
/// </summary>
public sealed partial class ChangePasswordViewModel : ViewModelBase, IAccountScopedPage
{
    private readonly IAccountService _account;
    private readonly IApiErrorPresenter _errors;
    private readonly ILocalizationService _localization;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SubmitCommand))]
    private string _currentPassword = string.Empty;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SubmitCommand))]
    private string _newPassword = string.Empty;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SubmitCommand))]
    private string _confirmPassword = string.Empty;

    /// <summary>
    /// Shown as typed, on request. The same reasoning as the sign-in form: somebody typing a
    /// password they cannot see has no other way to check it — and on this page they are
    /// typing three of them, one of which was read out to them over the phone.
    /// </summary>
    [ObservableProperty]
    private bool _showPasswords;

    [ObservableProperty]
    private bool _isBusy;

    [ObservableProperty]
    private string? _errorMessage;

    /// <summary>
    /// Whether the account may not do anything else until this is finished. Set by the shell
    /// from the session, never guessed here.
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanCancel))]
    private bool _isForced;

    public ChangePasswordViewModel(
        IAccountService account,
        IApiErrorPresenter errors,
        ILocalizationService localization)
    {
        _account = account;
        _errors = errors;
        _localization = localization;
    }

    /// <summary>Raised when the password has been replaced and a live session is in hand.</summary>
    public event EventHandler? Changed;

    /// <summary>Raised when somebody who was not forced here asks to leave.</summary>
    public event EventHandler? Cancelled;

    /// <summary>
    /// There is no way out of the forced case, and that is the feature: every other route
    /// answers 403 until the password is replaced, so a "later" button would lead to a launcher
    /// where nothing works and nothing says why.
    /// </summary>
    public bool CanCancel => !IsForced;

    private bool CanSubmit =>
        !IsBusy
        && CurrentPassword.Length > 0
        && NewPassword.Length > 0
        && ConfirmPassword.Length > 0;

    /// <summary>
    /// A password is state of the account, so it goes with the account — and this page holds
    /// three of them in plain text on the way to being sent. It is also the page a forced
    /// sign-in lands on, so leaving <see cref="IsForced"/> set would put the next person into
    /// a screen they cannot leave.
    /// </summary>
    public void ResetForAccountChange()
    {
        CurrentPassword = string.Empty;
        NewPassword = string.Empty;
        ConfirmPassword = string.Empty;
        ShowPasswords = false;
        IsBusy = false;
        IsForced = false;
        ErrorMessage = null;
    }

    [RelayCommand(CanExecute = nameof(CanCancel))]
    private void Cancel()
    {
        ResetForAccountChange();
        Cancelled?.Invoke(this, EventArgs.Empty);
    }

    [RelayCommand(CanExecute = nameof(CanSubmit))]
    private async Task SubmitAsync(CancellationToken cancellationToken)
    {
        // The one rule that is this client's to enforce: only one of the two new passwords is
        // sent, so a server can never notice that they differ.
        if (!string.Equals(NewPassword, ConfirmPassword, StringComparison.Ordinal))
        {
            ErrorMessage = _localization.Translate("Account.PasswordsDoNotMatch");
            return;
        }

        IsBusy = true;
        ErrorMessage = null;

        try
        {
            await _account
                .ChangePasswordAsync(CurrentPassword, NewPassword, cancellationToken)
                .ConfigureAwait(true);

            // Emptied before anything else sees the page: what is in these boxes is a live
            // credential and a dead one, and neither has any business outliving the request.
            CurrentPassword = string.Empty;
            NewPassword = string.Empty;
            ConfirmPassword = string.Empty;
            ShowPasswords = false;
            IsForced = false;

            Changed?.Invoke(this, EventArgs.Empty);
        }
        catch (ApiException exception)
        {
            // On this page a 401 means the current password was wrong, not that the session
            // aged out: the request that would have aged it out is the one being made.
            ErrorMessage = _errors.Describe(exception, "Account.CurrentPasswordWrong");
        }
        finally
        {
            IsBusy = false;
        }
    }
}

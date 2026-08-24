using GameLauncher.App.ViewModels;
using GameLauncher.Core.Api;
using GameLauncher.Core.Authentication;
using GameLauncher.Core.Localization;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using NSubstitute.ExceptionExtensions;

namespace GameLauncher.App.Tests.ViewModels;

public sealed class ChangePasswordViewModelTests
{
    private readonly IAccountService _account = Substitute.For<IAccountService>();

    private readonly ResourceManagerLocalizationService _localization = new("en");

    private ChangePasswordViewModel CreateViewModel() =>
        new(
            _account,
            new ApiErrorPresenter(_localization, NullLogger<ApiErrorPresenter>.Instance),
            _localization);

    private static ChangePasswordViewModel Filled(
        ChangePasswordViewModel model, string confirm = "a brand new passphrase")
    {
        model.CurrentPassword = "the temporary one";
        model.NewPassword = "a brand new passphrase";
        model.ConfirmPassword = confirm;
        return model;
    }

    [Fact]
    public void SubmittingAnEmptyFormIsNotOffered()
    {
        ChangePasswordViewModel model = CreateViewModel();

        Assert.False(model.SubmitCommand.CanExecute(null));
    }

    [Fact]
    public async Task ASuccessfulChangeSendsBothPasswordsAndAnnouncesItself()
    {
        ChangePasswordViewModel model = Filled(CreateViewModel());
        bool announced = false;
        model.Changed += (_, _) => announced = true;

        await model.SubmitCommand.ExecuteAsync(null);

        await _account.Received(1).ChangePasswordAsync(
            "the temporary one", "a brand new passphrase", Arg.Any<CancellationToken>());
        Assert.True(announced);
        Assert.Null(model.ErrorMessage);
    }

    // Three passwords in plain text on a page the shell keeps for the life of the window.
    [Fact]
    public async Task TheBoxesAreEmptiedOnceTheChangeLands()
    {
        ChangePasswordViewModel model = Filled(CreateViewModel());
        model.ShowPasswords = true;

        await model.SubmitCommand.ExecuteAsync(null);

        Assert.Equal(string.Empty, model.CurrentPassword);
        Assert.Equal(string.Empty, model.NewPassword);
        Assert.Equal(string.Empty, model.ConfirmPassword);
        Assert.False(model.ShowPasswords);
    }

    // The one rule this client owns: only one of the two new passwords is ever sent, so no
    // server can notice that they differ.
    [Fact]
    public async Task TwoDifferentNewPasswordsAreRefusedWithoutASingleRequest()
    {
        ChangePasswordViewModel model = Filled(CreateViewModel(), confirm: "a typo");

        await model.SubmitCommand.ExecuteAsync(null);

        Assert.Equal("The two new passwords are not the same.", model.ErrorMessage);
        await _account.DidNotReceive().ChangePasswordAsync(
            Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    // On this page a 401 is the current password being wrong, not a session that aged out:
    // the request that would have aged it out is the one being made.
    [Fact]
    public async Task AWrongCurrentPasswordSaysSoRatherThanTalkingAboutTheSession()
    {
        _account.ChangePasswordAsync(
                Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .ThrowsAsync(new ApiException(ApiErrorCode.Unauthenticated, "no"));

        ChangePasswordViewModel model = Filled(CreateViewModel());

        await model.SubmitCommand.ExecuteAsync(null);

        Assert.Equal("That is not your current password.", model.ErrorMessage);
    }

    // The server names the rule and the client translates it (D64). Re-entering the operator's
    // one-time password is the refusal this page exists to produce.
    [Fact]
    public async Task KeepingTheTemporaryPasswordShowsTheServersOwnRule()
    {
        _account.ChangePasswordAsync(
                Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .ThrowsAsync(new ApiException(
                ApiErrorCode.InvalidInput,
                "the new password has to be different from the current one",
                statusCode: 422)
            {
                Rule = "password_unchanged",
            });

        ChangePasswordViewModel model = Filled(CreateViewModel());

        await model.SubmitCommand.ExecuteAsync(null);

        Assert.Equal(
            "The new password has to be different from the current one.", model.ErrorMessage);
    }

    // There is no way out of the forced case, and that is the feature: every other route
    // answers 403 until this is done.
    [Fact]
    public void AForcedChangeCannotBeCancelled()
    {
        ChangePasswordViewModel model = CreateViewModel();

        model.IsForced = true;
        Assert.False(model.CanCancel);
        Assert.False(model.CancelCommand.CanExecute(null));

        model.IsForced = false;
        Assert.True(model.CanCancel);
        Assert.True(model.CancelCommand.CanExecute(null));
    }

    [Fact]
    public void CancellingEmptiesTheFormAndSaysSo()
    {
        ChangePasswordViewModel model = Filled(CreateViewModel());
        bool cancelled = false;
        model.Cancelled += (_, _) => cancelled = true;

        model.CancelCommand.Execute(null);

        Assert.True(cancelled);
        Assert.Equal(string.Empty, model.CurrentPassword);
    }

    // D70. The page holds three passwords and, in the forced case, a state the next person
    // must not inherit — a screen they cannot leave.
    [Fact]
    public void AChangeOfAccountEmptiesEverythingIncludingTheForcedState()
    {
        ChangePasswordViewModel model = Filled(CreateViewModel());
        model.IsForced = true;
        model.ErrorMessage = "something";

        model.ResetForAccountChange();

        Assert.Equal(string.Empty, model.CurrentPassword);
        Assert.Equal(string.Empty, model.NewPassword);
        Assert.Equal(string.Empty, model.ConfirmPassword);
        Assert.False(model.IsForced);
        Assert.Null(model.ErrorMessage);
    }
}

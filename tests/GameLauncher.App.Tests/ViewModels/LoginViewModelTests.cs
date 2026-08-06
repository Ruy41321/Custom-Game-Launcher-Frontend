using GameLauncher.App.ViewModels;
using GameLauncher.Core.Api;
using GameLauncher.Core.Authentication;
using GameLauncher.Core.Localization;
using NSubstitute;
using NSubstitute.ExceptionExtensions;

namespace GameLauncher.App.Tests.ViewModels;

public sealed class LoginViewModelTests
{
    private readonly IAuthenticationService _authentication =
        Substitute.For<IAuthenticationService>();

    private readonly ResourceManagerLocalizationService _localization =
        new("en");

    private LoginViewModel CreateViewModel() =>
        new(_authentication, new ApiErrorPresenter(_localization), _localization);

    private static LoginViewModel WithCredentials(LoginViewModel model)
    {
        model.Email = "luigi@example.com";
        model.Password = "correct horse";
        return model;
    }

    // The button is a courtesy, not a validator: it only refuses a form that obviously has
    // nothing to send. Every real rule belongs to the server.
    [Fact]
    public void SubmittingAnEmptyFormIsNotOffered()
    {
        LoginViewModel model = CreateViewModel();

        Assert.False(model.SubmitCommand.CanExecute(null));
    }

    [Fact]
    public void AnAddressWithoutAnAtSignIsNotWorthSending()
    {
        LoginViewModel model = CreateViewModel();
        model.Email = "luigi";
        model.Password = "correct horse";

        Assert.False(model.SubmitCommand.CanExecute(null));
    }

    [Fact]
    public void RegisteringAlsoNeedsADisplayName()
    {
        LoginViewModel model = WithCredentials(CreateViewModel());
        Assert.True(model.SubmitCommand.CanExecute(null));

        model.IsRegistering = true;

        Assert.False(model.SubmitCommand.CanExecute(null));

        model.DisplayName = "Luigi";

        Assert.True(model.SubmitCommand.CanExecute(null));
    }

    [Fact]
    public async Task SigningInAnnouncesTheNewSession()
    {
        LoginViewModel model = WithCredentials(CreateViewModel());
        bool announced = false;
        model.SignedIn += (_, _) => announced = true;

        await model.SubmitCommand.ExecuteAsync(null);

        await _authentication.Received(1).SignInAsync(
            "luigi@example.com", "correct horse", Arg.Any<CancellationToken>());
        Assert.True(announced);
    }

    // Nothing should be able to read the password back off the view model after it was used.
    [Fact]
    public async Task ThePasswordIsClearedOnceItHasBeenUsed()
    {
        LoginViewModel model = WithCredentials(CreateViewModel());

        await model.SubmitCommand.ExecuteAsync(null);

        Assert.Empty(model.Password);
    }

    // On this screen a 401 means the password was wrong; there is no session to have expired.
    [Fact]
    public async Task WrongCredentialsSayExactlyThat()
    {
        _authentication.SignInAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Throws(new ApiException(ApiErrorCode.Unauthenticated, "Invalid credentials."));
        LoginViewModel model = WithCredentials(CreateViewModel());

        await model.SubmitCommand.ExecuteAsync(null);

        Assert.Equal(_localization.Translate("Auth.InvalidCredentials"), model.ErrorMessage);
    }

    [Fact]
    public async Task AnUnreachableServerIsReportedAsSuch()
    {
        _authentication.SignInAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Throws(new ApiException(ApiErrorCode.Network, "offline"));
        LoginViewModel model = WithCredentials(CreateViewModel());

        await model.SubmitCommand.ExecuteAsync(null);

        Assert.Equal(_localization.Translate("Error.Network"), model.ErrorMessage);
    }

    // The request id is what makes a support report actionable, so it survives into the text.
    [Fact]
    public async Task TheRequestIdIsShownWhenTheServerSentOne()
    {
        _authentication.SignInAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Throws(new ApiException(
                ApiErrorCode.Internal, "boom", statusCode: 500, requestId: "01HZY"));
        LoginViewModel model = WithCredentials(CreateViewModel());

        await model.SubmitCommand.ExecuteAsync(null);

        Assert.Contains("01HZY", model.ErrorMessage!, StringComparison.Ordinal);
    }

    // Signing in immediately would only earn a rejection, so the form says what comes first.
    [Fact]
    public async Task RegisteringWithVerificationRequiredGoesBackToSignIn()
    {
        _authentication.RegisterAsync(
                Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(new RegistrationResult
            {
                EmailVerificationRequired = true,
                VerificationEmailSent = true,
            });

        LoginViewModel model = WithCredentials(CreateViewModel());
        model.IsRegistering = true;
        model.DisplayName = "Luigi";

        await model.SubmitCommand.ExecuteAsync(null);

        Assert.False(model.IsRegistering);
        Assert.Contains("luigi@example.com", model.InfoMessage!, StringComparison.Ordinal);
        await _authentication.DidNotReceive().SignInAsync(
            Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    // The account exists either way, so the difference is what the person is told to do next:
    // watching an inbox nothing was sent to is a wait with no end.
    [Fact]
    public async Task RegisteringWhenTheMessageDidNotGoOutSaysSoInsteadOfSayingToCheckTheInbox()
    {
        _authentication.RegisterAsync(
                Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(new RegistrationResult
            {
                EmailVerificationRequired = true,
                VerificationEmailSent = false,
            });

        LoginViewModel model = WithCredentials(CreateViewModel());
        model.IsRegistering = true;
        model.DisplayName = "Luigi";

        await model.SubmitCommand.ExecuteAsync(null);

        Assert.False(model.IsRegistering);
        Assert.Contains("luigi@example.com", model.InfoMessage!, StringComparison.Ordinal);
        Assert.NotEqual(
            _localization.Translate("Auth.VerifyEmailNotice", "luigi@example.com"),
            model.InfoMessage);
        Assert.Equal(
            _localization.Translate("Auth.VerifyEmailNotSent", "luigi@example.com"),
            model.InfoMessage);
        Assert.Null(model.ErrorMessage);
    }

    [Fact]
    public async Task RegisteringOnAServerThatDoesNotVerifySignsStraightIn()
    {
        _authentication.RegisterAsync(
                Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(new RegistrationResult { EmailVerificationRequired = false });

        LoginViewModel model = WithCredentials(CreateViewModel());
        model.IsRegistering = true;
        model.DisplayName = "Luigi";

        await model.SubmitCommand.ExecuteAsync(null);

        await _authentication.Received(1).SignInAsync(
            "luigi@example.com", "correct horse", Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task AnAlreadyRegisteredAddressIsReportedAsAConflict()
    {
        _authentication.RegisterAsync(
                Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Throws(new ApiException(ApiErrorCode.Conflict, "already registered"));

        LoginViewModel model = WithCredentials(CreateViewModel());
        model.IsRegistering = true;
        model.DisplayName = "Luigi";

        await model.SubmitCommand.ExecuteAsync(null);

        Assert.Equal(_localization.Translate("Error.Conflict"), model.ErrorMessage);
    }

    // Switching between the two forms keeps what was typed; only the stale message goes.
    [Fact]
    public void SwitchingModeClearsTheMessagesButNotTheFields()
    {
        LoginViewModel model = WithCredentials(CreateViewModel());
        model.ErrorMessage = "stale";

        model.ToggleModeCommand.Execute(null);

        Assert.True(model.IsRegistering);
        Assert.Null(model.ErrorMessage);
        Assert.Equal("luigi@example.com", model.Email);
    }

    [Fact]
    public async Task TheAddressIsTrimmedBeforeItIsSent()
    {
        LoginViewModel model = CreateViewModel();
        model.Email = "  luigi@example.com  ";
        model.Password = "correct horse";

        await model.SubmitCommand.ExecuteAsync(null);

        await _authentication.Received(1).SignInAsync(
            "luigi@example.com", Arg.Any<string>(), Arg.Any<CancellationToken>());
    }
}

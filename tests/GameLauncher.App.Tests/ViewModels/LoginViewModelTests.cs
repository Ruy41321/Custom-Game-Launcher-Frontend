using GameLauncher.App.ViewModels;
using GameLauncher.Core.Api;
using GameLauncher.Core.Authentication;
using GameLauncher.Core.Installs;
using GameLauncher.Core.Localization;
using GameLauncher.Core.Models;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using NSubstitute.ExceptionExtensions;

namespace GameLauncher.App.Tests.ViewModels;

public sealed class LoginViewModelTests
{
    private readonly IAuthenticationService _authentication =
        Substitute.For<IAuthenticationService>();

    private readonly IServerCapabilityProvider _capabilities =
        Substitute.For<IServerCapabilityProvider>();

    private readonly ServerReachability _reachability = new(TimeProvider.System);

    private readonly IInstallStore _installs = Substitute.For<IInstallStore>();

    private readonly ResourceManagerLocalizationService _localization =
        new("en");

    public LoginViewModelTests() =>
        // Arranged here rather than in the factory: an unconfigured Task<T> member yields null,
        // and the sign-in screen now reads this on every load (§7).
        _capabilities.GetAsync(Arg.Any<CancellationToken>())
            .Returns(ServerCapabilities.Fallback);

    private LoginViewModel CreateViewModel() =>
        new(
            _authentication,
            _capabilities,
            _reachability,
            _installs,
            new ApiErrorPresenter(_localization, NullLogger<ApiErrorPresenter>.Instance),
            _localization);

    private static LoginViewModel WithCredentials(LoginViewModel model)
    {
        model.Email = "luigi@example.com";
        model.Password = "correct horse";
        return model;
    }

    // Signing in offline cannot work — only a server knows whether a password is right — so
    // the screen says so before a password is typed rather than after one has been waited on.
    [Fact]
    public async Task AMissingServerIsSaidOnTheScreenRatherThanDiscoveredByTyping()
    {
        _reachability.ReportUnreachable();

        LoginViewModel model = CreateViewModel();
        await model.LoadAsync(TestContext.Current.CancellationToken);

        Assert.True(model.IsServerUnreachable);
    }

    [Fact]
    public async Task TheNoticeGoesAwayWhenTheServerAnswersAgain()
    {
        _reachability.ReportUnreachable();
        LoginViewModel model = CreateViewModel();
        await model.LoadAsync(TestContext.Current.CancellationToken);
        Assert.True(model.IsServerUnreachable);

        _reachability.ReportReachable();

        Assert.False(model.IsServerUnreachable);
    }

    // A door is only worth showing where it leads somewhere. Signing in needs a server;
    // playing what is already on this disk does not.
    [Fact]
    public async Task WithNoServerAndAGameOnThisDiskThereIsAWayIn()
    {
        _installs.GetAllAsync(Arg.Any<CancellationToken>()).Returns([InstalledGame()]);
        _reachability.ReportUnreachable();

        LoginViewModel model = CreateViewModel();
        await model.LoadAsync(TestContext.Current.CancellationToken);

        Assert.True(model.CanContinueOffline);
    }

    [Fact]
    public async Task WithNothingInstalledTheOfferWouldLeadToAnEmptyPage()
    {
        _installs.GetAllAsync(Arg.Any<CancellationToken>()).Returns([]);
        _reachability.ReportUnreachable();

        LoginViewModel model = CreateViewModel();
        await model.LoadAsync(TestContext.Current.CancellationToken);

        Assert.True(model.IsServerUnreachable);
        Assert.False(model.CanContinueOffline);
    }

    // While the server is answering, the way in is to sign in.
    [Fact]
    public async Task AnAnsweringServerIsNoPlaceForTheOffer()
    {
        _installs.GetAllAsync(Arg.Any<CancellationToken>()).Returns([InstalledGame()]);

        LoginViewModel model = CreateViewModel();
        await model.LoadAsync(TestContext.Current.CancellationToken);

        Assert.False(model.CanContinueOffline);
    }

    [Fact]
    public async Task TheOfferGoesAwayWhenTheServerAnswersAgain()
    {
        _installs.GetAllAsync(Arg.Any<CancellationToken>()).Returns([InstalledGame()]);
        _reachability.ReportUnreachable();
        LoginViewModel model = CreateViewModel();
        await model.LoadAsync(TestContext.Current.CancellationToken);
        Assert.True(model.CanContinueOffline);

        _reachability.ReportReachable();

        Assert.False(model.CanContinueOffline);
    }

    // The page says what happened; the shell decides where that leads (D17).
    [Fact]
    public void ContinuingOfflineIsAnnouncedRatherThanNavigated()
    {
        LoginViewModel model = CreateViewModel();
        bool asked = false;
        model.ContinueOfflineRequested += (_, _) => asked = true;

        model.ContinueOfflineCommand.Execute(null);

        Assert.True(asked);
    }

    // A database this launcher cannot read is one fewer offer, not a sign-in screen that fails.
    [Fact]
    public async Task AnUnreadableInstallStoreOnlyCostsTheOffer()
    {
        _installs.GetAllAsync(Arg.Any<CancellationToken>())
            .Throws(new IOException("the database is locked"));
        _reachability.ReportUnreachable();

        LoginViewModel model = CreateViewModel();
        await model.LoadAsync(TestContext.Current.CancellationToken);

        Assert.True(model.IsServerUnreachable);
        Assert.False(model.CanContinueOffline);
    }

    private static InstalledGame InstalledGame() => new()
    {
        GameId = "g1",
        GameSlug = "makhia",
        GameTitle = "Makhia",
        BuildId = "b1",
        VersionSemver = "0.1",
        InstallDirectory = "/games/makhia",
        Entrypoint = "Game.exe",
        State = InstallState.Installed,
    };

    // A deliberate press is always allowed to find out for itself.
    [Fact]
    public async Task RetryingAsksTheServerAgainAtOnce()
    {
        _reachability.ReportUnreachable();
        LoginViewModel model = CreateViewModel();

        await model.RetryCommand.ExecuteAsync(null);

        Assert.True(_reachability.AllowsRequests);
        await _capabilities.Received().GetAsync(Arg.Any<CancellationToken>());
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

    // The sign-in screen is where the maintainer met the reference, so it is asserted here as
    // well as in the presenter's own tests: it goes to the log and never onto the screen (D67).
    [Fact]
    public async Task TheRequestIdIsNeverShownOnTheSignInScreen()
    {
        _authentication.SignInAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Throws(new ApiException(
                ApiErrorCode.Internal, "boom", statusCode: 500, requestId: "01HZY"));
        LoginViewModel model = WithCredentials(CreateViewModel());

        await model.SubmitCommand.ExecuteAsync(null);

        Assert.DoesNotContain("01HZY", model.ErrorMessage!, StringComparison.Ordinal);
        Assert.Equal(_localization.Translate("Error.Generic"), model.ErrorMessage);
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

    [Fact]
    public void AskingForAResetLinkNeedsAnAddressToAskFor()
    {
        LoginViewModel model = CreateViewModel();

        Assert.False(model.RequestPasswordResetCommand.CanExecute(null));

        model.Email = "luigi@example.com";

        Assert.True(model.RequestPasswordResetCommand.CanExecute(null));
    }

    // The server answers the same for an address it has never seen, and the client must not
    // undo that from this side: the sentence is conditional and mentions no account.
    [Fact]
    public async Task AResetLinkIsRequestedWithoutConfirmingThatTheAccountExists()
    {
        LoginViewModel model = WithCredentials(CreateViewModel());

        await model.RequestPasswordResetCommand.ExecuteAsync(null);

        await _authentication.Received(1).RequestPasswordResetAsync(
            "luigi@example.com", Arg.Any<CancellationToken>());
        Assert.Equal(
            _localization.Translate("Auth.PasswordResetRequested", "luigi@example.com"),
            model.InfoMessage);
        Assert.Null(model.ErrorMessage);
    }

    // The same call, the same success, the same sentence — the only difference being an
    // address nobody registered. This is the property the server pays for; asserting it here
    // is what stops a future edit from turning the reply into an enumeration oracle.
    [Fact]
    public async Task AnAddressThatDoesNotExistIsAnsweredIdentically()
    {
        LoginViewModel known = WithCredentials(CreateViewModel());
        await known.RequestPasswordResetCommand.ExecuteAsync(null);

        LoginViewModel unknown = CreateViewModel();
        unknown.Email = "nobody@example.com";
        await unknown.RequestPasswordResetCommand.ExecuteAsync(null);

        Assert.Equal(
            known.InfoMessage!.Replace(
                "luigi@example.com", "nobody@example.com", StringComparison.Ordinal),
            unknown.InfoMessage);
        Assert.Null(unknown.ErrorMessage);
    }

    // The realistic way to spend a three-in-fifteen-minutes budget is pressing twice, so a
    // successful request disarms its own button until the address changes.
    [Fact]
    public async Task TheSameAddressIsNotAskedForTwiceInARow()
    {
        LoginViewModel model = WithCredentials(CreateViewModel());

        await model.RequestPasswordResetCommand.ExecuteAsync(null);

        Assert.False(model.RequestPasswordResetCommand.CanExecute(null));

        model.Email = "luigi@example.org";

        Assert.True(model.RequestPasswordResetCommand.CanExecute(null));
    }

    // A refusal is the one case where pressing again is the right thing to do, so the guard
    // stays open: the button is live the moment the wait is over.
    [Fact]
    public async Task ARefusedRequestLeavesTheButtonPressable()
    {
        _authentication.RequestPasswordResetAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Throws(new ApiException(
                ApiErrorCode.RateLimited, "slow down", retryAfter: TimeSpan.FromSeconds(900)));
        LoginViewModel model = WithCredentials(CreateViewModel());

        await model.RequestPasswordResetCommand.ExecuteAsync(null);

        Assert.True(model.RequestPasswordResetCommand.CanExecute(null));
    }

    // Not an error: the person most likely to reach the limit is the one whose message never
    // arrived, and a red "too many attempts" reads as a refusal of something they did wrong.
    [Fact]
    public async Task BeingThrottledIsSaidOnTheInfoLineWithTheWait()
    {
        _authentication.RequestPasswordResetAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Throws(new ApiException(
                ApiErrorCode.RateLimited, "slow down", retryAfter: TimeSpan.FromSeconds(900)));
        LoginViewModel model = WithCredentials(CreateViewModel());

        await model.RequestPasswordResetCommand.ExecuteAsync(null);

        Assert.Equal(_localization.Translate("Auth.MailThrottled", 15), model.InfoMessage);
        Assert.Null(model.ErrorMessage);
    }

    // A server that sent no Retry-After, or one under two minutes: the wait is not named
    // rather than named as "1 minutes".
    [Fact]
    public async Task AThrottledRequestWithNoDelayToNameDoesNotNameOne()
    {
        _authentication.RequestPasswordResetAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Throws(new ApiException(ApiErrorCode.RateLimited, "slow down"));
        LoginViewModel model = WithCredentials(CreateViewModel());

        await model.RequestPasswordResetCommand.ExecuteAsync(null);

        Assert.Equal(_localization.Translate("Auth.MailThrottledSoon"), model.InfoMessage);
        Assert.Null(model.ErrorMessage);
    }

    // `mail.transport: "none"` answers 404 on both routes. "That is not available" is true
    // and says nothing about which thing, on the one failure retrying will never fix.
    [Fact]
    public async Task AServerThatSendsNoMailSaysSoRatherThanSayingNotFound()
    {
        _authentication.RequestPasswordResetAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Throws(new ApiException(ApiErrorCode.NotFound, "no such endpoint"));
        LoginViewModel model = WithCredentials(CreateViewModel());

        await model.RequestPasswordResetCommand.ExecuteAsync(null);

        Assert.Equal(_localization.Translate("Auth.MailUnavailable"), model.ErrorMessage);
        Assert.NotEqual(_localization.Translate("Error.NotFound"), model.ErrorMessage);
        Assert.Null(model.InfoMessage);
    }

    // On a screen nobody has failed anything on yet, the button is an invitation to spend a
    // rate limit for nothing.
    [Fact]
    public void TheResendLinkIsNotOfferedOnAFreshSignInScreen()
    {
        LoginViewModel model = WithCredentials(CreateViewModel());

        Assert.False(model.CanResendVerification);
        Assert.False(model.ResendVerificationEmailCommand.CanExecute(null));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task RegisteringWithVerificationRequiredOffersTheResend(bool sent)
    {
        _authentication.RegisterAsync(
                Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(new RegistrationResult
            {
                EmailVerificationRequired = true,
                VerificationEmailSent = sent,
            });

        LoginViewModel model = WithCredentials(CreateViewModel());
        model.IsRegistering = true;
        model.DisplayName = "Luigi";

        await model.SubmitCommand.ExecuteAsync(null);

        Assert.True(model.CanResendVerification);
        Assert.True(model.ResendVerificationEmailCommand.CanExecute(null));
    }

    // A 403 on the sign-in route means an unconfirmed address or a disabled account, and the
    // server does not distinguish them. The client says the one that can be acted on — and
    // stops saying "your account is not allowed to do that", which named neither.
    [Fact]
    public async Task ASignInRefusedForAnUnconfirmedAddressSaysSoAndOffersTheResend()
    {
        _authentication.SignInAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Throws(new ApiException(ApiErrorCode.Forbidden, "confirm your email address"));
        LoginViewModel model = WithCredentials(CreateViewModel());

        await model.SubmitCommand.ExecuteAsync(null);

        Assert.Equal(_localization.Translate("Auth.ConfirmAddressFirst"), model.ErrorMessage);
        Assert.NotEqual(_localization.Translate("Error.Forbidden"), model.ErrorMessage);
        Assert.True(model.CanResendVerification);
    }

    [Fact]
    public async Task TheVerificationLinkIsAskedForWithoutConfirmingThatTheAddressExists()
    {
        _authentication.SignInAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Throws(new ApiException(ApiErrorCode.Forbidden, "confirm your email address"));
        LoginViewModel model = WithCredentials(CreateViewModel());
        await model.SubmitCommand.ExecuteAsync(null);

        await model.ResendVerificationEmailCommand.ExecuteAsync(null);

        await _authentication.Received(1).ResendVerificationEmailAsync(
            "luigi@example.com", Arg.Any<CancellationToken>());
        Assert.Equal(
            _localization.Translate("Auth.VerificationLinkRequested", "luigi@example.com"),
            model.InfoMessage);
        Assert.False(model.ResendVerificationEmailCommand.CanExecute(null));
    }

    // Reaching the state again is new evidence that the last message never arrived, so the
    // "already asked" guard opens even though the address has not changed.
    [Fact]
    public async Task FailingToSignInAgainReopensTheResend()
    {
        _authentication.SignInAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Throws(new ApiException(ApiErrorCode.Forbidden, "confirm your email address"));
        LoginViewModel model = WithCredentials(CreateViewModel());
        await model.SubmitCommand.ExecuteAsync(null);
        await model.ResendVerificationEmailCommand.ExecuteAsync(null);

        model.Password = "correct horse";
        await model.SubmitCommand.ExecuteAsync(null);

        Assert.True(model.ResendVerificationEmailCommand.CanExecute(null));
    }

    [Fact]
    public async Task SwitchingToRegistrationTakesTheResendOfferAway()
    {
        _authentication.SignInAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Throws(new ApiException(ApiErrorCode.Forbidden, "confirm your email address"));
        LoginViewModel model = WithCredentials(CreateViewModel());
        await model.SubmitCommand.ExecuteAsync(null);

        model.ToggleModeCommand.Execute(null);

        Assert.False(model.CanResendVerification);
    }

    // --- a deployment that sends nothing (D40) ------------------------------------------

    // The offer would be a button whose only outcome is a 404: the routes that send are
    // switched off with the transport. What replaces it is a sentence naming who to ask.
    [Fact]
    public async Task AServerThatSendsNoMailTakesTheResetOfferAway()
    {
        _capabilities.GetAsync(Arg.Any<CancellationToken>()).Returns(
            ServerCapabilities.Fallback with { Mail = new MailCapabilities { Enabled = false } });

        LoginViewModel model = CreateViewModel();
        await model.LoadAsync(TestContext.Current.CancellationToken);
        model.Email = "luigi@example.com";

        Assert.False(model.MailAvailable);
        Assert.False(model.RequestPasswordResetCommand.CanExecute(null));
    }

    [Fact]
    public async Task AServerThatSendsMailKeepsIt()
    {
        LoginViewModel model = CreateViewModel();
        await model.LoadAsync(TestContext.Current.CancellationToken);
        model.Email = "luigi@example.com";

        Assert.True(model.MailAvailable);
        Assert.True(model.RequestPasswordResetCommand.CanExecute(null));
    }

    // True until told otherwise, which is also how a server too old to carry the key reads:
    // guessing wrong here costs one refusal the screen already explains, and guessing wrong
    // the other way hides the way back into an account.
    [Fact]
    public void TheOfferIsThereBeforeTheServerHasAnswered()
    {
        Assert.True(CreateViewModel().MailAvailable);
    }

    // The resend is the same button on the same reasoning: after a 403 there is nothing to
    // offer on a server that cannot send.
    [Fact]
    public async Task NoResendIsOfferedWhereNothingCanBeSent()
    {
        _capabilities.GetAsync(Arg.Any<CancellationToken>()).Returns(
            ServerCapabilities.Fallback with { Mail = new MailCapabilities { Enabled = false } });
        _authentication
            .SignInAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .ThrowsAsync(new ApiException(ApiErrorCode.Forbidden, "confirm your address"));

        LoginViewModel model = CreateViewModel();
        await model.LoadAsync(TestContext.Current.CancellationToken);
        await WithCredentials(model).SubmitCommand.ExecuteAsync(null);

        Assert.False(model.CanResendVerification);
    }
}

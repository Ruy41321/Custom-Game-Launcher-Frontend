using System.Net;
using GameLauncher.Core.Api;
using GameLauncher.Core.Authentication;
using GameLauncher.Infrastructure.Api;

namespace GameLauncher.Infrastructure.Tests.Api;

public sealed class AuthApiClientTests
{
    private static readonly Uri BaseAddress = new("https://launcher.example/api/v1/");

    private static readonly DateTimeOffset Now = new(2026, 8, 3, 12, 0, 0, TimeSpan.Zero);

    private const string SessionBody = """
        {
          "accessToken": "eyJhbGciOi…",
          "refreshToken": "3YkQrefresh",
          "tokenType": "Bearer",
          "expiresIn": 900,
          "user": { "id": "u1", "email": "luigi@example.com", "displayName": "Luigi",
                    "emailVerified": true, "uploadQuotaBytes": 5368709120, "uploadUsedBytes": 12 },
          "permissions": ["library.read", "game.download"]
        }
        """;

    private static AuthApiClient ClientOver(StubHttpMessageHandler handler) =>
        new(new HttpClient(handler) { BaseAddress = BaseAddress }, new FixedTimeProvider(Now));

    [Fact]
    public async Task LoggingInReadsTheWholeSession()
    {
        var handler = StubHttpMessageHandler.RespondingWith(HttpStatusCode.OK, SessionBody);

        AuthSession session = await ClientOver(handler).LoginAsync(
            "luigi@example.com", "correct horse", TestContext.Current.CancellationToken);

        Assert.Equal("/api/v1/auth/login", handler.LastRequest.PathAndQuery);
        Assert.Equal(HttpMethod.Post, handler.LastRequest.Method);
        Assert.Contains("\"password\":\"correct horse\"", handler.LastRequest.Body, StringComparison.Ordinal);
        Assert.Equal("eyJhbGciOi…", session.AccessToken);
        Assert.Equal("Luigi", session.User.DisplayName);
        Assert.Equal(5368709120, session.User.UploadQuotaBytes);
        Assert.Equal(["library.read", "game.download"], session.Permissions);
    }

    // expiresIn is a lifetime; everything above wants an instant, and it has to be measured
    // against the same clock it will later be compared with.
    [Fact]
    public async Task TheRelativeLifetimeBecomesAnAbsoluteExpiry()
    {
        var handler = StubHttpMessageHandler.RespondingWith(HttpStatusCode.OK, SessionBody);

        AuthSession session = await ClientOver(handler).LoginAsync(
            "luigi@example.com", "pw", TestContext.Current.CancellationToken);

        Assert.Equal(Now.AddSeconds(900), session.AccessTokenExpiresAt);
    }

    [Fact]
    public async Task RefreshingPresentsTheTokenInTheBody()
    {
        var handler = StubHttpMessageHandler.RespondingWith(HttpStatusCode.OK, SessionBody);

        await ClientOver(handler).RefreshAsync("3YkQrefresh", TestContext.Current.CancellationToken);

        Assert.Equal("/api/v1/auth/refresh", handler.LastRequest.PathAndQuery);
        Assert.Contains("\"refreshToken\":\"3YkQrefresh\"", handler.LastRequest.Body, StringComparison.Ordinal);
    }

    // Nothing on this client may carry a bearer token: the whole point of the separate
    // HttpClient is that refreshing works when the access token has already expired.
    [Fact]
    public async Task NoAuthorizationHeaderIsEverAttached()
    {
        var handler = StubHttpMessageHandler.RespondingWith(HttpStatusCode.OK, SessionBody);

        await ClientOver(handler).LoginAsync("a@b.c", "pw", TestContext.Current.CancellationToken);

        Assert.Null(handler.LastRequest.Authorization);
    }

    [Fact]
    public async Task RegisteringReportsWhetherTheAddressMustBeVerified()
    {
        const string body = """
            {
              "user": { "id": "u1", "email": "luigi@example.com", "displayName": "Luigi",
                        "emailVerified": false, "uploadQuotaBytes": 0, "uploadUsedBytes": 0 },
              "emailVerificationRequired": true,
              "verificationEmailSent": true
            }
            """;
        var handler = StubHttpMessageHandler.RespondingWith(HttpStatusCode.Created, body);

        RegistrationResult result = await ClientOver(handler).RegisterAsync(
            "luigi@example.com", "correct horse", "Luigi", TestContext.Current.CancellationToken);

        Assert.Equal("/api/v1/auth/register", handler.LastRequest.PathAndQuery);
        Assert.True(result.EmailVerificationRequired);
        Assert.True(result.VerificationEmailSent);
        Assert.False(result.User.EmailVerified);
    }

    // The account is created whether or not the message went out, so the client has to be able
    // to tell the two apart — and an answer with the field missing is read as "it did not".
    [Fact]
    public async Task RegisteringReportsAMessageThatDidNotGoOut()
    {
        const string body = """
            { "user": { "id": "u1" }, "emailVerificationRequired": true,
              "verificationEmailSent": false }
            """;
        var handler = StubHttpMessageHandler.RespondingWith(HttpStatusCode.Created, body);

        RegistrationResult result = await ClientOver(handler).RegisterAsync(
            "a@b.c", "pw", "Luigi", TestContext.Current.CancellationToken);

        Assert.True(result.EmailVerificationRequired);
        Assert.False(result.VerificationEmailSent);
    }

    [Fact]
    public async Task AServerThatSaysNothingAboutDeliveryIsReadAsNotHavingSent()
    {
        const string body = """
            { "user": { "id": "u1" }, "emailVerificationRequired": true }
            """;
        var handler = StubHttpMessageHandler.RespondingWith(HttpStatusCode.Created, body);

        RegistrationResult result = await ClientOver(handler).RegisterAsync(
            "a@b.c", "pw", "Luigi", TestContext.Current.CancellationToken);

        Assert.False(result.VerificationEmailSent);
    }

    [Fact]
    public async Task AnAlreadyRegisteredAddressIsAConflict()
    {
        var handler = StubHttpMessageHandler.RespondingWith(
            HttpStatusCode.Conflict,
            """{ "code": "conflict", "detail": "That address is already registered." }""");

        ApiException exception = await Assert.ThrowsAsync<ApiException>(
            () => ClientOver(handler).RegisterAsync(
                "a@b.c", "pw", "Luigi", TestContext.Current.CancellationToken));

        Assert.Equal(ApiErrorCode.Conflict, exception.Code);
    }

    [Fact]
    public async Task SigningOutPostsTheRefreshToken()
    {
        var handler = StubHttpMessageHandler.RespondingWith(
            HttpStatusCode.OK, """{ "status": "signed out" }""");

        await ClientOver(handler).LogoutAsync("3YkQrefresh", TestContext.Current.CancellationToken);

        Assert.Equal("/api/v1/auth/logout", handler.LastRequest.PathAndQuery);
        Assert.Contains("3YkQrefresh", handler.LastRequest.Body, StringComparison.Ordinal);
    }

    // Nothing comes back any more: the link is delivered by mail, and the page it lands on is
    // the server's. All the client can do is ask.
    [Fact]
    public async Task APasswordResetRequestPostsTheAddressAndReadsNothingBack()
    {
        var handler = StubHttpMessageHandler.RespondingWith(
            HttpStatusCode.OK,
            """{ "status": "if that address is registered, a reset link has been sent" }""");

        await ClientOver(handler).RequestPasswordResetAsync(
            "a@b.c", TestContext.Current.CancellationToken);

        Assert.Equal("/api/v1/auth/password-reset/request", handler.LastRequest.PathAndQuery);
        Assert.Contains("a@b.c", handler.LastRequest.Body, StringComparison.Ordinal);
    }

    // The way back for an account created while the relay was down. Same shape as the reset
    // request, and the same identical answer whatever the address turns out to be.
    [Fact]
    public async Task AskingForAnotherVerificationLinkPostsTheAddress()
    {
        var handler = StubHttpMessageHandler.RespondingWith(
            HttpStatusCode.OK,
            """{ "status": "if that address needs confirming, a new link has been sent" }""");

        await ClientOver(handler).ResendVerificationEmailAsync(
            "a@b.c", TestContext.Current.CancellationToken);

        Assert.Equal("/api/v1/auth/verify-email/resend", handler.LastRequest.PathAndQuery);
        Assert.Equal(HttpMethod.Post, handler.LastRequest.Method);
        Assert.Contains("a@b.c", handler.LastRequest.Body, StringComparison.Ordinal);
    }

    // Both mail routes carry a bucket of their own, and a deployment that sends no mail
    // answers 404. Neither is special-cased in the transport; this pins down that they arrive
    // as the codes the sign-in screen switches on.
    [Fact]
    public async Task TheMailBucketArrivesAsRateLimitedWithItsWait()
    {
        var handler = StubHttpMessageHandler.RespondingWith(
            HttpStatusCode.TooManyRequests,
            """{ "code": "rate_limited", "detail": "too many messages requested" }""",
            ("Retry-After", "900"));

        ApiException exception = await Assert.ThrowsAsync<ApiException>(
            () => ClientOver(handler).ResendVerificationEmailAsync(
                "a@b.c", TestContext.Current.CancellationToken));

        Assert.Equal(ApiErrorCode.RateLimited, exception.Code);
        Assert.Equal(TimeSpan.FromSeconds(900), exception.RetryAfter);
    }

    [Fact]
    public async Task AServerConfiguredToSendNoMailAnswersNotFound()
    {
        var handler = StubHttpMessageHandler.RespondingWith(
            HttpStatusCode.NotFound,
            """{ "code": "not_found", "detail": "no such endpoint" }""");

        ApiException exception = await Assert.ThrowsAsync<ApiException>(
            () => ClientOver(handler).RequestPasswordResetAsync(
                "a@b.c", TestContext.Current.CancellationToken));

        Assert.Equal(ApiErrorCode.NotFound, exception.Code);
    }

    [Fact]
    public async Task ConfirmingAResetSendsTheTokenAndTheNewPassword()
    {
        var handler = StubHttpMessageHandler.RespondingWith(
            HttpStatusCode.OK, """{ "status": "password updated" }""");

        await ClientOver(handler).ConfirmPasswordResetAsync(
            "reset-me", "a whole new password", TestContext.Current.CancellationToken);

        Assert.Equal("/api/v1/auth/password-reset/confirm", handler.LastRequest.PathAndQuery);
        Assert.Contains("\"token\":\"reset-me\"", handler.LastRequest.Body, StringComparison.Ordinal);
        Assert.Contains(
            "\"password\":\"a whole new password\"", handler.LastRequest.Body, StringComparison.Ordinal);
    }

    // Passwords are 12-256 characters with no character-class rules, so a passphrase full of
    // accents is entirely ordinary. The serializer escapes non-ASCII rather than emitting it
    // raw; that is still the same string once the server decodes it, and this pins it down.
    [Fact]
    public async Task ANonAsciiPasswordIsEscapedRatherThanMangled()
    {
        var handler = StubHttpMessageHandler.RespondingWith(HttpStatusCode.OK, SessionBody);

        await ClientOver(handler).LoginAsync(
            "a@b.c", "però un cavallo", TestContext.Current.CancellationToken);

        string body = handler.LastRequest.Body!;
        Assert.DoesNotContain("però", body, StringComparison.Ordinal);
        Assert.Equal(
            "però un cavallo",
            System.Text.Json.JsonDocument.Parse(body).RootElement.GetProperty("password").GetString());
    }

    private sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }
}

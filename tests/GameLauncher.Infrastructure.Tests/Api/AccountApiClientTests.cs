using System.Net;
using GameLauncher.Core.Api;
using GameLauncher.Core.Authentication;
using GameLauncher.Infrastructure.Api;

namespace GameLauncher.Infrastructure.Tests.Api;

public sealed class AccountApiClientTests
{
    private static readonly Uri BaseAddress = new("https://launcher.example/api/v1/");

    private static HttpClient ClientOver(StubHttpMessageHandler handler) =>
        new(handler) { BaseAddress = BaseAddress };

    /// <summary>
    /// A POST, not a DELETE. The request carries a password and therefore a body, and a body on
    /// DELETE is the one thing HTTP declines to promise — the server named the route to match.
    /// </summary>
    [Fact]
    public async Task ErasureIsAPostCarryingThePassword()
    {
        var handler = StubHttpMessageHandler.RespondingWith(HttpStatusCode.NoContent, "{}");
        var client = new AccountApiClient(ClientOver(handler), TimeProvider.System);

        await client.DeleteAccountAsync(
            new DeleteAccountRequest { Password = "hunter2", Reason = "moving on" },
            TestContext.Current.CancellationToken);

        Assert.Equal(HttpMethod.Post, handler.LastRequest.Method);
        Assert.Equal("/api/v1/me/deletion", handler.LastRequest.PathAndQuery);
        Assert.Equal(
            """{"password":"hunter2","reason":"moving on"}""", handler.LastRequest.Body);
    }

    [Fact]
    public async Task AnAbsentReasonIsNotSent()
    {
        var handler = StubHttpMessageHandler.RespondingWith(HttpStatusCode.NoContent, "{}");
        var client = new AccountApiClient(ClientOver(handler), TimeProvider.System);

        await client.DeleteAccountAsync(
            new DeleteAccountRequest { Password = "hunter2" },
            TestContext.Current.CancellationToken);

        Assert.Equal("""{"password":"hunter2"}""", handler.LastRequest.Body);
    }

    // The wrong password is the likeliest way to arrive here, and it has to stay recognisable:
    // the page keeps what was typed and says so, rather than reporting a generic failure.
    [Fact]
    public async Task AWrongPasswordArrivesAsUnauthenticated()
    {
        var handler = StubHttpMessageHandler.RespondingWith(
            HttpStatusCode.Unauthorized,
            """{"code":"unauthenticated","detail":"the password is incorrect","status":401}""");

        var client = new AccountApiClient(ClientOver(handler), TimeProvider.System);

        ApiException failure = await Assert.ThrowsAsync<ApiException>(() =>
            client.DeleteAccountAsync(
                new DeleteAccountRequest { Password = "wrong" },
                TestContext.Current.CancellationToken));

        Assert.Equal(ApiErrorCode.Unauthenticated, failure.Code);
    }

    /// <summary>
    /// The answer is a whole session, not an acknowledgement: the server revoked every session
    /// the account held, this caller's included, so a 204 would leave the launcher signed out
    /// by succeeding.
    /// </summary>
    [Fact]
    public async Task ChangingThePasswordSendsBothAndReadsBackASession()
    {
        var handler = StubHttpMessageHandler.RespondingWith(
            HttpStatusCode.OK,
            """
            {"accessToken":"fresh","refreshToken":"also-fresh","tokenType":"Bearer",
             "expiresIn":900,
             "user":{"id":"u1","email":"a@b.c","displayName":"A","emailVerified":true,
                     "passwordChangeRequired":false,
                     "uploadQuotaBytes":1,"uploadUsedBytes":0},
             "permissions":["library.read"]}
            """);

        var client = new AccountApiClient(ClientOver(handler), TimeProvider.System);

        AuthSession session = await client.ChangePasswordAsync(
            new ChangePasswordRequest
            {
                CurrentPassword = "the temporary one",
                NewPassword = "a brand new passphrase",
            },
            TestContext.Current.CancellationToken);

        Assert.Equal(HttpMethod.Post, handler.LastRequest.Method);
        Assert.Equal("/api/v1/me/password", handler.LastRequest.PathAndQuery);
        Assert.Equal(
            """{"currentPassword":"the temporary one","newPassword":"a brand new passphrase"}""",
            handler.LastRequest.Body);

        Assert.Equal("fresh", session.AccessToken);
        Assert.Equal("also-fresh", session.RefreshToken);
        Assert.False(session.User.PasswordChangeRequired);
    }

    /// <summary>
    /// The flag the whole feature turns on. It arrives on the session so the shell is told
    /// rather than left to discover it from the first 403.
    /// </summary>
    [Fact]
    public async Task AForcedChangeIsCarriedOnTheSession()
    {
        var handler = StubHttpMessageHandler.RespondingWith(
            HttpStatusCode.OK,
            """
            {"accessToken":"t","refreshToken":"r","tokenType":"Bearer","expiresIn":900,
             "user":{"id":"u1","email":"a@b.c","displayName":"A","emailVerified":true,
                     "passwordChangeRequired":true,
                     "uploadQuotaBytes":1,"uploadUsedBytes":0},
             "permissions":[]}
            """);

        var client = new AccountApiClient(ClientOver(handler), TimeProvider.System);

        AuthSession session = await client.ChangePasswordAsync(
            new ChangePasswordRequest { CurrentPassword = "a", NewPassword = "b" },
            TestContext.Current.CancellationToken);

        Assert.True(session.User.PasswordChangeRequired);
    }
}

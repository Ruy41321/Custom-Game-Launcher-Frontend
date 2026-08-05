using System.Net;
using GameLauncher.Core.Api;
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
        var client = new AccountApiClient(ClientOver(handler));

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
        var client = new AccountApiClient(ClientOver(handler));

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

        var client = new AccountApiClient(ClientOver(handler));

        ApiException failure = await Assert.ThrowsAsync<ApiException>(() =>
            client.DeleteAccountAsync(
                new DeleteAccountRequest { Password = "wrong" },
                TestContext.Current.CancellationToken));

        Assert.Equal(ApiErrorCode.Unauthenticated, failure.Code);
    }
}

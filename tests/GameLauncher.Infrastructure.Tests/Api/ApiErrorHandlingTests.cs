using System.Net;
using GameLauncher.Core.Api;
using GameLauncher.Core.Models;
using GameLauncher.Infrastructure.Api;

namespace GameLauncher.Infrastructure.Tests.Api;

/// <summary>
/// Everything above the API client sees <see cref="ApiException"/> and nothing else. These
/// tests are the proof of that, one failure shape at a time.
/// </summary>
public sealed class ApiErrorHandlingTests
{
    private static readonly Uri BaseAddress = new("https://launcher.example/api/v1/");

    private static CatalogApiClient ClientOver(StubHttpMessageHandler handler) =>
        new(new HttpClient(handler) { BaseAddress = BaseAddress });

    private static Task<GameDetail> CallAsync(StubHttpMessageHandler handler) =>
        ClientOver(handler).GetGameAsync("orbit", TestContext.Current.CancellationToken);

    [Fact]
    public async Task TheErrorEnvelopeBecomesATypedException()
    {
        const string envelope = """
            { "type": "about:blank", "title": "Not found", "status": 404, "code": "not_found",
              "detail": "No such game.", "requestId": "01HZY" }
            """;

        ApiException exception = await Assert.ThrowsAsync<ApiException>(
            () => CallAsync(StubHttpMessageHandler.RespondingWith(HttpStatusCode.NotFound, envelope)));

        Assert.Equal(ApiErrorCode.NotFound, exception.Code);
        Assert.Equal("No such game.", exception.Message);
        Assert.Equal(404, exception.StatusCode);
        Assert.Equal("01HZY", exception.RequestId);
    }

    // The rule is what makes a 422 sayable in the user's own language; the arguments are what
    // let the sentence name the limit instead of only that one was broken.
    [Fact]
    public async Task AValidationEnvelopeCarriesItsRuleAndArguments()
    {
        const string envelope = """
            { "title": "Validation failed", "status": 422, "code": "invalid_input",
              "rule": "password_too_short", "ruleArgs": ["8"],
              "detail": "password must be at least 8 characters", "requestId": "01HZY" }
            """;

        ApiException exception = await Assert.ThrowsAsync<ApiException>(() => CallAsync(
            StubHttpMessageHandler.RespondingWith(HttpStatusCode.UnprocessableEntity, envelope)));

        Assert.Equal(ApiErrorCode.InvalidInput, exception.Code);
        Assert.Equal("password_too_short", exception.Rule);
        Assert.Equal("8", Assert.Single(exception.RuleArgs));
    }

    // Which is every refusal that is not about a field somebody typed, and every response from
    // a server older than the field. Both have to read the same from here.
    [Fact]
    public async Task AnEnvelopeWithoutARuleLeavesTheExceptionWithoutOne()
    {
        var handler = StubHttpMessageHandler.RespondingWith(
            HttpStatusCode.UnprocessableEntity,
            """{ "code": "invalid_input", "detail": "password is required" }""");

        ApiException exception = await Assert.ThrowsAsync<ApiException>(() => CallAsync(handler));

        Assert.Null(exception.Rule);
        Assert.Empty(exception.RuleArgs);
    }

    // The request id is the one string that finds the request in the server's logs, so it is
    // taken from the header when the body has no room for it.
    [Fact]
    public async Task TheRequestIdIsReadFromTheHeaderWhenTheBodyOmitsIt()
    {
        var handler = StubHttpMessageHandler.RespondingWith(
            HttpStatusCode.Forbidden,
            """{ "code": "forbidden", "detail": "Nope." }""",
            ("X-Request-Id", "01HEADER"));

        ApiException exception = await Assert.ThrowsAsync<ApiException>(() => CallAsync(handler));

        Assert.Equal("01HEADER", exception.RequestId);
    }

    // nginx, not the API, answers when the API is down — and it answers in HTML.
    [Fact]
    public async Task AnErrorThatIsNotTheEnvelopeStillMapsToSomethingUseful()
    {
        var handler = StubHttpMessageHandler.RespondingWithBody(
            HttpStatusCode.BadGateway, "<html><body>502 Bad Gateway</body></html>", "text/html");

        ApiException exception = await Assert.ThrowsAsync<ApiException>(() => CallAsync(handler));

        Assert.Equal(ApiErrorCode.Internal, exception.Code);
        Assert.Equal(502, exception.StatusCode);
    }

    [Fact]
    public async Task ThrottlingCarriesHowLongToWait()
    {
        var handler = StubHttpMessageHandler.RespondingWith(
            HttpStatusCode.TooManyRequests,
            """{ "code": "rate_limited", "detail": "Slow down." }""",
            ("Retry-After", "42"));

        ApiException exception = await Assert.ThrowsAsync<ApiException>(() => CallAsync(handler));

        Assert.Equal(ApiErrorCode.RateLimited, exception.Code);
        Assert.Equal(TimeSpan.FromSeconds(42), exception.RetryAfter);
        Assert.True(exception.IsTransient);
    }

    [Fact]
    public async Task AnUnreachableServerIsANetworkFailureAndNotAnHttpException()
    {
        var handler = StubHttpMessageHandler.Throwing(
            new HttpRequestException("No such host is known."));

        ApiException exception = await Assert.ThrowsAsync<ApiException>(() => CallAsync(handler));

        Assert.Equal(ApiErrorCode.Network, exception.Code);
        Assert.Null(exception.StatusCode);
        Assert.IsType<HttpRequestException>(exception.InnerException);
    }

    // A timeout arrives as OperationCanceledException with nobody having cancelled anything.
    // Letting it through would make a dead server indistinguishable from a user action.
    [Fact]
    public async Task ATimeoutIsReportedAsANetworkFailure()
    {
        var handler = StubHttpMessageHandler.Throwing(new TaskCanceledException("timed out"));

        ApiException exception = await Assert.ThrowsAsync<ApiException>(() => CallAsync(handler));

        Assert.Equal(ApiErrorCode.Network, exception.Code);
    }

    [Fact]
    public async Task CancellingIsStillACancellation()
    {
        var handler = StubHttpMessageHandler.Throwing(new TaskCanceledException("cancelled"));
        using var cancelled = new CancellationTokenSource();
        await cancelled.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => ClientOver(handler).GetGameAsync("orbit", cancelled.Token));
    }

    [Fact]
    public async Task ASuccessfulResponseThatIsNotJsonIsNotSilentlyAccepted()
    {
        var handler = StubHttpMessageHandler.RespondingWithBody(
            HttpStatusCode.OK, "<html>login page</html>", "text/html");

        ApiException exception = await Assert.ThrowsAsync<ApiException>(() => CallAsync(handler));

        Assert.Equal(ApiErrorCode.Unknown, exception.Code);
    }

    [Fact]
    public async Task AnEmptyBodyWhereADocumentWasExpectedIsAnError()
    {
        var handler = StubHttpMessageHandler.RespondingWith(HttpStatusCode.OK, "null");

        ApiException exception = await Assert.ThrowsAsync<ApiException>(() => CallAsync(handler));

        Assert.Equal(ApiErrorCode.Unknown, exception.Code);
    }
}

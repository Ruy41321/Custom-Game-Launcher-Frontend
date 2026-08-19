using System.Net;
using GameLauncher.Core.Api;
using GameLauncher.Core.Models;
using GameLauncher.Infrastructure.Api;

namespace GameLauncher.Infrastructure.Tests.Api;

/// <summary>
/// The handler that writes down what every API request found, and refuses to send one while
/// the circuit is open. What matters is where the refusal lands: a caller sees the same
/// <see cref="ApiErrorCode.Network"/> it would have seen after a timeout, having paid nothing
/// for it.
/// </summary>
public sealed class ReachabilityHandlerTests
{
    private static readonly Uri BaseAddress = new("https://launcher.example/api/v1/");

    private readonly FakeTimeProvider _clock = new(new DateTimeOffset(
        2026, 8, 18, 21, 0, 0, TimeSpan.Zero));

    /// <summary>
    /// A deadline of 50ms rather than the shipped eight seconds: what is being proved is that
    /// a silent server is given up on, not how long the launcher is willing to wait.
    /// </summary>
    private static readonly TimeSpan TestBudget = TimeSpan.FromMilliseconds(50);

    private static CatalogApiClient ClientOver(
        StubHttpMessageHandler inner, ServerReachability reachability) =>
        new(new HttpClient(
            new ReachabilityHandler(reachability, TestBudget) { InnerHandler = inner })
        {
            BaseAddress = BaseAddress,
        });

    private static Task<GameDetail> CallAsync(CatalogApiClient client) =>
        client.GetGameAsync("orbit", TestContext.Current.CancellationToken);

    [Fact]
    public async Task ARefusedConnectionIsWrittenDown()
    {
        ServerReachability reachability = new(_clock);
        using StubHttpMessageHandler inner = StubHttpMessageHandler.Throwing(
            new HttpRequestException("No route to host."));

        await Assert.ThrowsAsync<ApiException>(() => CallAsync(ClientOver(inner, reachability)));

        Assert.False(reachability.IsOnline);
    }

    /// <summary>
    /// The whole saving: the second call never reaches the socket, and the caller is told the
    /// same thing it would have been told twenty seconds later.
    /// </summary>
    [Fact]
    public async Task WhileTheCircuitIsOpenNothingIsSent()
    {
        ServerReachability reachability = new(_clock);
        reachability.ReportUnreachable();

        using StubHttpMessageHandler inner = StubHttpMessageHandler.RespondingWith(
            HttpStatusCode.OK, "{}");

        ApiException exception = await Assert.ThrowsAsync<ApiException>(
            () => CallAsync(ClientOver(inner, reachability)));

        Assert.Equal(ApiErrorCode.Network, exception.Code);
        Assert.Empty(inner.Requests);
    }

    /// <summary>
    /// The failure this was built for, and the one a connect timeout cannot catch: a proxy in
    /// front of a stopped backend accepts the connection and then says nothing. The launcher
    /// gives up on its own deadline rather than on the client's thirty seconds.
    /// </summary>
    [Fact]
    public async Task AServerThatAcceptsTheConnectionAndSaysNothingIsStillAMissingServer()
    {
        ServerReachability reachability = new(_clock);
        using StubHttpMessageHandler inner = StubHttpMessageHandler.Hanging();

        ApiException exception = await Assert.ThrowsAsync<ApiException>(
            () => CallAsync(ClientOver(inner, reachability)));

        Assert.Equal(ApiErrorCode.Network, exception.Code);
        Assert.False(reachability.IsOnline);
    }

    /// <summary>
    /// The deadline is for finding out whether a server is there. Once one has answered, its
    /// slow routes — the download plan diffs two manifests — are given the client's own time.
    /// </summary>
    [Fact]
    public async Task AProvenServerIsAllowedToTakeItsTime()
    {
        ServerReachability reachability = new(_clock);
        reachability.ReportReachable();

        using StubHttpMessageHandler inner = StubHttpMessageHandler.HangingFor(
            TestBudget + TimeSpan.FromMilliseconds(150));

        await CallAsync(ClientOver(inner, reachability));

        Assert.True(reachability.IsOnline);
    }

    [Fact]
    public async Task OnceTheWindowHasPassedTheNextCallIsSent()
    {
        ServerReachability reachability = new(_clock);
        reachability.ReportUnreachable();
        _clock.Advance(ServerReachability.RetryAfter);

        using StubHttpMessageHandler inner = StubHttpMessageHandler.RespondingWith(
            HttpStatusCode.OK, "{}");

        await CallAsync(ClientOver(inner, reachability));

        Assert.Single(inner.Requests);
        Assert.True(reachability.IsOnline);
    }

    /// <summary>
    /// What this tracks is whether the server can be talked to. A refusal is an answer, and an
    /// answer proves the transport works — otherwise one 404 would take the launcher offline.
    /// </summary>
    [Fact]
    public async Task AServerThatRefusesIsStillAServerThatIsThere()
    {
        ServerReachability reachability = new(_clock);
        reachability.ReportUnreachable();
        _clock.Advance(ServerReachability.RetryAfter);

        using StubHttpMessageHandler inner = StubHttpMessageHandler.RespondingWith(
            HttpStatusCode.NotFound,
            """{ "status": 404, "code": "not_found", "detail": "No such game." }""");

        await Assert.ThrowsAsync<ApiException>(() => CallAsync(ClientOver(inner, reachability)));

        Assert.True(reachability.IsOnline);
    }
}

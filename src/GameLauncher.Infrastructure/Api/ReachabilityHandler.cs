using GameLauncher.Core.Api;

namespace GameLauncher.Infrastructure.Api;

/// <summary>
/// Reports the outcome of every API request to <see cref="IServerReachability"/>, and refuses
/// to send one while the circuit is open.
///
/// It sits here rather than in <see cref="ApiTransport"/> for the same reason
/// <see cref="BearerTokenHandler"/> does: a handler cannot be forgotten by the next resource
/// client somebody adds, and the alternative was threading the same dependency through nine
/// constructors.
///
/// Deliberately outermost, above the bearer token: while the server is known to be missing
/// there is nothing to be gained by first rotating a session against it.
///
/// Only the API clients carry it. The file server and the artwork host are different
/// machines, and a game download that stopped because <c>/library</c> timed out would be a
/// worse bug than the one this fixes.
/// </summary>
/// <param name="responseBudget">
/// Overrides <see cref="ResponseBudget"/>. It exists for the tests, which would otherwise have
/// to wait out a real deadline to prove that one exists; the container resolves the default.
/// </param>
public sealed class ReachabilityHandler(
    IServerReachability reachability, TimeSpan? responseBudget = null) : DelegatingHandler
{
    /// <summary>
    /// How long a request that carries no file may wait for an answer before the server counts
    /// as missing.
    ///
    /// It exists because "offline" is rarely a refused connection. A stopped backend behind a
    /// proxy — Docker's port forwarder, nginx, a load balancer, a captive portal — **accepts**
    /// the connection in milliseconds and then says nothing, so a connect timeout never fires
    /// and what a person watches is the client's own thirty seconds. Measured on this machine
    /// against a stopped stack: 0.2s to connect, 21s of silence, once per request.
    ///
    /// It applies only while the server is **unproven** — the first request of a run, and any
    /// request after a failure. Once something has come back, the slow routes are given all
    /// the time they need: a download plan diffs two manifests server-side, and refusing that
    /// at eight seconds would break a working launcher in order to fix a broken server.
    ///
    /// Eight seconds is far longer than a first answer takes over a bad mobile link, and short
    /// enough that the worst start-up against a dead server is one of them rather than three.
    /// </summary>
    public static readonly TimeSpan ResponseBudget = TimeSpan.FromSeconds(8);

    /// <summary>
    /// Bodies above this are exempt, because for them the wait is the *upload*: a deadline
    /// there would refuse a slow link rather than a missing server. It is the publisher's
    /// chunked upload and nothing else — every other request here carries a document.
    /// </summary>
    private const long LargestBudgetedBody = 1024 * 1024;

    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request, CancellationToken cancellationToken)
    {
        if (!reachability.AllowsRequests)
        {
            // The exception the transport already understands, rather than a type of its own:
            // this is not a new kind of failure, it is the same unreachable server the last
            // attempt found, reported without spending another timeout on it.
            throw new HttpRequestException("The server was unreachable a moment ago.");
        }

        using CancellationTokenSource? deadline = reachability.IsProven ? null : DeadlineFor(request);
        using CancellationTokenSource? linked = deadline is null
            ? null
            : CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, deadline.Token);

        try
        {
            HttpResponseMessage response = await base
                .SendAsync(request, linked?.Token ?? cancellationToken)
                .ConfigureAwait(false);

            // Any answer at all, including a 500. What this tracks is whether the server can be
            // talked to; what it said is the caller's business.
            reachability.ReportReachable();
            return response;
        }
        catch (HttpRequestException)
        {
            reachability.ReportUnreachable();
            throw;
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            // This handler's deadline, or the client's own timeout. The caller did not cancel,
            // so nothing answered in time — which is the same fact as a refused connection as
            // far as the next caller is concerned, and is reported as the same failure so that
            // a hung proxy and a dead host are one story rather than two.
            reachability.ReportUnreachable();
            throw new HttpRequestException("The server did not answer in time.");
        }
    }

    private CancellationTokenSource? DeadlineFor(HttpRequestMessage request) =>
        request.Content?.Headers.ContentLength is > LargestBudgetedBody
            ? null
            : new CancellationTokenSource(responseBudget ?? ResponseBudget);
}

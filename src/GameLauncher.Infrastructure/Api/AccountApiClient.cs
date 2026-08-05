using GameLauncher.Core.Api;

namespace GameLauncher.Infrastructure.Api;

/// <summary>
/// What the account does to itself. Runs on the authenticated <see cref="HttpClient"/>, unlike
/// <see cref="AuthApiClient"/> — signing in has to work without a token and this needs one.
///
/// A POST rather than a DELETE, because the request carries a password and therefore a body,
/// and a body on DELETE is the one thing HTTP declines to promise: no defined semantics, and
/// intermediaries may drop it. The server named the route for the same reason.
/// </summary>
public sealed class AccountApiClient(HttpClient httpClient) : IAccountApi
{
    private readonly ApiTransport _transport = new(httpClient);

    public Task DeleteAccountAsync(
        DeleteAccountRequest request, CancellationToken cancellationToken = default) =>
        _transport.PostAsync("me/deletion", request, cancellationToken);
}

using System.Net.Http.Headers;
using GameLauncher.Core.Authentication;

namespace GameLauncher.Infrastructure.Api;

/// <summary>
/// Attaches the access token to every request on the authenticated client. Putting it here
/// rather than in each resource client is what makes it impossible for a new endpoint to
/// forget it, which is the same reasoning the server applies to its authorization filter.
///
/// There is deliberately no retry on 401. The token is obtained at send time and
/// <see cref="IAuthenticationService.GetAccessTokenAsync"/> already rotates it when it is
/// close to expiring, so a rejection here means the session was revoked server-side — and
/// replaying the request with the same credentials would only ask a second time.
/// </summary>
public sealed class BearerTokenHandler(IAuthenticationService authentication) : DelegatingHandler
{
    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request, CancellationToken cancellationToken)
    {
        string accessToken = await authentication
            .GetAccessTokenAsync(cancellationToken)
            .ConfigureAwait(false);

        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);

        return await base.SendAsync(request, cancellationToken).ConfigureAwait(false);
    }
}

using GameLauncher.Core.Api;
using Microsoft.Extensions.Logging;

namespace GameLauncher.Core.Authentication;

/// <summary>
/// Composes the erasure out of the two operations that already exist: the server's route, and
/// the session service's sign-out. Pure orchestration, so the ordering rule the whole thing
/// exists for is unit tested with no server and no file system.
/// </summary>
public sealed class AccountService(
    IAccountApi account,
    IAuthenticationService authentication,
    ILogger<AccountService> logger) : IAccountService
{
    public async Task DeleteAsync(
        string password, string? reason = null, CancellationToken cancellationToken = default)
    {
        if (!authentication.IsAuthenticated)
        {
            throw new ApiException(ApiErrorCode.Unauthenticated, "Not signed in.");
        }

        DeleteAccountRequest request = new()
        {
            Password = password,
            Reason = string.IsNullOrWhiteSpace(reason) ? null : reason.Trim(),
        };

        // Nothing is caught here. A wrong password and the last-operator conflict are both
        // answers the person can act on, and turning either into a generic failure would leave
        // them pressing the same button again.
        await account.DeleteAccountAsync(request, cancellationToken).ConfigureAwait(false);

        // Only now, and through sign-out rather than by clearing the store directly: the token
        // on disk is a dead credential — the server destroyed every session this account held —
        // and everything watching for a session change has to hear about it. The logout call
        // sign-out makes finds nothing to revoke and succeeds, which is what it does for an
        // unknown token by design.
        logger.LogInformation("The account was erased; signing out.");
        await authentication.SignOutAsync(cancellationToken).ConfigureAwait(false);
    }
}

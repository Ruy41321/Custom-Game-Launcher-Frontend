namespace GameLauncher.Core.Authentication;

/// <summary>
/// What the signed-in account can do to itself. One operation today, and it is the one that
/// cannot be taken back.
///
/// Erasing an account is two things that must happen in one order — the server erases it, and
/// then this launcher forgets the session — so it is one method rather than a rule two callers
/// have to remember. It is deliberately **not** a method on
/// <see cref="IAuthenticationService"/>, and the reason is structural rather than aesthetic:
/// the account route runs on the authenticated client, whose token handler depends on
/// <see cref="IAuthenticationService"/>. Putting the call there would make the session service
/// depend on a client that depends on the session service, and the container would refuse to
/// build the graph at all. Composing the two from outside keeps it acyclic.
/// </summary>
public interface IAccountService
{
    /// <summary>
    /// Erases the account, then signs this launcher out.
    ///
    /// The sign-out is **conditional on the erasure succeeding**. Signing out is a local truth
    /// the server is merely told about; an erasure is the server's answer, and forgetting the
    /// session after a refusal would leave somebody signed out of an account that still exists,
    /// unable to read the reason they were given.
    ///
    /// Throws <see cref="Api.ApiException"/> exactly as the server answered: a wrong password is
    /// <see cref="Api.ApiErrorCode.Unauthenticated"/>, and the last operator who can manage
    /// users is refused with <see cref="Api.ApiErrorCode.Conflict"/>. Both are things the person
    /// can act on, so neither is flattened into "something went wrong".
    /// </summary>
    Task DeleteAsync(
        string password, string? reason = null, CancellationToken cancellationToken = default);
}

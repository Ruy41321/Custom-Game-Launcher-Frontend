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

    /// <summary>
    /// Replaces the account's password and takes over the session the server answers with.
    ///
    /// Here for the same structural reason as the erasure, and it is worth stating because the
    /// natural home looks even more obvious: this ends every session the account held, and
    /// sessions are <see cref="IAuthenticationService"/>'s. The route is on the authenticated
    /// client, whose token handler depends on that service, so putting it there closes the
    /// cycle the container refuses to build (D47).
    ///
    /// The order is the substance again, and the opposite way round from the erasure: the
    /// server's answer is adopted **only** when the change succeeded, because a refusal leaves
    /// the old password — and the old session — in force, and forgetting either would sign
    /// somebody out for typing their current password wrong.
    ///
    /// Throws <see cref="Api.ApiException"/> as the server answered:
    /// <see cref="Api.ApiErrorCode.Unauthenticated"/> for a wrong current password, and
    /// <see cref="Api.ApiErrorCode.InvalidInput"/> — with a rule — for a new one that breaks the
    /// policy or repeats the old one.
    /// </summary>
    Task ChangePasswordAsync(
        string currentPassword, string newPassword, CancellationToken cancellationToken = default);
}

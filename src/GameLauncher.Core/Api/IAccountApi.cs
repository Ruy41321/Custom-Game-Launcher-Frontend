using GameLauncher.Core.Authentication;

namespace GameLauncher.Core.Api;

/// <summary>
/// What the account asks for when it wants to be erased.
///
/// The password travels even though the request already carries a bearer token: a token says
/// who is asking, not that the owner is the one at the keyboard, and this is the one request
/// with no undo. The server refuses without it, so sending it is not politeness — it is the
/// contract.
/// </summary>
public sealed record DeleteAccountRequest
{
    public required string Password { get; init; }

    /// <summary>
    /// Optional and free text. Null is absence on the wire, and nobody but a server operator
    /// ever reads it.
    /// </summary>
    public string? Reason { get; init; }
}

/// <summary>
/// What the account sends to replace its own password.
///
/// The current one travels alongside the bearer token for the reason
/// <see cref="DeleteAccountRequest"/> gives about erasure: a token says who is asking, not that
/// the owner is at the keyboard. It matters more here than usual, because the account this
/// route exists for is one signed in on a password an operator read out loud.
/// </summary>
public sealed record ChangePasswordRequest
{
    public required string CurrentPassword { get; init; }

    public required string NewPassword { get; init; }
}

/// <summary>
/// What an account can do to itself. Two routes, and one of them is the destructive one.
///
/// Its own interface rather than a method on <see cref="IAuthApi"/>, because that client is the
/// tokenless one (D14) and this route needs a token; and rather than a method on
/// <see cref="ICatalogApi"/>, because nothing about it is the catalog. It mirrors the split the
/// server makes for the same reason.
///
/// Nothing in the UI calls this directly: erasing an account also ends the session, and the
/// thing that owns the session is <c>IAuthenticationService</c>. Going through it is what keeps
/// "the account is gone" and "this launcher is signed out" from being two facts that can
/// disagree.
/// </summary>
public interface IAccountApi
{
    /// <summary>
    /// Erases the signed-in account. The server anonymises the row rather than deleting it —
    /// anything the account published stays online, attributed to a deleted account — and ends
    /// every session it holds, so the token used to make this call is dead when it returns.
    /// </summary>
    Task DeleteAccountAsync(
        DeleteAccountRequest request, CancellationToken cancellationToken = default);

    /// <summary>
    /// Replaces the password and answers with a <b>whole new session</b>.
    ///
    /// Not an acknowledgement, because the server revokes every session the account held —
    /// this caller's included — in the same breath. A 204 would leave the launcher holding a
    /// refresh token that no longer works and an access token that still says the password
    /// must be changed: signed out by succeeding. The session that comes back is the one the
    /// launcher carries on with.
    /// </summary>
    Task<AuthSession> ChangePasswordAsync(
        ChangePasswordRequest request, CancellationToken cancellationToken = default);
}

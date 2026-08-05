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
/// What an account can do to itself. One route today, and it is the destructive one.
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
}

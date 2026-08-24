namespace GameLauncher.Core.Authentication;

/// <summary>Raised whenever the signed-in account changes, including on sign-out.</summary>
public sealed class SessionChangedEventArgs(AuthSession? session) : EventArgs
{
    /// <summary>Null when the launcher is signed out.</summary>
    public AuthSession? Session { get; } = session;
}

/// <summary>
/// The launcher's view of "who is signed in". Owns the session lifecycle — restore on
/// startup, rotate before expiry, clear on sign-out — so that no caller ever has to reason
/// about token lifetimes.
/// </summary>
public interface IAuthenticationService
{
    /// <summary>Null until a session is restored or established.</summary>
    AuthSession? CurrentSession { get; }

    bool IsAuthenticated { get; }

    event EventHandler<SessionChangedEventArgs>? SessionChanged;

    /// <summary>
    /// Reloads the persisted session on startup and rotates it if the access token has aged
    /// out. Returns false when there is nothing to restore or the refresh token is spent —
    /// both mean the same thing to the caller: show the login view.
    ///
    /// A server that cannot be reached is **not** one of those cases. The stored session is
    /// kept and this returns true, because signing in is no more possible offline than
    /// refreshing is, and the games on the player's disk do not need either.
    /// </summary>
    Task<bool> RestoreAsync(CancellationToken cancellationToken = default);

    Task<AuthSession> SignInAsync(
        string email, string password, CancellationToken cancellationToken = default);

    /// <summary>Registers an account. Does not sign in: the address may need verifying first.</summary>
    Task<Api.RegistrationResult> RegisterAsync(
        string email, string password, string displayName, CancellationToken cancellationToken = default);

    /// <summary>
    /// Asks the server to send the verification link again. Like <see cref="RegisterAsync"/>
    /// this touches no session at all — it rides here rather than on a service of its own
    /// because the sign-in screen is the only caller and the route is on the same tokenless
    /// client (D14), so there is no cycle to compose around as there was for erasure (D47).
    ///
    /// The answer is the same whether or not the address is registered, so a caller must
    /// never present it as confirmation that an account exists.
    /// </summary>
    Task ResendVerificationEmailAsync(string email, CancellationToken cancellationToken = default);

    /// <summary>
    /// Asks the server to send a password-reset link. Same rule about the answer as
    /// <see cref="ResendVerificationEmailAsync"/>; the link is finished in a browser, on a
    /// page the server serves, so nothing comes back here.
    /// </summary>
    Task RequestPasswordResetAsync(string email, CancellationToken cancellationToken = default);

    /// <summary>
    /// Takes over a session somebody else's call produced, persisting it and announcing it as
    /// though it had been signed in for.
    ///
    /// It exists for exactly one caller: the password change, which is a route on the
    /// *authenticated* client and therefore cannot live on this service at all (D47), yet
    /// answers with a whole session because the server has just revoked every other one. Without
    /// this seam the launcher would hold a live session it never stored and never announced —
    /// signed out by succeeding.
    ///
    /// Deliberately narrow: it does not sign anybody in, because the session it takes already
    /// belongs to the account that is signed in.
    /// </summary>
    Task AdoptAsync(AuthSession session, CancellationToken cancellationToken = default);

    /// <summary>
    /// Revokes the session server-side and forgets it locally. The local half happens even if
    /// the server cannot be reached — a user who asks to sign out is signed out.
    /// </summary>
    Task SignOutAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// A currently valid access token, rotating the session first if it is about to expire.
    /// Throws <see cref="Api.ApiException"/> with
    /// <see cref="Api.ApiErrorCode.Unauthenticated"/> when there is no usable session.
    /// </summary>
    Task<string> GetAccessTokenAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Advisory only — it exists so the UI does not offer an action that will fail. The server
    /// re-checks every rule regardless of what the interface already hid (see D8).
    /// </summary>
    bool HasPermission(string permission);
}

/// <summary>Persists the session across restarts.</summary>
public interface ITokenStore
{
    Task<AuthSession?> LoadAsync(CancellationToken cancellationToken = default);

    Task SaveAsync(AuthSession session, CancellationToken cancellationToken = default);

    Task ClearAsync(CancellationToken cancellationToken = default);
}

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
    /// </summary>
    Task<bool> RestoreAsync(CancellationToken cancellationToken = default);

    Task<AuthSession> SignInAsync(
        string email, string password, CancellationToken cancellationToken = default);

    /// <summary>Registers an account. Does not sign in: the address may need verifying first.</summary>
    Task<Api.RegistrationResult> RegisterAsync(
        string email, string password, string displayName, CancellationToken cancellationToken = default);

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

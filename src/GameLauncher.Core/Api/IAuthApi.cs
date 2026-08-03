using GameLauncher.Core.Authentication;

namespace GameLauncher.Core.Api;

/// <summary>What <c>POST /auth/register</c> answers.</summary>
public sealed record RegistrationResult
{
    public AuthenticatedUser User { get; init; } = new();

    /// <summary>When true the account cannot sign in until the address is verified.</summary>
    public bool EmailVerificationRequired { get; init; }

    /// <summary>
    /// Present only against a development server, which has no mail transport yet and returns
    /// the token directly. Never populated by a deployed environment.
    /// </summary>
    public string? DevEmailVerificationToken { get; init; }
}

/// <summary>
/// The unauthenticated half of the API, plus session rotation. Separate from the rest because
/// it is the one client that must not attach a bearer token — refreshing with an expired
/// access token would otherwise fail before the refresh could fix anything.
/// </summary>
public interface IAuthApi
{
    Task<RegistrationResult> RegisterAsync(
        string email, string password, string displayName, CancellationToken cancellationToken = default);

    Task<AuthSession> LoginAsync(
        string email, string password, CancellationToken cancellationToken = default);

    /// <summary>
    /// Rotates the session. The presented token is revoked; replaying it later revokes the
    /// whole family, so a caller must persist the returned one before using it.
    /// </summary>
    Task<AuthSession> RefreshAsync(string refreshToken, CancellationToken cancellationToken = default);

    /// <summary>Revokes one session. Succeeds even for a token the server does not know.</summary>
    Task LogoutAsync(string refreshToken, CancellationToken cancellationToken = default);

    Task VerifyEmailAsync(string token, CancellationToken cancellationToken = default);

    /// <summary>
    /// Reports success whether or not the address exists — the server refuses to be an
    /// account-enumeration oracle, and the client must not present the answer as confirmation.
    /// </summary>
    Task<string?> RequestPasswordResetAsync(string email, CancellationToken cancellationToken = default);

    Task ConfirmPasswordResetAsync(
        string token, string newPassword, CancellationToken cancellationToken = default);
}

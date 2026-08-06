using GameLauncher.Core.Api;
using GameLauncher.Core.Authentication;

namespace GameLauncher.Infrastructure.Api;

/// <summary>
/// The <c>/auth</c> endpoints. Deliberately built on an <see cref="HttpClient"/> that attaches
/// no bearer token: refreshing has to work precisely when the access token has expired, and a
/// handler that tried to obtain one first would recurse into this very call.
/// </summary>
public sealed class AuthApiClient(HttpClient httpClient, TimeProvider timeProvider) : IAuthApi
{
    private readonly ApiTransport _transport = new(httpClient);

    public async Task<RegistrationResult> RegisterAsync(
        string email, string password, string displayName, CancellationToken cancellationToken = default)
    {
        RegistrationPayload payload = await _transport
            .PostAsync<RegistrationPayload>(
                "auth/register",
                new { email, password, displayName },
                cancellationToken)
            .ConfigureAwait(false);

        return new RegistrationResult
        {
            User = payload.User.ToUser(),
            EmailVerificationRequired = payload.EmailVerificationRequired,
            VerificationEmailSent = payload.VerificationEmailSent,
        };
    }

    public Task<AuthSession> LoginAsync(
        string email, string password, CancellationToken cancellationToken = default) =>
        SessionAsync("auth/login", new { email, password }, cancellationToken);

    public Task<AuthSession> RefreshAsync(
        string refreshToken, CancellationToken cancellationToken = default) =>
        SessionAsync("auth/refresh", new { refreshToken }, cancellationToken);

    public Task LogoutAsync(string refreshToken, CancellationToken cancellationToken = default) =>
        _transport.PostAsync("auth/logout", new { refreshToken }, cancellationToken);

    public Task VerifyEmailAsync(string token, CancellationToken cancellationToken = default) =>
        _transport.PostAsync("auth/verify-email", new { token }, cancellationToken);

    public Task ResendVerificationEmailAsync(
        string email, CancellationToken cancellationToken = default) =>
        _transport.PostAsync("auth/verify-email/resend", new { email }, cancellationToken);

    public Task RequestPasswordResetAsync(
        string email, CancellationToken cancellationToken = default) =>
        _transport.PostAsync("auth/password-reset/request", new { email }, cancellationToken);

    public Task ConfirmPasswordResetAsync(
        string token, string newPassword, CancellationToken cancellationToken = default) =>
        _transport.PostAsync(
            "auth/password-reset/confirm", new { token, password = newPassword }, cancellationToken);

    private async Task<AuthSession> SessionAsync(
        string path, object body, CancellationToken cancellationToken)
    {
        SessionPayload payload = await _transport
            .PostAsync<SessionPayload>(path, body, cancellationToken)
            .ConfigureAwait(false);

        return payload.ToSession(timeProvider.GetUtcNow());
    }
}

using GameLauncher.Core.Api;
using Microsoft.Extensions.Logging;

namespace GameLauncher.Core.Authentication;

/// <summary>
/// Owns the session. Pure orchestration over <see cref="IAuthApi"/> and
/// <see cref="ITokenStore"/> — no HTTP and no file system — which is what lets the whole
/// token-rotation policy be unit tested without a server.
/// </summary>
public sealed class AuthenticationService : IAuthenticationService, IDisposable
{
    /// <summary>
    /// How early a token is considered spent. Access tokens live ~15 minutes, so a minute
    /// costs nothing and comfortably covers a slow round trip and a skewed client clock.
    /// </summary>
    public static readonly TimeSpan RefreshMargin = TimeSpan.FromMinutes(1);

    private readonly IAuthApi _api;
    private readonly ITokenStore _tokenStore;
    private readonly ILogger<AuthenticationService> _logger;
    private readonly TimeProvider _time;

    // Serialises refresh. Two views loading at once would otherwise both rotate the session,
    // and the second rotation replays a token the first one already spent — which the server
    // reads as a stolen token and answers by revoking the entire family.
    private readonly SemaphoreSlim _refreshLock = new(1, 1);

    public AuthenticationService(
        IAuthApi api,
        ITokenStore tokenStore,
        ILogger<AuthenticationService> logger,
        TimeProvider? timeProvider = null)
    {
        _api = api;
        _tokenStore = tokenStore;
        _logger = logger;
        _time = timeProvider ?? TimeProvider.System;
    }

    public AuthSession? CurrentSession { get; private set; }

    public bool IsAuthenticated => CurrentSession is not null;

    public event EventHandler<SessionChangedEventArgs>? SessionChanged;

    public async Task<bool> RestoreAsync(CancellationToken cancellationToken = default)
    {
        AuthSession? stored = await _tokenStore.LoadAsync(cancellationToken).ConfigureAwait(false);
        if (stored is null)
        {
            return false;
        }

        if (!stored.NeedsRefresh(_time.GetUtcNow(), RefreshMargin))
        {
            Publish(stored);
            return true;
        }

        try
        {
            await RotateAsync(stored.RefreshToken, cancellationToken).ConfigureAwait(false);
            return true;
        }
        catch (ApiException exception) when (exception.IsTransient)
        {
            // Offline at startup. Nothing has said this session is spent — the question could
            // not even be asked — and a launcher that answered by demanding a password it is
            // equally unable to check would lock a player out of games already on their disk.
            // The session is kept as it stands; the first call that reaches a server rotates it.
            _logger.LogInformation(
                "The server could not be reached ({Code}); continuing with the stored session.",
                exception.Code);

            Publish(stored);
            return true;
        }
        catch (ApiException exception)
        {
            // The refresh token was rejected: it expired, was revoked, or its family was
            // wiped because somebody replayed one. None of that is recoverable here.
            _logger.LogInformation(
                "The stored session could not be restored ({Code}); signing out.", exception.Code);
            await ForgetAsync(cancellationToken).ConfigureAwait(false);
            return false;
        }
    }

    public async Task<AuthSession> SignInAsync(
        string email, string password, CancellationToken cancellationToken = default)
    {
        AuthSession session = await _api
            .LoginAsync(email, password, cancellationToken)
            .ConfigureAwait(false);

        await _tokenStore.SaveAsync(session, cancellationToken).ConfigureAwait(false);
        Publish(session);
        return session;
    }

    public Task<RegistrationResult> RegisterAsync(
        string email, string password, string displayName, CancellationToken cancellationToken = default) =>
        _api.RegisterAsync(email, password, displayName, cancellationToken);

    public Task ResendVerificationEmailAsync(
        string email, CancellationToken cancellationToken = default) =>
        _api.ResendVerificationEmailAsync(email, cancellationToken);

    public Task RequestPasswordResetAsync(
        string email, CancellationToken cancellationToken = default) =>
        _api.RequestPasswordResetAsync(email, cancellationToken);

    public async Task SignOutAsync(CancellationToken cancellationToken = default)
    {
        string? refreshToken = CurrentSession?.RefreshToken;

        // Local state is cleared first and unconditionally. A user who asks to sign out is
        // signed out, whether or not the server is reachable to be told about it.
        await ForgetAsync(cancellationToken).ConfigureAwait(false);

        if (string.IsNullOrEmpty(refreshToken))
        {
            return;
        }

        try
        {
            await _api.LogoutAsync(refreshToken, cancellationToken).ConfigureAwait(false);
        }
        catch (ApiException exception)
        {
            _logger.LogWarning(
                "Could not revoke the session server-side ({Code}); it will expire on its own.",
                exception.Code);
        }
    }

    public async Task<string> GetAccessTokenAsync(CancellationToken cancellationToken = default)
    {
        AuthSession session = CurrentSession
            ?? throw new ApiException(ApiErrorCode.Unauthenticated, "Not signed in.");

        if (!session.NeedsRefresh(_time.GetUtcNow(), RefreshMargin))
        {
            return session.AccessToken;
        }

        await _refreshLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            // Whoever held the lock may already have done the work.
            AuthSession? current = CurrentSession;
            if (current is null)
            {
                throw new ApiException(ApiErrorCode.Unauthenticated, "Not signed in.");
            }

            if (!current.NeedsRefresh(_time.GetUtcNow(), RefreshMargin))
            {
                return current.AccessToken;
            }

            AuthSession rotated = await RotateAsync(current.RefreshToken, cancellationToken)
                .ConfigureAwait(false);
            return rotated.AccessToken;
        }
        catch (ApiException exception) when (!exception.IsTransient)
        {
            await ForgetAsync(cancellationToken).ConfigureAwait(false);
            throw;
        }
        finally
        {
            _refreshLock.Release();
        }
    }

    public bool HasPermission(string permission) =>
        CurrentSession?.HasPermission(permission) ?? false;

    public void Dispose() => _refreshLock.Dispose();

    private async Task<AuthSession> RotateAsync(
        string refreshToken, CancellationToken cancellationToken)
    {
        AuthSession session = await _api
            .RefreshAsync(refreshToken, cancellationToken)
            .ConfigureAwait(false);

        // Persisted before it is published: a crash between the two would otherwise leave the
        // spent token on disk, and presenting it again revokes the whole family.
        await _tokenStore.SaveAsync(session, cancellationToken).ConfigureAwait(false);
        Publish(session);
        return session;
    }

    private async Task ForgetAsync(CancellationToken cancellationToken)
    {
        await _tokenStore.ClearAsync(cancellationToken).ConfigureAwait(false);
        Publish(null);
    }

    private void Publish(AuthSession? session)
    {
        CurrentSession = session;
        SessionChanged?.Invoke(this, new SessionChangedEventArgs(session));
    }
}

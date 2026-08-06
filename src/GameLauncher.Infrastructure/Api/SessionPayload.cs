using GameLauncher.Core.Authentication;

namespace GameLauncher.Infrastructure.Api;

/// <summary>
/// The <c>/auth</c> response bodies, exactly as they arrive. They are kept apart from the
/// Core models because the wire speaks in <c>expiresIn</c> seconds while everything above
/// wants an instant, and because a development-only field has no business in a domain type.
/// </summary>
internal sealed record SessionPayload
{
    public string AccessToken { get; init; } = string.Empty;

    public string RefreshToken { get; init; } = string.Empty;

    public string TokenType { get; init; } = "Bearer";

    /// <summary>Lifetime in seconds, counted from when the server answered.</summary>
    public int ExpiresIn { get; init; }

    public UserPayload User { get; init; } = new();

    public IReadOnlyList<string> Permissions { get; init; } = [];

    /// <summary>
    /// <paramref name="receivedAt"/> is the client's clock, not the server's: the expiry is
    /// only ever compared against the same clock, so skew between the two cannot make a token
    /// look valid for longer than it is.
    /// </summary>
    public AuthSession ToSession(DateTimeOffset receivedAt) => new()
    {
        AccessToken = AccessToken,
        RefreshToken = RefreshToken,
        AccessTokenExpiresAt = receivedAt.AddSeconds(ExpiresIn),
        User = User.ToUser(),
        Permissions = Permissions,
    };
}

internal sealed record UserPayload
{
    public string Id { get; init; } = string.Empty;

    public string Email { get; init; } = string.Empty;

    public string DisplayName { get; init; } = string.Empty;

    public bool EmailVerified { get; init; }

    public long UploadQuotaBytes { get; init; }

    public long UploadUsedBytes { get; init; }

    public AuthenticatedUser ToUser() => new()
    {
        Id = Id,
        Email = Email,
        DisplayName = DisplayName,
        EmailVerified = EmailVerified,
        UploadQuotaBytes = UploadQuotaBytes,
        UploadUsedBytes = UploadUsedBytes,
    };
}

internal sealed record RegistrationPayload
{
    public UserPayload User { get; init; } = new();

    public bool EmailVerificationRequired { get; init; }

    public bool VerificationEmailSent { get; init; }
}

/// <summary>The endpoints whose answer is a bare acknowledgement.</summary>
internal sealed record StatusPayload
{
    public string Status { get; init; } = string.Empty;
}

namespace GameLauncher.Core.Authentication;

/// <summary>The account the launcher is signed in as.</summary>
public sealed record AuthenticatedUser
{
    public string Id { get; init; } = string.Empty;

    public string Email { get; init; } = string.Empty;

    public string DisplayName { get; init; } = string.Empty;

    public bool EmailVerified { get; init; }

    public long UploadQuotaBytes { get; init; }

    public long UploadUsedBytes { get; init; }
}

/// <summary>
/// A live session. The server hands out <c>expiresIn</c> seconds; this holds the absolute
/// instant instead, because the session is written to disk and a relative lifetime means
/// nothing after a restart.
/// </summary>
public sealed record AuthSession
{
    public string AccessToken { get; init; } = string.Empty;

    public string RefreshToken { get; init; } = string.Empty;

    public DateTimeOffset AccessTokenExpiresAt { get; init; }

    public AuthenticatedUser User { get; init; } = new();

    /// <summary>
    /// Flattened permissions, as carried by the access token. Used only to decide what the UI
    /// offers: every one of them is enforced again server-side (see D8).
    /// </summary>
    public IReadOnlyList<string> Permissions { get; init; } = [];

    /// <summary>
    /// Whether the access token should be rotated before being used. The margin covers the
    /// round trip and any clock skew, so a token never expires between the check and the call.
    /// </summary>
    public bool NeedsRefresh(DateTimeOffset now, TimeSpan margin) =>
        AccessTokenExpiresAt - margin <= now;

    public bool HasPermission(string permission) =>
        Permissions.Contains(permission, StringComparer.Ordinal);
}

/// <summary>
/// Permission names the client checks. Kept as constants rather than an enum because the
/// server's list is table-driven and can grow without a client release.
/// </summary>
public static class Permissions
{
    public const string LibraryRead = "library.read";
    public const string LibraryManage = "library.manage";
    public const string GameRead = "game.read";
    public const string GameDownload = "game.download";
    public const string GamePublish = "game.publish";
    public const string BuildUpload = "build.upload";
}

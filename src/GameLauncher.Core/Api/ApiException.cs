namespace GameLauncher.Core.Api;

/// <summary>
/// The <c>code</c> of the server's error envelope, plus the two failures that never reach the
/// server at all. Callers switch on this instead of on a status number, because the mapping
/// from status to meaning is the server's to define — see the backend's <c>common/Error.h</c>.
/// </summary>
public enum ApiErrorCode
{
    /// <summary>The request never completed: no route to the host, DNS, TLS, timeout.</summary>
    Network,

    /// <summary>A response arrived that this client cannot make sense of.</summary>
    Unknown,

    InvalidInput,
    Unauthenticated,
    Forbidden,
    NotFound,
    Conflict,
    QuotaExceeded,
    RateLimited,
    DependencyFailure,
    Internal,
}

/// <summary>
/// Every failure of an API call, in one type. The API client is the only place that knows the
/// RFC 7807 envelope exists; everything above it sees this exception, and a single handler
/// turns it into a localized message. View models never build error strings from a status code.
/// </summary>
public sealed class ApiException : Exception
{
    public ApiException(
        ApiErrorCode code,
        string message,
        int? statusCode = null,
        string? requestId = null,
        TimeSpan? retryAfter = null,
        Exception? innerException = null)
        : base(message, innerException)
    {
        Code = code;
        StatusCode = statusCode;
        RequestId = requestId;
        RetryAfter = retryAfter;
    }

    public ApiErrorCode Code { get; }

    /// <summary>Null when the request never produced a response.</summary>
    public int? StatusCode { get; }

    /// <summary>
    /// The server's <c>X-Request-Id</c>. Showing it to the user is what makes a support
    /// report actionable: it is the one string that finds the request in the server's logs.
    /// </summary>
    public string? RequestId { get; }

    /// <summary>Set on <see cref="ApiErrorCode.RateLimited"/>, from <c>Retry-After</c>.</summary>
    public TimeSpan? RetryAfter { get; }

    /// <summary>
    /// Whether trying the same call again could plausibly succeed without the user changing
    /// anything. A validation failure never will; a timeout might.
    /// </summary>
    public bool IsTransient => Code is ApiErrorCode.Network
        or ApiErrorCode.RateLimited
        or ApiErrorCode.DependencyFailure
        or ApiErrorCode.Internal;
}

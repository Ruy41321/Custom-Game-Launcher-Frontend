namespace GameLauncher.Core.Api;

/// <summary>
/// The server's RFC 7807-style error body. Every field is optional on the wire: a proxy or a
/// crash can produce a non-JSON error response, and the client still has to say something
/// useful when it does.
/// </summary>
public sealed record ApiProblem
{
    public string? Type { get; init; }

    public string? Title { get; init; }

    public int? Status { get; init; }

    /// <summary>The machine-readable name, e.g. <c>not_found</c>. Maps to <see cref="ApiErrorCode"/>.</summary>
    public string? Code { get; init; }

    public string? Detail { get; init; }

    public string? RequestId { get; init; }

    /// <summary>
    /// Falls back to the HTTP status when the body carried no <c>code</c>, which is what a
    /// response from something other than the API looks like.
    /// </summary>
    public ApiErrorCode ToErrorCode(int httpStatus) => Code switch
    {
        "invalid_input" => ApiErrorCode.InvalidInput,
        "unauthenticated" => ApiErrorCode.Unauthenticated,
        "forbidden" => ApiErrorCode.Forbidden,
        "not_found" => ApiErrorCode.NotFound,
        "conflict" => ApiErrorCode.Conflict,
        "quota_exceeded" => ApiErrorCode.QuotaExceeded,
        "rate_limited" => ApiErrorCode.RateLimited,
        "dependency_failure" => ApiErrorCode.DependencyFailure,
        "internal" => ApiErrorCode.Internal,
        _ => FromHttpStatus(httpStatus),
    };

    private static ApiErrorCode FromHttpStatus(int httpStatus) => httpStatus switch
    {
        401 => ApiErrorCode.Unauthenticated,
        403 => ApiErrorCode.Forbidden,
        404 => ApiErrorCode.NotFound,
        409 => ApiErrorCode.Conflict,
        // Only the file server answers this, for a URL whose signature verifies but whose
        // expiry has passed. A tampered expiry breaks the signature and is a 403 instead.
        410 => ApiErrorCode.LinkExpired,
        413 => ApiErrorCode.QuotaExceeded,
        422 => ApiErrorCode.InvalidInput,
        429 => ApiErrorCode.RateLimited,
        503 => ApiErrorCode.DependencyFailure,
        >= 500 => ApiErrorCode.Internal,
        _ => ApiErrorCode.Unknown,
    };
}

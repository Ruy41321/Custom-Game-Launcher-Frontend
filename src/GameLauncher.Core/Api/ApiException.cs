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

    /// <summary>
    /// A response arrived intact as far as HTTP is concerned, but its bytes are not the ones
    /// their content address names. Client-side like <see cref="Network"/>: no server says
    /// this, because a server that knew would not have sent it.
    /// </summary>
    Integrity,

    /// <summary>
    /// A signed download URL outlived its expiry. The file server reports it apart from a bad
    /// signature on purpose, and the client acts on the difference: this one is fixed by
    /// asking for a fresh plan, and nothing about the account or the build has changed.
    /// </summary>
    LinkExpired,

    InvalidInput,
    Unauthenticated,
    Forbidden,

    /// <summary>
    /// The session is valid and the account is holding a password somebody else chose for it.
    /// A 403 like <see cref="Forbidden"/>, and its own code because it is the one refusal with
    /// a single thing to do about it: every route but the password change answers this until
    /// the password is replaced. Told apart from a plain refusal by the category rather than
    /// by its prose, which is what lets the shell send somebody to the screen that fixes it.
    /// </summary>
    PasswordChangeRequired,
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

    /// <summary>
    /// The server's <c>rule</c>: which rule refused, where <see cref="Code"/> says only what
    /// kind of refusal it was. Null whenever the server named none, which every server does
    /// for most failures and an older one does for all of them. Only validation failures carry
    /// one today, and <see cref="ApiErrorPresenter"/> only consults it for those.
    /// </summary>
    public string? Rule { get; init; }

    /// <summary>The rule's arguments, in order — usually the limit that was exceeded.</summary>
    public IReadOnlyList<string> RuleArgs { get; init; } = [];

    /// <summary>Null when the request never produced a response.</summary>
    public int? StatusCode { get; }

    /// <summary>
    /// The server's <c>X-Request-Id</c>: the one string that finds the request in the server's
    /// logs. It is written to this launcher's log and never shown to the user (D67).
    /// </summary>
    public string? RequestId { get; }

    /// <summary>Set on <see cref="ApiErrorCode.RateLimited"/>, from <c>Retry-After</c>.</summary>
    public TimeSpan? RetryAfter { get; }

    /// <summary>
    /// Whether trying the same call again could plausibly succeed without the user changing
    /// anything. A validation failure never will; a timeout might.
    /// </summary>
    public bool IsTransient => Code is ApiErrorCode.Network
        or ApiErrorCode.Integrity
        or ApiErrorCode.LinkExpired
        or ApiErrorCode.RateLimited
        or ApiErrorCode.DependencyFailure
        or ApiErrorCode.Internal;
}

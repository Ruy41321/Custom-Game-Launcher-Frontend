namespace GameLauncher.Core.Api;

/// <summary>
/// <c>GET /api/v1/launcher/releases/latest</c>, which takes <b>no token</b> — and not for
/// convenience: the launcher that most needs an update is the one that cannot sign in. Pointed
/// at a server it has never reached, holding an address nobody confirmed, or carrying the very
/// bug the update fixes. A route behind a token would be missing exactly the installations an
/// update mechanism exists for, so this rides on the tokenless client beside <c>/auth</c>,
/// <c>/capabilities</c> and the crash reports.
/// </summary>
public interface ILauncherReleaseApi
{
    /// <summary>
    /// All three are sent: the server refuses a request that leaves platform or architecture
    /// out, because a default for either would be a guess about the program that is going to
    /// replace somebody's own.
    /// </summary>
    /// <exception cref="ApiException">
    /// Including <see cref="ApiErrorCode.NotFound"/>, which means one of three things — no
    /// signing key configured, nothing published, nothing published for this platform. From
    /// here they are the same situation: there is nothing to update to.
    /// </exception>
    Task<LauncherReleaseResponse> GetLatestAsync(
        string channel, string platform, string arch, CancellationToken cancellationToken = default);
}

/// <summary>
/// What the route answers. <see cref="Document"/> is the <b>exact text the signature covers</b>,
/// carried as an opaque string rather than as a nested object: re-serialising it would hand the
/// client something it could not check, and rebuilding a canonical form here would put a second
/// definition of a wire contract in a second language.
/// </summary>
public sealed record LauncherReleaseResponse
{
    public string Document { get; init; } = string.Empty;

    /// <summary>base64 of a DER ECDSA P-256/SHA-256 signature.</summary>
    public string Signature { get; init; } = string.Empty;

    /// <summary>
    /// Where the artifact is served, unsigned and public. A convenience, not something to
    /// trust: what makes the download safe is the content address inside the signed document.
    /// </summary>
    public string Url { get; init; } = string.Empty;
}

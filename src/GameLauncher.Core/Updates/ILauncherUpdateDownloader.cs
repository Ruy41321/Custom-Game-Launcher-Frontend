namespace GameLauncher.Core.Updates;

/// <summary>
/// Fetches the artifact a verified release document names, and refuses bytes that do not hash
/// to the content address inside it.
///
/// That refusal is the third of the server's five rules, and it is what makes the URL safe to
/// follow at all: <c>url</c> is a convenience, not something to trust. An attacker who could
/// rewrite it entirely would still have to produce bytes hashing to a value somebody signed.
/// </summary>
public interface ILauncherUpdateDownloader
{
    /// <summary>
    /// Returns the path of the verified archive. Nothing reaches that path until its bytes hash
    /// to <see cref="ReleaseDocument.Sha256"/> — the same invariant the blob fetcher holds, for
    /// the same reason.
    /// </summary>
    /// <param name="transferred">Bytes on disk so far, reported as a running total.</param>
    /// <exception cref="Api.ApiException">
    /// <see cref="Api.ApiErrorCode.Integrity"/> when the bytes are not the ones the signed
    /// document named, <see cref="Api.ApiErrorCode.Network"/> when the transfer did not finish.
    /// </exception>
    Task<string> DownloadAsync(
        ReleaseDocument release,
        string url,
        IProgress<long>? transferred = null,
        CancellationToken cancellationToken = default);
}

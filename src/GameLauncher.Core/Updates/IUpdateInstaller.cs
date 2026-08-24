namespace GameLauncher.Core.Updates;

/// <summary>
/// Everything between a verified archive on disk and a running updater: unpack it, put the
/// helper somewhere it will survive the directory it is about to replace, and start it.
///
/// The launcher does the unpacking rather than the updater, and that is a decision rather than
/// a convenience. The updater is the only thing running when nothing can fix anything any more,
/// so it stays small; and the rules that make unpacking safe — <see cref="UpdateArchiveRules"/>,
/// which is <c>ManifestPathRules</c> and <c>PathSafety</c> under another name — already live
/// here and are already applied to every file of every build.
/// </summary>
public interface IUpdateInstaller
{
    /// <summary>
    /// Prepares the swap and starts the helper. When this returns, the caller's remaining job
    /// is to <b>exit</b>: the helper is waiting for exactly that.
    /// </summary>
    /// <exception cref="Api.ApiException">
    /// <see cref="Api.ApiErrorCode.Integrity"/> for an archive that names a file outside the
    /// directory it is unpacked into, before anything is written.
    /// </exception>
    /// <returns>The helper's process id, for the log line that says the swap began.</returns>
    Task<int> StartAsync(
        ReleaseDocument release,
        string archivePath,
        CancellationToken cancellationToken = default);
}

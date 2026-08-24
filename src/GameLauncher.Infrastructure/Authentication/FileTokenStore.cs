using System.Runtime.InteropServices;
using System.Text.Json;
using GameLauncher.Core.Authentication;
using GameLauncher.Core.Platform;
using GameLauncher.Infrastructure.Configuration;
using Microsoft.Extensions.Logging;

namespace GameLauncher.Infrastructure.Authentication;

/// <summary>
/// Keeps the session under the user's own data directory so the launcher opens signed in.
///
/// The refresh token is a bearer credential and is stored in clear. The alternatives all cost
/// more than they buy here: DPAPI is Windows-only, and a keyring means a libsecret dependency
/// that is absent on a headless or minimal Linux install — either way a fork would have to
/// solve the other two platforms itself. What the file gets instead is the strongest
/// protection available on every platform: it lives in a per-user directory, and on Unix its
/// permissions are narrowed to the owner. The exposure is bounded by design — a stolen token
/// is revoked by signing out, and replaying one after the real client has rotated it revokes
/// the entire family.
/// </summary>
public sealed class FileTokenStore : ITokenStore, IDisposable
{
    public const string FileName = "session.json";

    private readonly string _filePath;
    private readonly ILogger<FileTokenStore> _logger;
    private readonly SemaphoreSlim _writeLock = new(1, 1);

    public FileTokenStore(IPathProvider pathProvider, ILogger<FileTokenStore> logger)
        : this(Path.Combine(pathProvider.UserDataDirectory, FileName), logger)
    {
    }

    public FileTokenStore(string filePath, ILogger<FileTokenStore> logger)
    {
        _filePath = filePath;
        _logger = logger;
    }

    public async Task<AuthSession?> LoadAsync(CancellationToken cancellationToken = default)
    {
        if (!File.Exists(_filePath))
        {
            return null;
        }

        try
        {
            await using FileStream stream = File.OpenRead(_filePath);
            AuthSession? session = await JsonSerializer
                .DeserializeAsync<AuthSession>(
                    stream, LauncherJsonSerializer.Options, cancellationToken)
                .ConfigureAwait(false);

            // A file with no refresh token cannot produce a session, and pretending otherwise
            // only moves the failure to the first request that needs one.
            return string.IsNullOrEmpty(session?.RefreshToken) ? null : session;
        }
        catch (Exception exception) when (exception is JsonException or IOException)
        {
            _logger.LogWarning(
                exception, "Could not read the stored session; signing in again is required.");
            return null;
        }
    }

    public async Task SaveAsync(AuthSession session, CancellationToken cancellationToken = default)
    {
        await _writeLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            string? directory = Path.GetDirectoryName(_filePath);
            if (!string.IsNullOrEmpty(directory))
            {
                Directory.CreateDirectory(directory);
            }

            // Written to a temporary file and moved into place, so an interrupted write cannot
            // leave a half-session that reads as a corrupt file on the next start.
            string temporaryPath = _filePath + ".tmp";
            await using (FileStream stream = File.Create(temporaryPath))
            {
                RestrictToOwner(stream);
                await JsonSerializer
                    .SerializeAsync(stream, session, LauncherJsonSerializer.Options, cancellationToken)
                    .ConfigureAwait(false);
            }

            File.Move(temporaryPath, _filePath, overwrite: true);
        }
        finally
        {
            _writeLock.Release();
        }
    }

    public async Task ClearAsync(CancellationToken cancellationToken = default)
    {
        await _writeLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            File.Delete(_filePath);
        }
        catch (IOException exception)
        {
            // Signing out must not fail because a file is locked; the session is already gone
            // from memory and the server has been told to revoke it.
            _logger.LogWarning(exception, "Could not delete the stored session.");
        }
        finally
        {
            _writeLock.Release();
        }
    }

    public void Dispose() => _writeLock.Dispose();

    /// <summary>
    /// Applied to the temporary file before anything is written to it, so the token is never
    /// briefly world-readable. A no-op on Windows, where the per-user directory's ACL already
    /// says the same thing.
    /// </summary>
    private static void RestrictToOwner(FileStream stream)
    {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            return;
        }

        File.SetUnixFileMode(
            stream.SafeFileHandle, UnixFileMode.UserRead | UnixFileMode.UserWrite);
    }
}

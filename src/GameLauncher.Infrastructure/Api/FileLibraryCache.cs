using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using GameLauncher.Core.Api;
using GameLauncher.Core.Models;
using GameLauncher.Core.Platform;
using GameLauncher.Infrastructure.Configuration;
using Microsoft.Extensions.Logging;

namespace GameLauncher.Infrastructure.Api;

/// <summary>
/// The library cache, as one JSON document per account under the user's data directory.
///
/// A file rather than a table in the install database on purpose: the install store records
/// what this machine has done, survives a reinstall and is migrated when its schema moves,
/// while this is a copy of something the server already knows and can be deleted at any
/// moment with no consequence beyond one empty page while offline.
///
/// The file is named after a hash of the account id rather than the id itself. Nothing here
/// is secret, but a directory listing that names every account that ever signed in on a
/// shared machine is a small thing to give away for no benefit at all.
/// </summary>
public sealed class FileLibraryCache : ILibraryCache, IDisposable
{
    /// <summary>The directory under the user's data directory. One file per account.</summary>
    public const string DirectoryName = "library";

    private readonly string _directory;
    private readonly ILogger<FileLibraryCache> _logger;
    private readonly SemaphoreSlim _writeLock = new(1, 1);

    public FileLibraryCache(IPathProvider pathProvider, ILogger<FileLibraryCache> logger)
        : this(Path.Combine(pathProvider.UserDataDirectory, DirectoryName), logger)
    {
    }

    public FileLibraryCache(string directory, ILogger<FileLibraryCache> logger)
    {
        _directory = directory;
        _logger = logger;
    }

    public async Task<IReadOnlyList<Game>> ReadAsync(
        string accountId, CancellationToken cancellationToken = default)
    {
        if (PathFor(accountId) is not { } path || !File.Exists(path))
        {
            return [];
        }

        try
        {
            await using FileStream stream = File.OpenRead(path);
            List<Game>? games = await JsonSerializer
                .DeserializeAsync<List<Game>>(
                    stream, LauncherJsonSerializer.Options, cancellationToken)
                .ConfigureAwait(false);

            return games ?? [];
        }
        catch (Exception exception) when (exception is JsonException or IOException
            or UnauthorizedAccessException)
        {
            // A cache that cannot be read is a cache miss. It is replaced by the next
            // successful load, and until then the page falls back to what is installed.
            _logger.LogDebug(exception, "The stored library could not be read.");
            return [];
        }
    }

    public async Task WriteAsync(
        string accountId,
        IReadOnlyList<Game> games,
        CancellationToken cancellationToken = default)
    {
        if (PathFor(accountId) is not { } path)
        {
            return;
        }

        await _writeLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            Directory.CreateDirectory(_directory);

            // Written beside the real file and moved over it, so a launcher killed mid-write
            // leaves the previous answer rather than half of the new one.
            string temporaryPath = path + ".tmp";
            await using (FileStream stream = File.Create(temporaryPath))
            {
                await JsonSerializer
                    .SerializeAsync(stream, games, LauncherJsonSerializer.Options, cancellationToken)
                    .ConfigureAwait(false);
            }

            File.Move(temporaryPath, path, overwrite: true);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // Deliberately swallowed. The library is on screen either way; all that is lost is
            // how good the next offline start looks.
            _logger.LogDebug(exception, "The library could not be stored for offline use.");
        }
        finally
        {
            _writeLock.Release();
        }
    }

    public async Task ClearAsync(string accountId, CancellationToken cancellationToken = default)
    {
        if (PathFor(accountId) is not { } path)
        {
            return;
        }

        await _writeLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            File.Delete(path);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            _logger.LogDebug(exception, "The stored library could not be deleted.");
        }
        finally
        {
            _writeLock.Release();
        }
    }

    public void Dispose() => _writeLock.Dispose();

    /// <summary>
    /// Null for an account with no id, which is the signed-out launcher: there is no list to
    /// keep and nowhere to keep it.
    /// </summary>
    private string? PathFor(string accountId)
    {
        if (string.IsNullOrEmpty(accountId))
        {
            return null;
        }

        byte[] digest = SHA256.HashData(Encoding.UTF8.GetBytes(accountId));
        return Path.Combine(_directory, Convert.ToHexStringLower(digest)[..32] + ".json");
    }
}

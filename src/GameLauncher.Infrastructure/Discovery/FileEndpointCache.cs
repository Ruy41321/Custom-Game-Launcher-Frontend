using GameLauncher.Core.Discovery;
using GameLauncher.Core.Platform;
using Microsoft.Extensions.Logging;

namespace GameLauncher.Infrastructure.Discovery;

/// <summary>
/// The last verified envelope, as one file per service under the user's data directory.
///
/// A file rather than a row in the install database, for <see cref="Api.FileLibraryCache"/>'s
/// reason: this is a copy of something a server can send again, and deleting it costs one
/// lookup at the next start. It stores the envelope <b>as it arrived</b>, signature and all, so
/// that reading it is the same check as receiving it.
/// </summary>
public sealed class FileEndpointCache : IEndpointCache, IDisposable
{
    /// <summary>The directory under the user's data directory. One file per service.</summary>
    public const string DirectoryName = "registry";

    private readonly string _directory;
    private readonly ILogger<FileEndpointCache> _logger;
    private readonly SemaphoreSlim _writeLock = new(1, 1);

    public FileEndpointCache(IPathProvider pathProvider, ILogger<FileEndpointCache> logger)
        : this(Path.Combine(pathProvider.UserDataDirectory, DirectoryName), logger)
    {
    }

    public FileEndpointCache(string directory, ILogger<FileEndpointCache> logger)
    {
        _directory = directory;
        _logger = logger;
    }

    public async Task<string?> ReadAsync(
        string serviceKey, string environment, CancellationToken cancellationToken = default)
    {
        if (PathFor(serviceKey, environment) is not { } path || !File.Exists(path))
        {
            return null;
        }

        try
        {
            return await File.ReadAllTextAsync(path, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // A cache that cannot be read is a cache miss; the registry is asked instead.
            _logger.LogDebug(exception, "The stored endpoint could not be read.");
            return null;
        }
    }

    public async Task WriteAsync(
        string serviceKey,
        string environment,
        string envelope,
        CancellationToken cancellationToken = default)
    {
        if (PathFor(serviceKey, environment) is not { } path)
        {
            return;
        }

        await _writeLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            Directory.CreateDirectory(_directory);
            await File.WriteAllTextAsync(path, envelope, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // Costs one lookup at the next start, and nothing else.
            _logger.LogDebug(exception, "The resolved endpoint could not be stored.");
        }
        finally
        {
            _writeLock.Release();
        }
    }

    public void Dispose() => _writeLock.Dispose();

    /// <summary>
    /// Null for a key or an environment that could name something outside the directory. The
    /// values come from a configuration file rather than from a server, and this still costs a
    /// comparison: a path built from input is a path worth checking.
    /// </summary>
    private string? PathFor(string serviceKey, string environment)
    {
        if (string.IsNullOrWhiteSpace(serviceKey) || string.IsNullOrWhiteSpace(environment))
        {
            return null;
        }

        string name = $"{serviceKey}.{environment}.json";
        if (name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
        {
            _logger.LogDebug("The service key or environment cannot name a file; nothing is cached.");
            return null;
        }

        return Path.Combine(_directory, name);
    }
}

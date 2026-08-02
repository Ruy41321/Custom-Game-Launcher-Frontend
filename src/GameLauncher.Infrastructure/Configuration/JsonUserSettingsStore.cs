using System.Text.Json;
using GameLauncher.Core.Configuration;
using GameLauncher.Core.Platform;
using Microsoft.Extensions.Logging;

namespace GameLauncher.Infrastructure.Configuration;

/// <summary>
/// Stores user preferences as JSON under the platform's app-data directory.
/// Writes go to a temporary file that is then moved into place, so a crash mid-write cannot
/// leave the user with a truncated settings file and a launcher that will not start.
/// </summary>
public sealed class JsonUserSettingsStore : IUserSettingsStore, IDisposable
{
    public const string FileName = "launcher.settings.json";

    private readonly string _filePath;
    private readonly ILogger<JsonUserSettingsStore> _logger;
    private readonly SemaphoreSlim _writeLock = new(1, 1);

    public JsonUserSettingsStore(IPathProvider pathProvider, ILogger<JsonUserSettingsStore> logger)
        : this(Path.Combine(pathProvider.UserDataDirectory, FileName), logger)
    {
    }

    public JsonUserSettingsStore(string filePath, ILogger<JsonUserSettingsStore> logger)
    {
        _filePath = filePath;
        _logger = logger;
    }

    public async Task<UserSettings> LoadAsync(CancellationToken cancellationToken = default)
    {
        if (!File.Exists(_filePath))
        {
            return new UserSettings();
        }

        try
        {
            await using FileStream stream = File.OpenRead(_filePath);
            return await JsonSerializer
                       .DeserializeAsync<UserSettings>(
                           stream, LauncherJsonSerializer.Options, cancellationToken)
                       .ConfigureAwait(false)
                   ?? new UserSettings();
        }
        catch (Exception exception) when (exception is JsonException or IOException)
        {
            // Preferences are not worth failing startup over: fall back to defaults and say so.
            _logger.LogWarning(
                exception, "Could not read {Path}; falling back to default settings.", _filePath);
            return new UserSettings();
        }
    }

    public async Task SaveAsync(UserSettings settings, CancellationToken cancellationToken = default)
    {
        await _writeLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            string? directory = Path.GetDirectoryName(_filePath);
            if (!string.IsNullOrEmpty(directory))
            {
                Directory.CreateDirectory(directory);
            }

            string temporaryPath = _filePath + ".tmp";
            await using (FileStream stream = File.Create(temporaryPath))
            {
                await JsonSerializer
                    .SerializeAsync(stream, settings, LauncherJsonSerializer.Options, cancellationToken)
                    .ConfigureAwait(false);
            }

            File.Move(temporaryPath, _filePath, overwrite: true);
        }
        finally
        {
            _writeLock.Release();
        }
    }

    public void Dispose() => _writeLock.Dispose();
}

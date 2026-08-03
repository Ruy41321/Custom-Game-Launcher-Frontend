using System.Text.Json;
using GameLauncher.Core.Configuration;
using GameLauncher.Core.Platform;
using Microsoft.Extensions.Logging;

namespace GameLauncher.Infrastructure.Configuration;

/// <summary>Thrown when <c>launcher.config.json</c> exists but cannot be used.</summary>
public sealed class LauncherConfigurationException : Exception
{
    public LauncherConfigurationException(string message) : base(message)
    {
    }

    public LauncherConfigurationException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}

public sealed class LauncherConfigurationProvider : ILauncherConfigurationProvider
{
    public const string FileName = "launcher.config.json";

    private readonly string _filePath;
    private readonly ILogger<LauncherConfigurationProvider> _logger;

    public LauncherConfigurationProvider(
        IPathProvider pathProvider,
        ILogger<LauncherConfigurationProvider> logger)
        : this(Path.Combine(pathProvider.ApplicationDirectory, FileName), logger)
    {
    }

    public LauncherConfigurationProvider(
        string filePath,
        ILogger<LauncherConfigurationProvider> logger)
    {
        _filePath = filePath;
        _logger = logger;
    }

    public async Task<LauncherConfiguration> LoadAsync(
        CancellationToken cancellationToken = default)
    {
        if (!File.Exists(_filePath))
        {
            // The guard is what CA1873 asks for: the analyzer cannot tell that these
            // arguments are cheap, only that they are boxed into a params array.
            if (_logger.IsEnabled(LogLevel.Information))
            {
                _logger.LogInformation(
                    "No {FileName} at {Path}; using built-in defaults.", FileName, _filePath);
            }

            return new LauncherConfiguration();
        }

        LauncherConfiguration? configuration;
        try
        {
            await using FileStream stream = File.OpenRead(_filePath);
            configuration = await JsonSerializer
                .DeserializeAsync<LauncherConfiguration>(
                    stream, LauncherJsonSerializer.Options, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (JsonException exception)
        {
            throw new LauncherConfigurationException(
                $"{_filePath} is not valid JSON: {exception.Message}", exception);
        }

        if (configuration is null)
        {
            throw new LauncherConfigurationException($"{_filePath} is empty.");
        }

        // Starting with half-applied branding and a bad endpoint is worse than not starting.
        IReadOnlyList<string> problems = configuration.Validate();
        if (problems.Count > 0)
        {
            throw new LauncherConfigurationException(
                $"{_filePath} is invalid:{Environment.NewLine}- " +
                string.Join($"{Environment.NewLine}- ", problems));
        }

        return configuration;
    }
}

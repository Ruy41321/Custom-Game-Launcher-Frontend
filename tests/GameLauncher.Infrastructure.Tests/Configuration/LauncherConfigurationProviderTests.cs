using GameLauncher.Core.Configuration;
using GameLauncher.Infrastructure.Configuration;
using Microsoft.Extensions.Logging.Abstractions;

namespace GameLauncher.Infrastructure.Tests.Configuration;

public sealed class LauncherConfigurationProviderTests
{
    private static LauncherConfigurationProvider ProviderFor(string path) =>
        new(path, NullLogger<LauncherConfigurationProvider>.Instance);

    // A fork that ships without the file should still start, on the built-in defaults.
    [Fact]
    public async Task AMissingFileYieldsTheDefaults()
    {
        using var directory = new TemporaryDirectory();

        LauncherConfiguration configuration =
            await ProviderFor(directory.File("launcher.config.json")).LoadAsync(TestContext.Current.CancellationToken);

        Assert.Equal(new LauncherConfiguration().AppName, configuration.AppName);
    }

    [Fact]
    public async Task ReadsAFullDocument()
    {
        using var directory = new TemporaryDirectory();
        string path = directory.WriteFile(
            "launcher.config.json",
            """
            {
              "appName": "Indie Hub",
              "apiBaseUrl": "https://games.example.com/api/v1/",
              "theme": { "variant": "light", "accentColor": "#FF8800" },
              "localization": { "defaultLanguage": "it", "supportedLanguages": ["en", "it", "fr"] },
              "updates": { "channel": "beta" },
              "defaultInstallDirectory": "D:/Games"
            }
            """);

        LauncherConfiguration configuration =
            await ProviderFor(path).LoadAsync(TestContext.Current.CancellationToken);

        Assert.Equal("Indie Hub", configuration.AppName);
        Assert.Equal("https://games.example.com/api/v1/", configuration.ApiBaseUrl);
        Assert.Equal("light", configuration.Theme.Variant);
        Assert.Equal("#FF8800", configuration.Theme.AccentColor);
        Assert.Equal("it", configuration.Localization.DefaultLanguage);

        // Which release stream a launcher follows is the packager's choice, not the player's,
        // so it lives in the shipped file rather than in the user's settings.
        Assert.Equal("beta", configuration.Updates.Channel);
        Assert.Equal("D:/Games", configuration.DefaultInstallDirectory);
    }

    [Fact]
    public async Task OmittedSectionsFallBackToTheirDefaults()
    {
        using var directory = new TemporaryDirectory();
        string path = directory.WriteFile("launcher.config.json", """{ "appName": "Minimal" }""");

        LauncherConfiguration configuration =
            await ProviderFor(path).LoadAsync(TestContext.Current.CancellationToken);

        Assert.Equal("Minimal", configuration.AppName);
        Assert.Equal("dark", configuration.Theme.Variant);
        Assert.Equal(["en", "it", "fr"], configuration.Localization.SupportedLanguages);
        Assert.Equal("stable", configuration.Updates.Channel);
    }

    [Fact]
    public async Task PropertyNamesAreCaseInsensitive()
    {
        using var directory = new TemporaryDirectory();
        string path = directory.WriteFile("launcher.config.json", """{ "AppName": "Pascal Case" }""");

        LauncherConfiguration configuration =
            await ProviderFor(path).LoadAsync(TestContext.Current.CancellationToken);

        Assert.Equal("Pascal Case", configuration.AppName);
    }

    [Fact]
    public async Task CommentsAndTrailingCommasAreTolerated()
    {
        using var directory = new TemporaryDirectory();
        string path = directory.WriteFile(
            "launcher.config.json",
            """
            {
              // Branding for this fork.
              "appName": "Commented",
            }
            """);

        LauncherConfiguration configuration =
            await ProviderFor(path).LoadAsync(TestContext.Current.CancellationToken);

        Assert.Equal("Commented", configuration.AppName);
    }

    [Fact]
    public async Task MalformedJsonThrowsWithThePathInTheMessage()
    {
        using var directory = new TemporaryDirectory();
        string path = directory.WriteFile("launcher.config.json", "{ not json");

        LauncherConfigurationException exception =
            await Assert.ThrowsAsync<LauncherConfigurationException>(
                () => ProviderFor(path).LoadAsync(TestContext.Current.CancellationToken));

        Assert.Contains(path, exception.Message, StringComparison.Ordinal);
    }

    // Half-applied branding plus an unreachable endpoint is a worse outcome than not starting.
    [Fact]
    public async Task AnInvalidDocumentThrowsAndListsEveryProblem()
    {
        using var directory = new TemporaryDirectory();
        string path = directory.WriteFile(
            "launcher.config.json",
            """{ "appName": "", "apiBaseUrl": "nonsense" }""");

        LauncherConfigurationException exception =
            await Assert.ThrowsAsync<LauncherConfigurationException>(
                () => ProviderFor(path).LoadAsync(TestContext.Current.CancellationToken));

        Assert.Contains("AppName", exception.Message, StringComparison.Ordinal);
        Assert.Contains("ApiBaseUrl", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AnEmptyFileThrows()
    {
        using var directory = new TemporaryDirectory();
        string path = directory.WriteFile("launcher.config.json", "null");

        await Assert.ThrowsAsync<LauncherConfigurationException>(
            () => ProviderFor(path).LoadAsync(TestContext.Current.CancellationToken));
    }

    // The file the application actually ships must itself be valid.
    [Fact]
    public async Task TheRepositoryConfigurationFileIsValid()
    {
        string? path = FindRepositoryConfiguration();
        Assert.NotNull(path);

        LauncherConfiguration configuration =
            await ProviderFor(path).LoadAsync(TestContext.Current.CancellationToken);

        Assert.Empty(configuration.Validate());
    }

    private static string? FindRepositoryConfiguration()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            string candidate = Path.Combine(directory.FullName, "launcher.config.json");
            if (File.Exists(candidate))
            {
                return candidate;
            }

            directory = directory.Parent;
        }

        return null;
    }
}

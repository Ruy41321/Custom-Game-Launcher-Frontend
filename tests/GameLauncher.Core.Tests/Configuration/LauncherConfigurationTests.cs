using GameLauncher.Core.Configuration;

namespace GameLauncher.Core.Tests.Configuration;

public sealed class LauncherConfigurationTests
{
    [Fact]
    public void DefaultsAreValid()
    {
        Assert.Empty(new LauncherConfiguration().Validate());
    }

    [Fact]
    public void DefaultsToADarkThemeAndTheThreeLaunchLanguages()
    {
        var configuration = new LauncherConfiguration();

        Assert.Equal("dark", configuration.Theme.Variant);
        Assert.Equal(["en", "it", "fr"], configuration.Localization.SupportedLanguages);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void AnEmptyAppNameIsRejected(string appName)
    {
        IReadOnlyList<string> problems =
            new LauncherConfiguration { AppName = appName }.Validate();

        Assert.Contains(problems, problem => problem.Contains("AppName", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("not-a-url")]
    [InlineData("/api/v1/")]
    [InlineData("")]
    public void ARelativeOrMalformedApiUrlIsRejected(string url)
    {
        IReadOnlyList<string> problems =
            new LauncherConfiguration { ApiBaseUrl = url }.Validate();

        Assert.Contains(problems, problem => problem.Contains("ApiBaseUrl", StringComparison.Ordinal));
    }

    [Fact]
    public void ANonHttpApiSchemeIsRejected()
    {
        IReadOnlyList<string> problems =
            new LauncherConfiguration { ApiBaseUrl = "ftp://example.com/api/" }.Validate();

        Assert.Contains(problems, problem => problem.Contains("http", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("http://localhost:8080/api/v1/")]
    [InlineData("https://launcher.example.com/api/v1/")]
    public void HttpAndHttpsApiUrlsAreAccepted(string url)
    {
        Assert.Empty(new LauncherConfiguration { ApiBaseUrl = url }.Validate());
    }

    [Fact]
    public void ADefaultLanguageOutsideTheSupportedSetIsRejected()
    {
        var configuration = new LauncherConfiguration
        {
            Localization = new LocalizationConfiguration
            {
                DefaultLanguage = "de",
                SupportedLanguages = ["en", "it"],
            },
        };

        Assert.Contains(
            configuration.Validate(),
            problem => problem.Contains("DefaultLanguage", StringComparison.Ordinal));
    }

    [Fact]
    public void ANullDefaultLanguageIsAllowedAndMeansFollowTheOperatingSystem()
    {
        var configuration = new LauncherConfiguration
        {
            Localization = new LocalizationConfiguration { DefaultLanguage = null },
        };

        Assert.Empty(configuration.Validate());
    }

    // Reporting one problem per run would make fixing a config file a slow guessing game.
    [Fact]
    public void EveryProblemIsReportedAtOnce()
    {
        var configuration = new LauncherConfiguration
        {
            AppName = "",
            ApiBaseUrl = "nonsense",
        };

        Assert.Equal(2, configuration.Validate().Count);
    }

    [Theory]
    [InlineData("EN", true)]
    [InlineData("it", true)]
    [InlineData("de", false)]
    public void SupportedLanguageLookupIsCaseInsensitive(string culture, bool expected)
    {
        var localization = new LocalizationConfiguration { SupportedLanguages = ["en", "it", "fr"] };

        Assert.Equal(expected, localization.IsSupported(culture));
    }
}

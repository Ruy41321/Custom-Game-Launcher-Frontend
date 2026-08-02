namespace GameLauncher.Core.Configuration;

/// <summary>
/// Everything a fork needs to rebrand the launcher, loaded from <c>launcher.config.json</c>
/// shipped read-only next to the executable. User-changeable preferences live separately in
/// <see cref="UserSettings"/> so that an update cannot clobber them.
/// </summary>
public sealed record LauncherConfiguration
{
    public string AppName { get; init; } = "Custom Game Launcher";

    /// <summary>Base address of the API, including the version segment.</summary>
    public string ApiBaseUrl { get; init; } = "http://localhost:8080/api/v1/";

    public ThemeConfiguration Theme { get; init; } = new();

    public BrandingConfiguration Branding { get; init; } = new();

    public LocalizationConfiguration Localization { get; init; } = new();

    /// <summary>
    /// Where games are installed by default. Null means "decide from the platform", which is
    /// the right answer on a machine the packager knows nothing about.
    /// </summary>
    public string? DefaultInstallDirectory { get; init; }

    /// <summary>
    /// Returns every problem found, empty when the document is usable. Reporting all of them
    /// at once beats making the packager fix one typo per run.
    /// </summary>
    public IReadOnlyList<string> Validate()
    {
        List<string> problems = [];

        if (string.IsNullOrWhiteSpace(AppName))
        {
            problems.Add($"{nameof(AppName)} must not be empty.");
        }

        if (!Uri.TryCreate(ApiBaseUrl, UriKind.Absolute, out Uri? apiUri))
        {
            problems.Add($"{nameof(ApiBaseUrl)} is not an absolute URL: '{ApiBaseUrl}'.");
        }
        else if (apiUri.Scheme != Uri.UriSchemeHttp && apiUri.Scheme != Uri.UriSchemeHttps)
        {
            problems.Add($"{nameof(ApiBaseUrl)} must use http or https, not '{apiUri.Scheme}'.");
        }

        if (!string.IsNullOrWhiteSpace(Localization.DefaultLanguage) &&
            !Localization.IsSupported(Localization.DefaultLanguage))
        {
            problems.Add(
                $"{nameof(Localization)}.{nameof(LocalizationConfiguration.DefaultLanguage)} " +
                $"'{Localization.DefaultLanguage}' is not one of the supported languages.");
        }

        return problems;
    }
}

public sealed record ThemeConfiguration
{
    /// <summary><c>dark</c>, <c>light</c> or <c>system</c>. Dark is the product default.</summary>
    public string Variant { get; init; } = "dark";

    /// <summary>Accent colour as <c>#RRGGBB</c> or <c>#AARRGGBB</c>.</summary>
    public string AccentColor { get; init; } = "#7C5CFF";
}

public sealed record BrandingConfiguration
{
    /// <summary>Path relative to the application directory; null uses the built-in asset.</summary>
    public string? LogoPath { get; init; }

    public string? WindowIconPath { get; init; }
}

public sealed record LocalizationConfiguration
{
    /// <summary>Null follows the operating system's UI language.</summary>
    public string? DefaultLanguage { get; init; }

    public IReadOnlyList<string> SupportedLanguages { get; init; } = ["en", "it", "fr"];

    public bool IsSupported(string cultureName) =>
        SupportedLanguages.Any(language =>
            string.Equals(language, cultureName, StringComparison.OrdinalIgnoreCase));
}

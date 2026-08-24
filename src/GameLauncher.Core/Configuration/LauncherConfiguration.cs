namespace GameLauncher.Core.Configuration;

/// <summary>
/// Everything a fork needs to rebrand the launcher, loaded from <c>launcher.config.json</c>
/// shipped read-only next to the executable. User-changeable preferences live separately in
/// <see cref="UserSettings"/> so that an update cannot clobber them.
/// </summary>
public sealed record LauncherConfiguration
{
    public string AppName { get; init; } = "Custom Game Launcher";

    /// <summary>
    /// Base address of the API, including the version segment.
    ///
    /// This is the <i>fallback</i> when a service registry is configured: it is what a launcher
    /// uses on a machine that has never reached the registry, and what it falls back to when the
    /// registry cannot be reached and nothing is cached. See <see cref="ServiceRegistry"/>.
    /// </summary>
    public string ApiBaseUrl { get; init; } = "http://localhost:8080/api/v1/";

    /// <summary>Where the launcher asks for the API's current address. Off unless configured.</summary>
    public ServiceRegistryConfiguration ServiceRegistry { get; init; } = new();

    public ThemeConfiguration Theme { get; init; } = new();

    public BrandingConfiguration Branding { get; init; } = new();

    public LocalizationConfiguration Localization { get; init; } = new();

    public UpdateConfiguration Updates { get; init; } = new();

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

        problems.AddRange(ServiceRegistry.Validate());

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

/// <summary>
/// How to find the API's current address, instead of trusting the one baked into this file
/// forever.
///
/// A launcher ships with an endpoint and stops working the day that endpoint moves. The
/// registry breaks that coupling: the address here becomes the fallback, and the live answer
/// comes from a service whose own address is the one thing that never changes.
///
/// The <b>verification key is deliberately not here</b>. It lives in
/// <c>ServiceRegistryKey</c>, compiled into the binary, for the same reason the release key
/// does: this file sits inside the directory a self-update replaces, so a key kept here would
/// be replaced by whatever the update brought with it. The URL may live here safely — pointing
/// a launcher at a hostile registry gains an attacker nothing, because the answer it returns
/// will not carry a signature the compiled-in key accepts.
/// </summary>
public sealed record ServiceRegistryConfiguration
{
    /// <summary>
    /// Absolute URL of the registry, for example <c>https://registry.example.com/</c>. Empty
    /// means no registry, and the launcher uses <see cref="LauncherConfiguration.ApiBaseUrl"/>
    /// exactly as it always has.
    /// </summary>
    public string? Url { get; init; }

    /// <summary>Which record to ask for.</summary>
    public string ServiceKey { get; init; } = "game-launcher-api";

    /// <summary><c>production</c>, <c>staging</c> or <c>development</c>.</summary>
    public string Environment { get; init; } = "production";

    /// <summary>Whether a registry is configured at all.</summary>
    public bool IsConfigured => !string.IsNullOrWhiteSpace(Url);

    public IReadOnlyList<string> Validate()
    {
        List<string> problems = [];

        if (!IsConfigured)
        {
            // Nothing else matters when the feature is off, and a fork that never touched
            // this section must not be told its defaults are wrong.
            return problems;
        }

        if (!Uri.TryCreate(Url, UriKind.Absolute, out Uri? uri))
        {
            problems.Add($"{Prefix}.{nameof(Url)} is not an absolute URL: '{Url}'.");
        }
        else if (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps)
        {
            problems.Add($"{Prefix}.{nameof(Url)} must use http or https, not '{uri.Scheme}'.");
        }

        if (string.IsNullOrWhiteSpace(ServiceKey))
        {
            problems.Add($"{Prefix}.{nameof(ServiceKey)} must not be empty.");
        }

        if (string.IsNullOrWhiteSpace(Environment))
        {
            problems.Add($"{Prefix}.{nameof(Environment)} must not be empty.");
        }

        return problems;
    }

    private const string Prefix = nameof(LauncherConfiguration.ServiceRegistry);
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

public sealed record UpdateConfiguration
{
    /// <summary>
    /// Which release stream this launcher is on: <c>stable</c> or <c>beta</c>.
    ///
    /// It is a <i>shipped</i> setting and deliberately not a user preference. Which stream a
    /// launcher follows is the choice of whoever distributes it: a player who could move
    /// themselves onto a stream their distributor never published to would be a player who can
    /// replace their own launcher with a build nobody meant them to have, and the launcher is
    /// the program that has to still start in order to fix anything.
    ///
    /// Anything unrecognised is read as <c>stable</c> rather than failing validation — see
    /// <c>ReleaseTargets.Channel</c> for why a typo here must not be what stops a launcher from
    /// opening.
    /// </summary>
    public string Channel { get; init; } = "stable";
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

using System.Globalization;
using System.Resources;

namespace GameLauncher.Core.Localization;

/// <summary>
/// Localization backed by <c>Strings.resx</c> and its satellite assemblies.
/// Adding a language means adding <c>Strings.&lt;culture&gt;.resx</c> and one entry in
/// <see cref="SupportedLanguages"/> — no code changes anywhere else.
/// </summary>
public sealed class ResourceManagerLocalizationService : ILocalizationService
{
    /// <summary>
    /// Wraps an unresolved key so a missing translation is impossible to miss in the UI
    /// instead of silently rendering as an empty label.
    /// </summary>
    public static string FormatMissingKey(string key) => $"!{key}!";

    public static readonly IReadOnlyList<LanguageOption> SupportedLanguages =
    [
        new LanguageOption("en", "English"),
        new LanguageOption("it", "Italiano"),
        new LanguageOption("fr", "Français"),
    ];

    private readonly ResourceManager _resources;
    private CultureInfo _currentCulture;

    public ResourceManagerLocalizationService(string? initialCultureName = null)
    {
        _resources = new ResourceManager(
            "GameLauncher.Core.Localization.Strings",
            typeof(ResourceManagerLocalizationService).Assembly);

        _currentCulture = ResolveCulture(initialCultureName)
                          ?? ResolveCulture(CultureInfo.CurrentUICulture.TwoLetterISOLanguageName)
                          ?? new CultureInfo("en");
    }

    public CultureInfo CurrentCulture => _currentCulture;

    public IReadOnlyList<LanguageOption> AvailableLanguages => SupportedLanguages;

    public event EventHandler? LanguageChanged;

    public string this[string key] => Translate(key);

    public string Translate(string key)
    {
        if (string.IsNullOrWhiteSpace(key))
        {
            return string.Empty;
        }

        return _resources.GetString(key, _currentCulture) ?? FormatMissingKey(key);
    }

    public string Translate(string key, params object[] arguments)
    {
        string template = Translate(key);
        if (arguments.Length == 0)
        {
            return template;
        }

        try
        {
            return string.Format(_currentCulture, template, arguments);
        }
        catch (FormatException)
        {
            // A malformed placeholder in a translation must not take the UI down.
            return template;
        }
    }

    public bool TrySetLanguage(string cultureName)
    {
        CultureInfo? culture = ResolveCulture(cultureName);
        if (culture is null)
        {
            return false;
        }

        if (string.Equals(culture.Name, _currentCulture.Name, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        _currentCulture = culture;
        CultureInfo.CurrentUICulture = culture;
        LanguageChanged?.Invoke(this, EventArgs.Empty);
        return true;
    }

    private static CultureInfo? ResolveCulture(string? cultureName)
    {
        if (string.IsNullOrWhiteSpace(cultureName))
        {
            return null;
        }

        // "it-IT" should resolve to the "it" resources rather than falling back to English.
        string twoLetter = cultureName.Split('-')[0];

        return SupportedLanguages.Any(language =>
            string.Equals(language.CultureName, twoLetter, StringComparison.OrdinalIgnoreCase))
            ? new CultureInfo(twoLetter)
            : null;
    }
}

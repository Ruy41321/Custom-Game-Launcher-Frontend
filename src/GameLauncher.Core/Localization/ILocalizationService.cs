using System.Globalization;

namespace GameLauncher.Core.Localization;

/// <summary>
/// Resolves user-visible strings. The UI binds through this rather than referencing
/// resources directly, so switching language takes effect without restarting the app.
/// </summary>
public interface ILocalizationService
{
    CultureInfo CurrentCulture { get; }

    IReadOnlyList<LanguageOption> AvailableLanguages { get; }

    /// <summary>Raised after <see cref="TrySetLanguage"/> actually changes the culture.</summary>
    event EventHandler? LanguageChanged;

    /// <summary>Indexer form, used by the XAML <c>{loc:Tr}</c> markup extension.</summary>
    string this[string key] { get; }

    string Translate(string key);

    string Translate(string key, params object[] arguments);

    /// <summary>
    /// Switches language. Returns false when the culture is not one of
    /// <see cref="AvailableLanguages"/>, leaving the current culture untouched.
    /// </summary>
    bool TrySetLanguage(string cultureName);
}

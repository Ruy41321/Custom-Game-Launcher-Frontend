namespace GameLauncher.Core.Localization;

/// <summary>
/// A language offered in the UI. <see cref="NativeName"/> is deliberately the endonym —
/// somebody looking for Italian is looking for "Italiano", not "Italian".
/// </summary>
/// <param name="CultureName">BCP-47 tag, e.g. <c>it</c>.</param>
/// <param name="NativeName">The language's name in that language.</param>
public sealed record LanguageOption(string CultureName, string NativeName);

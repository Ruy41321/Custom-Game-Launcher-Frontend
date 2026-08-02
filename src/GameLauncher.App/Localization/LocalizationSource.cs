using System.ComponentModel;
using GameLauncher.Core.Localization;

namespace GameLauncher.App.Localization;

/// <summary>
/// Binding surface for the <c>{loc:Tr}</c> markup extension.
///
/// XAML cannot bind to a method call, so translations are exposed through an indexer.
/// Raising <see cref="INotifyPropertyChanged"/> for the indexer on a language change makes
/// every localized element in the UI re-read its string, which is what lets the language be
/// switched without restarting.
/// </summary>
public sealed class LocalizationSource : INotifyPropertyChanged
{
    private static LocalizationSource? _instance;

    private readonly ILocalizationService _localization;

    private LocalizationSource(ILocalizationService localization)
    {
        _localization = localization;
        _localization.LanguageChanged += (_, _) =>
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs("Item[]"));
    }

    /// <summary>
    /// The markup extension has no access to the DI container, so the single instance is
    /// published here during start-up. This is the only global in the application.
    /// </summary>
    public static LocalizationSource Instance =>
        _instance ?? throw new InvalidOperationException(
            $"{nameof(LocalizationSource)} was used before {nameof(Initialize)} was called.");

    public event PropertyChangedEventHandler? PropertyChanged;

    public string this[string key] => _localization[key];

    public static void Initialize(ILocalizationService localization) =>
        _instance = new LocalizationSource(localization);
}

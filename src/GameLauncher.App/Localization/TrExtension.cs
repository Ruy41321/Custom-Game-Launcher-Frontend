using Avalonia.Data;
using Avalonia.Markup.Xaml;

namespace GameLauncher.App.Localization;

/// <summary>
/// XAML markup extension for localized text: <c>Text="{loc:Tr Nav.Library}"</c>.
///
/// It produces a binding rather than a plain string so the value re-evaluates when the
/// language changes. No user-visible string may be hardcoded in a view.
/// </summary>
public sealed class TrExtension : MarkupExtension
{
    public TrExtension()
    {
    }

    public TrExtension(string key) => Key = key;

    public string Key { get; set; } = string.Empty;

    public override object ProvideValue(IServiceProvider serviceProvider) =>
        new Binding
        {
            Path = $"[{Key}]",
            Mode = BindingMode.OneWay,
            Source = LocalizationSource.Instance,
        };
}

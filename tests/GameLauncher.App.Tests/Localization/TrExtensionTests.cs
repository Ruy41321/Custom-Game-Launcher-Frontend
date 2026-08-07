using Avalonia;
using Avalonia.Data;
using GameLauncher.App.Localization;
using GameLauncher.Core.Localization;

namespace GameLauncher.App.Tests.Localization;

/// <summary>
/// The promise of D3 is that switching the language needs no restart, and the only thing that
/// can prove it short of looking at the window is Avalonia's own binding machinery. These tests
/// therefore bind a real <see cref="AvaloniaObject"/> property to what <see cref="TrExtension"/>
/// produces and read the value back - no UI toolkit initialisation is involved, because a
/// binding on a plain <see cref="AvaloniaObject"/> needs none.
///
/// A test that only asserted which <c>PropertyChanged</c> name is raised would have passed
/// against the broken code just as happily: WPF's <c>"Item[]"</c> is a perfectly reasonable
/// answer that Avalonia ignores.
/// </summary>
public sealed class TrExtensionTests
{
    private readonly ResourceManagerLocalizationService _localization = new("en");

    public TrExtensionTests() => LocalizationSource.Initialize(_localization);

    [Fact]
    public void ABoundLabelReadsTheCurrentLanguage()
    {
        var target = new BindingTarget();
        using BindingExpressionBase subscription = Bind(target, "Nav.Library");

        Assert.Equal(_localization["Nav.Library"], target.GetValue(BindingTarget.TextProperty));
    }

    [Fact]
    public void ABoundLabelFollowsALanguageChange()
    {
        var target = new BindingTarget();
        using BindingExpressionBase subscription = Bind(target, "Nav.Library");

        string english = target.GetValue(BindingTarget.TextProperty)!;
        Assert.True(_localization.TrySetLanguage("it"));
        string italian = target.GetValue(BindingTarget.TextProperty)!;

        Assert.Equal(_localization["Nav.Library"], italian);
        Assert.NotEqual(english, italian);
    }

    [Fact]
    public void EveryBoundLabelFollowsNotOnlyTheOneBoundLast()
    {
        var library = new BindingTarget();
        var explore = new BindingTarget();
        using BindingExpressionBase first = Bind(library, "Nav.Library");
        using BindingExpressionBase second = Bind(explore, "Nav.Explore");

        Assert.True(_localization.TrySetLanguage("fr"));

        Assert.Equal(_localization["Nav.Library"], library.GetValue(BindingTarget.TextProperty));
        Assert.Equal(_localization["Nav.Explore"], explore.GetValue(BindingTarget.TextProperty));
    }

    private static BindingExpressionBase Bind(BindingTarget target, string key) =>
        target.Bind(
            BindingTarget.TextProperty,
            (Binding)new TrExtension(key).ProvideValue(null!));

    private sealed class BindingTarget : AvaloniaObject
    {
        public static readonly StyledProperty<string?> TextProperty =
            AvaloniaProperty.Register<BindingTarget, string?>(nameof(Text));

        public string? Text
        {
            get => GetValue(TextProperty);
            set => SetValue(TextProperty, value);
        }
    }
}

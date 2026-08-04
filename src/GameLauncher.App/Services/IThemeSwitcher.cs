using Avalonia;
using Avalonia.Styling;

namespace GameLauncher.App.Services;

/// <summary>
/// Applies a theme to the running application. Behind an interface for the same reason the
/// folder picker is: the settings view model has to be exercisable without an Avalonia
/// application existing.
/// </summary>
public interface IThemeSwitcher
{
    /// <summary>
    /// <c>dark</c>, <c>light</c> or <c>system</c>, matching what the configuration file and
    /// the settings document already spell. Anything else falls back to dark, which is this
    /// product's default rather than the platform's.
    /// </summary>
    void Apply(string variant);
}

public sealed class ApplicationThemeSwitcher(Application application) : IThemeSwitcher
{
    public void Apply(string variant) =>
        application.RequestedThemeVariant = variant.ToLowerInvariant() switch
        {
            "light" => ThemeVariant.Light,
            "system" => ThemeVariant.Default,
            _ => ThemeVariant.Dark,
        };
}

using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Styling;
using GameLauncher.App.Localization;
using GameLauncher.App.ViewModels;
using GameLauncher.App.Views;
using GameLauncher.Core.Configuration;
using GameLauncher.Core.Localization;
using GameLauncher.Infrastructure.DependencyInjection;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Serilog;
using Serilog.Extensions.Logging;

namespace GameLauncher.App;

public partial class App : Application
{
    private ServiceProvider? _services;

    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    public override void OnFrameworkInitializationCompleted()
    {
        _services = BuildServiceProvider();

        // Configuration and settings are read once here, synchronously: the shell cannot be
        // rendered before we know the app's name, theme and language.
        LauncherConfiguration configuration = _services
            .GetRequiredService<ILauncherConfigurationProvider>()
            .LoadAsync().GetAwaiter().GetResult();

        UserSettings settings = _services
            .GetRequiredService<IUserSettingsStore>()
            .LoadAsync().GetAwaiter().GetResult();

        ApplyTheme(configuration, settings);
        ApplyLanguage(configuration, settings);

        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            desktop.MainWindow = new MainWindow
            {
                DataContext = new MainWindowViewModel(
                    _services.GetRequiredService<ILocalizationService>(),
                    _services.GetRequiredService<IUserSettingsStore>(),
                    configuration),
            };

            desktop.ShutdownRequested += (_, _) =>
            {
                _services?.Dispose();
                Log.CloseAndFlush();
            };
        }

        base.OnFrameworkInitializationCompleted();
    }

    private static ServiceProvider BuildServiceProvider()
    {
        ServiceCollection services = new();

        services.AddLogging(builder => builder.AddProvider(new SerilogLoggerProvider(Log.Logger)));
        services.AddLauncherInfrastructure();

        return services.BuildServiceProvider();
    }

    /// <summary>
    /// The user's explicit choice wins over the shipped configuration, which in turn wins
    /// over the product default of dark.
    /// </summary>
    private void ApplyTheme(LauncherConfiguration configuration, UserSettings settings)
    {
        string variant = settings.ThemeVariant ?? configuration.Theme.Variant;

        RequestedThemeVariant = variant.ToLowerInvariant() switch
        {
            "light" => ThemeVariant.Light,
            "system" => ThemeVariant.Default,
            _ => ThemeVariant.Dark,
        };
    }

    private void ApplyLanguage(LauncherConfiguration configuration, UserSettings settings)
    {
        ILocalizationService localization = _services!.GetRequiredService<ILocalizationService>();

        string? preferred = settings.Language ?? configuration.Localization.DefaultLanguage;
        if (!string.IsNullOrWhiteSpace(preferred))
        {
            localization.TrySetLanguage(preferred);
        }

        LocalizationSource.Initialize(localization);
    }
}

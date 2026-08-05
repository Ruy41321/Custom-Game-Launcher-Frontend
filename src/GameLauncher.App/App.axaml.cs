using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Styling;
using Avalonia.Threading;
using GameLauncher.App.Localization;
using GameLauncher.App.Services;
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
        LauncherConfiguration configuration = _services.GetRequiredService<LauncherConfiguration>();

        UserSettings settings = _services
            .GetRequiredService<IUserSettingsStore>()
            .LoadAsync().GetAwaiter().GetResult();

        ApplyTheme(configuration, settings);
        ApplyLanguage(configuration, settings);

        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            MainWindowViewModel shell = _services.GetRequiredService<MainWindowViewModel>();

            desktop.MainWindow = new MainWindow { DataContext = shell };

            // Restoring the session is a round trip, so it happens after the window is up
            // rather than in front of it: a launcher that shows nothing until the server
            // answers looks broken on a slow connection.
            _ = Dispatcher.UIThread.InvokeAsync(async () =>
            {
                try
                {
                    await shell.InitializeAsync().ConfigureAwait(true);
                }
                catch (Exception exception)
                {
                    Log.Error(exception, "Restoring the stored session failed.");
                }
            });

            desktop.ShutdownRequested += (_, _) =>
            {
                _services?.Dispose();
                Log.CloseAndFlush();
            };
        }

        base.OnFrameworkInitializationCompleted();
    }

    private ServiceProvider BuildServiceProvider()
    {
        ServiceCollection services = new();

        services.AddLogging(builder => builder.AddProvider(new SerilogLoggerProvider(Log.Logger)));
        services.AddLauncherInfrastructure();

        // The picker resolves the window at call time rather than holding it: the shell is
        // built before the window exists.
        services.AddSingleton<IFolderPicker>(_ => new StorageProviderFolderPicker(this));
        services.AddSingleton<IFilePicker>(_ => new StorageProviderFilePicker(this));
        services.AddSingleton<IThemeSwitcher>(_ => new ApplicationThemeSwitcher(this));

        // Decoded artwork is shared by every page: a cover seen in Explore is the same bitmap
        // the library and the detail page show.
        services.AddSingleton<IImageProvider, CachedImageProvider>();

        // View models are singletons: the shell holds all of them for the life of the window,
        // and a page that keeps its scroll position and search term when you come back to it
        // is what a user expects.
        services.AddSingleton<LoginViewModel>();
        services.AddSingleton<ExploreViewModel>();
        services.AddSingleton<LibraryViewModel>();
        services.AddSingleton<GameDetailViewModel>();
        services.AddSingleton<GameEditorViewModel>();
        services.AddSingleton<GameMediaViewModel>();
        services.AddSingleton<GameDevlogViewModel>();
        services.AddSingleton<DeveloperViewModel>();
        services.AddSingleton<SettingsViewModel>();
        services.AddSingleton<MainWindowViewModel>();

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

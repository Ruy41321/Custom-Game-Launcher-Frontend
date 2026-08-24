using Avalonia;
using GameLauncher.Core.Platform;
using GameLauncher.Infrastructure.Logging;
using GameLauncher.Infrastructure.Platform;
using Serilog;

namespace GameLauncher.App;

internal static class Program
{
    // Avalonia must be initialised before anything touches a UI type, so nothing may run
    // ahead of BuildAvaloniaApp here.
    [STAThread]
    public static int Main(string[] args)
    {
        IPathProvider paths = new PathProvider();
        LauncherLogging.Configure(paths, verbose: args.Contains("--verbose"));
        LauncherLogging.InstallGlobalHandlers(paths);

        try
        {
            Log.Information("Launcher starting");
            BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
            return 0;
        }
        catch (Exception exception)
        {
            LauncherLogging.WriteCrashReport(paths, exception, "startup");
            return 1;
        }
        finally
        {
            Log.CloseAndFlush();
        }
    }

    /// <summary>Also used by the Avalonia XAML previewer, which requires this exact name.</summary>
    public static AppBuilder BuildAvaloniaApp() =>
        AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .WithInterFont()
            .LogToTrace();
}

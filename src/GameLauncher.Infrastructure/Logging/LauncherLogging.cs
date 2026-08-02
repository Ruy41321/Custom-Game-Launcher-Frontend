using System.Globalization;
using GameLauncher.Core.Platform;
using Serilog;
using Serilog.Events;

namespace GameLauncher.Infrastructure.Logging;

/// <summary>
/// Local rolling-file logging plus the last-resort crash handlers.
///
/// Crash reports are written to disk only. Nothing is ever transmitted unless the user has
/// opted in, and even then upload is a separate, explicit step.
/// </summary>
public static class LauncherLogging
{
    private const string OutputTemplate =
        "{Timestamp:yyyy-MM-dd HH:mm:ss.fff zzz} [{Level:u3}] {Message:lj}{NewLine}{Exception}";

    public static ILogger Configure(IPathProvider pathProvider, bool verbose = false)
    {
        pathProvider.EnsureDirectoriesExist();

        Log.Logger = new LoggerConfiguration()
            .MinimumLevel.Is(verbose ? LogEventLevel.Debug : LogEventLevel.Information)
            .Enrich.FromLogContext()
            .WriteTo.File(
                Path.Combine(pathProvider.LogDirectory, "launcher-.log"),
                // Logs are read by developers and diagnostic tooling, never by end users:
                // they must not change shape with the machine's locale.
                formatProvider: CultureInfo.InvariantCulture,
                rollingInterval: RollingInterval.Day,
                retainedFileCountLimit: 14,
                fileSizeLimitBytes: 20L * 1024 * 1024,
                rollOnFileSizeLimit: true,
                // A hard kill or a native crash never reaches CloseAndFlush, and an empty
                // log file is worthless precisely when something has gone wrong.
                flushToDiskInterval: TimeSpan.FromSeconds(2),
                outputTemplate: OutputTemplate)
            .CreateLogger();

        return Log.Logger;
    }

    /// <summary>
    /// Catches what the UI cannot: exceptions escaping the dispatcher and faulted tasks
    /// nobody awaited. Without this the process dies with nothing written down.
    /// </summary>
    public static void InstallGlobalHandlers(IPathProvider pathProvider)
    {
        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
        {
            if (args.ExceptionObject is Exception exception)
            {
                WriteCrashReport(pathProvider, exception, "unhandled");
            }

            Log.CloseAndFlush();
        };

        TaskScheduler.UnobservedTaskException += (_, args) =>
        {
            WriteCrashReport(pathProvider, args.Exception, "unobserved-task");

            // The process is still healthy; an unawaited failure must not kill the launcher.
            args.SetObserved();
        };
    }

    public static void WriteCrashReport(IPathProvider pathProvider, Exception exception, string kind)
    {
        Log.Fatal(exception, "Crash ({Kind})", kind);

        try
        {
            Directory.CreateDirectory(pathProvider.LogDirectory);
            string path = Path.Combine(
                pathProvider.LogDirectory,
                $"crash-{DateTime.UtcNow:yyyyMMdd-HHmmssfff}-{kind}.log");

            File.WriteAllText(
                path,
                $"""
                 Kind:        {kind}
                 UTC:         {DateTime.UtcNow:O}
                 OS:          {Environment.OSVersion}
                 Runtime:     {Environment.Version}
                 App version: {typeof(LauncherLogging).Assembly.GetName().Version}

                 {exception}
                 """);
        }
        catch (IOException)
        {
            // Nothing useful left to do: the crash report itself failed to write.
        }
    }
}

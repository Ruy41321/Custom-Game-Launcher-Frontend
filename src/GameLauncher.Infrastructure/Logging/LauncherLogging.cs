using System.Globalization;
using GameLauncher.Core.Diagnostics;
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

    /// <summary>
    /// Writes the report that a later run may send, if its owner has asked for that.
    ///
    /// **Redacted here, not at upload time.** The file on disk is the request body, so
    /// redacting later would leave the unredacted copy sitting in the log directory of a
    /// machine whose owner asked for the opposite — and would mean the thing somebody could
    /// review was not the thing that got sent. The rolling log beside it keeps the exception in
    /// full, and that copy never leaves the machine.
    /// </summary>
    public static void WriteCrashReport(IPathProvider pathProvider, Exception exception, string kind)
    {
        Log.Fatal(exception, "Crash ({Kind})", kind);

        try
        {
            Directory.CreateDirectory(pathProvider.LogDirectory);

            CrashReport report = Describe(exception, kind);
            report = CrashReportRedactor.Redact(
                report,
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                pathProvider.UserDataDirectory,
                pathProvider.DefaultInstallDirectory);

            File.WriteAllText(
                Path.Combine(
                    pathProvider.LogDirectory, CrashReportFiles.NameFor(report.OccurredAt, kind)),
                CrashReportFiles.Serialize(report));
        }
        catch (Exception failure) when (
            failure is IOException or UnauthorizedAccessException or NotSupportedException)
        {
            // Nothing useful left to do: the crash report itself failed to write.
        }
    }

    private static CrashReport Describe(Exception exception, string kind) => new()
    {
        Kind = kind,
        OccurredAt = DateTimeOffset.UtcNow,
        LauncherVersion =
            typeof(LauncherLogging).Assembly.GetName().Version?.ToString() ?? string.Empty,
        // The OS and the runtime, and deliberately not the machine name or the user name:
        // "which Windows" is diagnostic and "whose Windows" is not.
        Platform = $"{Environment.OSVersion} / .NET {Environment.Version}",
        ExceptionType = exception.GetType().FullName ?? exception.GetType().Name,
        Message = exception.Message,
        // ToString() rather than StackTrace, because it carries the inner exceptions — which
        // are usually the ones that say what actually went wrong.
        StackTrace = exception.ToString(),
    };
}

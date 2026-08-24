using GameLauncher.Core.Api;
using GameLauncher.Core.Configuration;
using GameLauncher.Core.Diagnostics;
using GameLauncher.Core.Models;
using GameLauncher.Core.Platform;
using Microsoft.Extensions.Logging;

namespace GameLauncher.Infrastructure.Logging;

/// <summary>
/// Sends what previous runs wrote down. Runs once at startup: a crash report is written by a
/// process that is dying, so the run that can send it is always the next one.
/// </summary>
public sealed class CrashReportUploader(
    ICrashReportApi api,
    IUserSettingsStore settings,
    IServerCapabilityProvider capabilities,
    IPathProvider paths,
    ILogger<CrashReportUploader> logger) : ICrashReportUploader
{
    /// <summary>
    /// How many are sent in one start. A launcher that crashed thirty times overnight has one
    /// bug, not thirty, and the server's rate limit would refuse the tail anyway; the rest are
    /// left for the next start rather than spent against that limit.
    /// </summary>
    private const int MaxPerRun = 5;

    public async Task<CrashUploadResult> UploadPendingAsync(
        CancellationToken cancellationToken = default)
    {
        try
        {
            return await SweepAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            // A launcher that failed to start because it could not report a previous failure
            // would be the worst possible outcome of this feature.
            logger.LogWarning(exception, "Crash reports could not be processed.");
            return new CrashUploadResult();
        }
    }

    private async Task<CrashUploadResult> SweepAsync(CancellationToken cancellationToken)
    {
        string[] pending = PendingFiles();
        if (pending.Length == 0)
        {
            return new CrashUploadResult();
        }

        UserSettings preferences = await settings.LoadAsync(cancellationToken).ConfigureAwait(false);
        if (!preferences.SendCrashReports)
        {
            // Not merely "do not send": a person who said no should not have a growing pile of
            // unsent crash reports about them accumulating on their own disk.
            return new CrashUploadResult { Discarded = DiscardAll(pending) };
        }

        ServerCapabilities server = await capabilities.GetAsync(cancellationToken)
            .ConfigureAwait(false);
        if (!server.CrashReports.Enabled)
        {
            // The deployment does not collect them. Keeping the files would mean carrying them
            // forever for a server that will never take them.
            logger.LogInformation("This server does not accept crash reports; discarding {Count}.",
                pending.Length);
            return new CrashUploadResult { Discarded = DiscardAll(pending) };
        }

        int sent = 0;
        int discarded = 0;
        int deferred = 0;

        foreach (string file in pending.Take(MaxPerRun))
        {
            switch (await SendAsync(file, cancellationToken).ConfigureAwait(false))
            {
                case Outcome.Sent:
                    ++sent;
                    break;
                case Outcome.Discarded:
                    ++discarded;
                    break;
                default:
                    // The server could not be reached, or asked us to slow down. Stop: the rest
                    // would fail the same way, and burning the rate limit on them would delay
                    // the report that matters rather than deliver it.
                    ++deferred;
                    return Report(sent, discarded, deferred + pending.Length - sent - discarded - 1);
            }
        }

        deferred = Math.Max(0, pending.Length - sent - discarded);
        return Report(sent, discarded, deferred);
    }

    private CrashUploadResult Report(int sent, int discarded, int deferred)
    {
        if (sent > 0 || discarded > 0)
        {
            logger.LogInformation(
                "Crash reports: {Sent} sent, {Discarded} discarded, {Deferred} left for next time.",
                sent, discarded, deferred);
        }

        return new CrashUploadResult { Sent = sent, Discarded = discarded, Deferred = deferred };
    }

    private enum Outcome
    {
        Sent,
        Discarded,
        Deferred,
    }

    private async Task<Outcome> SendAsync(string file, CancellationToken cancellationToken)
    {
        CrashReport? report;
        try
        {
            report = CrashReportFiles.Deserialize(
                await File.ReadAllTextAsync(file, cancellationToken).ConfigureAwait(false));
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException)
        {
            return Outcome.Deferred;
        }

        if (report is null)
        {
            // Truncated, or written by something else. Retrying it forever would mean one bad
            // file blocking every real report behind it.
            Discard(file);
            return Outcome.Discarded;
        }

        try
        {
            await api.SubmitAsync(report, cancellationToken).ConfigureAwait(false);
        }
        catch (ApiException exception) when (exception.IsTransient || exception.Code == ApiErrorCode.RateLimited)
        {
            return Outcome.Deferred;
        }
        catch (ApiException exception)
        {
            // The server refused it and will refuse it again: too large, malformed, or a route
            // this deployment does not have. Keeping it would be keeping it forever.
            logger.LogInformation(
                "A crash report was refused ({Code}) and will not be retried.", exception.Code);
            Discard(file);
            return Outcome.Discarded;
        }

        Discard(file);
        return Outcome.Sent;
    }

    /// <summary>Oldest first: the file name begins with the timestamp, so the sort is the order
    /// the crashes happened in, and a truncated run sends the earliest rather than the newest.</summary>
    private string[] PendingFiles()
    {
        try
        {
            string[] files = Directory.GetFiles(
                paths.LogDirectory, CrashReportFiles.SearchPattern);
            Array.Sort(files, StringComparer.Ordinal);
            return files;
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException)
        {
            return [];
        }
    }

    private int DiscardAll(IEnumerable<string> files)
    {
        int discarded = 0;
        foreach (string file in files)
        {
            if (Discard(file))
            {
                ++discarded;
            }
        }

        return discarded;
    }

    private bool Discard(string file)
    {
        try
        {
            File.Delete(file);
            return true;
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException)
        {
            logger.LogDebug("A processed crash report could not be deleted: {File}", file);
            return false;
        }
    }
}

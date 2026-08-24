using GameLauncher.Core.Api;
using GameLauncher.Core.Diagnostics;

namespace GameLauncher.Infrastructure.Api;

/// <summary>
/// Sends a crash report. On the tokenless client, for the reason <see cref="ICrashReportApi"/>
/// gives: the crashes worth having are often the ones that happen before anybody signs in.
/// </summary>
public sealed class CrashReportApiClient(HttpClient httpClient) : ICrashReportApi
{
    private readonly ApiTransport _transport = new(httpClient);

    public async Task<string> SubmitAsync(
        CrashReport report, CancellationToken cancellationToken = default)
    {
        SubmissionResult result = await _transport
            .PostAsync<SubmissionResult>("crash-reports", report, cancellationToken)
            .ConfigureAwait(false);

        return result.Fingerprint;
    }

    private sealed record SubmissionResult
    {
        public string Fingerprint { get; init; } = string.Empty;
    }
}

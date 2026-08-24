using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using GameLauncher.Core.Api;
using GameLauncher.Core.Downloads;
using GameLauncher.Core.Models;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace GameLauncher.Infrastructure.Downloads;

/// <summary>
/// One blob, off the file server and onto this disk. Runs on an <see cref="HttpClient"/> that
/// attaches no bearer token: the URL carries its own authorization, and sending credentials to
/// a host the API named would hand them to whatever that host turns out to be.
///
/// Nothing reaches its content address until its bytes hash to it — the same invariant the
/// server's blob store holds, for the same reason: a half-written file named after content it
/// does not have is indistinguishable from the real thing forever after.
/// </summary>
public sealed class BlobFetcher : IBlobFetcher
{
    /// <summary>
    /// Suffix of a transfer in flight. Not a valid content address, so a partial file is never
    /// mistaken for a finished one by anything that looks at the staging directory.
    /// </summary>
    public const string PartialSuffix = ".part";

    private const int DefaultMaxAttempts = 3;

    private const int CopyBufferBytes = 128 * 1024;

    private readonly HttpClient _httpClient;
    private readonly ILogger<BlobFetcher> _logger;
    private readonly int _maxAttempts;
    private readonly TimeSpan _retryDelay;

    /// <summary>
    /// The container has two constructors to choose from and no way to prefer one, so the
    /// choice is stated rather than left to whichever it happens to find first.
    /// </summary>
    [ActivatorUtilitiesConstructor]
    public BlobFetcher(HttpClient httpClient, ILogger<BlobFetcher> logger)
        : this(httpClient, logger, DefaultMaxAttempts, TimeSpan.FromSeconds(2))
    {
    }

    public BlobFetcher(
        HttpClient httpClient,
        ILogger<BlobFetcher> logger,
        int maxAttempts,
        TimeSpan retryDelay)
    {
        _httpClient = httpClient;
        _logger = logger;
        _maxAttempts = Math.Max(1, maxAttempts);
        _retryDelay = retryDelay;
    }

    public async Task FetchAsync(
        PlannedFile file,
        string destinationPath,
        IProgress<long>? transferred = null,
        CancellationToken cancellationToken = default)
    {
        // Progress is reported as a delta, but reasoned about here as the total this file has
        // on disk: a retry that throws away a partial transfer has to give those bytes back,
        // and computing that from deltas at each call site is how a progress bar starts lying.
        long reported = 0;
        void Report(long total)
        {
            if (total != reported)
            {
                transferred?.Report(total - reported);
                reported = total;
            }
        }

        if (File.Exists(destinationPath))
        {
            Report(file.Size);
            return;
        }

        Directory.CreateDirectory(
            Path.GetDirectoryName(destinationPath)
            ?? throw new ArgumentException(
                "A blob destination must be a path inside a directory.", nameof(destinationPath)));

        string partialPath = destinationPath + PartialSuffix;

        for (int attempt = 1; ; attempt++)
        {
            try
            {
                await TransferAsync(file, partialPath, Report, cancellationToken)
                    .ConfigureAwait(false);
                await VerifyAsync(file, partialPath, cancellationToken).ConfigureAwait(false);
                break;
            }
            catch (ApiException exception) when (CanRetry(exception, attempt))
            {
                _logger.LogWarning(
                    "Fetching blob {Blob} failed ({Code}), attempt {Attempt} of {Attempts}: {Reason}",
                    file.Sha256, exception.Code, attempt, _maxAttempts, exception.Message);

                // Nothing on disk can be trusted after an integrity failure. A network failure
                // is different: what arrived so far is still the blob's first bytes.
                if (exception.Code == ApiErrorCode.Integrity)
                {
                    Delete(partialPath);
                    Report(0);
                }

                await Task.Delay(_retryDelay, cancellationToken).ConfigureAwait(false);
            }
        }

        // The rename is what publishes the blob, and it is the last thing that happens.
        File.Move(partialPath, destinationPath, overwrite: true);
    }

    private bool CanRetry(ApiException exception, int attempt) =>
        attempt < _maxAttempts
        && exception.Code is ApiErrorCode.Integrity or ApiErrorCode.Network;

    private async Task TransferAsync(
        PlannedFile file,
        string partialPath,
        Action<long> report,
        CancellationToken cancellationToken)
    {
        long offset = SizeOf(partialPath);
        if (offset > file.Size)
        {
            // Longer than the blob it claims to be: leftovers from something else entirely.
            Delete(partialPath);
            offset = 0;
        }

        report(offset);

        if (offset == file.Size)
        {
            return;
        }

        using HttpResponseMessage response = await SendAsync(file, offset, cancellationToken)
            .ConfigureAwait(false);

        // A file server that ignores Range answers 200 with the whole blob, and appending that
        // to what is already there would produce a file made of the same bytes twice.
        long position = response.StatusCode == HttpStatusCode.PartialContent ? offset : 0;
        report(position);

        await using FileStream partial = new(
            partialPath, FileMode.OpenOrCreate, FileAccess.Write, FileShare.None,
            CopyBufferBytes, useAsync: true);

        partial.SetLength(position);
        partial.Seek(position, SeekOrigin.Begin);

        await using Stream body = await response.Content
            .ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);

        byte[] buffer = new byte[CopyBufferBytes];
        while (true)
        {
            int read;
            try
            {
                read = await body.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception exception) when (exception is HttpRequestException or IOException)
            {
                await partial.FlushAsync(cancellationToken).ConfigureAwait(false);
                throw new ApiException(
                    ApiErrorCode.Network,
                    "The transfer was interrupted.",
                    innerException: exception);
            }

            if (read == 0)
            {
                break;
            }

            await partial.WriteAsync(buffer.AsMemory(0, read), cancellationToken)
                .ConfigureAwait(false);

            position += read;
            report(position);
        }

        await partial.FlushAsync(cancellationToken).ConfigureAwait(false);
    }

    private async Task<HttpResponseMessage> SendAsync(
        PlannedFile file, long offset, CancellationToken cancellationToken)
    {
        using HttpRequestMessage request = new(HttpMethod.Get, file.Url);
        if (offset > 0)
        {
            request.Headers.Range = new RangeHeaderValue(offset, null);
        }

        HttpResponseMessage response;
        try
        {
            response = await _httpClient
                .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is HttpRequestException or IOException)
        {
            throw new ApiException(
                ApiErrorCode.Network,
                "The file server could not be reached.",
                innerException: exception);
        }

        if (response.IsSuccessStatusCode)
        {
            return response;
        }

        using (response)
        {
            int status = (int)response.StatusCode;

            // The partial file is longer than the blob the server is willing to serve. Only a
            // restart from zero can resolve it, so it is reported as an integrity failure and
            // the leftovers go with it.
            if (response.StatusCode == HttpStatusCode.RequestedRangeNotSatisfiable)
            {
                throw new ApiException(
                    ApiErrorCode.Integrity,
                    string.Create(
                        CultureInfo.InvariantCulture,
                        $"The file server refused the resume offset {offset} for blob {file.Sha256}."),
                    status);
            }

            throw new ApiException(
                new ApiProblem().ToErrorCode(status),
                $"The file server refused to serve blob {file.Sha256}.",
                status);
        }
    }

    private static async Task VerifyAsync(
        PlannedFile file, string partialPath, CancellationToken cancellationToken)
    {
        // Hashed after the fact rather than as the bytes go past, because a transfer that
        // resumes after a restart has no running hash to carry over — and the check has to
        // cover what is on disk, not what this process happens to have seen.
        await using FileStream stream = new(
            partialPath, FileMode.Open, FileAccess.Read, FileShare.None,
            CopyBufferBytes, useAsync: true);

        byte[] hash = await SHA256.HashDataAsync(stream, cancellationToken).ConfigureAwait(false);
        string actual = Convert.ToHexStringLower(hash);

        if (!string.Equals(actual, file.Sha256, StringComparison.OrdinalIgnoreCase))
        {
            throw new ApiException(
                ApiErrorCode.Integrity, $"Blob {file.Sha256} arrived hashing to {actual}.");
        }
    }

    private static long SizeOf(string path) =>
        File.Exists(path) ? new FileInfo(path).Length : 0;

    private static void Delete(string path)
    {
        if (File.Exists(path))
        {
            File.Delete(path);
        }
    }
}

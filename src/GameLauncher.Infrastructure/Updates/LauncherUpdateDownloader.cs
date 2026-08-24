using System.Globalization;
using System.Security.Cryptography;
using GameLauncher.Core.Api;
using GameLauncher.Core.Platform;
using GameLauncher.Core.Updates;
using Microsoft.Extensions.Logging;

namespace GameLauncher.Infrastructure.Updates;

/// <summary>
/// The artifact, off whatever host the server named and onto this disk.
///
/// It runs on the file-server-shaped client: no bearer token, because the URL is public and the
/// host is one the API chose — the reasoning of D20 and D35, met a third time — and no timeout,
/// because a self-contained launcher is tens of megabytes and <see cref="HttpClient.Timeout"/>
/// covers the body as well as the headers.
///
/// Nothing reaches its final name until its bytes hash to the content address in the signed
/// document, which is the same invariant <c>BlobFetcher</c> holds. There is no resume: an
/// update is one archive fetched once, and a partial file left by an interrupted attempt is
/// thrown away rather than continued, because the alternative — trusting bytes from one
/// connection and bytes from another — buys nothing here that the hash does not already have
/// to catch.
/// </summary>
public sealed class LauncherUpdateDownloader(
    HttpClient httpClient,
    IPathProvider paths,
    ILogger<LauncherUpdateDownloader> logger) : ILauncherUpdateDownloader
{
    private const int CopyBufferBytes = 128 * 1024;

    private const string PartialSuffix = ".part";

    public async Task<string> DownloadAsync(
        ReleaseDocument release,
        string url,
        IProgress<long>? transferred = null,
        CancellationToken cancellationToken = default)
    {
        string directory = Path.Combine(paths.UpdateDirectory, release.Version.ToString());
        string destination = Path.Combine(directory, release.Sha256 + ".zip");

        Directory.CreateDirectory(directory);
        DiscardOtherVersions(directory);

        if (File.Exists(destination))
        {
            // Verified when it was renamed into place, and re-checked here rather than trusted:
            // this file has been sitting on a disk somebody else can write to.
            if (await HashOf(destination, cancellationToken).ConfigureAwait(false) == release.Sha256)
            {
                transferred?.Report(release.Size);
                return destination;
            }

            File.Delete(destination);
        }

        string partial = destination + PartialSuffix;
        try
        {
            await TransferAsync(release, url, partial, transferred, cancellationToken)
                .ConfigureAwait(false);

            string actual = await HashOf(partial, cancellationToken).ConfigureAwait(false);
            if (!string.Equals(actual, release.Sha256, StringComparison.Ordinal))
            {
                // The document and its signature can be perfectly intact and this still fail:
                // it is the check that says the file is the thing the key vouched for.
                logger.LogWarning(
                    "Launcher release {Version} arrived hashing to {Actual}, not {Expected}.",
                    release.Version, actual, release.Sha256);

                throw new ApiException(
                    ApiErrorCode.Integrity,
                    $"The launcher update arrived hashing to {actual}.");
            }
        }
        catch
        {
            Discard(partial);
            throw;
        }

        File.Move(partial, destination, overwrite: true);
        return destination;
    }

    private async Task TransferAsync(
        ReleaseDocument release,
        string url,
        string partial,
        IProgress<long>? transferred,
        CancellationToken cancellationToken)
    {
        using HttpRequestMessage request = new(HttpMethod.Get, url);

        HttpResponseMessage response;
        try
        {
            response = await httpClient
                .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is HttpRequestException or IOException)
        {
            throw new ApiException(
                ApiErrorCode.Network,
                "The launcher update could not be fetched.",
                innerException: exception);
        }

        using (response)
        {
            if (!response.IsSuccessStatusCode)
            {
                throw new ApiException(
                    new ApiProblem().ToErrorCode((int)response.StatusCode),
                    "The launcher update could not be fetched.",
                    (int)response.StatusCode);
            }

            await using FileStream file = new(
                partial, FileMode.Create, FileAccess.Write, FileShare.None,
                CopyBufferBytes, useAsync: true);

            await using Stream body = await response.Content
                .ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);

            byte[] buffer = new byte[CopyBufferBytes];
            long written = 0;

            while (true)
            {
                int read;
                try
                {
                    read = await body.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
                }
                catch (Exception exception) when (exception is HttpRequestException or IOException)
                {
                    throw new ApiException(
                        ApiErrorCode.Network,
                        "The launcher update transfer was interrupted.",
                        innerException: exception);
                }

                if (read == 0)
                {
                    break;
                }

                written += read;
                if (written > release.Size)
                {
                    // The signed document says how big the artifact is, so a response that keeps
                    // going is stopped here rather than at the hash: the hash would refuse it
                    // too, after however many gigabytes the host felt like sending.
                    throw new ApiException(
                        ApiErrorCode.Integrity,
                        string.Create(
                            CultureInfo.InvariantCulture,
                            $"The launcher update is longer than the {release.Size} bytes it declares."));
                }

                await file.WriteAsync(buffer.AsMemory(0, read), cancellationToken)
                    .ConfigureAwait(false);

                transferred?.Report(written);
            }

            await file.FlushAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    private static async Task<string> HashOf(string path, CancellationToken cancellationToken)
    {
        await using FileStream stream = new(
            path, FileMode.Open, FileAccess.Read, FileShare.None, CopyBufferBytes, useAsync: true);

        return Convert.ToHexStringLower(
            await SHA256.HashDataAsync(stream, cancellationToken).ConfigureAwait(false));
    }

    /// <summary>
    /// One version's worth of archive at a time. A launcher offered three updates over a month
    /// would otherwise leave three self-contained builds under the user's data directory, and
    /// only the newest of them is worth anything.
    /// </summary>
    private void DiscardOtherVersions(string keep)
    {
        try
        {
            foreach (string directory in Directory.GetDirectories(paths.UpdateDirectory))
            {
                if (!string.Equals(directory, keep, StringComparison.Ordinal))
                {
                    Directory.Delete(directory, recursive: true);
                }
            }
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException)
        {
            logger.LogDebug(exception, "An older downloaded launcher update could not be removed.");
        }
    }

    private void Discard(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException)
        {
            logger.LogDebug(exception, "A failed launcher update download could not be removed.");
        }
    }
}

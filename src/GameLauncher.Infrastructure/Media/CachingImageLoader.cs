using System.Globalization;
using System.Security.Cryptography;
using GameLauncher.Core.Media;
using GameLauncher.Core.Platform;
using Microsoft.Extensions.Logging;

namespace GameLauncher.Infrastructure.Media;

/// <summary>
/// Artwork, fetched once and then read off the disk. Caching by URL is safe because artwork is
/// content-addressed server-side: the same picture is always the same URL, and changing a
/// game's cover means a different one. A cached entry can therefore never be stale, which is
/// why there is no revalidation here and no expiry other than the size cap.
/// </summary>
public sealed class CachingImageLoader(
    HttpClient httpClient,
    IPathProvider paths,
    ILogger<CachingImageLoader> logger) : IImageLoader
{
    /// <summary>
    /// Bigger than any cover the server accepts, and small enough that a host answering with
    /// something that is not a picture cannot make the launcher read forever.
    /// </summary>
    public const long MaxImageBytes = 16 * 1024 * 1024;

    /// <summary>What the cache directory may grow to before the oldest entries go.</summary>
    public const long MaxCacheBytes = 128 * 1024 * 1024;

    public async Task<byte[]?> LoadAsync(string url, CancellationToken cancellationToken = default)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out Uri? address)
            || (address.Scheme != Uri.UriSchemeHttp && address.Scheme != Uri.UriSchemeHttps))
        {
            return null;
        }

        string path = CachePathFor(url);
        if (ReadCached(path) is { } cached)
        {
            return cached;
        }

        byte[]? downloaded = await DownloadAsync(address, cancellationToken).ConfigureAwait(false);
        if (downloaded is null)
        {
            return null;
        }

        Store(path, downloaded);
        return downloaded;
    }

    private async Task<byte[]?> DownloadAsync(Uri address, CancellationToken cancellationToken)
    {
        try
        {
            using HttpResponseMessage response = await httpClient
                .GetAsync(address, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
                .ConfigureAwait(false);

            if (!response.IsSuccessStatusCode)
            {
                logger.LogDebug(
                    "Artwork at {Url} answered {Status}", address, (int)response.StatusCode);
                return null;
            }

            if (response.Content.Headers.ContentLength > MaxImageBytes)
            {
                logger.LogWarning("Artwork at {Url} is larger than a picture should be", address);
                return null;
            }

            byte[]? bytes = await ReadCappedAsync(response, cancellationToken)
                .ConfigureAwait(false);

            // What the bytes are is decided by the bytes, never by the declared type — the
            // same rule the server applies on upload, applied again by the side that is going
            // to hand them to an image decoder. Shared with the uploader, in Core.
            if (bytes is null || !ImageFormats.LooksLikeAnImage(bytes))
            {
                logger.LogWarning("What {Url} served is not an image the launcher renders", address);
                return null;
            }

            return bytes;
        }
        catch (Exception exception) when (
            exception is HttpRequestException or IOException
                || (exception is OperationCanceledException
                    && !cancellationToken.IsCancellationRequested))
        {
            // A picture that will not load is a card without a picture. It is never worth
            // failing the page that was going to show it.
            logger.LogDebug(exception, "Artwork at {Url} could not be fetched", address);
            return null;
        }
    }

    /// <summary>
    /// Reads at most <see cref="MaxImageBytes"/>, whatever the response claimed: a missing or
    /// dishonest <c>Content-Length</c> must not turn into an unbounded read.
    /// </summary>
    private static async Task<byte[]?> ReadCappedAsync(
        HttpResponseMessage response, CancellationToken cancellationToken)
    {
        await using Stream body = await response.Content
            .ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);

        using MemoryStream buffer = new();
        byte[] chunk = new byte[64 * 1024];

        while (true)
        {
            int read = await body.ReadAsync(chunk, cancellationToken).ConfigureAwait(false);
            if (read == 0)
            {
                return buffer.ToArray();
            }

            if (buffer.Length + read > MaxImageBytes)
            {
                return null;
            }

            buffer.Write(chunk, 0, read);
        }
    }

    private static byte[]? ReadCached(string path)
    {
        try
        {
            return File.Exists(path) ? File.ReadAllBytes(path) : null;
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private void Store(string path, byte[] bytes)
    {
        try
        {
            Directory.CreateDirectory(paths.ImageCacheDirectory);

            // Through a temporary name, so a second launcher writing the same picture at the
            // same moment cannot leave a half-written file that reads as a corrupt image.
            string temporary = path + "." + Guid.NewGuid().ToString("N") + ".part";
            File.WriteAllBytes(temporary, bytes);
            File.Move(temporary, path, overwrite: true);

            Trim();
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException)
        {
            // The cache is an optimisation. Failing to write it is not failing to load.
            logger.LogDebug(exception, "Could not cache artwork at {Path}", path);
        }
    }

    /// <summary>
    /// Keeps the directory under the cap by dropping the least recently written entries. Runs
    /// on a miss only — a cache that is being read is a cache that is not growing.
    /// </summary>
    private void Trim()
    {
        FileInfo[] entries = new DirectoryInfo(paths.ImageCacheDirectory).GetFiles();
        long total = entries.Sum(entry => entry.Length);
        if (total <= MaxCacheBytes)
        {
            return;
        }

        foreach (FileInfo entry in entries.OrderBy(entry => entry.LastWriteTimeUtc))
        {
            if (total <= MaxCacheBytes * 8 / 10)
            {
                return;
            }

            try
            {
                long size = entry.Length;
                entry.Delete();
                total -= size;
            }
            catch (Exception exception) when (
                exception is IOException or UnauthorizedAccessException)
            {
                logger.LogDebug(exception, "Could not evict cached artwork {Name}", entry.Name);
            }
        }
    }

    /// <summary>
    /// The URL's own hash, so nothing about a remote name reaches the file system: a path is
    /// 64 hex characters whatever the server called the picture.
    /// </summary>
    private string CachePathFor(string url)
    {
        byte[] digest = SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(url));
        return Path.Combine(paths.ImageCacheDirectory, Convert.ToHexString(digest)
            .ToLower(CultureInfo.InvariantCulture));
    }
}

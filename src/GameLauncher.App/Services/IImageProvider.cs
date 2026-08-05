using System.Collections.Concurrent;
using Avalonia.Media.Imaging;
using GameLauncher.Core.Media;
using Microsoft.Extensions.Logging;

namespace GameLauncher.App.Services;

/// <summary>
/// Turns an artwork URL into something a view can show. Behind an interface for the same
/// reason the folder picker is: decoding a bitmap needs an initialised Avalonia, and a view
/// model that could not be constructed without one is a view model nobody tests.
/// </summary>
public interface IImageProvider
{
    /// <summary>
    /// Null when there is no picture — an empty URL, a fetch that failed, bytes that do not
    /// decode. Callers bind the result and show a placeholder when it is null; nothing here
    /// is worth an error message, because a missing cover is not a failure the user can act on.
    /// </summary>
    Task<Bitmap?> GetAsync(string? url, CancellationToken cancellationToken = default);
}

/// <summary>
/// Decodes once per URL for the life of the process. The bytes behind a URL never change
/// (artwork is content-addressed), so a decoded bitmap can be kept as long as it is wanted —
/// which is what keeps scrolling Explore from decoding the same cover on every pass.
/// </summary>
public sealed class CachedImageProvider(IImageLoader loader, ILogger<CachedImageProvider> logger)
    : IImageProvider
{
    private readonly ConcurrentDictionary<string, Bitmap?> _decoded = new(StringComparer.Ordinal);

    public async Task<Bitmap?> GetAsync(
        string? url, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(url))
        {
            return null;
        }

        if (_decoded.TryGetValue(url, out Bitmap? known))
        {
            return known;
        }

        byte[]? bytes = await loader.LoadAsync(url, cancellationToken).ConfigureAwait(false);
        Bitmap? bitmap = Decode(bytes, url);

        // A failure is remembered too: a cover the server does not have is not worth asking
        // for again every time the grid is scrolled past it.
        _decoded[url] = bitmap;
        return bitmap;
    }

    private Bitmap? Decode(byte[]? bytes, string url)
    {
        if (bytes is null || bytes.Length == 0)
        {
            return null;
        }

        try
        {
            using MemoryStream stream = new(bytes, writable: false);
            return new Bitmap(stream);
        }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException)
        {
            logger.LogDebug(exception, "Artwork from {Url} could not be decoded", url);
            return null;
        }
    }
}

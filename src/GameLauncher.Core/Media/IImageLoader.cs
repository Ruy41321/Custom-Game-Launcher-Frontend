namespace GameLauncher.Core.Media;

/// <summary>
/// Fetches a picture the catalog named. Artwork URLs are public and unsigned, so this never
/// carries the launcher's bearer token — the API names the host, and handing it a credential
/// would be handing a credential to whatever host it named (the reasoning of D20, again).
/// </summary>
public interface IImageLoader
{
    /// <summary>
    /// The bytes of the image, or null when there is nothing to show — an empty URL, a host
    /// that did not answer, a response too large to be a picture, or bytes that are not one.
    /// A cover that will not load is a card without a picture, never a page that fails to
    /// open, which is why this reports failure as null rather than as an exception.
    /// </summary>
    Task<byte[]?> LoadAsync(string url, CancellationToken cancellationToken = default);
}

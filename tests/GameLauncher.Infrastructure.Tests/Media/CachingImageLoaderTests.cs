using System.Net;
using GameLauncher.Infrastructure.Media;
using GameLauncher.Infrastructure.Platform;
using Microsoft.Extensions.Logging.Abstractions;

namespace GameLauncher.Infrastructure.Tests.Media;

public sealed class CachingImageLoaderTests
{
    private const string Url = "https://files.example/media/ab/cd/abcd.png";

    /// <summary>A valid, minimal PNG signature followed by filler. Only the header is read.</summary>
    private static byte[] Png(int size = 64)
    {
        byte[] bytes = new byte[size];
        byte[] signature = [0x89, (byte)'P', (byte)'N', (byte)'G', 0x0D, 0x0A, 0x1A, 0x0A];
        signature.CopyTo(bytes, 0);
        return bytes;
    }

    private static CachingImageLoader LoaderOver(
        BinaryHttpMessageHandler handler, TemporaryDirectory directory) =>
        new(
            new HttpClient(handler),
            new PathProvider(userDataDirectory: directory.Path),
            NullLogger<CachingImageLoader>.Instance);

    [Fact]
    public async Task AnImageIsFetchedOnceAndThenReadFromTheCache()
    {
        using var directory = new TemporaryDirectory();
        var handler = BinaryHttpMessageHandler.Serving(Png());
        CachingImageLoader loader = LoaderOver(handler, directory);

        byte[]? first = await loader.LoadAsync(Url, TestContext.Current.CancellationToken);
        byte[]? second = await loader.LoadAsync(Url, TestContext.Current.CancellationToken);

        Assert.NotNull(first);
        Assert.Equal(first, second);

        // The second call is the point: artwork is content-addressed, so a URL that answered
        // once never has to be asked again.
        Assert.Equal(1, handler.Requests);
    }

    // The cache survives the object, which is what makes a second launch show its covers
    // immediately.
    [Fact]
    public async Task AFreshLoaderReadsWhatAPreviousOneCached()
    {
        using var directory = new TemporaryDirectory();
        var handler = BinaryHttpMessageHandler.Serving(Png());

        await LoaderOver(handler, directory).LoadAsync(Url, TestContext.Current.CancellationToken);
        byte[]? again = await LoaderOver(handler, directory)
            .LoadAsync(Url, TestContext.Current.CancellationToken);

        Assert.NotNull(again);
        Assert.Equal(1, handler.Requests);
    }

    [Fact]
    public async Task NothingAboutTheRemoteNameReachesTheFileSystem()
    {
        using var directory = new TemporaryDirectory();
        var handler = BinaryHttpMessageHandler.Serving(Png());
        CachingImageLoader loader = LoaderOver(handler, directory);

        await loader.LoadAsync(
            "https://files.example/media/../../etc/passwd.png",
            TestContext.Current.CancellationToken);

        string[] cached = Directory.GetFiles(
            new PathProvider(userDataDirectory: directory.Path).ImageCacheDirectory);

        string name = Path.GetFileName(Assert.Single(cached));
        Assert.Equal(64, name.Length);
        Assert.All(name, character => Assert.Contains(character, "0123456789abcdef"));
    }

    [Fact]
    public async Task ARefusedRequestIsNoPictureRatherThanAFailure()
    {
        using var directory = new TemporaryDirectory();
        var handler = BinaryHttpMessageHandler.Answering(HttpStatusCode.NotFound, []);

        byte[]? bytes = await LoaderOver(handler, directory)
            .LoadAsync(Url, TestContext.Current.CancellationToken);

        Assert.Null(bytes);
    }

    [Fact]
    public async Task AnUnreachableHostIsNoPictureRatherThanAFailure()
    {
        using var directory = new TemporaryDirectory();
        var handler = BinaryHttpMessageHandler.Throwing(new HttpRequestException("no route"));

        byte[]? bytes = await LoaderOver(handler, directory)
            .LoadAsync(Url, TestContext.Current.CancellationToken);

        Assert.Null(bytes);
    }

    // What the bytes are is decided by the bytes. The declared type is the uploader's claim,
    // and these bytes are about to be handed to an image decoder.
    [Fact]
    public async Task SomethingThatIsNotAnImageIsRefusedWhateverItClaimsToBe()
    {
        using var directory = new TemporaryDirectory();
        var handler = BinaryHttpMessageHandler.Serving(
            "<svg xmlns='http://www.w3.org/2000/svg'><script/></svg>"u8.ToArray(), "image/png");

        byte[]? bytes = await LoaderOver(handler, directory)
            .LoadAsync(Url, TestContext.Current.CancellationToken);

        Assert.Null(bytes);
    }

    [Fact]
    public async Task JpegAndWebpAreImagesToo()
    {
        using var directory = new TemporaryDirectory();

        byte[] jpeg = [0xFF, 0xD8, 0xFF, 0xE0, 0, 0, 0, 0];
        byte[] webp = [.. "RIFF"u8, 0, 0, 0, 0, .. "WEBP"u8, 0, 0, 0, 0];

        using var jpegDirectory = new TemporaryDirectory();
        Assert.NotNull(await LoaderOver(BinaryHttpMessageHandler.Serving(jpeg), jpegDirectory)
            .LoadAsync(Url, TestContext.Current.CancellationToken));

        Assert.NotNull(await LoaderOver(BinaryHttpMessageHandler.Serving(webp), directory)
            .LoadAsync(Url, TestContext.Current.CancellationToken));
    }

    // A dishonest or missing Content-Length must not turn into an unbounded read.
    [Fact]
    public async Task AResponseLargerThanAPictureIsAbandoned()
    {
        using var directory = new TemporaryDirectory();
        var handler = BinaryHttpMessageHandler.ServingUncounted(
            Png((int)CachingImageLoader.MaxImageBytes + 1024));

        byte[]? bytes = await LoaderOver(handler, directory)
            .LoadAsync(Url, TestContext.Current.CancellationToken);

        Assert.Null(bytes);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("not a url")]
    [InlineData("file:///C:/Windows/System32/config/SAM")]
    [InlineData("ftp://files.example/cover.png")]
    public async Task OnlyAnHttpUrlIsEverFetched(string url)
    {
        using var directory = new TemporaryDirectory();
        var handler = BinaryHttpMessageHandler.Serving(Png());

        byte[]? bytes = await LoaderOver(handler, directory)
            .LoadAsync(url, TestContext.Current.CancellationToken);

        Assert.Null(bytes);
        Assert.Equal(0, handler.Requests);
    }

    [Fact]
    public async Task TwoUrlsAreTwoCacheEntries()
    {
        using var directory = new TemporaryDirectory();
        var handler = BinaryHttpMessageHandler.Serving(Png());
        CachingImageLoader loader = LoaderOver(handler, directory);

        await loader.LoadAsync(Url, TestContext.Current.CancellationToken);
        await loader.LoadAsync(
            "https://files.example/media/ef/01/ef01.png", TestContext.Current.CancellationToken);

        Assert.Equal(2, Directory.GetFiles(
            new PathProvider(userDataDirectory: directory.Path).ImageCacheDirectory).Length);
        Assert.Equal(2, handler.Requests);
    }
}

/// <summary>
/// Serves bytes rather than JSON, which is what an artwork host does. Counts requests, because
/// what most of these tests are actually about is how often one is made.
/// </summary>
internal sealed class BinaryHttpMessageHandler : HttpMessageHandler
{
    private readonly Func<HttpResponseMessage> _respond;

    private BinaryHttpMessageHandler(Func<HttpResponseMessage> respond) => _respond = respond;

    public int Requests { get; private set; }

    /// <summary>What the last request carried, which for artwork must be nothing at all.</summary>
    public string? LastAuthorization { get; private set; }

    public static BinaryHttpMessageHandler Serving(byte[] bytes, string contentType = "image/png")
        => Answering(HttpStatusCode.OK, bytes, contentType);

    public static BinaryHttpMessageHandler Answering(
        HttpStatusCode status, byte[] bytes, string contentType = "image/png") =>
        new(() =>
        {
            var content = new ByteArrayContent(bytes);
            content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue(contentType);
            return new HttpResponseMessage(status) { Content = content };
        });

    /// <summary>
    /// The same bytes with no <c>Content-Length</c>, so the cap has to be enforced while
    /// reading rather than by trusting a header.
    /// </summary>
    public static BinaryHttpMessageHandler ServingUncounted(byte[] bytes) =>
        new(() => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StreamContent(new UnsizedStream(bytes)),
        });

    public static BinaryHttpMessageHandler Throwing(Exception exception) =>
        new(() => throw exception);

    protected override Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request, CancellationToken cancellationToken)
    {
        Requests++;
        LastAuthorization = request.Headers.Authorization?.ToString();
        return Task.FromResult(_respond());
    }
}

/// <summary>
/// A stream that will not say how long it is, so <c>Content-Length</c> is absent and the size
/// cap has to be enforced while reading.
/// </summary>
internal sealed class UnsizedStream(byte[] bytes) : Stream
{
    private readonly MemoryStream _inner = new(bytes);

    public override bool CanRead => true;

    public override bool CanSeek => false;

    public override bool CanWrite => false;

    public override long Length => throw new NotSupportedException();

    public override long Position
    {
        get => throw new NotSupportedException();
        set => throw new NotSupportedException();
    }

    public override int Read(byte[] buffer, int offset, int count) =>
        _inner.Read(buffer, offset, count);

    public override void Flush() => _inner.Flush();

    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

    public override void SetLength(long value) => throw new NotSupportedException();

    public override void Write(byte[] buffer, int offset, int count) =>
        throw new NotSupportedException();

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _inner.Dispose();
        }

        base.Dispose(disposing);
    }
}

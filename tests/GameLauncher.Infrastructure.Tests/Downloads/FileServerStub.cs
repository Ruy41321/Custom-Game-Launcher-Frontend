using System.Net;
using System.Net.Http.Headers;

namespace GameLauncher.Infrastructure.Tests.Downloads;

/// <summary>
/// Stands in for nginx. It answers <c>Range</c> the way the module does — 206 with a
/// <c>Content-Range</c>, or the whole thing — and records what it was asked, because the point
/// of the fetcher is which request it sends after an interruption.
/// </summary>
internal sealed class FileServerStub : HttpMessageHandler
{
    private readonly List<Func<HttpRequestMessage, HttpResponseMessage>> _responses;

    private FileServerStub(IEnumerable<Func<HttpRequestMessage, HttpResponseMessage>> responses) =>
        _responses = [.. responses];

    public List<FileServerRequest> Requests { get; } = [];

    /// <summary>Honours <c>Range</c>, as the real file server does.</summary>
    public static FileServerStub Serving(byte[] content) =>
        new([request => RangeAware(request, content)]);

    /// <summary>Answers the full body whatever was asked, which a naive server would.</summary>
    public static FileServerStub IgnoringRange(byte[] content) =>
        new([_ => Whole(content)]);

    public static FileServerStub Refusing(HttpStatusCode status) =>
        new([_ => new HttpResponseMessage(status)]);

    /// <summary>One entry per attempt; the last one answers every attempt after it.</summary>
    public static FileServerStub Answering(
        params Func<HttpRequestMessage, HttpResponseMessage>[] responses) => new(responses);

    public static Func<HttpRequestMessage, HttpResponseMessage> Body(byte[] content) =>
        request => RangeAware(request, content);

    public static Func<HttpRequestMessage, HttpResponseMessage> Status(HttpStatusCode status) =>
        _ => new HttpResponseMessage(status);

    protected override Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request, CancellationToken cancellationToken)
    {
        Requests.Add(new FileServerRequest(
            request.RequestUri!,
            request.Headers.Range?.Ranges.FirstOrDefault()?.From,
            request.Headers.Authorization?.ToString()));

        Func<HttpRequestMessage, HttpResponseMessage> respond =
            _responses[Math.Min(Requests.Count - 1, _responses.Count - 1)];

        return Task.FromResult(respond(request));
    }

    private static HttpResponseMessage RangeAware(HttpRequestMessage request, byte[] content)
    {
        long? from = request.Headers.Range?.Ranges.FirstOrDefault()?.From;
        if (from is not { } offset)
        {
            return Whole(content);
        }

        if (offset >= content.Length)
        {
            return new HttpResponseMessage(HttpStatusCode.RequestedRangeNotSatisfiable);
        }

        byte[] slice = content[(int)offset..];
        var response = new HttpResponseMessage(HttpStatusCode.PartialContent)
        {
            Content = new ByteArrayContent(slice),
        };
        response.Content.Headers.ContentRange =
            new ContentRangeHeaderValue(offset, content.Length - 1, content.Length);
        return response;
    }

    private static HttpResponseMessage Whole(byte[] content) =>
        new(HttpStatusCode.OK) { Content = new ByteArrayContent(content) };
}

internal sealed record FileServerRequest(Uri Uri, long? RangeFrom, string? Authorization);

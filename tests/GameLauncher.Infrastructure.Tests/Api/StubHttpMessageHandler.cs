using System.Net;
using System.Text;

namespace GameLauncher.Infrastructure.Tests.Api;

/// <summary>
/// Answers whatever the test says, and records what was asked. Testing the API client against
/// a real server would test the server; what needs proving here is that this client sends the
/// right request and understands the documented response.
/// </summary>
internal sealed class StubHttpMessageHandler : HttpMessageHandler
{
    private readonly Func<HttpRequestMessage, HttpResponseMessage> _respond;

    /// <summary>How long the answer is withheld. A stopped backend behind a proxy does this.</summary>
    private readonly TimeSpan _silence;

    private StubHttpMessageHandler(
        Func<HttpRequestMessage, HttpResponseMessage> respond, TimeSpan? silence = null)
    {
        _respond = respond;
        _silence = silence ?? TimeSpan.Zero;
    }

    /// <summary>Every request that reached the handler, in order.</summary>
    public List<RecordedRequest> Requests { get; } = [];

    public RecordedRequest LastRequest => Requests[^1];

    public static StubHttpMessageHandler RespondingWith(
        HttpStatusCode status, string json, params (string Name, string Value)[] headers)
    {
        return new StubHttpMessageHandler(_ =>
        {
            var response = new HttpResponseMessage(status)
            {
                Content = new StringContent(json, Encoding.UTF8, "application/json"),
            };

            foreach ((string name, string value) in headers)
            {
                response.Headers.TryAddWithoutValidation(name, value);
            }

            return response;
        });
    }

    public static StubHttpMessageHandler RespondingWithBody(
        HttpStatusCode status, string body, string contentType) =>
        new(_ => new HttpResponseMessage(status)
        {
            Content = new StringContent(body, Encoding.UTF8, contentType),
        });

    public static StubHttpMessageHandler Throwing(Exception exception) =>
        new(_ => throw exception);

    /// <summary>Accepts the request and never answers, until somebody gives up.</summary>
    public static StubHttpMessageHandler Hanging() =>
        HangingFor(Timeout.InfiniteTimeSpan);

    /// <summary>Answers, eventually.</summary>
    public static StubHttpMessageHandler HangingFor(TimeSpan silence) =>
        new(
            _ => new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("{}", Encoding.UTF8, "application/json"),
            },
            silence);

    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request, CancellationToken cancellationToken)
    {
        // Captured as bytes as well as text: an image upload is a binary body, and reading it
        // back as UTF-8 mangles exactly the leading bytes a test about image formats is for.
        byte[]? bytes = request.Content is null
            ? null
            : await request.Content.ReadAsByteArrayAsync(cancellationToken).ConfigureAwait(false);

        string? body = bytes is null ? null : Encoding.UTF8.GetString(bytes);

        Dictionary<string, string> headers = request.Headers.ToDictionary(
            header => header.Key,
            header => string.Join(", ", header.Value),
            StringComparer.OrdinalIgnoreCase);

        Requests.Add(new RecordedRequest(
            request.Method,
            request.RequestUri!,
            body,
            request.Headers.Authorization?.ToString(),
            headers,
            request.Content?.Headers.ContentType?.MediaType,
            bytes));

        if (_silence != TimeSpan.Zero)
        {
            await Task.Delay(_silence, cancellationToken).ConfigureAwait(false);
        }

        return _respond(request);
    }
}

internal sealed record RecordedRequest(
    HttpMethod Method,
    Uri Uri,
    string? Body,
    string? Authorization,
    IReadOnlyDictionary<string, string> Headers,
    string? ContentType,
    byte[]? Bytes = null)
{
    /// <summary>Path and query, which is what a route assertion is actually about.</summary>
    public string PathAndQuery => Uri.PathAndQuery;

    public string? Header(string name) =>
        Headers.TryGetValue(name, out string? value) ? value : null;
}

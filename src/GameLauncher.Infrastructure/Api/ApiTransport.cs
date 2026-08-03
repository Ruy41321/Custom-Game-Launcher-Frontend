using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using GameLauncher.Core.Api;
using GameLauncher.Infrastructure.Configuration;

namespace GameLauncher.Infrastructure.Api;

/// <summary>
/// The one place that knows the API speaks HTTP. Every failure — a refused connection, a
/// truncated body, an error envelope — leaves here as an <see cref="ApiException"/>, so the
/// resource clients above read as plain method calls and no caller ever sees an
/// <see cref="HttpRequestException"/>.
/// </summary>
internal sealed class ApiTransport(HttpClient httpClient)
{
    public async Task<T> GetAsync<T>(string path, CancellationToken cancellationToken)
        => await SendAsync<T>(new HttpRequestMessage(HttpMethod.Get, path), cancellationToken)
            .ConfigureAwait(false);

    public async Task<T> PostAsync<T>(string path, object body, CancellationToken cancellationToken)
        => await SendAsync<T>(Request(HttpMethod.Post, path, body), cancellationToken)
            .ConfigureAwait(false);

    public Task PostAsync(string path, object body, CancellationToken cancellationToken)
        => SendAsync(Request(HttpMethod.Post, path, body), cancellationToken);

    public Task PutAsync(string path, CancellationToken cancellationToken)
        => SendAsync(new HttpRequestMessage(HttpMethod.Put, path), cancellationToken);

    public Task DeleteAsync(string path, CancellationToken cancellationToken)
        => SendAsync(new HttpRequestMessage(HttpMethod.Delete, path), cancellationToken);

    private static HttpRequestMessage Request(HttpMethod method, string path, object body) =>
        new(method, path)
        {
            Content = JsonContent.Create(body, body.GetType(), options: LauncherJsonSerializer.Compact),
        };

    private async Task<T> SendAsync<T>(
        HttpRequestMessage request, CancellationToken cancellationToken)
    {
        using HttpResponseMessage response = await SendCoreAsync(request, cancellationToken)
            .ConfigureAwait(false);

        try
        {
            T? payload = await response.Content
                .ReadFromJsonAsync<T>(LauncherJsonSerializer.Compact, cancellationToken)
                .ConfigureAwait(false);

            return payload ?? throw new ApiException(
                ApiErrorCode.Unknown,
                "The server returned an empty body where a document was expected.",
                (int)response.StatusCode,
                RequestIdOf(response));
        }
        catch (JsonException exception)
        {
            throw new ApiException(
                ApiErrorCode.Unknown,
                "The server's response could not be understood.",
                (int)response.StatusCode,
                RequestIdOf(response),
                innerException: exception);
        }
    }

    /// <summary>For the endpoints whose success body says nothing the client acts on.</summary>
    private async Task SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        HttpResponseMessage response = await SendCoreAsync(request, cancellationToken)
            .ConfigureAwait(false);
        response.Dispose();
    }

    private async Task<HttpResponseMessage> SendCoreAsync(
        HttpRequestMessage request, CancellationToken cancellationToken)
    {
        HttpResponseMessage response;
        try
        {
            response = await httpClient
                .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (HttpRequestException exception)
        {
            throw new ApiException(
                ApiErrorCode.Network,
                "The server could not be reached.",
                innerException: exception);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            // The caller did not cancel, so this is the HttpClient timeout. Reporting it as a
            // cancellation would make a dead server look like a user action.
            throw new ApiException(ApiErrorCode.Network, "The server did not respond in time.");
        }
        finally
        {
            request.Dispose();
        }

        if (response.IsSuccessStatusCode)
        {
            return response;
        }

        using (response)
        {
            throw await ErrorFor(response, cancellationToken).ConfigureAwait(false);
        }
    }

    private static async Task<ApiException> ErrorFor(
        HttpResponseMessage response, CancellationToken cancellationToken)
    {
        int status = (int)response.StatusCode;
        string? requestId = RequestIdOf(response);
        ApiProblem? problem = null;

        try
        {
            problem = await response.Content
                .ReadFromJsonAsync<ApiProblem>(LauncherJsonSerializer.Compact, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is JsonException or NotSupportedException)
        {
            // Not every error response comes from the API: a proxy in front of it answers in
            // HTML, and the status is then all there is to go on.
        }

        return new ApiException(
            problem?.ToErrorCode(status) ?? new ApiProblem().ToErrorCode(status),
            problem?.Detail is { Length: > 0 } detail
                ? detail
                : problem?.Title ?? response.ReasonPhrase ?? "The request failed.",
            status,
            problem?.RequestId ?? requestId,
            RetryAfterOf(response));
    }

    private static string? RequestIdOf(HttpResponseMessage response) =>
        response.Headers.TryGetValues("X-Request-Id", out IEnumerable<string>? values)
            ? values.FirstOrDefault()
            : null;

    /// <summary>
    /// <c>Retry-After</c> is either a delay in seconds or an HTTP date. The throttling filter
    /// sends the former, but a proxy in front of the API may well send the latter.
    /// </summary>
    private static TimeSpan? RetryAfterOf(HttpResponseMessage response)
    {
        if (response.StatusCode != HttpStatusCode.TooManyRequests)
        {
            return null;
        }

        System.Net.Http.Headers.RetryConditionHeaderValue? header = response.Headers.RetryAfter;
        if (header?.Delta is { } delta)
        {
            return delta;
        }

        if (header?.Date is { } date)
        {
            TimeSpan remaining = date - DateTimeOffset.UtcNow;
            return remaining > TimeSpan.Zero ? remaining : TimeSpan.Zero;
        }

        return null;
    }
}

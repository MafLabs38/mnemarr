using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Options;
using Mnemarr.Core.Chat;

namespace Mnemarr.Api.V1.Chat;

/// <summary>What the runner answered: its status code and JSON body, passed through to the page unchanged.</summary>
public sealed record AgentRunnerResponse(int StatusCode, string? Json);

/// <summary>
/// Talks to the assistant runner (ADR 0035): its address and shared secret come from <see cref="AgentOptions"/>, nothing from the browser's request is forwarded (no cookie, no
/// header), and the runner owns the shape of what it returns.
/// </summary>
public sealed class AgentRunnerClient
{
    /// <summary>Answers to everything but the event stream come quickly; a runner that hangs is a 502.</summary>
    public static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(30);

    private readonly HttpClient _http;
    private readonly AgentOptions _options;

    [System.Diagnostics.CodeAnalysis.SuppressMessage("Minor Code Smell", "S1075:URIs should not be hardcoded", Justification = "The slash trimmed is a URL separator, not a file system path.")]
    public AgentRunnerClient(HttpClient http, IOptions<AgentOptions> options)
    {
        _http = http;
        _options = options.Value;
        if (_options.Enabled)
        {
            _http.BaseAddress = new Uri(_options.Url!.TrimEnd('/') + "/");
        }

        // The stream stays open for as long as the page shows a conversation; the other calls get their own timeout.
        _http.Timeout = Timeout.InfiniteTimeSpan;
    }

    public async Task<AgentRunnerResponse> SendAsync(HttpMethod method, string path, object? body, CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(RequestTimeout);
        using var request = Request(method, path);
        if (body is not null)
        {
            request.Content = new StringContent(JsonSerializer.Serialize(body, JsonSerializerOptions.Web), Encoding.UTF8, "application/json");
        }

        using var response = await _http.SendAsync(request, timeout.Token);
        var json = response.Content.Headers.ContentType?.MediaType == "application/json" ? await response.Content.ReadAsStringAsync(timeout.Token) : null;
        return new AgentRunnerResponse((int)response.StatusCode, json);
    }

    /// <summary>The conversation's server-sent events; the caller copies the body and disposes the response.</summary>
    public async Task<HttpResponseMessage> OpenStreamAsync(string path, string? lastEventId, CancellationToken cancellationToken)
    {
        using var request = Request(HttpMethod.Get, path);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("text/event-stream"));
        if (lastEventId is not null)
        {
            request.Headers.TryAddWithoutValidation("Last-Event-ID", lastEventId);
        }

        // A runner that never sends its headers must not hold the request: the headers get the request timeout, the body (open as long as the page shows the conversation) only the caller's token.
        using var headers = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        headers.CancelAfter(RequestTimeout);
        return await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, headers.Token);
    }

    private HttpRequestMessage Request(HttpMethod method, string path)
    {
        if (!_options.Enabled)
        {
            throw new InvalidOperationException("The assistant runner is not configured.");
        }

        var request = new HttpRequestMessage(method, path.TrimStart('/'));
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _options.Secret);
        return request;
    }
}

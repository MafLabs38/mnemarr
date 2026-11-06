using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.Options;

namespace Mnemarr.Core.Titles;

/// <summary>A model that writes the title of a conversation: the provider is behind this, so that another one is a class and not a change (ADR 0028).</summary>
public interface ITitleWriter
{
    /// <summary>The title and the summary of a conversation told as a transcript, or null when the model did not give a usable answer. Throws <see cref="TitleWriterException"/> when it could not be asked.</summary>
    Task<GeneratedTitle?> WriteAsync(string transcript, CancellationToken cancellationToken);
}

/// <summary>Where a call goes: the configured address is a base that may carry a path (<c>https://host/api/v1</c>), and the call is joined to it, not put at the root.</summary>
internal static class TitleAddress
{
    // The separator of an address is a slash on every system, whatever the system's own separator is.
    private static readonly char Slash = Path.AltDirectorySeparatorChar;

    public static Uri Join(string? baseAddress, string relative) => new(new Uri((baseAddress ?? string.Empty).TrimEnd(Slash) + Slash), relative);
}

/// <summary>The model could not be asked, or refused: nothing is said of the answer, which could carry what was sent.</summary>
public sealed class TitleWriterException : Exception
{
    public TitleWriterException(string message)
        : base(message)
    {
    }

    public TitleWriterException(string message, Exception inner)
        : base(message, inner)
    {
    }
}

/// <summary>The Messages API of Anthropic: one call for a title and a summary, with the key read from its file at each call (a rotation needs no restart).</summary>
public sealed class AnthropicTitleWriter : ITitleWriter
{
    internal const string DefaultEndpoint = "https://api.anthropic.com";
    private const int MaxTokens = 300;
    private readonly IOptions<TitleOptions> _options;
    private readonly HttpClient _http;

    public AnthropicTitleWriter(IOptions<TitleOptions> options)
        : this(options, new SocketsHttpHandler { AllowAutoRedirect = false })
    {
    }

    /// <summary>For a test: the handler stands in for the network.</summary>
    internal AnthropicTitleWriter(IOptions<TitleOptions> options, HttpMessageHandler handler)
    {
        _options = options;
        // The key is in a header: a redirect must not carry it somewhere else.
        _http = new HttpClient(handler, disposeHandler: true) { Timeout = TimeSpan.FromSeconds(60) };
    }

    public async Task<GeneratedTitle?> WriteAsync(string transcript, CancellationToken cancellationToken)
    {
        var options = _options.Value;
        string key;
        try
        {
            key = (await File.ReadAllTextAsync(options.ApiKeyFile ?? string.Empty, cancellationToken)).Trim();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            throw new TitleWriterException("The file of the API key cannot be read.", ex);
        }

        using var request = new HttpRequestMessage(HttpMethod.Post, TitleAddress.Join(options.Endpoint ?? AnthropicTitleWriter.DefaultEndpoint, "v1/messages"));
        request.Headers.Add("x-api-key", key);
        request.Headers.Add("anthropic-version", "2023-06-01");
        request.Content = JsonContent.Create(new
        {
            model = options.Model,
            max_tokens = MaxTokens,
            system = TitlePrompt.System,
            messages = new[] { new { role = "user", content = transcript } },
        });
        try
        {
            using var response = await _http.SendAsync(request, cancellationToken);
            if (response.StatusCode != HttpStatusCode.OK)
            {
                throw new TitleWriterException($"The model answered {(int)response.StatusCode}.");
            }

            var answer = await response.Content.ReadFromJsonAsync<JsonElement>(cancellationToken);
            var text = answer.TryGetProperty("content", out var content) && content.ValueKind == JsonValueKind.Array && content.GetArrayLength() > 0 && content[0].TryGetProperty("text", out var first)
                ? first.GetString()
                : null;
            return TitlePrompt.Parse(text);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException && !cancellationToken.IsCancellationRequested)
        {
            throw new TitleWriterException("The model could not be reached.", ex);
        }
    }
}

/// <summary>
/// A companion agent that runs an agent CLI (Claude Code) with the account's own login, the way Portarr's assistant does (ADR 0028): one call for a title and a summary.
/// The agent speaks a tiny protocol, <c>POST /title</c> with <c>{model, system, transcript}</c> and a bearer secret, and answers <c>{text}</c>. The secret is read at each call.
/// </summary>
public sealed class AgentTitleWriter : ITitleWriter
{
    private readonly IOptions<TitleOptions> _options;
    private readonly HttpClient _http;

    public AgentTitleWriter(IOptions<TitleOptions> options)
        : this(options, new SocketsHttpHandler { AllowAutoRedirect = false })
    {
    }

    /// <summary>For a test: the handler stands in for the network.</summary>
    internal AgentTitleWriter(IOptions<TitleOptions> options, HttpMessageHandler handler)
    {
        _options = options;
        // The secret is in a header: a redirect must not carry it somewhere else. The agent runs a model: it is slower than an API.
        _http = new HttpClient(handler, disposeHandler: true) { Timeout = TimeSpan.FromSeconds(120) };
    }

    public async Task<GeneratedTitle?> WriteAsync(string transcript, CancellationToken cancellationToken)
    {
        var options = _options.Value;
        string secret;
        try
        {
            secret = (await File.ReadAllTextAsync(options.AgentSecretFile ?? string.Empty, cancellationToken)).Trim();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            throw new TitleWriterException("The file of the agent secret cannot be read.", ex);
        }

        using var request = new HttpRequestMessage(HttpMethod.Post, TitleAddress.Join(options.AgentUrl, "title"));
        request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", secret);
        request.Content = JsonContent.Create(new { model = options.Model, system = TitlePrompt.System, transcript });
        try
        {
            using var response = await _http.SendAsync(request, cancellationToken);
            if (response.StatusCode != HttpStatusCode.OK)
            {
                throw new TitleWriterException($"The agent answered {(int)response.StatusCode}.");
            }

            var answer = await response.Content.ReadFromJsonAsync<JsonElement>(cancellationToken);
            var text = answer.ValueKind == JsonValueKind.Object && answer.TryGetProperty("text", out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;
            return TitlePrompt.Parse(text);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException && !cancellationToken.IsCancellationRequested)
        {
            throw new TitleWriterException("The agent could not be reached.", ex);
        }
    }
}

/// <summary>
/// Any API that speaks the chat completions protocol of OpenAI: OpenAI itself, and the servers that copy it (Ollama, LM Studio, vLLM, OpenRouter…), so a local model works too.
/// The key is optional (a local server has none) and is read from its file at each call.
/// </summary>
public sealed class OpenAiTitleWriter : ITitleWriter
{
    private const int MaxTokens = 300;
    private readonly IOptions<TitleOptions> _options;
    private readonly HttpClient _http;

    public OpenAiTitleWriter(IOptions<TitleOptions> options)
        : this(options, new SocketsHttpHandler { AllowAutoRedirect = false })
    {
    }

    /// <summary>For a test: the handler stands in for the network.</summary>
    internal OpenAiTitleWriter(IOptions<TitleOptions> options, HttpMessageHandler handler)
    {
        _options = options;
        // The key is in a header: a redirect must not carry it somewhere else. A local model can be slow.
        _http = new HttpClient(handler, disposeHandler: true) { Timeout = TimeSpan.FromSeconds(120) };
    }

    public async Task<GeneratedTitle?> WriteAsync(string transcript, CancellationToken cancellationToken)
    {
        var options = _options.Value;
        using var request = new HttpRequestMessage(HttpMethod.Post, TitleAddress.Join(options.Endpoint, "chat/completions"));
        if (!string.IsNullOrWhiteSpace(options.ApiKeyFile))
        {
            try
            {
                var key = (await File.ReadAllTextAsync(options.ApiKeyFile, cancellationToken)).Trim();
                request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", key);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
            {
                throw new TitleWriterException("The file of the API key cannot be read.", ex);
            }
        }

        request.Content = JsonContent.Create(new
        {
            model = options.Model,
            max_tokens = MaxTokens,
            messages = new[] { new { role = "system", content = TitlePrompt.System }, new { role = "user", content = transcript } },
        });
        try
        {
            using var response = await _http.SendAsync(request, cancellationToken);
            if (response.StatusCode != HttpStatusCode.OK)
            {
                throw new TitleWriterException($"The model answered {(int)response.StatusCode}.");
            }

            var answer = await response.Content.ReadFromJsonAsync<JsonElement>(cancellationToken);
            return TitlePrompt.Parse(FirstMessage(answer));
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException && !cancellationToken.IsCancellationRequested)
        {
            throw new TitleWriterException("The model could not be reached.", ex);
        }
    }

    private static string? FirstMessage(JsonElement answer) =>
        answer.ValueKind == JsonValueKind.Object
        && answer.TryGetProperty("choices", out var choices) && choices.ValueKind == JsonValueKind.Array && choices.GetArrayLength() > 0
        && choices[0].TryGetProperty("message", out var message) && message.TryGetProperty("content", out var content) && content.ValueKind == JsonValueKind.String
            ? content.GetString()
            : null;
}

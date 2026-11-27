using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using Microsoft.Extensions.Options;

namespace Mnemarr.Core.Transcription;

/// <summary>The <c>OpenAiCompatible</c> provider: OpenAI's <c>/audio/transcriptions</c>, or any service that speaks it at another address (ADR 0047).</summary>
public sealed class OpenAiCompatibleTranscriptionProvider : ITranscriptionProvider
{
    /// <summary>OpenAI's models, in the order of preference: the first is the default.</summary>
    public static IReadOnlyList<TranscriptionModel> Models { get; } =
    [
        new("gpt-transcribe", "GPT Transcribe", 0.0045m),
        new("gpt-4o-mini-transcribe", "GPT-4o mini Transcribe", 0.003m),
        new("gpt-4o-transcribe", "GPT-4o Transcribe", 0.006m),
        new("whisper-1", "Whisper", 0.006m),
    ];

    public string Name => TranscriptionProviders.OpenAiCompatible;

    public IReadOnlyList<TranscriptionModel> DefaultModels => Models;

    public string? Validate(TranscriptionOptions options)
    {
        var settings = options.OpenAiCompatible;
        if (!Uri.TryCreate(settings.BaseUrl, UriKind.Absolute, out var address) || (address.Scheme != Uri.UriSchemeHttps && address.Scheme != Uri.UriSchemeHttp) || !string.IsNullOrEmpty(address.UserInfo))
        {
            return "Transcription:OpenAiCompatible:BaseUrl is an http or https address without a user, such as https://api.openai.com/v1.";
        }

        if (address.Scheme == Uri.UriSchemeHttp && !string.IsNullOrWhiteSpace(settings.KeyFile))
        {
            return "Transcription:OpenAiCompatible:BaseUrl must be https when a key is used: the key would be sent in clear.";
        }

        return settings.TimeoutSeconds is < 5 or > 600 ? "Transcription:OpenAiCompatible:TimeoutSeconds is 5 to 600." : null;
    }

    public ITranscriber Create(IOptions<TranscriptionOptions> options) => new OpenAiCompatibleTranscriber(options);

    /// <summary>The key file is opened for reading, as a call does: a file that exists but cannot be read is reported too.</summary>
    public string? CheckSecrets(TranscriptionOptions options)
    {
        var file = options.OpenAiCompatible.KeyFile;
        if (string.IsNullOrWhiteSpace(file))
        {
            return null;
        }

        try
        {
            using var stream = File.OpenRead(file);
            return null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return "The file of the transcription key cannot be read.";
        }
    }

    public string? Destination(TranscriptionOptions options) =>
        Uri.TryCreate(options.OpenAiCompatible.BaseUrl, UriKind.Absolute, out var address) ? address.Host : null;

    public string Setup(TranscriptionOptions options)
    {
        var settings = options.OpenAiCompatible;
        return $"{settings.BaseUrl}|{settings.KeyFile}|{KeyFileState(settings.KeyFile)}";
    }

    /// <summary>When the key file last changed and how long it is, never what it holds: "none" without one, "unreadable" for a path that cannot be looked at.</summary>
    private static string KeyFileState(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return "none";
        }

        try
        {
            var key = new FileInfo(path);
            return key.Exists ? string.Create(CultureInfo.InvariantCulture, $"{key.LastWriteTimeUtc.Ticks}:{key.Length}") : "none";
        }
        catch (Exception ex) when (ex is ArgumentException or IOException or UnauthorizedAccessException or NotSupportedException)
        {
            return "unreadable";
        }
    }
}

/// <summary>Sends one recording as a multipart form and reads <c>{ "text": … }</c> back. No redirect is followed: the key must not be sent to another address.</summary>
internal sealed class OpenAiCompatibleTranscriber : ITranscriber
{
    private static readonly Dictionary<string, string> Extensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ["audio/webm"] = "webm",
        ["audio/ogg"] = "ogg",
        ["audio/mp4"] = "m4a",
        ["audio/mpeg"] = "mp3",
        ["audio/wav"] = "wav",
        ["audio/x-wav"] = "wav",
    };

    private readonly IOptions<TranscriptionOptions> _options;
    private readonly HttpClient _http;

    public OpenAiCompatibleTranscriber(IOptions<TranscriptionOptions> options, HttpMessageHandler? handler = null)
    {
        _options = options;
        _http = new HttpClient(handler ?? new SocketsHttpHandler { AllowAutoRedirect = false, PooledConnectionLifetime = TimeSpan.FromMinutes(2) })
        {
            // The call is timed by the token below; an answer is a few kilobytes of text.
            Timeout = Timeout.InfiniteTimeSpan,
            MaxResponseContentBufferSize = 1024 * 1024,
        };
    }

    public async Task<TranscriptionResult> TranscribeAsync(TranscriptionRequest request, CancellationToken cancellationToken)
    {
        var settings = _options.Value.OpenAiCompatible;
        string? key;
        try
        {
            key = string.IsNullOrWhiteSpace(settings.KeyFile) ? null : (await File.ReadAllTextAsync(settings.KeyFile, cancellationToken)).Trim();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return TranscriptionResult.Failed("The file of the transcription key cannot be read.");
        }

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(settings.TimeoutSeconds));
        try
        {
            using var message = Build(request, settings.BaseUrl, key);
            using var response = await _http.SendAsync(message, timeout.Token);
            if (!response.IsSuccessStatusCode)
            {
                return Refusal(response.StatusCode);
            }

            return Read(await response.Content.ReadAsStringAsync(timeout.Token));
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return TranscriptionResult.Failed("The transcription service did not answer in time.");
        }
        catch (HttpRequestException)
        {
            return TranscriptionResult.Failed("The transcription service cannot be reached.");
        }
    }

    private static HttpRequestMessage Build(TranscriptionRequest request, string baseUrl, string? key)
    {
        var mediaType = request.ContentType.Split(';')[0].Trim();
        var audio = new ReadOnlyMemoryContent(request.Audio);
        audio.Headers.ContentType = new MediaTypeHeaderValue(mediaType);
        // The names are quoted, as browsers and the services' own clients send them: some servers do not read an unquoted name.
        audio.Headers.ContentDisposition = new ContentDispositionHeaderValue("form-data")
        {
            Name = "\"file\"",
            FileName = $"\"recording.{Extensions.GetValueOrDefault(mediaType, "webm")}\"",
        };
        var form = new MultipartFormDataContent { audio, Field("model", request.Model), Field("response_format", "json") };
        if (!string.IsNullOrWhiteSpace(request.Language))
        {
            form.Add(Field("language", request.Language));
        }

        var message = new HttpRequestMessage(HttpMethod.Post, $"{baseUrl.TrimEnd('/')}/audio/transcriptions") { Content = form };
        if (key is { Length: > 0 })
        {
            message.Headers.Authorization = new AuthenticationHeaderValue("Bearer", key);
        }

        return message;
    }

    private static StringContent Field(string name, string value)
    {
        var content = new StringContent(value);
        content.Headers.ContentType = null;
        content.Headers.ContentDisposition = new ContentDispositionHeaderValue("form-data") { Name = $"\"{name}\"" };
        return content;
    }

    /// <summary>What a refusal means, and whose it is: a refused key, a spent quota, a wrong address or a failing service are the operator's to fix; any other refusal is about the recording.</summary>
    private static TranscriptionResult Refusal(HttpStatusCode status) => status switch
    {
        HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden => TranscriptionResult.Failed("The transcription service refused the key."),
        HttpStatusCode.TooManyRequests => TranscriptionResult.Failed("The transcription service is busy, or the account's quota is spent."),
        HttpStatusCode.NotFound => TranscriptionResult.Failed("The transcription service does not know this model or this address."),
        >= HttpStatusCode.MultipleChoices and < HttpStatusCode.BadRequest => TranscriptionResult.Failed("The transcription service answered with a redirect: check its address."),
        >= HttpStatusCode.InternalServerError => TranscriptionResult.Failed("The transcription service failed."),
        _ => TranscriptionResult.Refused("The transcription service refused the recording."),
    };

    /// <summary>The text, and the duration when the service reports it as a <c>usage</c> of type <c>duration</c>.</summary>
    internal static TranscriptionResult Read(string body)
    {
        try
        {
            using var document = JsonDocument.Parse(body);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("text", out var text) || text.ValueKind != JsonValueKind.String)
            {
                return TranscriptionResult.Failed("The transcription service answered something Mnemarr does not understand.");
            }

            double? seconds = root.TryGetProperty("usage", out var usage) && usage.ValueKind == JsonValueKind.Object
                && usage.TryGetProperty("seconds", out var value) && value.TryGetDouble(out var number) ? number : null;
            return TranscriptionResult.Done(text.GetString()!.Trim(), seconds);
        }
        catch (JsonException)
        {
            return TranscriptionResult.Failed("The transcription service answered something Mnemarr does not understand.");
        }
    }
}

using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Options;

namespace Mnemarr.Core.Embeddings;

/// <summary>What Mnemarr asks of a server that computes vectors (ADR 0040): the model it expects, whether the texts are passages or a question, and the texts.</summary>
public sealed record RemoteEmbeddingRequest(
    [property: JsonPropertyName("model")] string Model,
    [property: JsonPropertyName("kind")] string Kind,
    [property: JsonPropertyName("texts")] IReadOnlyList<string> Texts);

/// <summary>What the server answers: the model it ran, the size of the vectors, where it ran, and one vector per text, in the order of the texts.</summary>
public sealed record RemoteEmbeddingResponse(
    [property: JsonPropertyName("model")] string? Model,
    [property: JsonPropertyName("dimension")] int Dimension,
    [property: JsonPropertyName("device")] string? Device,
    [property: JsonPropertyName("vectors")] IReadOnlyList<float[]>? Vectors);

/// <summary>
/// Asks a server for vectors (ADR 0040). The key is read from the file the operator named, sent as a bearer token, and never logged; redirects are not followed, so a key
/// cannot be sent to another address. What comes back is checked before it is used: the model, the number of vectors, their size, and that every value is a number.
/// </summary>
public sealed class RemoteEmbeddingClient
{
    /// <summary>The path of the endpoint, shared by the client and the server.</summary>
    public const string Path = "v1/embeddings";

    private readonly HttpClient _http;
    private readonly EmbeddingOptions _options;
    private volatile string? _key;

    public RemoteEmbeddingClient(HttpClient http, IOptions<EmbeddingOptions> options)
    {
        _http = http;
        _options = options.Value;
    }

    /// <summary>Where the server computed the last vectors it gave: <c>cpu</c>, <c>cuda</c> or <c>unknown</c>, or null before the first answer.</summary>
    public string? Device { get; private set; }

    public bool Wanted => _options.Remote.Wanted;

    /// <summary>Asks for the vectors of the texts. Throws <see cref="EmbeddingException"/> when the server does not answer, refuses, or answers something that is not what was asked.</summary>
    public IReadOnlyList<float[]> Embed(IReadOnlyList<string> texts, string kind, string model, int dimension)
    {
        using var cancel = new CancellationTokenSource(TimeSpan.FromSeconds(_options.Remote.TimeoutSeconds));
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, Address());
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", Key());
            request.Content = JsonContent.Create(new RemoteEmbeddingRequest(model, kind, texts));
            using var response = _http.Send(request, HttpCompletionOption.ResponseContentRead, cancel.Token);
            if (!response.IsSuccessStatusCode)
            {
                // A refused key is read again from its file next time: the operator may have changed it.
                if (response.StatusCode is System.Net.HttpStatusCode.Unauthorized or System.Net.HttpStatusCode.Forbidden)
                {
                    _key = null;
                }

                throw new EmbeddingException($"The embedding server answered {(int)response.StatusCode}.");
            }

            var answer = response.Content.ReadFromJsonAsync<RemoteEmbeddingResponse>(cancel.Token).GetAwaiter().GetResult();
            var vectors = Checked(answer, texts.Count, model, dimension);
            // Text from outside: only the two known places are kept.
            Device = answer!.Device is "cpu" or "cuda" ? answer.Device : "unknown";
            return vectors;
        }
        catch (OperationCanceledException ex) when (cancel.IsCancellationRequested)
        {
            // Only the time this client allowed itself: a cancellation from the host (it stops) goes through.
            throw new EmbeddingException("The embedding server did not answer in time.", ex);
        }
        catch (Exception ex) when (ex is HttpRequestException or System.Text.Json.JsonException or IOException or InvalidOperationException or UriFormatException or NotSupportedException)
        {
            // Whatever the network does, the callers know one failure. The detail (an address, a socket error) is for the log of the caller, not for the page.
            throw new EmbeddingException("The embedding server could not be reached or did not answer as expected.", ex);
        }
    }

    private static IReadOnlyList<float[]> Checked(RemoteEmbeddingResponse? answer, int count, string model, int dimension)
    {
        if (answer?.Vectors is not { } vectors || answer.Model != model || answer.Dimension != dimension || vectors.Count != count)
        {
            throw new EmbeddingException("The embedding server answered for another model, or with another number of vectors than texts.");
        }

        foreach (var vector in vectors)
        {
            if (vector is null || vector.Length != dimension || vector.Any(value => !float.IsFinite(value)))
            {
                throw new EmbeddingException("The embedding server gave a vector that is not of the size of the model, or that holds a value that is not a number.");
            }
        }

        return vectors;
    }

    private Uri Address()
    {
        var baseAddress = _options.Remote.Url!.EndsWith('/') ? _options.Remote.Url : _options.Remote.Url + "/";
        return new Uri(new Uri(baseAddress), Path);
    }

    private string Key()
    {
        if (_key is { } known)
        {
            return known;
        }

        try
        {
            var key = File.ReadAllText(_options.Remote.KeyFile!).Trim();
            if (key.Length == 0)
            {
                throw new EmbeddingException("The key file of the embedding server is empty.");
            }

            _key = key;
            return key;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new EmbeddingException("The key file of the embedding server cannot be read.", ex);
        }
    }
}

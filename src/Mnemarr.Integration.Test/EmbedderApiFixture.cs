using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using Mnemarr.Core.Embeddings;
using Mnemarr.Embedder;

namespace Mnemarr.Integration.Test;

/// <summary>
/// The embedding server (ADR 0040), started for real on a loopback port with a model that is a counter: who may ask, what is refused, that nothing the model fails on leaks,
/// and that Mnemarr's own client and the server speak the same contract.
/// </summary>
public sealed class EmbedderApiFixture : IAsyncLifetime
{
    private const string Key = "a-key-of-the-server-0123456789";

    private readonly string _keyFile = Path.Combine(Path.GetTempPath(), $"mnemarr-embedder-key-{Guid.NewGuid():N}");
    private readonly FakeProvider _provider = new();
    private WebApplication? _app;
    private HttpClient? _http;

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    public async ValueTask InitializeAsync()
    {
        File.WriteAllText(_keyFile, Key + "\n");
        _app = await StartAsync(_keyFile, _provider);
        _http = new HttpClient { BaseAddress = new Uri(AddressOf(_app)) };
    }

    public async ValueTask DisposeAsync()
    {
        _http?.Dispose();
        if (_app is not null)
        {
            await _app.DisposeAsync();
        }

        File.Delete(_keyFile);
    }

    // The port is the one the system gave: the addresses of the application say 0, the ones of the server say which.
    private static string AddressOf(WebApplication app) => app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.First();

    private static async Task<WebApplication> StartAsync(string keyFile, IEmbeddingProvider provider)
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?> { ["Embedder:KeyFile"] = keyFile });
        builder.Services.AddEmbedder(builder.Configuration);
        builder.Services.AddSingleton(provider);
        var app = builder.Build();
        app.MapEmbedder();
        await app.StartAsync(Token);
        return app;
    }

    private async Task<HttpResponseMessage> PostAsync(object body, string? key = Key)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "/v1/embeddings") { Content = JsonContent.Create(body) };
        if (key is not null)
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", key);
        }

        return await _http!.SendAsync(request, Token);
    }

    private static object Ask(string[] texts, string kind = "passage", string model = "fake-model") => new { model, kind, texts };

    [Fact]
    public async Task should_say_whether_the_model_is_loaded()
    {
        _provider.State = EmbeddingState.Ready;
        (await _http!.GetAsync("/health", Token)).StatusCode.Should().Be(HttpStatusCode.OK);

        _provider.State = EmbeddingState.Failed;
        (await _http.GetAsync("/health", Token)).StatusCode.Should().Be(HttpStatusCode.ServiceUnavailable);

        _provider.KnownIsNull = true;
        (await _http.GetAsync("/health", Token)).StatusCode.Should().Be(HttpStatusCode.ServiceUnavailable);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("not-the-key-0123456789")]
    [InlineData("")]
    public async Task should_refuse_a_client_that_does_not_hold_the_key(string? key)
    {
        var response = await PostAsync(Ask(["a"]), key);

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        _provider.Computed.Should().Be(0);
    }

    [Fact]
    public async Task should_give_the_vectors_of_passages_and_where_they_were_computed()
    {
        var response = await PostAsync(Ask(["a", "bb"]));

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var answer = await response.Content.ReadFromJsonAsync<RemoteEmbeddingResponse>(Token);
        answer!.Model.Should().Be("fake-model");
        answer.Dimension.Should().Be(2);
        answer.Device.Should().Be("cuda");
        answer.Vectors.Should().HaveCount(2);
        answer.Vectors![1].Should().Equal(2f, 0f);
    }

    [Fact]
    public async Task should_give_the_vectors_of_questions_one_by_one()
    {
        var response = await PostAsync(Ask(["a", "bb"], kind: "query"));

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        _provider.QueriesComputed.Should().Be(2);
    }

    [Fact]
    public async Task should_refuse_a_client_that_expects_another_model()
    {
        (await PostAsync(Ask(["a"], model: "another-model"))).StatusCode.Should().Be(HttpStatusCode.Conflict);
    }

    [Theory]
    [InlineData(0, 10, "passage")]
    [InlineData(65, 10, "passage")]
    [InlineData(1, 8001, "passage")]
    [InlineData(1, 10, "something")]
    public async Task should_refuse_a_request_that_is_empty_too_big_or_of_an_unknown_kind(int count, int length, string kind)
    {
        var texts = Enumerable.Range(0, count).Select(_ => new string('x', length)).ToArray();

        var response = await PostAsync(Ask(texts, kind));

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        _provider.Computed.Should().Be(0);
    }

    [Fact]
    public async Task should_not_say_what_the_model_failed_on()
    {
        _provider.FailWith = new EmbeddingException("the passage 'my secret text' could not be read from /models/x");

        var response = await PostAsync(Ask(["my secret text"]));

        response.StatusCode.Should().Be(HttpStatusCode.ServiceUnavailable);
        (await response.Content.ReadAsStringAsync(Token)).Should().NotContain("secret").And.NotContain("/models");
    }

    [Fact]
    public async Task should_be_understood_by_the_client_of_mnemarr()
    {
        var options = Options.Create(new EmbeddingOptions { Remote = new RemoteEmbeddingOptions { Url = AddressOf(_app!), KeyFile = _keyFile } });
        var client = new RemoteEmbeddingClient(new HttpClient(), options);

        var vectors = await Task.Run(() => client.Embed(["abc", "d"], "passage", "fake-model", 2), Token);

        vectors.Select(vector => vector[0]).Should().Equal(3f, 1f);
        client.Device.Should().Be("remote (cuda)");
    }

    [Fact]
    public async Task should_not_start_without_a_key_file_or_with_a_key_that_is_too_short()
    {
        var missing = async () => await StartAsync(Path.Combine(Path.GetTempPath(), "does-not-exist"), _provider);
        await missing.Should().ThrowAsync<Exception>();

        var shortKey = Path.Combine(Path.GetTempPath(), $"mnemarr-embedder-short-{Guid.NewGuid():N}");
        File.WriteAllText(shortKey, "short");
        try
        {
            var tooShort = async () => await StartAsync(shortKey, _provider);
            await tooShort.Should().ThrowAsync<InvalidOperationException>();
        }
        finally
        {
            File.Delete(shortKey);
        }
    }

    private sealed class FakeProvider : IEmbeddingProvider
    {
        private int _computed;
        private int _queries;

        public EmbeddingState State { get; set; } = EmbeddingState.Ready;

        public bool KnownIsNull { get; set; }

        public Exception? FailWith { get; set; }

        public int Computed => _computed;

        public int QueriesComputed => _queries;

        public string ModelId => "fake-model";

        public int Dimension => 2;

        public double DefaultMaxDistance => 0.2;

        public string Device => "cuda";

        public EmbeddingStatus? Known() => KnownIsNull ? null : new EmbeddingStatus(State);

        public EmbeddingStatus Status() => new(State);

        public float[] EmbedQuery(string text)
        {
            Interlocked.Increment(ref _queries);
            return Vector(text);
        }

        public IReadOnlyList<float[]> EmbedPassages(IReadOnlyList<string> texts)
        {
            if (FailWith is { } failure)
            {
                throw failure;
            }

            Interlocked.Add(ref _computed, texts.Count);
            return [.. texts.Select(Vector)];
        }

        private static float[] Vector(string text) => [text.Length, 0f];
    }
}

using System.Net;
using System.Net.Http.Json;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using Mnemarr.Core.Embeddings;

namespace Mnemarr.Core.Test.Embeddings;

/// <summary>
/// The embedding server (ADR 0040) from Mnemarr's side: what is asked, what is checked in the answer, and that the indexing goes on with the CPU of the host when the server does not
/// answer, and goes back to the server when it does again.
/// </summary>
public sealed class RemoteEmbeddingFixture : IDisposable
{
    private readonly string _keyFile = Path.Combine(Path.GetTempPath(), $"mnemarr-key-{Guid.NewGuid():N}");
    private readonly FakeTimeProvider _clock = new(new DateTimeOffset(2026, 11, 12, 9, 0, 0, TimeSpan.Zero));

    public RemoteEmbeddingFixture() => File.WriteAllText(_keyFile, "a-key-of-the-server-0123456789\n");

    public void Dispose() => File.Delete(_keyFile);

    private IOptions<EmbeddingOptions> Options(int retryAfterSeconds = 60) => Microsoft.Extensions.Options.Options.Create(new EmbeddingOptions
    {
        Remote = new RemoteEmbeddingOptions { Url = "https://embedder.homelab.example", KeyFile = _keyFile, TimeoutSeconds = 5, RetryAfterSeconds = retryAfterSeconds },
    });

    private RemoteEmbeddingClient Client(Func<HttpRequestMessage, HttpResponseMessage> answer, out List<HttpRequestMessage> seen)
    {
        var requests = new List<HttpRequestMessage>();
        seen = requests;
        return new RemoteEmbeddingClient(new HttpClient(new StubHandler(request =>
        {
            requests.Add(request);
            return answer(request);
        })), Options());
    }

    private static HttpResponseMessage Answer(string model = "m", int dimension = 3, int count = 2, string device = "cuda", float value = 0.5f) =>
        new(HttpStatusCode.OK) { Content = JsonContent.Create(new RemoteEmbeddingResponse(model, dimension, device, [.. Enumerable.Range(0, count).Select(_ => Enumerable.Repeat(value, dimension).ToArray())])) };

    [Fact]
    public void should_ask_the_server_with_the_key_and_give_back_the_vectors_and_where_they_were_computed()
    {
        var client = Client(_ => Answer(), out var seen);

        var vectors = client.Embed(["a", "b"], "passage", "m", 3);

        vectors.Should().HaveCount(2);
        client.Device.Should().Be("remote (cuda)");
        seen.Single().RequestUri.Should().Be(new Uri("https://embedder.homelab.example/v1/embeddings"));
        seen.Single().Headers.Authorization!.ToString().Should().Be("Bearer a-key-of-the-server-0123456789");
    }

    [Theory]
    [InlineData("other-model", 3, 2, 0.5f)]
    [InlineData("m", 4, 2, 0.5f)]
    [InlineData("m", 3, 1, 0.5f)]
    public void should_refuse_an_answer_that_is_not_what_was_asked(string model, int dimension, int count, float value)
    {
        var client = Client(_ => Answer(model, dimension, count, value: value), out _);

        var act = () => client.Embed(["a", "b"], "passage", "m", 3);

        act.Should().Throw<EmbeddingException>();
    }

    [Theory]
    [InlineData("[[1e999,0,0]]")]
    [InlineData("[[0,0,null]]")]
    [InlineData("[[0,0,\"x\"]]")]
    public void should_refuse_a_vector_that_holds_something_that_is_not_a_number(string vectors)
    {
        var client = Client(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent($"{{\"model\":\"m\",\"dimension\":3,\"device\":\"cpu\",\"vectors\":{vectors}}}", System.Text.Encoding.UTF8, "application/json"),
        }, out _);

        var act = () => client.Embed(["a"], "passage", "m", 3);

        act.Should().Throw<EmbeddingException>();
    }

    [Theory]
    [InlineData(HttpStatusCode.Unauthorized)]
    [InlineData(HttpStatusCode.ServiceUnavailable)]
    [InlineData(HttpStatusCode.Found)]
    public void should_fail_with_the_one_failure_the_callers_know_when_the_server_refuses_or_is_down(HttpStatusCode status)
    {
        var client = Client(_ => new HttpResponseMessage(status), out _);

        var act = () => client.Embed(["a"], "query", "m", 3);

        act.Should().Throw<EmbeddingException>().Which.Message.Should().NotContain("embedder.homelab.example");
    }

    [Fact]
    public void should_fail_when_the_network_does()
    {
        var client = Client(_ => throw new HttpRequestException("no route to host embedder.homelab.example"), out _);

        var act = () => client.Embed(["a"], "passage", "m", 3);

        act.Should().Throw<EmbeddingException>().Which.Message.Should().NotContain("embedder.homelab.example");
    }

    [Fact]
    public void should_read_the_key_again_after_the_server_refused_it()
    {
        var calls = 0;
        var client = Client(_ => ++calls == 1 ? new HttpResponseMessage(HttpStatusCode.Unauthorized) : Answer(count: 1), out var seen);
        client.Invoking(c => c.Embed(["a"], "passage", "m", 3)).Should().Throw<EmbeddingException>();
        File.WriteAllText(_keyFile, "a-new-key-of-the-server-9876543210");

        client.Embed(["a"], "passage", "m", 3);

        seen[1].Headers.Authorization!.ToString().Should().Be("Bearer a-new-key-of-the-server-9876543210");
    }

    [Fact]
    public void should_compute_on_the_server_while_it_answers_and_not_load_the_model_of_the_host()
    {
        var local = new FakeLocal();
        var provider = Failover(local, Client(_ => Answer(count: 2), out _));

        provider.Status().State.Should().Be(EmbeddingState.Ready);
        var vectors = provider.EmbedPassages(["a", "b"]);

        vectors.Should().HaveCount(2);
        provider.Device.Should().Be("remote (cuda)");
        local.Loaded.Should().BeFalse("the model of the host is only read when the server cannot be");
        local.Computed.Should().Be(0);
    }

    [Fact]
    public void should_compute_on_the_host_when_the_server_does_not_answer_and_go_back_to_it_after_a_while()
    {
        var up = false;
        var local = new FakeLocal();
        var calls = 0;
        var provider = Failover(local, Client(_ =>
        {
            calls++;
            return up ? Answer(count: 1) : throw new HttpRequestException("down");
        }, out _));

        provider.EmbedPassages(["a"]).Should().HaveCount(1);
        provider.Device.Should().Be("cpu");
        local.Computed.Should().Be(1);
        provider.EmbedPassages(["b"]);
        calls.Should().Be(1, "the server is left alone while the CPU computes");
        provider.Status().State.Should().Be(EmbeddingState.Ready);
        local.Loaded.Should().BeTrue();

        up = true;
        _clock.Advance(TimeSpan.FromSeconds(61));
        provider.EmbedPassages(["c"]);

        provider.Device.Should().Be("remote (cuda)");
        calls.Should().Be(2);
        local.Computed.Should().Be(2);
    }

    [Fact]
    public void should_answer_a_question_from_the_host_when_the_server_does_not_answer()
    {
        var local = new FakeLocal();
        var provider = Failover(local, Client(_ => new HttpResponseMessage(HttpStatusCode.ServiceUnavailable), out _));

        provider.EmbedQuery("a question").Should().HaveCount(3);

        local.Computed.Should().Be(1);
    }

    private FailoverEmbeddingProvider Failover(FakeLocal local, RemoteEmbeddingClient client) =>
        new(local, client, Options(), NullLogger<FailoverEmbeddingProvider>.Instance, _clock);

    private sealed class FakeLocal : IEmbeddingProvider
    {
        public bool Loaded { get; private set; }

        public int Computed { get; private set; }

        public string ModelId => "m";

        public int Dimension => 3;

        public double DefaultMaxDistance => 0.2;

        public EmbeddingStatus? Known() => Loaded ? new EmbeddingStatus(EmbeddingState.Ready) : null;

        public EmbeddingStatus Status()
        {
            Loaded = true;
            return new EmbeddingStatus(EmbeddingState.Ready);
        }

        public float[] EmbedQuery(string text) => EmbedPassages([text])[0];

        public IReadOnlyList<float[]> EmbedPassages(IReadOnlyList<string> texts)
        {
            Loaded = true;
            Computed += texts.Count;
            return [.. texts.Select(_ => new[] { 1f, 0f, 0f })];
        }
    }

    private sealed class StubHandler(Func<HttpRequestMessage, HttpResponseMessage> answer) : HttpMessageHandler
    {
        protected override HttpResponseMessage Send(HttpRequestMessage request, CancellationToken cancellationToken) => answer(request);

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => Task.FromResult(answer(request));
    }
}

public sealed class EmbeddingOptionsValidationFixture
{
    private static string? Failure(Action<EmbeddingOptions> change)
    {
        var options = new EmbeddingOptions { ModelFolder = "m", ExtensionPath = "e" };
        change(options);
        var result = new EmbeddingOptionsValidator().Validate(null, options);
        return result.Failed ? result.FailureMessage : null;
    }

    [Fact]
    public void should_accept_the_defaults_and_a_server_over_https_with_its_key_file()
    {
        Failure(_ => { }).Should().BeNull();
        Failure(options => options.Remote = new RemoteEmbeddingOptions { Url = "https://embedder.homelab.example:8390", KeyFile = "/run/secrets/key" }).Should().BeNull();
    }

    [Theory]
    [InlineData("embedder.homelab.example")]
    [InlineData("ftp://embedder.homelab.example")]
    [InlineData("https://user:secret@embedder.homelab.example")]
    [InlineData("http://embedder.homelab.example")]
    public void should_refuse_an_address_that_is_not_https_or_holds_credentials(string url)
    {
        Failure(options => options.Remote = new RemoteEmbeddingOptions { Url = url, KeyFile = "k" }).Should().Contain("Remote");
    }

    [Fact]
    public void should_allow_http_to_this_host_or_when_the_operator_says_so_and_ask_for_the_key_file()
    {
        Failure(options => options.Remote = new RemoteEmbeddingOptions { Url = "http://127.0.0.1:8390", KeyFile = "k" }).Should().BeNull();
        Failure(options => options.Remote = new RemoteEmbeddingOptions { Url = "http://10.0.0.20:8390", KeyFile = "k", AllowInsecureHttp = true }).Should().BeNull();
        Failure(options => options.Remote = new RemoteEmbeddingOptions { Url = "https://embedder.homelab.example" }).Should().Contain("KeyFile");
    }

    [Theory]
    [InlineData(0, 60)]
    [InlineData(301, 60)]
    [InlineData(30, 4)]
    public void should_bound_the_time_the_server_may_take_and_the_time_it_is_left_alone(int timeout, int retry)
    {
        Failure(options => options.Remote = new RemoteEmbeddingOptions { Url = "https://embedder.homelab.example", KeyFile = "k", TimeoutSeconds = timeout, RetryAfterSeconds = retry }).Should().Contain("Remote");
    }

    [Fact]
    public void should_bound_the_memory_of_the_gpu_and_the_days_the_vectors_are_kept()
    {
        Failure(options => options.GpuMemoryMb = 10).Should().Contain("GpuMemoryMb");
        Failure(options => options.CacheDays = 0).Should().Contain("CacheDays");
    }
}

using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Options;
using Mnemarr.Core.Titles;

namespace Mnemarr.Core.Test.Titles;

public sealed class TitleProvidersFixture : IDisposable
{
    private readonly string _keyFile = Path.Combine(Path.GetTempPath(), $"mnemarr-openaikey-{Guid.NewGuid():N}");

    public TitleProvidersFixture() => File.WriteAllText(_keyFile, "sk-openai-key\n");

    public void Dispose() => File.Delete(_keyFile);

    private sealed class Handler(Func<HttpRequestMessage, HttpResponseMessage> answer) : HttpMessageHandler
    {
        public List<(Uri? Uri, string? Authorization, string Body)> Calls { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Calls.Add((request.RequestUri, request.Headers.Authorization?.ToString(), await request.Content!.ReadAsStringAsync(cancellationToken)));
            return answer(request);
        }
    }

    private static HttpResponseMessage Reply(string text, HttpStatusCode status = HttpStatusCode.OK) =>
        new(status) { Content = new StringContent(JsonSerializer.Serialize(new { choices = new[] { new { message = new { role = "assistant", content = text } } } }), Encoding.UTF8, "application/json") };

    private OpenAiTitleWriter Writer(Handler handler, string? keyFile, string endpoint = "http://localhost:11434/v1") =>
        new(Options.Create(new TitleOptions { Enabled = true, Provider = TitleProviders.OpenAi, Endpoint = endpoint, Model = "llama3", ApiKeyFile = keyFile }), handler);

    [Fact]
    public async Task should_ask_the_chat_completions_api_with_the_key_the_model_the_instructions_and_the_transcript()
    {
        var handler = new Handler(_ => Reply("""{"title": "Rotating tokens", "summary": "How, and done."}"""));

        var written = await Writer(handler, _keyFile).WriteAsync("[Person] hello", TestContext.Current.CancellationToken);

        written.Should().Be(new GeneratedTitle("Rotating tokens", "How, and done."));
        var call = handler.Calls.Single();
        call.Uri.Should().Be(new Uri("http://localhost:11434/v1/chat/completions"));
        call.Authorization.Should().Be("Bearer sk-openai-key");
        using var body = JsonDocument.Parse(call.Body);
        body.RootElement.GetProperty("model").GetString().Should().Be("llama3");
        var messages = body.RootElement.GetProperty("messages");
        messages[0].GetProperty("role").GetString().Should().Be("system");
        messages[0].GetProperty("content").GetString().Should().Be(TitlePrompt.System);
        messages[1].GetProperty("content").GetString().Should().Be("[Person] hello");
    }

    [Theory]
    [InlineData("https://openrouter.ai/api/v1", "https://openrouter.ai/api/v1/chat/completions")]
    [InlineData("https://gateway.example/llm/v1/", "https://gateway.example/llm/v1/chat/completions")]
    [InlineData("http://localhost:11434/v1", "http://localhost:11434/v1/chat/completions")]
    public async Task should_join_the_call_to_the_path_of_the_address_and_not_put_it_at_the_root(string endpoint, string expected)
    {
        var handler = new Handler(_ => Reply("""{"title": "T", "summary": ""}"""));

        await Writer(handler, null, endpoint).WriteAsync("x", TestContext.Current.CancellationToken);

        handler.Calls.Single().Uri.Should().Be(new Uri(expected));
    }

    [Fact]
    public async Task should_let_the_cancellation_of_the_caller_through_and_not_call_it_an_unreachable_model()
    {
        using var cancelled = new CancellationTokenSource();
        await cancelled.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Writer(new Handler(_ => Reply("x")), null).WriteAsync("x", cancelled.Token));
    }

    [Fact]
    public async Task should_work_without_a_key_for_a_local_server()
    {
        var handler = new Handler(_ => Reply("""{"title": "Local", "summary": ""}"""));

        (await Writer(handler, null).WriteAsync("x", TestContext.Current.CancellationToken))!.Title.Should().Be("Local");
        handler.Calls.Single().Authorization.Should().BeNull();
    }

    [Fact]
    public async Task should_say_the_status_and_nothing_of_the_answer_when_the_model_refuses_and_give_nothing_for_an_answer_that_is_not_a_title()
    {
        var refused = await Assert.ThrowsAsync<TitleWriterException>(() => Writer(new Handler(_ => Reply("the key sk-openai-key is wrong", HttpStatusCode.Unauthorized)), _keyFile).WriteAsync("x", TestContext.Current.CancellationToken));
        refused.Message.Should().Be("The model answered 401.");

        (await Writer(new Handler(_ => Reply("No.")), _keyFile).WriteAsync("x", TestContext.Current.CancellationToken)).Should().BeNull();
        (await Writer(new Handler(_ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("{}", Encoding.UTF8, "application/json") }), _keyFile).WriteAsync("x", TestContext.Current.CancellationToken)).Should().BeNull();
    }

    [Fact]
    public async Task should_fail_for_a_key_file_that_is_not_there_and_for_a_network_that_is_not()
    {
        await Assert.ThrowsAsync<TitleWriterException>(() => Writer(new Handler(_ => Reply("x")), "/no/such/file").WriteAsync("x", TestContext.Current.CancellationToken));
        await Assert.ThrowsAsync<TitleWriterException>(() => Writer(new Handler(_ => throw new HttpRequestException("no route")), _keyFile).WriteAsync("x", TestContext.Current.CancellationToken));
    }

    [Theory]
    [InlineData("https://api.openai.com/v1", "/key", true)]
    [InlineData("http://localhost:11434", null, true)]
    [InlineData("http://localhost:11434", "/key", true)]
    [InlineData("http://ollama.lan:11434", null, true)]
    [InlineData("http://api.example", "/key", false)]
    [InlineData(null, null, false)]
    [InlineData("not an address", null, false)]
    public void should_need_an_address_and_never_send_a_key_in_clear_over_the_network(string? endpoint, string? keyFile, bool valid)
    {
        var options = new TitleOptions { Enabled = true, Provider = "openai", Endpoint = endpoint, ApiKeyFile = keyFile };

        new TitleOptionsValidator(TitleProviders.BuiltIn).Validate(null, options).Succeeded.Should().Be(valid);
    }

    [Fact]
    public void should_name_the_providers_there_are_when_the_name_is_not_one_of_them()
    {
        var result = new TitleOptionsValidator(TitleProviders.BuiltIn).Validate(null, new TitleOptions { Enabled = true, Provider = "Nothing" });

        result.Failed.Should().BeTrue();
        result.FailureMessage.Should().Contain("Anthropic").And.Contain("OpenAI").And.Contain("Agent");
    }

    private sealed class ShoutingProvider : ITitleProvider
    {
        public string Name => "Shouting";

        public string? Validate(TitleOptions options) => null;

        public ITitleWriter Create(IOptions<TitleOptions> options) => new ShoutingWriter();
    }

    private sealed class ShoutingWriter : ITitleWriter
    {
        public Task<GeneratedTitle?> WriteAsync(string transcript, CancellationToken cancellationToken) =>
            Task.FromResult<GeneratedTitle?>(new GeneratedTitle(transcript.ToUpperInvariant(), string.Empty));
    }

    [Fact]
    public async Task should_use_a_provider_that_somebody_else_wrote_when_it_is_registered_and_selected_by_name()
    {
        ITitleProvider[] providers = [.. TitleProviders.BuiltIn, new ShoutingProvider()];
        var options = Options.Create(new TitleOptions { Enabled = true, Provider = "shouting" });

        new TitleOptionsValidator(providers).Validate(null, options.Value).Succeeded.Should().BeTrue();
        var writer = TitleProviders.Select(providers, options);

        (await writer.WriteAsync("hello", TestContext.Current.CancellationToken))!.Title.Should().Be("HELLO");
    }

    [Fact]
    public async Task should_say_why_when_a_call_is_made_with_a_provider_that_does_not_exist()
    {
        var writer = TitleProviders.Select(TitleProviders.BuiltIn, Options.Create(new TitleOptions { Provider = "Nothing" }));

        var failure = await Assert.ThrowsAsync<TitleWriterException>(() => writer.WriteAsync("x", TestContext.Current.CancellationToken));

        failure.Message.Should().Contain("Nothing");
    }
}

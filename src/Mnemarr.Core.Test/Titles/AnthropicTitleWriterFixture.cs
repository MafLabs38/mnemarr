using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Options;
using Mnemarr.Core.Titles;

namespace Mnemarr.Core.Test.Titles;

public sealed class AnthropicTitleWriterFixture : IDisposable
{
    private readonly string _keyFile = Path.Combine(Path.GetTempPath(), $"mnemarr-titlekey-{Guid.NewGuid():N}");

    public AnthropicTitleWriterFixture() => File.WriteAllText(_keyFile, "sk-test-key\n");

    public void Dispose() => File.Delete(_keyFile);

    private sealed class Handler(Func<HttpRequestMessage, HttpResponseMessage> answer) : HttpMessageHandler
    {
        public List<(Uri? Uri, string Key, string Version, string Body)> Calls { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Calls.Add((request.RequestUri, request.Headers.GetValues("x-api-key").Single(), request.Headers.GetValues("anthropic-version").Single(), await request.Content!.ReadAsStringAsync(cancellationToken)));
            return answer(request);
        }
    }

    private AnthropicTitleWriter Writer(Handler handler, string? keyFile = null) =>
        new(Options.Create(new TitleOptions { Enabled = true, ApiKeyFile = keyFile ?? _keyFile, Model = "a-model", Endpoint = "https://api.example" }), handler);

    private static HttpResponseMessage Reply(string text, HttpStatusCode status = HttpStatusCode.OK) =>
        new(status) { Content = new StringContent(JsonSerializer.Serialize(new { content = new[] { new { type = "text", text } } }), Encoding.UTF8, "application/json") };

    [Fact]
    public async Task should_ask_the_messages_api_with_the_key_the_model_the_instructions_and_the_transcript_and_read_the_title()
    {
        var handler = new Handler(_ => Reply("""{"title": "Rotating tokens", "summary": "How, and done."}"""));

        var written = await Writer(handler).WriteAsync("[Person] hello", TestContext.Current.CancellationToken);

        written.Should().Be(new GeneratedTitle("Rotating tokens", "How, and done."));
        var call = handler.Calls.Single();
        call.Uri.Should().Be(new Uri("https://api.example/v1/messages"));
        call.Key.Should().Be("sk-test-key", "the key is read from its file and trimmed");
        call.Version.Should().Be("2023-06-01");
        using var body = JsonDocument.Parse(call.Body);
        body.RootElement.GetProperty("model").GetString().Should().Be("a-model");
        body.RootElement.GetProperty("max_tokens").GetInt32().Should().BeLessThanOrEqualTo(500);
        body.RootElement.GetProperty("system").GetString().Should().Be(TitlePrompt.System);
        body.RootElement.GetProperty("messages")[0].GetProperty("content").GetString().Should().Be("[Person] hello");
    }

    [Fact]
    public async Task should_give_nothing_when_the_answer_is_not_a_title()
    {
        var handler = new Handler(_ => Reply("I would rather not."));

        (await Writer(handler).WriteAsync("x", TestContext.Current.CancellationToken)).Should().BeNull();
    }

    [Fact]
    public async Task should_say_the_status_and_nothing_of_the_answer_when_the_model_refuses()
    {
        var handler = new Handler(_ => Reply("the key sk-test-key is wrong", HttpStatusCode.Unauthorized));

        var failure = await Assert.ThrowsAsync<TitleWriterException>(() => Writer(handler).WriteAsync("x", TestContext.Current.CancellationToken));

        failure.Message.Should().Be("The model answered 401.");
    }

    [Fact]
    public async Task should_fail_for_a_key_file_that_is_not_there_and_for_a_network_that_is_not()
    {
        var handler = new Handler(_ => throw new HttpRequestException("no route"));

        await Assert.ThrowsAsync<TitleWriterException>(() => Writer(handler, "/no/such/file").WriteAsync("x", TestContext.Current.CancellationToken));
        await Assert.ThrowsAsync<TitleWriterException>(() => Writer(handler).WriteAsync("x", TestContext.Current.CancellationToken));
    }
}

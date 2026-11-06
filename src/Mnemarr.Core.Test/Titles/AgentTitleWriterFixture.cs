using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Options;
using Mnemarr.Core.Titles;

namespace Mnemarr.Core.Test.Titles;

public sealed class AgentTitleWriterFixture : IDisposable
{
    private readonly string _secretFile = Path.Combine(Path.GetTempPath(), $"mnemarr-agentsecret-{Guid.NewGuid():N}");

    public AgentTitleWriterFixture() => File.WriteAllText(_secretFile, "shared-secret\n");

    public void Dispose() => File.Delete(_secretFile);

    private sealed class Handler(Func<HttpRequestMessage, HttpResponseMessage> answer) : HttpMessageHandler
    {
        public List<(Uri? Uri, string? Scheme, string? Secret, string Body)> Calls { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Calls.Add((request.RequestUri, request.Headers.Authorization?.Scheme, request.Headers.Authorization?.Parameter, await request.Content!.ReadAsStringAsync(cancellationToken)));
            return answer(request);
        }
    }

    private AgentTitleWriter Writer(Handler handler, string? secretFile = null) =>
        new(Options.Create(new TitleOptions { Enabled = true, Provider = TitleProviders.Agent, AgentUrl = "http://agent.example:8490", AgentSecretFile = secretFile ?? _secretFile, Model = "haiku" }), handler);

    private static HttpResponseMessage Reply(string text, HttpStatusCode status = HttpStatusCode.OK) =>
        new(status) { Content = new StringContent(JsonSerializer.Serialize(new { text }), Encoding.UTF8, "application/json") };

    [Fact]
    public async Task should_ask_the_agent_with_the_secret_the_model_the_instructions_and_the_transcript_and_read_a_fenced_title()
    {
        var handler = new Handler(_ => Reply("```json\n{\"title\": \"Rotating tokens\", \"summary\": \"How, and done.\"}\n```"));

        var written = await Writer(handler).WriteAsync("[Person] hello", TestContext.Current.CancellationToken);

        written.Should().Be(new GeneratedTitle("Rotating tokens", "How, and done."));
        var call = handler.Calls.Single();
        call.Uri.Should().Be(new Uri("http://agent.example:8490/title"));
        call.Scheme.Should().Be("Bearer");
        call.Secret.Should().Be("shared-secret", "the secret is read from its file and trimmed");
        using var body = JsonDocument.Parse(call.Body);
        body.RootElement.GetProperty("model").GetString().Should().Be("haiku");
        body.RootElement.GetProperty("system").GetString().Should().Be(TitlePrompt.System);
        body.RootElement.GetProperty("transcript").GetString().Should().Be("[Person] hello");
    }

    [Fact]
    public async Task should_give_nothing_when_the_answer_is_not_a_title()
    {
        (await Writer(new Handler(_ => Reply("I would rather not."))).WriteAsync("x", TestContext.Current.CancellationToken)).Should().BeNull();
    }

    [Fact]
    public async Task should_say_the_status_and_nothing_of_the_answer_when_the_agent_refuses()
    {
        var handler = new Handler(_ => Reply("the secret shared-secret is wrong", HttpStatusCode.Unauthorized));

        var failure = await Assert.ThrowsAsync<TitleWriterException>(() => Writer(handler).WriteAsync("x", TestContext.Current.CancellationToken));

        failure.Message.Should().Be("The agent answered 401.");
    }

    [Fact]
    public async Task should_fail_for_a_secret_file_that_is_not_there_and_for_a_network_that_is_not()
    {
        var handler = new Handler(_ => throw new HttpRequestException("no route"));

        await Assert.ThrowsAsync<TitleWriterException>(() => Writer(handler, "/no/such/file").WriteAsync("x", TestContext.Current.CancellationToken));
        await Assert.ThrowsAsync<TitleWriterException>(() => Writer(handler).WriteAsync("x", TestContext.Current.CancellationToken));
    }

    [Theory]
    [InlineData("Agent", "http://agent:8490", null, false)]
    [InlineData("Agent", "http://agent:8490", "/run/secret", true)]
    [InlineData("Agent", null, "/run/secret", false)]
    [InlineData("Agent", "not a url", "/run/secret", false)]
    [InlineData("Other", "http://agent:8490", "/run/secret", false)]
    public void should_validate_the_agent_provider(string provider, string? url, string? secretFile, bool valid)
    {
        var options = new TitleOptions { Enabled = true, Provider = provider, AgentUrl = url, AgentSecretFile = secretFile };

        new TitleOptionsValidator(TitleProviders.BuiltIn).Validate(null, options).Succeeded.Should().Be(valid);
    }
}

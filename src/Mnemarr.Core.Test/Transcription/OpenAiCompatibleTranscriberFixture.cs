using System.Net;
using System.Text;
using Microsoft.Extensions.Options;
using Mnemarr.Core.Transcription;

namespace Mnemarr.Core.Test.Transcription;

/// <summary>The <c>OpenAiCompatible</c> provider (ADR 0047): what it sends, with which key, and what it makes of every answer.</summary>
public sealed class OpenAiCompatibleTranscriberFixture : IDisposable
{
    private readonly TestWorkspace _workspace = new();

    public void Dispose() => _workspace.Dispose();

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    private static TranscriptionRequest Recording(string? language = "fr") => new(new byte[] { 1, 2, 3 }, "audio/webm;codecs=opus", "gpt-transcribe", language);

    private (OpenAiCompatibleTranscriber Transcriber, Capture Capture) Make(HttpStatusCode status, string body, string? key = "test-key-0000", int timeoutSeconds = 30)
    {
        string? keyFile = null;
        if (key is not null)
        {
            keyFile = Path.Combine(_workspace.Root, "key");
            File.WriteAllText(keyFile, key + "\n");
        }

        var options = new TranscriptionOptions
        {
            Provider = TranscriptionProviders.OpenAiCompatible,
            OpenAiCompatible = { BaseUrl = "https://speech.homelab.example/v1/", KeyFile = keyFile, TimeoutSeconds = timeoutSeconds },
        };
        var capture = new Capture(status, body);
        return (new OpenAiCompatibleTranscriber(Options.Create(options), capture), capture);
    }

    [Fact]
    public async Task should_send_the_recording_with_the_model_and_the_language_and_the_key_and_read_the_text()
    {
        var (transcriber, capture) = Make(HttpStatusCode.OK, """{ "text": "  Put this idea in the incubator. ", "usage": { "type": "duration", "seconds": 4.2 } }""");

        var result = await transcriber.TranscribeAsync(Recording(), Token);

        result.Should().Be(TranscriptionResult.Done("Put this idea in the incubator.", 4.2));
        capture.Uri.Should().Be("https://speech.homelab.example/v1/audio/transcriptions");
        capture.Authorization.Should().Be("Bearer test-key-0000");
        capture.Body.Should().Contain("name=\"model\"\r\n\r\ngpt-transcribe")
            .And.Contain("name=\"language\"\r\n\r\nfr").And.Contain("name=\"response_format\"\r\n\r\njson")
            .And.Contain("name=\"file\"; filename=\"recording.webm\"").And.Contain("Content-Type: audio/webm")
            .And.NotContain("filename*", "a server reads the format from the plain name");
    }

    [Fact]
    public async Task should_send_no_key_and_no_language_when_there_is_none()
    {
        var (transcriber, capture) = Make(HttpStatusCode.OK, """{ "text": "hello" }""", key: null);

        var result = await transcriber.TranscribeAsync(Recording(language: null), Token);

        result.Should().Be(TranscriptionResult.Done("hello", null));
        capture.Authorization.Should().BeNull();
        capture.Body.Should().NotContain("name=\"language\"");
    }

    [Theory]
    [InlineData(HttpStatusCode.Unauthorized, "refused the key", true)]
    [InlineData(HttpStatusCode.TooManyRequests, "quota", true)]
    [InlineData(HttpStatusCode.NotFound, "does not know this model", true)]
    [InlineData(HttpStatusCode.BadGateway, "failed", true)]
    [InlineData(HttpStatusCode.MovedPermanently, "redirect", true)]
    [InlineData(HttpStatusCode.BadRequest, "refused the recording", false)]
    [InlineData(HttpStatusCode.UnsupportedMediaType, "refused the recording", false)]
    public async Task should_say_in_its_own_words_why_a_service_refused_and_whose_side_it_is(HttpStatusCode status, string reason, bool serviceAtFault)
    {
        var (transcriber, _) = Make(status, """{ "error": { "message": "test-key-0000 is invalid" } }""");

        var result = await transcriber.TranscribeAsync(Recording(), Token);

        result.Succeeded.Should().BeFalse();
        result.Reason.Should().Contain(reason).And.NotContain("test-key", "the service's text is never repeated");
        result.ServiceAtFault.Should().Be(serviceAtFault, "a key, a quota or an address is the operator's to fix; a refused sound is not");
    }

    [Fact]
    public async Task should_say_that_a_service_that_does_not_answer_in_time_did_not()
    {
        var (transcriber, capture) = Make(HttpStatusCode.OK, """{ "text": "late" }""", timeoutSeconds: 0);
        capture.Delay = TimeSpan.FromSeconds(30);

        var result = await transcriber.TranscribeAsync(Recording(), Token);

        result.Should().BeEquivalentTo(new { Succeeded = false, ServiceAtFault = true }, options => options.ExcludingMissingMembers());
        result.Reason.Should().Contain("did not answer in time");
    }

    [Fact]
    public async Task should_let_a_cancelled_call_be_cancelled_instead_of_calling_it_a_failure()
    {
        var (transcriber, _) = Make(HttpStatusCode.OK, """{ "text": "late" }""");
        using var cancel = CancellationTokenSource.CreateLinkedTokenSource(Token);
        await cancel.CancelAsync();

        var act = () => transcriber.TranscribeAsync(Recording(), cancel.Token);

        await act.Should().ThrowAsync<OperationCanceledException>();
    }

    [Fact]
    public void should_say_that_a_key_file_that_cannot_be_opened_cannot_be_read()
    {
        var provider = new OpenAiCompatibleTranscriptionProvider();
        var options = new TranscriptionOptions { OpenAiCompatible = { KeyFile = _workspace.Root } };

        provider.CheckSecrets(options).Should().Contain("cannot be read", "a folder exists but is not a key");
        options.OpenAiCompatible.KeyFile = Path.Combine(_workspace.Root, "absent");
        provider.CheckSecrets(options).Should().Contain("cannot be read");
        options.OpenAiCompatible.KeyFile = null;
        provider.CheckSecrets(options).Should().BeNull("a local service may need no key");
    }

    [Theory]
    [InlineData("not json")]
    [InlineData("""{ "transcript": "hello" }""")]
    [InlineData("""["hello"]""")]
    public async Task should_not_take_an_answer_it_does_not_understand(string body)
    {
        var (transcriber, _) = Make(HttpStatusCode.OK, body);

        var result = await transcriber.TranscribeAsync(Recording(), Token);

        result.Reason.Should().Contain("does not understand");
    }

    [Fact]
    public async Task should_say_that_the_key_cannot_be_read_without_calling_the_service()
    {
        var (transcriber, capture) = Make(HttpStatusCode.OK, """{ "text": "hello" }""");
        File.Delete(Path.Combine(_workspace.Root, "key"));

        var result = await transcriber.TranscribeAsync(Recording(), Token);

        result.Reason.Should().Contain("key cannot be read");
        capture.Uri.Should().BeNull();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task should_say_that_a_service_that_cannot_be_reached_cannot_be_reached(bool brokenWhileRead)
    {
        var (transcriber, capture) = Make(HttpStatusCode.OK, "{}");
        capture.Throw = brokenWhileRead ? null : new HttpRequestException("refused");
        capture.BrokenAnswer = brokenWhileRead;

        var result = await transcriber.TranscribeAsync(Recording(), Token);

        result.Reason.Should().Contain("cannot be reached");
    }

    [Theory]
    [InlineData("https://api.openai.com/v1", "/run/secrets/key", null)]
    [InlineData("http://10.0.0.10:8000/v1", null, null)]
    [InlineData("http://10.0.0.10:8000/v1", "/run/secrets/key", "in clear")]
    [InlineData("ftp://10.0.0.10/v1", null, "http or https")]
    [InlineData("https://user:pass@api.example/v1", null, "without a user")]
    public void should_refuse_a_key_sent_in_clear_and_an_address_that_is_not_one(string baseUrl, string? keyFile, string? problem)
    {
        var options = new TranscriptionOptions { Provider = "OpenAiCompatible", OpenAiCompatible = { BaseUrl = baseUrl, KeyFile = keyFile } };

        var failure = new OpenAiCompatibleTranscriptionProvider().Validate(options);

        if (problem is null)
        {
            failure.Should().BeNull();
        }
        else
        {
            failure.Should().Contain(problem);
        }
    }

    [Fact]
    public void should_tell_the_host_the_sound_goes_to()
    {
        var options = new TranscriptionOptions { OpenAiCompatible = { BaseUrl = "https://api.openai.com/v1" } };

        new OpenAiCompatibleTranscriptionProvider().Destination(options).Should().Be("api.openai.com");
    }

    /// <summary>A service that answers what a test says, and keeps what it was sent.</summary>
    private sealed class Capture(HttpStatusCode status, string body) : HttpMessageHandler
    {
        public string? Uri { get; private set; }

        public string? Authorization { get; private set; }

        public string Body { get; private set; } = string.Empty;

        public Exception? Throw { get; set; }

        /// <summary>The service answers 200, and the connection breaks while the answer is read.</summary>
        public bool BrokenAnswer { get; set; }

        public TimeSpan Delay { get; set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (Throw is { } failure)
            {
                throw failure;
            }

            await Task.Delay(Delay, cancellationToken);

            Uri = request.RequestUri!.ToString();
            Authorization = request.Headers.Authorization?.ToString();
            Body = Encoding.UTF8.GetString(await request.Content!.ReadAsByteArrayAsync(cancellationToken));
            return new HttpResponseMessage(status) { Content = BrokenAnswer ? new BrokenContent() : new StringContent(body, Encoding.UTF8, "application/json") };
        }
    }

    /// <summary>An answer whose connection breaks while it is read.</summary>
    private sealed class BrokenContent : HttpContent
    {
        protected override Task SerializeToStreamAsync(Stream stream, System.Net.TransportContext? context) => throw new IOException("The connection was reset.");

        protected override bool TryComputeLength(out long length)
        {
            length = 0;
            return false;
        }
    }
}

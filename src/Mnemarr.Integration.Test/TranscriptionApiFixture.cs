using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;

namespace Mnemarr.Integration.Test;

/// <summary>The chat's dictation through the API (ADR 0047): who may dictate, what is refused, what a failure looks like, and the model an administrator chooses.</summary>
public sealed class TranscriptionApiFixture : IClassFixture<MnemarrFactory>, IClassFixture<TranscriptionFactory>
{
    private readonly MnemarrFactory _off;
    private readonly TranscriptionFactory _on;

    public TranscriptionApiFixture(MnemarrFactory off, TranscriptionFactory on)
    {
        _off = off;
        _on = on;
    }

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    private static MultipartFormDataContent Recording(string type = "audio/webm;codecs=opus", int length = 64)
    {
        var file = new ByteArrayContent(new byte[length]);
        file.Headers.ContentType = MediaTypeHeaderValue.Parse(type);
        return new MultipartFormDataContent { { file, "file", "recording.webm" } };
    }

    [Fact]
    public async Task should_give_a_person_the_text_of_a_recording_and_never_send_it_to_the_chat()
    {
        var callers = await Callers.ForAsync(_on);

        var response = await callers["Reader"].PostAsync("/api/v1/chat/transcriptions", Recording(), Token);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        (await response.Content.ReadFromJsonAsync<JsonElement>(Token)).GetProperty("text").GetString().Should().Be("Put this idea in the incubator.");
        _on.Transcriber.Received.Should().Contain(request => request.Model == "gpt-transcribe" && request.ContentType == "audio/webm");
    }

    [Fact]
    public async Task should_offer_the_microphone_only_when_dictation_is_on()
    {
        var on = await (await Callers.ForAsync(_on))["Reader"].GetFromJsonAsync<JsonElement>("/api/v1/chat/transcriptions/status", Token);
        var off = await (await Callers.ForAsync(_off))["Reader"].GetFromJsonAsync<JsonElement>("/api/v1/chat/transcriptions/status", Token);

        on.GetProperty("enabled").GetBoolean().Should().BeTrue();
        on.GetProperty("maxMegabytes").GetInt32().Should().Be(1);
        off.GetProperty("enabled").GetBoolean().Should().BeFalse();
    }

    [Fact]
    public async Task should_be_not_found_when_dictation_is_off()
    {
        var callers = await Callers.ForAsync(_off);

        var response = await callers["Reader"].PostAsync("/api/v1/chat/transcriptions", Recording(), Token);

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Theory]
    [InlineData("Assistant")]
    [InlineData("Collector")]
    [InlineData("Monitor")]
    public async Task should_refuse_a_key_of_a_service_which_cannot_spend_the_transcription_key(string caller)
    {
        var callers = await Callers.ForAsync(_on);
        var before = _on.Transcriber.Received.Count;

        var response = await callers[caller].PostAsync("/api/v1/chat/transcriptions", Recording(), Token);

        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        _on.Transcriber.Received.Count.Should().Be(before);
    }

    [Fact]
    public async Task should_refuse_anyone_who_is_not_signed_in()
    {
        var callers = await Callers.ForAsync(_on);

        var response = await callers.Anonymous.PostAsync("/api/v1/chat/transcriptions", Recording(), Token);

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Theory]
    [InlineData("text/plain", 64, HttpStatusCode.BadRequest)]
    [InlineData("audio/webm", (1024 * 1024) + 1, HttpStatusCode.RequestEntityTooLarge)]
    public async Task should_refuse_a_recording_it_cannot_take_without_calling_the_service(string type, int length, HttpStatusCode status)
    {
        var callers = await Callers.ForAsync(_on);
        var before = _on.Transcriber.Received.Count;

        var response = await callers["Reader"].PostAsync("/api/v1/chat/transcriptions", Recording(type, length), Token);

        response.StatusCode.Should().Be(status);
        _on.Transcriber.Received.Should().HaveCount(before);
    }

    [Fact]
    public async Task should_answer_unprocessable_when_the_service_refused_the_recording_and_keep_the_probe_healthy()
    {
        var callers = await Callers.ForAsync(_on);
        _on.Transcriber.RefuseWith = "The transcription service refused the recording.";
        try
        {
            var response = await callers["Operator"].PostAsync("/api/v1/chat/transcriptions", Recording(), Token);

            response.StatusCode.Should().Be(HttpStatusCode.UnprocessableEntity, "the person may speak again: nothing is wrong with the setup");
            var problem = await response.Content.ReadFromJsonAsync<JsonElement>(Token);
            problem.GetProperty("type").GetString().Should().Be("urn:mnemarr:problem:recording-refused");
            var ready = await _on.CreateClient().GetStringAsync("/health/ready", Token);
            ready.Should().NotContain("transcription-failing");
        }
        finally
        {
            _on.Transcriber.RefuseWith = null;
        }
    }

    [Fact]
    public async Task should_answer_a_bad_gateway_with_mnemarrs_own_sentence_when_the_service_fails()
    {
        var callers = await Callers.ForAsync(_on);
        _on.Transcriber.FailWith = "The transcription service refused the key.";
        try
        {
            var response = await callers["Operator"].PostAsync("/api/v1/chat/transcriptions", Recording(), Token);

            response.StatusCode.Should().Be(HttpStatusCode.BadGateway);
            (await response.Content.ReadFromJsonAsync<JsonElement>(Token)).GetProperty("title").GetString().Should().Be("The transcription service refused the key.");
        }
        finally
        {
            _on.Transcriber.FailWith = null;
        }
    }

    [Fact]
    public async Task should_show_an_administrator_where_the_sound_goes_and_let_them_choose_a_model_of_the_catalog()
    {
        var callers = await Callers.ForAsync(_on);

        var settings = await callers.Admin.GetFromJsonAsync<JsonElement>("/api/v1/transcription", Token);
        settings.GetProperty("enabled").GetBoolean().Should().BeTrue();
        settings.GetProperty("destination").GetString().Should().Be("api.openai.com");
        settings.GetProperty("models").EnumerateArray().Select(model => model.GetProperty("id").GetString()).Should().Contain("whisper-1");

        var chosen = await callers.Admin.PutAsJsonAsync("/api/v1/transcription/model", new { model = "whisper-1" }, Token);
        (await chosen.Content.ReadFromJsonAsync<JsonElement>(Token)).GetProperty("modelLayer").GetString().Should().Be("interface");

        (await callers.Admin.PutAsJsonAsync("/api/v1/transcription/model", new { model = "made-up" }, Token)).StatusCode.Should().Be(HttpStatusCode.BadRequest);

        var back = await callers.Admin.PutAsJsonAsync("/api/v1/transcription/model", new { model = (string?)null }, Token);
        (await back.Content.ReadFromJsonAsync<JsonElement>(Token)).GetProperty("modelLayer").GetString().Should().Be("catalog");
    }

    [Theory]
    [InlineData("Operator")]
    [InlineData("Reader")]
    [InlineData("Assistant")]
    public async Task should_keep_the_settings_of_the_dictation_to_an_administrator(string caller)
    {
        var callers = await Callers.ForAsync(_on);

        (await callers[caller].GetAsync("/api/v1/transcription", Token)).StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await callers[caller].PutAsJsonAsync("/api/v1/transcription/model", new { model = "whisper-1" }, Token)).StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }
}

using System.Net;
using System.Net.Http.Headers;

namespace Mnemarr.Integration.Test;

/// <summary>The daily cap of the dictation through the API (ADR 0047): past it, a 429 that says when to come back, and the recording is not sent.</summary>
public sealed class TranscriptionCapApiFixture : IClassFixture<CappedTranscriptionFactory>
{
    private readonly CappedTranscriptionFactory _factory;

    public TranscriptionCapApiFixture(CappedTranscriptionFactory factory) => _factory = factory;

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    private static MultipartFormDataContent Recording()
    {
        var file = new ByteArrayContent(new byte[64]);
        file.Headers.ContentType = MediaTypeHeaderValue.Parse("audio/webm");
        return new MultipartFormDataContent { { file, "file", "recording.webm" } };
    }

    [Fact]
    public async Task should_answer_too_many_requests_past_the_cap_and_say_when_to_come_back()
    {
        var callers = await Callers.ForAsync(_factory);

        var first = await callers["Reader"].PostAsync("/api/v1/chat/transcriptions", Recording(), Token);
        var second = await callers["Reader"].PostAsync("/api/v1/chat/transcriptions", Recording(), Token);

        first.StatusCode.Should().Be(HttpStatusCode.OK);
        second.StatusCode.Should().Be(HttpStatusCode.TooManyRequests);
        second.Headers.RetryAfter?.Delta.Should().BeCloseTo(TimeSpan.FromDays(1), TimeSpan.FromMinutes(5), "a place frees one day after the first recording");
    }
}

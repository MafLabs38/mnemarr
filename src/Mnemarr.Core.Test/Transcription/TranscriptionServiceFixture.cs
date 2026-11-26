using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using Mnemarr.Core.Audit;
using Mnemarr.Core.Health;
using Mnemarr.Core.Transcription;

namespace Mnemarr.Core.Test.Transcription;

/// <summary>The dictation's rules (ADR 0047): what is refused, the cap per person, the model in use and where it comes from, what is recorded, and the readiness probe.</summary>
public sealed class TranscriptionServiceFixture : IDisposable
{
    private static readonly byte[] Sound = [1, 2, 3, 4];
    private readonly TestWorkspace _workspace = new();
    private readonly FakeTimeProvider _time = new(new DateTimeOffset(2026, 11, 26, 9, 0, 0, TimeSpan.Zero));
    private readonly FakeTranscriber _transcriber = new();
    private readonly RecordingAudit _audit = new();
    private readonly SqliteTranscriptionStore _store;
    private readonly TranscriptionOptions _options = new() { Provider = TranscriptionProviders.OpenAiCompatible, MaxMegabytes = 1, MaxPerPersonPerDay = 2, Language = "fr" };

    public TranscriptionServiceFixture()
    {
        _store = new SqliteTranscriptionStore(_workspace.Database);
    }

    public void Dispose() => _workspace.Dispose();

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    private TranscriptionService Service() =>
        new(Options.Create(_options), TranscriptionProviders.BuiltIn, _transcriber, new TranscriptionModels(Options.Create(_options)), _store, _audit, _time);

    [Fact]
    public async Task should_turn_a_recording_into_text_with_the_first_model_of_the_catalog_and_record_it_without_the_text()
    {
        _transcriber.Answer = TranscriptionResult.Done("Put this idea in the incubator.", 3.5);

        var result = await Service().TranscribeAsync("alice", Sound, "audio/webm;codecs=opus", Token);

        result.Text.Should().Be("Put this idea in the incubator.");
        _transcriber.Last.Should().BeEquivalentTo(new { Model = "gpt-transcribe", ContentType = "audio/webm", Language = "fr" });
        _store.Last().Should().Be(new TranscriptionAttempt("alice", _time.GetUtcNow(), 4, "gpt-transcribe", true, 3.5, null));
        _audit.Events.Should().ContainSingle().Which.Should().Be(("alice", "transcription.used", "gpt-transcribe", "4 bytes"));
        _audit.Events.Single().Detail.Should().NotContain("incubator", "the text is never audited");
    }

    [Fact]
    public async Task should_record_a_failure_with_its_reason_and_degrade_the_probe()
    {
        _transcriber.Answer = TranscriptionResult.Failed("The transcription service refused the key.");

        var result = await Service().TranscribeAsync("alice", Sound, "audio/webm", Token);

        result.Succeeded.Should().BeFalse();
        var health = await Probe().CheckHealthAsync(new HealthCheckContext(), Token);
        health.Status.Should().Be(HealthStatus.Degraded);
        health.Data[HealthReasons.Key].Should().Be("transcription-failing");
    }

    [Theory]
    [InlineData("text/plain", 4, "format")]
    [InlineData(null, 4, "format")]
    [InlineData("audio/webm", 0, "at most 1 MB")]
    [InlineData("audio/webm", (1024 * 1024) + 1, "at most 1 MB")]
    public async Task should_refuse_a_recording_it_cannot_take_without_calling_the_service(string? type, int length, string why)
    {
        var act = () => Service().TranscribeAsync("alice", new byte[length], type, Token);

        (await act.Should().ThrowAsync<RejectedException>()).Which.Message.Should().Contain(why);
        _transcriber.Last.Should().BeNull();
    }

    [Fact]
    public async Task should_be_off_without_a_provider()
    {
        _options.Provider = null;

        var act = () => Service().TranscribeAsync("alice", Sound, "audio/webm", Token);

        (await act.Should().ThrowAsync<RejectedException>()).Which.Problem.Should().Be(Problem.NotFound);
        (await Probe().CheckHealthAsync(new HealthCheckContext(), Token)).Status.Should().Be(HealthStatus.Healthy);
    }

    [Fact]
    public async Task should_hold_each_person_to_the_cap_for_24_hours_whatever_came_of_the_recordings()
    {
        var service = Service();
        _transcriber.Answer = TranscriptionResult.Failed("The transcription service failed.");
        await service.TranscribeAsync("alice", Sound, "audio/webm", Token);
        await service.TranscribeAsync("alice", Sound, "audio/webm", Token);

        var refused = () => service.TranscribeAsync("alice", Sound, "audio/webm", Token);
        (await refused.Should().ThrowAsync<RejectedException>()).Which.Problem.Should().Be(Problem.Busy);
        (await service.TranscribeAsync("bob", Sound, "audio/webm", Token)).Should().NotBeNull("the cap is per person");

        _time.Advance(TimeSpan.FromHours(24) + TimeSpan.FromMinutes(1));
        (await service.TranscribeAsync("alice", Sound, "audio/webm", Token)).Should().NotBeNull("a day later the person may dictate again");
    }

    [Fact]
    public void should_use_the_model_chosen_in_the_interface_then_the_deployments_then_the_catalogs_first()
    {
        var service = Service();
        service.Status().Should().BeEquivalentTo(new { Layer = ModelLayer.Catalog, ModelProblem = (string?)null }, options => options.ExcludingMissingMembers());

        _options.Model = "whisper-1";
        service.Status().Should().BeEquivalentTo(new { Model = new { Id = "whisper-1" }, Layer = ModelLayer.Deployment }, options => options.ExcludingMissingMembers());

        service.ChooseModel("gpt-4o-transcribe", "admin").Should().BeEquivalentTo(new { Model = new { Id = "gpt-4o-transcribe" }, Layer = ModelLayer.Interface }, options => options.ExcludingMissingMembers());
        _audit.Events.Should().Contain(("admin", "transcription.model", "settings", "gpt-4o-transcribe"));

        service.ChooseModel(null, "admin").Layer.Should().Be(ModelLayer.Deployment);
    }

    [Fact]
    public void should_say_when_the_model_asked_for_is_not_in_the_catalog_any_more_and_use_another()
    {
        _options.Model = "retired-model";

        var status = Service().Status();

        status.Model.Id.Should().Be("gpt-transcribe");
        status.Layer.Should().Be(ModelLayer.Catalog);
        status.ModelProblem.Should().Contain("retired-model");
    }

    [Fact]
    public void should_refuse_to_choose_a_model_that_is_not_in_the_catalog()
    {
        var act = () => Service().ChooseModel("made-up", "admin");

        act.Should().Throw<RejectedException>().Which.Problem.Should().Be(Problem.Invalid);
    }

    [Fact]
    public void should_tell_where_the_sound_goes()
    {
        Service().Status().Destination.Should().Be("api.openai.com");
    }

    [Fact]
    public async Task should_degrade_the_probe_when_the_key_file_cannot_be_read()
    {
        _options.OpenAiCompatible.KeyFile = Path.Combine(_workspace.Root, "absent");

        var health = await Probe().CheckHealthAsync(new HealthCheckContext(), Token);

        health.Data[HealthReasons.Key].Should().Be("transcription-secret");
    }

    [Theory]
    [InlineData("fr", true)]
    [InlineData("", true)]
    [InlineData("French", false)]
    [InlineData("FR", false)]
    public void should_take_a_language_code_or_none(string language, bool valid)
    {
        _options.Language = language;

        new TranscriptionOptionsValidator(TranscriptionProviders.BuiltIn).Validate(null, _options).Succeeded.Should().Be(valid);
    }

    private TranscriptionHealthCheck Probe() => new(Options.Create(_options), TranscriptionProviders.BuiltIn, _store);

    private sealed class FakeTranscriber : ITranscriber
    {
        public TranscriptionResult Answer { get; set; } = TranscriptionResult.Done("hello", null);

        public TranscriptionRequest? Last { get; private set; }

        public Task<TranscriptionResult> TranscribeAsync(TranscriptionRequest request, CancellationToken cancellationToken)
        {
            Last = request;
            return Task.FromResult(Answer);
        }
    }

    private sealed class RecordingAudit : IAuditLog
    {
        public List<(string Actor, string Action, string Target, string Detail)> Events { get; } = [];

        public void Append(string actor, string action, string target, string detail = "") => Events.Add((actor, action, target, detail));

        public int Prune(DateTimeOffset before) => 0;

        public IReadOnlyList<AuditEvent> Recent(int limit, string? action = null, long? before = null) => [];
    }
}

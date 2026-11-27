using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Logging.Abstractions;
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

    private TranscriptionService Service(ITranscriptionStore? store = null) =>
        new(
            Options.Create(_options),
            _transcriber,
            new TranscriptionModels(Options.Create(_options), TranscriptionProviders.BuiltIn),
            store ?? _store,
            _audit,
            _time,
            NullLogger<TranscriptionService>.Instance);

    [Fact]
    public async Task should_turn_a_recording_into_text_with_the_first_model_of_the_catalog_and_record_it_without_the_text()
    {
        _transcriber.Answer = TranscriptionResult.Done("Put this idea in the incubator.", 3.5);

        var result = await Service().TranscribeAsync("alice", Sound, "audio/webm;codecs=opus", Token);

        result.Text.Should().Be("Put this idea in the incubator.");
        _transcriber.Last.Should().BeEquivalentTo(new { Model = "gpt-transcribe", ContentType = "audio/webm", Language = "fr" });
        _store.LastOfService().Should().Be(new TranscriptionAttempt("alice", _time.GetUtcNow(), 4, "gpt-transcribe", TranscriptionOutcome.Succeeded, 3.5, null, Service().CurrentSetup()));
        _audit.Events.Should().ContainSingle().Which.Should().Be(("alice", "transcription.used", "gpt-transcribe", "4 bytes"));
        _audit.Events.Single().Detail.Should().NotContain("incubator", "the text is never audited");
    }

    [Fact]
    public async Task should_record_a_failure_with_its_reason_and_degrade_the_probe()
    {
        _transcriber.Answer = TranscriptionResult.Failed("The transcription service refused the key.");

        var result = await Service().TranscribeAsync("alice", Sound, "audio/webm", Token);

        result.Succeeded.Should().BeFalse();
        _audit.Events.Should().ContainSingle().Which.Detail.Should().Be("4 bytes; ServiceFailed: The transcription service refused the key.");
        var health = await Probe().CheckHealthAsync(new HealthCheckContext(), Token);
        health.Status.Should().Be(HealthStatus.Degraded);
        health.Data[HealthReasons.Key].Should().Be("transcription-failing");

        _transcriber.Answer = TranscriptionResult.Done("hello", null);
        await Service().TranscribeAsync("alice", Sound, "audio/webm", Token);
        (await Probe().CheckHealthAsync(new HealthCheckContext(), Token)).Status.Should().Be(HealthStatus.Healthy, "a later success clears it");
    }

    [Fact]
    public async Task should_keep_the_probe_degraded_after_a_restart_while_the_setup_is_the_same()
    {
        // Probe() builds a new service each time, as a restarted process does, over the same database.
        _transcriber.Answer = TranscriptionResult.Failed("The transcription service refused the key.");
        await Service().TranscribeAsync("alice", Sound, "audio/webm", Token);

        _time.Advance(TimeSpan.FromHours(1));

        var health = await Probe().CheckHealthAsync(new HealthCheckContext(), Token);
        health.Status.Should().Be(HealthStatus.Degraded, "a restart fixes nothing: the key is still the one that failed");
        health.Data[HealthReasons.Key].Should().Be("transcription-failing");
    }

    [Fact]
    public async Task should_forget_a_failure_once_the_setup_changes_so_that_a_fixed_setup_is_healthy_again()
    {
        _transcriber.Answer = TranscriptionResult.Failed("The transcription service cannot be reached.");
        await Service().TranscribeAsync("alice", Sound, "audio/webm", Token);
        var before = Service().CurrentSetup();

        _options.OpenAiCompatible.BaseUrl = "https://speech.homelab.example/v1";

        Service().CurrentSetup().Should().NotBe(before);
        (await Probe().CheckHealthAsync(new HealthCheckContext(), Token)).Status.Should().Be(HealthStatus.Healthy, "the operator fixed the address");
    }

    [Fact]
    public async Task should_forget_a_failure_once_the_key_file_is_written_again()
    {
        var key = Path.Combine(_workspace.Root, "key");
        await File.WriteAllTextAsync(key, "sk-old", Token);
        File.SetLastWriteTimeUtc(key, new DateTime(2026, 11, 1, 0, 0, 0, DateTimeKind.Utc));
        _options.OpenAiCompatible.KeyFile = key;
        _transcriber.Answer = TranscriptionResult.Failed("The transcription service refused the key.");
        await Service().TranscribeAsync("alice", Sound, "audio/webm", Token);

        await File.WriteAllTextAsync(key, "sk-new", Token);
        File.SetLastWriteTimeUtc(key, new DateTime(2026, 11, 26, 0, 0, 0, DateTimeKind.Utc));

        (await Probe().CheckHealthAsync(new HealthCheckContext(), Token)).Status.Should().Be(HealthStatus.Healthy, "the operator put a new key in place");
    }

    [Fact]
    public async Task should_forget_a_failure_once_an_administrator_chooses_another_model()
    {
        _transcriber.Answer = TranscriptionResult.Failed("The transcription service does not know the model.");
        await Service().TranscribeAsync("alice", Sound, "audio/webm", Token);

        Service().ChooseModel("whisper-1", "admin");

        (await Probe().CheckHealthAsync(new HealthCheckContext(), Token)).Status.Should().Be(HealthStatus.Healthy, "a model the service does not know is fixed by another one");
    }

    [Fact]
    public async Task should_not_count_a_failure_recorded_before_the_setup_was_kept()
    {
        _transcriber.Answer = TranscriptionResult.Failed("The transcription service refused the key.");
        await Service().TranscribeAsync("alice", Sound, "audio/webm", Token);
        using (var connection = _workspace.Database.Open())
        using (var command = connection.CreateCommand())
        {
            // A row written before schema step 26.
            command.CommandText = "UPDATE transcription_log SET setup = NULL";
            command.ExecuteNonQuery();
        }

        _store.LastOfService()!.Setup.Should().BeNull();
        (await Probe().CheckHealthAsync(new HealthCheckContext(), Token)).Status.Should().Be(HealthStatus.Healthy, "it says nothing about the current setup");
    }

    [Fact]
    public async Task should_see_a_new_key_copied_with_its_old_time_by_its_length_and_never_by_its_text()
    {
        var key = Path.Combine(_workspace.Root, "key");
        var time = new DateTime(2026, 11, 1, 0, 0, 0, DateTimeKind.Utc);
        await File.WriteAllTextAsync(key, "SECRET-VALUE", Token);
        File.SetLastWriteTimeUtc(key, time);
        _options.OpenAiCompatible.KeyFile = key;
        var provider = new OpenAiCompatibleTranscriptionProvider();
        var before = provider.Setup(_options);

        await File.WriteAllTextAsync(key, "ANOTHER-SECRET-VALUE", Token);
        File.SetLastWriteTimeUtc(key, time);

        provider.Setup(_options).Should().NotBe(before).And.NotContain("SECRET");
    }

    [Fact]
    public void should_not_fail_on_a_key_file_path_that_cannot_be_looked_at()
    {
        _options.OpenAiCompatible.KeyFile = "bad\0path";

        new OpenAiCompatibleTranscriptionProvider().Setup(_options).Should().EndWith("|unreadable");
    }

    [Theory]
    [InlineData(null)]
    [InlineData("missing")]
    public void should_fingerprint_the_key_file_whether_or_not_it_is_there(string? file)
    {
        _options.OpenAiCompatible.KeyFile = file is null ? null : Path.Combine(_workspace.Root, file);
        var provider = new OpenAiCompatibleTranscriptionProvider();

        var setup = provider.Setup(_options);

        setup.Should().EndWith("|none");
        _options.OpenAiCompatible.KeyFile = Path.Combine(_workspace.Root, "other");
        provider.Setup(_options).Should().NotBe(setup, "another key file is another setup");
    }

    [Fact]
    public void should_transcribe_two_recordings_at_once_one_per_person_and_ask_the_next_one_to_wait_a_few_seconds()
    {
        var service = Service();
        var alice = service.Enter("alice");
        using var bob = service.Enter("bob");

        var again = () => service.Enter("alice");
        var third = () => service.Enter("carol");

        again.Should().Throw<RejectedException>().Which.Message.Should().Contain("Your previous recording");
        var refused = third.Should().Throw<RejectedException>().Which;
        refused.Problem.Should().Be(Problem.Busy);
        refused.RetryAfter.Should().Be(TimeSpan.FromSeconds(5));

        alice.Dispose();
        using var aliceAgain = service.Enter("alice");
        alice.Dispose();
        again.Should().Throw<RejectedException>("an old lease given back twice does not give back the new one").Which.Message.Should().Contain("Your previous recording");
    }

    [Fact]
    public async Task should_give_the_text_even_when_the_attempt_cannot_be_recorded()
    {
        _transcriber.Answer = TranscriptionResult.Done("Put this idea in the incubator.", 2);
        var service = Service(new FailingOnComplete(_store, new SqliteException("database is locked", 5)));

        var result = await service.TranscribeAsync("alice", Sound, "audio/webm", Token);

        _store.CountSince("alice", _time.GetUtcNow() - TimeSpan.FromDays(1)).Should().Be(1, "the cap still counts the attempt");
        _audit.Events.Should().ContainSingle("the audit is written on its own");
        result.Text.Should().Be("Put this idea in the incubator.", "the service did the work: a log that cannot be written does not take it away");
    }

    [Fact]
    public async Task should_not_hide_an_error_that_is_not_the_database()
    {
        var service = Service(new FailingOnComplete(_store, new InvalidOperationException("a bug")));

        var transcribe = () => service.TranscribeAsync("alice", Sound, "audio/webm", Token);

        await transcribe.Should().ThrowAsync<InvalidOperationException>();
    }

    [Fact]
    public async Task should_forget_the_attempts_older_than_a_week_when_a_recording_comes()
    {
        var weekAgo = _time.GetUtcNow() - TimeSpan.FromDays(8);
        var old = _store.Reserve("bob", weekAgo, 4, "gpt-transcribe", "old", weekAgo - TimeSpan.FromDays(1), 100)!.Value;
        _store.Complete(old, TranscriptionOutcome.Cancelled, null, null);

        await Service().TranscribeAsync("alice", Sound, "audio/webm", Token);

        _store.CountSince("bob", _time.GetUtcNow() - TimeSpan.FromDays(30)).Should().Be(0);
    }

    [Fact]
    public async Task should_not_degrade_the_probe_for_a_recording_the_service_refused()
    {
        _transcriber.Answer = TranscriptionResult.Refused("The transcription service refused the recording.");

        await Service().TranscribeAsync("alice", Sound, "audio/webm", Token);

        (await Probe().CheckHealthAsync(new HealthCheckContext(), Token)).Status.Should().Be(HealthStatus.Healthy, "a bad sound says nothing about the setup");
        _audit.Events.Single().Detail.Should().Contain("RecordingRefused");
    }

    // A loaded machine (the whole suite in parallel on a small runner) can be slow to start the three calls: the test as a whole has a minute, not each wait.
    [Fact(Timeout = 60_000)]
    public async Task should_hold_the_cap_when_recordings_are_sent_at_the_same_time()
    {
        var token = TestContext.Current.CancellationToken;
        _options.MaxPerPersonPerDay = 1;
        var gate = new TaskCompletionSource<TranscriptionResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        _transcriber.Pending = gate.Task;
        var service = Service();

        var calls = Enumerable.Range(0, 3).Select(_ => Task.Run(() => service.TranscribeAsync("alice", Sound, "audio/webm", token), token)).ToList();

        // One call reaches the service and waits there; the two others are refused without it. Only then is the service let go.
        await _transcriber.Entered.WaitAsync(token);
        while (calls.Count(call => call.IsCompleted) < 2)
        {
            await Task.WhenAny(calls.Where(call => !call.IsCompleted)).WaitAsync(token);
        }

        gate.SetResult(TranscriptionResult.Done("hello", null));
        var outcomes = await Task.WhenAll(calls.Select(async call =>
        {
            try
            {
                await call;
                return "done";
            }
            catch (RejectedException ex) when (ex.Problem == Problem.Busy)
            {
                return "busy";
            }
        }));

        outcomes.Should().BeEquivalentTo(["done", "busy", "busy"], "the place in the cap is taken before the service is called");
        _transcriber.Calls.Should().Be(1, "the paid service is called once");
    }

    [Fact]
    public async Task should_count_and_audit_a_call_that_was_cancelled_while_the_service_worked()
    {
        _options.MaxPerPersonPerDay = 1;
        var gate = new TaskCompletionSource<TranscriptionResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        _transcriber.Pending = gate.Task;
        using var cancel = CancellationTokenSource.CreateLinkedTokenSource(Token);
        var call = Service().TranscribeAsync("alice", Sound, "audio/webm", cancel.Token);
        await _transcriber.Entered.WaitAsync(Token);
        await cancel.CancelAsync();

        await call.Awaiting(pending => pending).Should().ThrowAsync<OperationCanceledException>();

        _store.CountSince("alice", _time.GetUtcNow() - TimeSpan.FromDays(1)).Should().Be(1, "the service may still count it: so does the cap");
        _audit.Events.Should().ContainSingle().Which.Detail.Should().Contain("Cancelled");
        var again = () => Service().TranscribeAsync("alice", Sound, "audio/webm", Token);
        await again.Should().ThrowAsync<RejectedException>();
    }

    [Fact]
    public async Task should_count_a_call_that_broke_on_the_way_as_a_failure_of_the_service_not_a_cancellation()
    {
        _transcriber.Throw = new InvalidOperationException("broken");

        var act = () => Service().TranscribeAsync("alice", Sound, "audio/webm", Token);

        await act.Should().ThrowAsync<InvalidOperationException>();
        _audit.Events.Should().ContainSingle().Which.Detail.Should().Be("4 bytes; ServiceFailed: The transcription failed unexpectedly.");
    }

    [Fact]
    public async Task should_refuse_before_a_recording_is_read_what_it_would_refuse_after()
    {
        _options.MaxPerPersonPerDay = 1;
        var service = Service();

        service.Admit("alice", "audio/webm", 4);
        FluentActions.Invoking(() => service.Admit("alice", "text/plain", 4)).Should().Throw<RejectedException>().Which.Problem.Should().Be(Problem.Invalid);
        await service.TranscribeAsync("alice", Sound, "audio/webm", Token);

        FluentActions.Invoking(() => service.Admit("alice", "audio/webm", 4)).Should().Throw<RejectedException>().Which.Problem.Should().Be(Problem.Busy);
        _options.Provider = null;
        FluentActions.Invoking(() => service.Admit("bob", "audio/webm", 4)).Should().Throw<RejectedException>().Which.Problem.Should().Be(Problem.NotFound);
    }

    [Fact]
    public async Task should_say_when_the_cap_frees_a_place_one_day_after_the_oldest_recording()
    {
        _options.MaxPerPersonPerDay = 1;
        await Service().TranscribeAsync("alice", Sound, "audio/webm", Token);
        _time.Advance(TimeSpan.FromHours(2));

        var refused = () => Service().TranscribeAsync("alice", Sound, "audio/webm", Token);

        (await refused.Should().ThrowAsync<RejectedException>()).Which.RetryAfter.Should().Be(TimeSpan.FromHours(22));
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
        // An administrator chose it when the catalog had it; the catalog changed since.
        _store.ChooseModel("retired-model");

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
    [InlineData("fra", false)]
    public void should_take_a_language_code_or_none(string language, bool valid)
    {
        _options.Language = language;

        new TranscriptionOptionsValidator(TranscriptionProviders.BuiltIn).Validate(null, _options).Succeeded.Should().Be(valid);
    }

    [Theory]
    [InlineData(1, 1, true)]
    [InlineData(25, 10_000, true)]
    [InlineData(0, 100, false)]
    [InlineData(26, 100, false)]
    [InlineData(10, 0, false)]
    [InlineData(10, 10_001, false)]
    public void should_keep_the_size_and_the_cap_within_their_bounds(int megabytes, int perDay, bool valid)
    {
        _options.MaxMegabytes = megabytes;
        _options.MaxPerPersonPerDay = perDay;

        new TranscriptionOptionsValidator(TranscriptionProviders.BuiltIn).Validate(null, _options).Succeeded.Should().Be(valid);
    }

    [Theory]
    [InlineData("Whisperer", false)]
    [InlineData("openaicompatible", true)]
    public void should_name_the_providers_it_knows(string provider, bool valid)
    {
        _options.Provider = provider;

        var result = new TranscriptionOptionsValidator(TranscriptionProviders.BuiltIn).Validate(null, _options);

        result.Succeeded.Should().Be(valid);
        if (!valid)
        {
            result.FailureMessage.Should().Contain(TranscriptionProviders.OpenAiCompatible);
        }
    }

    [Fact]
    public void should_check_nothing_while_dictation_is_off()
    {
        _options.Provider = "  ";
        _options.MaxMegabytes = 0;

        new TranscriptionOptionsValidator(TranscriptionProviders.BuiltIn).Validate(null, _options).Succeeded.Should().BeTrue();
    }

    private TranscriptionHealthCheck Probe() => new(Options.Create(_options), TranscriptionProviders.BuiltIn, _store, Service());

    private sealed class FakeTranscriber : ITranscriber
    {
        public TranscriptionResult Answer { get; set; } = TranscriptionResult.Done("hello", null);

        public TranscriptionRequest? Last { get; private set; }

        /// <summary>When set, a call waits for it (or for its cancellation), as a slow service does.</summary>
        public Task<TranscriptionResult>? Pending { get; set; }

        /// <summary>When set, a call throws it, as a broken connection does.</summary>
        public Exception? Throw { get; set; }

        /// <summary>Done as soon as a call reaches the service.</summary>
        public Task Entered => _entered.Task;

        public int Calls => _calls;

        private readonly TaskCompletionSource _entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _calls;

        public async Task<TranscriptionResult> TranscribeAsync(TranscriptionRequest request, CancellationToken cancellationToken)
        {
            Last = request;
            Interlocked.Increment(ref _calls);
            _entered.TrySetResult();
            if (Throw is not null)
            {
                throw Throw;
            }

            return Pending is null ? Answer : await Pending.WaitAsync(cancellationToken);
        }
    }

    private sealed class RecordingAudit : IAuditLog
    {
        public List<(string Actor, string Action, string Target, string Detail)> Events { get; } = [];

        public void Append(string actor, string action, string target, string detail = "") => Events.Add((actor, action, target, detail));

        public int Prune(DateTimeOffset before) => 0;

        public IReadOnlyList<AuditEvent> Recent(int limit, string? action = null, long? before = null) => [];
    }

    /// <summary>A store that throws when an attempt ends (a locked database, a bug): everything else is the real store.</summary>
    private sealed class FailingOnComplete(ITranscriptionStore inner, Exception failure) : ITranscriptionStore
    {
        public string? ChosenModel() => inner.ChosenModel();

        public void ChooseModel(string? model) => inner.ChooseModel(model);

        public long? Reserve(string actor, DateTimeOffset at, long bytes, string model, string setup, DateTimeOffset since, int max) =>
            inner.Reserve(actor, at, bytes, model, setup, since, max);

        public void Complete(long id, TranscriptionOutcome outcome, double? seconds, string? reason) => throw failure;

        public int CountSince(string actor, DateTimeOffset since) => inner.CountSince(actor, since);

        public DateTimeOffset? OldestSince(string actor, DateTimeOffset since) => inner.OldestSince(actor, since);

        public TranscriptionAttempt? LastOfService() => inner.LastOfService();

        public void Prune(DateTimeOffset before) => inner.Prune(before);
    }
}

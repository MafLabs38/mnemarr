using Mnemarr.Core.Transcription;

namespace Mnemarr.Core.Test.Transcription;

/// <summary>The store of the dictation on a real database: the window of the cap, what is pruned, the model chosen and given back, and what the probe reads.</summary>
public sealed class TranscriptionStoreFixture : IDisposable
{
    private static readonly DateTimeOffset Now = new(2026, 11, 26, 9, 0, 0, TimeSpan.Zero);
    private readonly TestWorkspace _workspace = new();
    private readonly SqliteTranscriptionStore _store;

    public TranscriptionStoreFixture() => _store = new SqliteTranscriptionStore(_workspace.Database);

    public void Dispose() => _workspace.Dispose();

    private long Reserve(string actor, DateTimeOffset at, int max = 100) => _store.Reserve(actor, at, 10, "gpt-transcribe", "setup-1", at - TimeSpan.FromDays(1), max)!.Value;

    [Fact]
    public void should_count_the_recordings_of_one_person_inside_the_window_only()
    {
        Reserve("alice", Now - TimeSpan.FromHours(25));
        Reserve("alice", Now - TimeSpan.FromHours(24));
        Reserve("alice", Now);
        Reserve("bob", Now);

        _store.CountSince("alice", Now - TimeSpan.FromHours(24)).Should().Be(2, "the start of the window is in it");
        _store.OldestSince("alice", Now - TimeSpan.FromHours(24)).Should().Be(Now - TimeSpan.FromHours(24));
        _store.OldestSince("carol", Now - TimeSpan.FromHours(24)).Should().BeNull();
    }

    [Fact]
    public void should_refuse_a_place_once_the_cap_is_reached()
    {
        _store.Reserve("alice", Now, 10, "gpt-transcribe", "setup-1", Now - TimeSpan.FromDays(1), 2).Should().NotBeNull();
        _store.Reserve("alice", Now, 10, "gpt-transcribe", "setup-1", Now - TimeSpan.FromDays(1), 2).Should().NotBeNull();

        _store.Reserve("alice", Now, 10, "gpt-transcribe", "setup-1", Now - TimeSpan.FromDays(1), 2).Should().BeNull();
        _store.Reserve("bob", Now, 10, "gpt-transcribe", "setup-1", Now - TimeSpan.FromDays(1), 2).Should().NotBeNull("the cap is per person");
    }

    [Fact]
    public void should_forget_only_what_is_older_than_asked()
    {
        Reserve("alice", Now - TimeSpan.FromDays(8));
        Reserve("alice", Now - TimeSpan.FromDays(6));

        _store.Prune(Now - TimeSpan.FromDays(7));

        _store.CountSince("alice", Now - TimeSpan.FromDays(30)).Should().Be(1);
    }

    [Fact]
    public void should_keep_the_last_word_of_the_service_however_old_so_that_the_probe_does_not_forget_a_failure()
    {
        _store.Complete(Reserve("alice", Now - TimeSpan.FromDays(10)), TranscriptionOutcome.ServiceFailed, null, "The transcription service refused the key.");
        _store.Complete(Reserve("alice", Now - TimeSpan.FromDays(9)), TranscriptionOutcome.RecordingRefused, null, "The transcription service refused the recording.");

        _store.Prune(Now - TimeSpan.FromDays(7));

        _store.LastOfService()!.Outcome.Should().Be(TranscriptionOutcome.ServiceFailed);
        _store.CountSince("alice", Now - TimeSpan.FromDays(30)).Should().Be(1, "the refused recording is forgotten");
    }

    [Fact]
    public void should_give_the_probe_the_last_word_of_the_service_and_not_a_refused_or_cancelled_recording()
    {
        _store.LastOfService().Should().BeNull();
        _store.Complete(Reserve("alice", Now), TranscriptionOutcome.ServiceFailed, null, "The transcription service refused the key.");
        _store.Complete(Reserve("alice", Now), TranscriptionOutcome.RecordingRefused, null, "The transcription service refused the recording.");
        _store.Complete(Reserve("alice", Now), TranscriptionOutcome.Cancelled, null, null);
        Reserve("alice", Now);

        _store.LastOfService().Should().Be(new TranscriptionAttempt("alice", Now, 10, "gpt-transcribe", TranscriptionOutcome.ServiceFailed, null, "The transcription service refused the key.", "setup-1"));
    }

    [Fact]
    public void should_keep_the_model_chosen_and_give_it_back()
    {
        _store.ChosenModel().Should().BeNull();

        _store.ChooseModel("whisper-1");
        _store.ChosenModel().Should().Be("whisper-1");

        _store.ChooseModel(null);
        _store.ChosenModel().Should().BeNull();
    }
}

using System.Text.Json;
using Microsoft.Extensions.Time.Testing;
using Mnemarr.Core.Accounts;
using Mnemarr.Core.Audit;
using Mnemarr.Core.Conversations;
using Mnemarr.Core.Indexing;
using Mnemarr.Core.Sources;

namespace Mnemarr.Core.Test.Conversations;

/// <summary>
/// The format a web assistant's satellite sends (ADR 0041): one JSON object per line, line 0 a <c>meta</c> line that is not a turn but takes position 0. A line that is not what is expected
/// is skipped and never a failure; the title the sender gives is the title of the conversation, and no model is asked for another.
/// </summary>
public sealed class ChatTurnsFixture : IDisposable
{
    private readonly TestWorkspace _workspace = new();
    private readonly SourceLocks _locks = new();
    private readonly FakeTimeProvider _clock = new(new DateTimeOffset(2026, 11, 12, 9, 0, 0, TimeSpan.Zero));
    private readonly SqliteNoteIndex _index;
    private readonly SqliteConversationStore _store;
    private readonly ConversationIngestor _ingestor;

    public ChatTurnsFixture()
    {
        var paths = _workspace.Paths();
        _index = new SqliteNoteIndex(_workspace.Database);
        _store = new SqliteConversationStore(_workspace.Database);
        var catalog = new SourceCatalog(new SqliteSourceStore(_workspace.Database), new SqliteAuditLog(_workspace.Database, _clock), paths, _clock);
        var indexer = new SourceIndexer(_index, paths, new ScanOutcomes(), _clock, _locks);
        var raw = new RawConversations(_workspace.Options);
        _ingestor = new ConversationIngestor(catalog, _locks, indexer, raw, _store, new ConversationRelocator(catalog, _locks, indexer, raw, _store, new Mnemarr.Core.Collectors.SqliteMachineStore(_workspace.Database)), _clock);
    }

    public void Dispose() => _workspace.Dispose();

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    private static string Meta(string? title, string source = "web") =>
        JsonSerializer.Serialize(new { v = 1, meta = new { title, source, id = "c1a2b3c4d5e6" } });

    private static string TurnLine(string role, string? at, string text) => JsonSerializer.Serialize(new { v = 1, role, at, text });

    private static IngestBatch Batch(long from, params string[] lines) => new(ConversationFormats.ChatTurns, "gemini", "gemini", "gemini-c1a2b3c4d5e6", from, lines);

    private string NoteFile => Path.Combine(_workspace.DataFolder, "conversations", "gemini", "gemini", "2026-11-12-gemini-c1a2b3c4d5e6.md");

    // The two lines of the sample conversation: invented, like every text of the tests.
    private static readonly string[] Sample =
    [
        Meta("A distributed AI?"),
        TurnLine("user", "2026-11-12T08:03:12Z", "Could an assistant run across several small machines?"),
        TurnLine("assistant", null, "It could, by splitting the work and keeping the state in one place."),
    ];

    [Fact]
    public void should_read_the_turns_and_leave_the_meta_line_out_of_them()
    {
        var turns = ConversationFormats.Turns(ConversationFormats.ChatTurns, Sample);

        turns.Select(turn => turn.Role).Should().Equal("user", "assistant");
        turns[0].At.Should().Be(new DateTimeOffset(2026, 11, 12, 8, 3, 12, TimeSpan.Zero));
        turns[1].At.Should().BeNull();
        turns[1].Text.Should().Contain("splitting the work");
    }

    [Theory]
    [InlineData("{\"v\":1,\"role\":\"system\",\"at\":null,\"text\":\"hidden\"}")]
    [InlineData("{\"v\":2,\"role\":\"user\",\"at\":null,\"text\":\"from a later version\"}")]
    [InlineData("{\"role\":\"user\",\"at\":null,\"text\":\"no version\"}")]
    [InlineData("{\"v\":1,\"role\":\"user\",\"at\":null,\"text\":\"   \"}")]
    [InlineData("{\"v\":1,\"role\":\"user\",\"at\":null}")]
    [InlineData("{\"v\":1,\"role\":7,\"at\":null,\"text\":\"role is not a string\"}")]
    [InlineData("not json at all")]
    [InlineData("[\"user\",\"text\"]")]
    [InlineData("\"just a string\"")]
    [InlineData("")]
    public void should_skip_a_line_that_is_not_what_is_expected_without_failing(string line)
    {
        ConversationFormats.Turns(ConversationFormats.ChatTurns, [line, TurnLine("user", null, "kept")]).Should().ContainSingle().Which.Text.Should().Be("kept");
    }

    [Fact]
    public void should_cut_a_turn_that_is_too_long_and_mask_a_secret_that_only_shows_once_decoded()
    {
        var secret = "{\"v\":1,\"role\":\"user\",\"at\":null,\"text\":\"here: \\u0067hp_" + new string('a', 36) + "\"}";

        var turns = ConversationFormats.Turns(ConversationFormats.ChatTurns, [secret, TurnLine("assistant", null, new string('x', ConversationFormats.MaxTurnChars + 500))]);

        turns[0].Text.Should().NotContain("ghp_");
        turns[1].Text.Length.Should().BeLessThan(ConversationFormats.MaxTurnChars + 20);
    }

    [Theory]
    [InlineData("A distributed AI?", "A distributed AI?")]
    [InlineData("  spaces   and\nlines  ", "spaces and lines")]
    [InlineData("", null)]
    [InlineData("   ", null)]
    public void should_take_the_title_of_the_meta_line_and_none_when_it_has_none(string title, string? expected)
    {
        ConversationFormats.SenderTitle(ConversationFormats.ChatTurns, Meta(title)).Should().Be(expected);
    }

    [Fact]
    public void should_not_take_a_title_from_a_line_that_is_not_a_meta_line_or_from_another_format()
    {
        ConversationFormats.SenderTitle(ConversationFormats.ChatTurns, TurnLine("user", null, "hello")).Should().BeNull();
        ConversationFormats.SenderTitle(ConversationFormats.ChatTurns, "{\"v\":1,\"meta\":\"not an object\"}").Should().BeNull();
        ConversationFormats.SenderTitle(ConversationFormats.ChatTurns, null).Should().BeNull();
        ConversationFormats.SenderTitle(ConversationFormats.ClaudeCode, Meta("A title")).Should().BeNull();
    }

    [Fact]
    public void should_cut_and_mask_the_title_the_sender_gives()
    {
        var title = ConversationFormats.SenderTitle(ConversationFormats.ChatTurns, Meta(new string('t', 500)));
        title.Should().HaveLength(ConversationFormats.MaxSenderTitle);

        var masked = ConversationFormats.SenderTitle(ConversationFormats.ChatTurns, Meta("about ghp_" + new string('a', 36)));
        masked.Should().NotContain("ghp_");
    }

    [Fact]
    public async Task should_file_the_conversation_under_the_machine_and_the_application_and_title_it_with_the_title_of_the_sender()
    {
        var result = await _ingestor.IngestAsync(Batch(0, Sample), Token);

        result.Should().Be(new IngestResult(3, 3), "the meta line is a line: it takes position 0");
        var note = File.ReadAllText(NoteFile);
        note.Should().Contain("# A distributed AI?").And.Contain("## User · 2026-11-12 08:03").And.Contain("## Assistant").And.Contain("source: chat-turns");
        note.Split("## ").Should().HaveCount(3, "the header and two turns: the meta line is not a turn");
        var record = _store.Find("gemini", "gemini-c1a2b3c4d5e6")!;
        record.Should().Match<ConversationRecord>(found => found.Title == "A distributed AI?" && found.TitleSource == TitleSources.Sender && found.Lines == 3);
        _index.Search(new SearchRequest("splitting"), SourceScope.All).Should().ContainSingle();
    }

    [Fact]
    public async Task should_keep_the_title_of_the_sender_when_the_conversation_grows()
    {
        await _ingestor.IngestAsync(Batch(0, Sample), Token);

        var result = await _ingestor.IngestAsync(Batch(3, TurnLine("user", "2026-11-12T08:10:00Z", "And what about the network?")), Token);

        result.Should().Be(new IngestResult(4, 1));
        File.ReadAllText(NoteFile).Should().StartWith("---").And.Contain("# A distributed AI?").And.Contain("what about the network");
        _store.Find("gemini", "gemini-c1a2b3c4d5e6")!.Title.Should().Be("A distributed AI?");
    }

    [Fact]
    public async Task should_keep_the_title_when_the_first_batch_holds_only_the_meta_line()
    {
        (await _ingestor.IngestAsync(Batch(0, Meta("Named before it is said")), Token)).Should().Be(new IngestResult(1, 1));
        File.Exists(NoteFile).Should().BeFalse("there is nothing to say yet");

        await _ingestor.IngestAsync(Batch(1, TurnLine("user", null, "the first thing said")), Token);

        File.ReadAllText(NoteFile).Should().Contain("# Named before it is said");
        _store.Find("gemini", "gemini-c1a2b3c4d5e6")!.TitleSource.Should().Be(TitleSources.Sender);
    }

    [Fact]
    public async Task should_use_the_first_sentence_when_the_meta_line_has_no_title_and_never_move_a_position()
    {
        await _ingestor.IngestAsync(Batch(0, Meta(""), TurnLine("user", null, "A question about certificates and their renewal.")), Token);

        var record = _store.Find("gemini", "gemini-c1a2b3c4d5e6")!;
        record.TitleSource.Should().Be(TitleSources.FirstSentence);
        record.Lines.Should().Be(2);
        File.ReadAllText(NoteFile).Should().Contain("# A question about certificates and their renewal.");
    }

    [Fact]
    public async Task should_behave_by_position_like_the_other_format_when_a_batch_overlaps_is_sent_again_or_leaves_a_hole()
    {
        await _ingestor.IngestAsync(Batch(0, Sample), Token);

        (await _ingestor.IngestAsync(Batch(0, Sample), Token)).Should().Be(new IngestResult(3, 0));
        (await _ingestor.IngestAsync(Batch(2, Sample[2], TurnLine("user", null, "one more")), Token)).Should().Be(new IngestResult(4, 1));
        (await _ingestor.IngestAsync(Batch(9, TurnLine("user", null, "too far")), Token)).Should().Be(new IngestResult(4, 0), "a hole is not filled: the sender is told where to resume");
        _ = _ingestor.Positions("gemini").Should().ContainSingle().Which.Lines.Should().Be(4);
    }

    [Fact]
    public async Task should_refuse_to_read_a_known_conversation_in_another_format()
    {
        await _ingestor.IngestAsync(Batch(0, Sample), Token);

        var act = () => _ingestor.IngestAsync(new IngestBatch(ConversationFormats.ClaudeCode, "gemini", "gemini", "gemini-c1a2b3c4d5e6", 3, ["{}"]), Token);

        await act.Should().ThrowAsync<RejectedException>();
    }
}

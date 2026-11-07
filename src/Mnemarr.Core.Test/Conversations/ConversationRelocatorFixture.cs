using Microsoft.Extensions.Time.Testing;
using Mnemarr.Core.Accounts;
using Mnemarr.Core.Audit;
using Mnemarr.Core.Collectors;
using Mnemarr.Core.Conversations;
using Mnemarr.Core.Indexing;
using Mnemarr.Core.Sources;

namespace Mnemarr.Core.Test.Conversations;

public sealed class ConversationRelocatorFixture : IDisposable
{
    private readonly TestWorkspace _workspace = new();
    private readonly FakeTimeProvider _clock = new(new DateTimeOffset(2026, 11, 7, 9, 0, 0, TimeSpan.Zero));
    private readonly SqliteNoteIndex _index;
    private readonly SqliteConversationStore _store;
    private readonly SqliteMachineStore _machines;
    private readonly ConversationIngestor _ingestor;
    private readonly ConversationRelocator _relocator;

    public ConversationRelocatorFixture()
    {
        var locks = new SourceLocks();
        var paths = _workspace.Paths();
        _index = new SqliteNoteIndex(_workspace.Database);
        _store = new SqliteConversationStore(_workspace.Database);
        _machines = new SqliteMachineStore(_workspace.Database);
        var catalog = new SourceCatalog(new SqliteSourceStore(_workspace.Database), new SqliteAuditLog(_workspace.Database, _clock), paths, _clock);
        var indexer = new SourceIndexer(_index, paths, new ScanOutcomes(), _clock, locks);
        var raw = new RawConversations(_workspace.Options);
        _relocator = new ConversationRelocator(catalog, locks, indexer, raw, _store, _machines);
        _ingestor = new ConversationIngestor(catalog, locks, indexer, raw, _store, _relocator, _clock);
    }

    public void Dispose() => _workspace.Dispose();

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    private static string Line(string type, string text, string at) =>
        System.Text.Json.JsonSerializer.Serialize(new { type, timestamp = at, message = new { role = type, content = text } });

    private const string Computer = "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef";

    private static IngestBatch Batch(string machine, string session, long from, params string[] lines) => new(ConversationFormats.ClaudeCode, machine, "Mnemarr Core", session, from, lines, ComputerId: Computer);

    private static IngestBatch Anonymous(string machine, string session, long from, params string[] lines) => Batch(machine, session, from, lines) with { ComputerId = null };

    private static IngestBatch Elsewhere(string machine, string session, long from, params string[] lines) => Batch(machine, session, from, lines) with { ComputerId = "fedcba9876543210fedcba9876543210fedcba9876543210fedcba9876543210" };

    private static readonly string[] Talk = [Line("user", "How do I rotate the tokens?", "2026-11-04T14:03:00Z"), Line("assistant", "Use the admin page, then revoke the old key.", "2026-11-04T14:04:00Z")];

    private string NotesOf(string machine) => Path.Combine(_workspace.DataFolder, "conversations", machine);

    private string RawOf(string machine) => Path.Combine(_workspace.DataFolder, "raw", machine);

    private List<string> Hits(string words) => [.. _index.Search(new SearchRequest(words), SourceScope.All).Select(hit => hit.Path)];

    private void Enrol(string name) => _machines.Enrol(new Machine(name, MachineKind.Intermittent, Guid.NewGuid(), _clock.GetUtcNow(), null, null, 0, null, null));

    [Fact]
    public async Task should_file_what_a_machine_sent_under_its_new_name_the_note_the_lines_the_row_and_the_search()
    {
        await _ingestor.IngestAsync(Batch("gamng-pc", "aaaa1111", 0, Talk), Token);

        var moved = await _relocator.MoveAllAsync("gamng-pc", "gaming-pc", Token);

        moved.Should().Be(new Relocation(1, 0));
        Directory.Exists(NotesOf("gamng-pc")).Should().BeFalse("the folder of the old name is gone with its last note");
        Directory.Exists(RawOf("gamng-pc")).Should().BeFalse();
        var note = Directory.EnumerateFiles(NotesOf("gaming-pc"), "*.md", SearchOption.AllDirectories).Single();
        File.ReadAllText(note).Should().Contain("gaming-pc").And.NotContain("gamng-pc").And.Contain("revoke the old key");
        Hits("revoke").Should().ContainSingle().Which.Should().StartWith("gaming-pc/");
        _store.OfMachine("gamng-pc").Should().BeEmpty();
        _store.OfMachine("gaming-pc").Should().ContainSingle();
    }

    [Fact]
    public async Task should_not_make_a_conversation_twice_when_the_other_machine_holds_it_already()
    {
        Enrol("gamng-pc");
        Enrol("gaming-pc");
        await _ingestor.IngestAsync(Batch("gamng-pc", "aaaa1111", 0, Talk), Token);
        await _ingestor.IngestAsync(Batch("gaming-pc", "aaaa1111", 0, Talk), Token);
        await _ingestor.IngestAsync(Batch("gamng-pc", "bbbb2222", 0, Talk), Token);

        var moved = await _relocator.MoveAllAsync("gamng-pc", "gaming-pc", Token);

        moved.Should().Be(new Relocation(1, 1), "the one both had is kept once, the other is filed under the new name");
        _store.OfMachine("gaming-pc").Select(c => c.Session).Should().BeEquivalentTo(["aaaa1111", "bbbb2222"]);
        Hits("revoke").Should().HaveCount(2, "two conversations, each in the index once");
    }

    [Fact]
    public async Task should_delete_what_a_machine_sent_and_nothing_of_another()
    {
        await _ingestor.IngestAsync(Batch("old-laptop", "aaaa1111", 0, Talk), Token);
        await _ingestor.IngestAsync(Batch("server-1", "cccc3333", 0, Talk), Token);

        var count = await _relocator.EraseAsync("old-laptop", Token);

        count.Should().Be(1);
        Directory.Exists(NotesOf("old-laptop")).Should().BeFalse();
        Directory.Exists(RawOf("old-laptop")).Should().BeFalse();
        _store.OfMachine("old-laptop").Should().BeEmpty();
        Hits("revoke").Should().ContainSingle().Which.Should().StartWith("server-1/");
        Directory.EnumerateFiles(NotesOf("server-1"), "*.md", SearchOption.AllDirectories).Should().ContainSingle();
    }

    [Fact]
    public async Task should_take_over_a_conversation_held_under_a_name_that_is_not_a_machine_and_go_on_from_where_it_was()
    {
        await _ingestor.IngestAsync(Batch("gamng-pc", "aaaa1111", 0, Talk), Token);
        Enrol("gaming-pc");

        var result = await _ingestor.IngestAsync(Batch("gaming-pc", "aaaa1111", 0, [.. Talk, Line("user", "And the third one?", "2026-11-04T14:05:00Z")]), Token);

        result.Should().Be(new IngestResult(3, 1), "the two lines it had are not taken again");
        _store.OfMachine("gamng-pc").Should().BeEmpty();
        _store.OfMachine("gaming-pc").Should().ContainSingle().Which.Lines.Should().Be(3);
        Directory.Exists(NotesOf("gamng-pc")).Should().BeFalse();
        Hits("third").Should().ContainSingle().Which.Should().StartWith("gaming-pc/");
    }

    [Fact]
    public async Task should_keep_a_conversation_held_by_another_machine_that_is_enrolled()
    {
        Enrol("server-1");
        await _ingestor.IngestAsync(Batch("server-1", "aaaa1111", 0, Talk), Token);
        Enrol("gaming-pc");

        await _ingestor.IngestAsync(Batch("gaming-pc", "aaaa1111", 0, Talk), Token);

        _store.OfMachine("server-1").Should().ContainSingle("a machine that exists keeps what it sent");
        _store.OfMachine("gaming-pc").Should().ContainSingle();
    }

    [Fact]
    public async Task should_take_over_the_longest_copy_and_drop_the_other_copies_held_under_names_that_are_not_machines()
    {
        await _ingestor.IngestAsync(Batch("gamng-pc", "aaaa1111", 0, Talk[0]), Token);
        await _ingestor.IngestAsync(Batch("gaming-pc", "aaaa1111", 0, Talk), Token);
        Enrol("gaming-pc-3");

        var result = await _ingestor.IngestAsync(Batch("gaming-pc-3", "aaaa1111", 0, [.. Talk, Line("user", "And the third one?", "2026-11-04T14:05:00Z")]), Token);

        result.Should().Be(new IngestResult(3, 1), "it went on from the longest copy, which had two lines");
        _store.OfSession("aaaa1111").Select(c => c.Machine).Should().Equal("gaming-pc-3");
        Directory.Exists(NotesOf("gamng-pc")).Should().BeFalse();
        Directory.Exists(NotesOf("gaming-pc")).Should().BeFalse();
        Directory.Exists(RawOf("gamng-pc")).Should().BeFalse();
        Hits("revoke").Should().ContainSingle().Which.Should().StartWith("gaming-pc-3/");
    }

    [Fact]
    public async Task should_keep_the_longer_copy_when_both_machines_hold_the_conversation_and_lose_no_line()
    {
        Enrol("gaming-pc-3");
        await _ingestor.IngestAsync(Batch("gaming-pc-3", "aaaa1111", 0, Talk[0]), Token);
        await _ingestor.IngestAsync(Batch("old-name", "aaaa1111", 0, [.. Talk, Line("user", "And the third one?", "2026-11-04T14:05:00Z")]), Token);

        var moved = await _relocator.MoveAllAsync("old-name", "gaming-pc-3", Token);

        moved.Should().Be(new Relocation(1, 0), "the copy of the source was longer, so it is the one that is filed");
        _store.OfSession("aaaa1111").Should().ContainSingle().Which.Should().Match<ConversationRecord>(c => c.Machine == "gaming-pc-3" && c.Lines == 3);
        Hits("third").Should().ContainSingle().Which.Should().StartWith("gaming-pc-3/");
    }

    [Fact]
    public async Task should_keep_the_copy_of_the_target_when_it_is_the_longer()
    {
        Enrol("gaming-pc-3");
        await _ingestor.IngestAsync(Batch("gaming-pc-3", "aaaa1111", 0, Talk), Token);
        await _ingestor.IngestAsync(Batch("old-name", "aaaa1111", 0, Talk[0]), Token);

        var moved = await _relocator.MoveAllAsync("old-name", "gaming-pc-3", Token);

        moved.Should().Be(new Relocation(0, 1));
        _store.OfSession("aaaa1111").Should().ContainSingle().Which.Lines.Should().Be(2);
    }

    [Fact]
    public async Task should_not_take_over_a_conversation_for_a_key_that_is_not_the_same_computer_or_does_not_say_which()
    {
        await _ingestor.IngestAsync(Batch("old-name", "aaaa1111", 0, Talk), Token);
        Enrol("pc");
        Enrol("other-pc");

        await _ingestor.IngestAsync(Anonymous("pc", "aaaa1111", 0, Talk), Token);
        await _ingestor.IngestAsync(Elsewhere("other-pc", "aaaa1111", 0, Talk), Token);

        _store.OfMachine("old-name").Should().ContainSingle("a key that merely knows the session id takes nothing over");
        _store.OfMachine("pc").Should().ContainSingle();
        _store.OfMachine("other-pc").Should().ContainSingle();
    }

    [Fact]
    public async Task should_not_take_over_a_conversation_that_was_collected_in_another_format()
    {
        await _ingestor.IngestAsync(Batch("old-name", "aaaa1111", 0, Talk), Token);
        Enrol("pc");

        var batch = Batch("pc", "aaaa1111", 0, Talk) with { Format = "other-format" };
        var act = () => _ingestor.IngestAsync(batch, Token);

        await act.Should().ThrowAsync<RejectedException>("the format is not one Mnemarr reads, and nothing was moved before it was refused");
        _store.OfMachine("old-name").Should().ContainSingle();
    }

    [Fact]
    public async Task should_count_nothing_when_a_name_that_has_no_conversation_is_deleted_and_file_equal_copies_once()
    {
        (await _relocator.EraseAsync("nobody", Token)).Should().Be(0);
        Enrol("pc");
        Enrol("other");
        await _ingestor.IngestAsync(Batch("pc", "aaaa1111", 0, Talk), Token);
        await _ingestor.IngestAsync(Batch("other", "aaaa1111", 0, Talk), Token);

        var moved = await _relocator.MoveAllAsync("other", "pc", Token);

        moved.Should().Be(new Relocation(0, 1), "equal copies: the target's stays");
        _store.OfSession("aaaa1111").Should().ContainSingle().Which.Machine.Should().Be("pc");
    }

    [Fact]
    public async Task should_finish_a_move_that_was_stopped_after_the_lines_were_moved_without_losing_the_note()
    {
        Enrol("pc");
        await _ingestor.IngestAsync(Batch("old-name", "aaaa1111", 0, Talk), Token);
        Directory.CreateDirectory(RawOf("pc"));
        Directory.Move(Path.Combine(RawOf("old-name"), "aaaa1111"), Path.Combine(RawOf("pc"), "aaaa1111"));

        var moved = await _relocator.MoveAllAsync("old-name", "pc", Token);

        moved.Should().Be(new Relocation(1, 0));
        var note = Directory.EnumerateFiles(NotesOf("pc"), "*.md", SearchOption.AllDirectories).Single();
        File.ReadAllText(note).Should().Contain("revoke the old key", "the note was written again from the lines, wherever they were");
        _store.OfMachine("old-name").Should().BeEmpty();
    }

    [Fact]
    public async Task should_stop_and_move_nothing_when_the_lines_are_kept_under_both_names()
    {
        Enrol("pc");
        await _ingestor.IngestAsync(Batch("old-name", "aaaa1111", 0, Talk), Token);
        Directory.CreateDirectory(Path.Combine(RawOf("pc"), "aaaa1111"));

        var act = () => _relocator.MoveAllAsync("old-name", "pc", Token);

        await act.Should().ThrowAsync<IOException>();
        _store.OfMachine("old-name").Should().ContainSingle("the row did not move to lines that are not its own");
        Directory.Exists(NotesOf("pc")).Should().BeFalse("nothing was written before it stopped");
        Directory.Exists(Path.Combine(RawOf("old-name"), "aaaa1111")).Should().BeTrue();
    }
}

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

    private static IngestBatch Batch(string machine, string session, long from, params string[] lines) => new(ConversationFormats.ClaudeCode, machine, "Mnemarr Core", session, from, lines);

    private static readonly string[] Talk = [Line("user", "How do I rotate the tokens?", "2026-11-04T14:03:00Z"), Line("assistant", "Use the admin page, then revoke the old key.", "2026-11-04T14:04:00Z")];

    private string NotesOf(string machine) => Path.Combine(_workspace.DataFolder, "conversations", machine);

    private string RawOf(string machine) => Path.Combine(_workspace.DataFolder, "raw", machine);

    private List<string> Hits(string words) => [.. _index.Search(new SearchRequest(words), SourceScope.All).Select(hit => hit.Path)];

    private void Enrol(string name) => _machines.Enrol(new Machine(name, MachineKind.Intermittent, Guid.NewGuid(), _clock.GetUtcNow(), null, null, 0, null, null));

    [Fact]
    public async Task should_file_what_a_machine_sent_under_its_new_name_the_note_the_lines_the_row_and_the_search()
    {
        await _ingestor.IngestAsync(Batch("qimper-gaming", "aaaa1111", 0, Talk), Token);

        var moved = await _relocator.MoveAllAsync("qimper-gaming", "quimper-gaming", Token);

        moved.Should().Be(new Relocation(1, 0));
        Directory.Exists(NotesOf("qimper-gaming")).Should().BeFalse("the folder of the old name is gone with its last note");
        Directory.Exists(RawOf("qimper-gaming")).Should().BeFalse();
        var note = Directory.EnumerateFiles(NotesOf("quimper-gaming"), "*.md", SearchOption.AllDirectories).Single();
        File.ReadAllText(note).Should().Contain("quimper-gaming").And.NotContain("qimper-gaming").And.Contain("revoke the old key");
        Hits("revoke").Should().ContainSingle().Which.Should().StartWith("quimper-gaming/");
        _store.OfMachine("qimper-gaming").Should().BeEmpty();
        _store.OfMachine("quimper-gaming").Should().ContainSingle();
    }

    [Fact]
    public async Task should_not_make_a_conversation_twice_when_the_other_machine_holds_it_already()
    {
        Enrol("qimper-gaming");
        Enrol("quimper-gaming");
        await _ingestor.IngestAsync(Batch("qimper-gaming", "aaaa1111", 0, Talk), Token);
        await _ingestor.IngestAsync(Batch("quimper-gaming", "aaaa1111", 0, Talk), Token);
        await _ingestor.IngestAsync(Batch("qimper-gaming", "bbbb2222", 0, Talk), Token);

        var moved = await _relocator.MoveAllAsync("qimper-gaming", "quimper-gaming", Token);

        moved.Should().Be(new Relocation(1, 1), "the one both had is kept once, the other is filed under the new name");
        _store.OfMachine("quimper-gaming").Select(c => c.Session).Should().BeEquivalentTo(["aaaa1111", "bbbb2222"]);
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
        await _ingestor.IngestAsync(Batch("qimper-gaming", "aaaa1111", 0, Talk), Token);
        Enrol("quimper-gaming");

        var result = await _ingestor.IngestAsync(Batch("quimper-gaming", "aaaa1111", 0, [.. Talk, Line("user", "And the third one?", "2026-11-04T14:05:00Z")]), Token);

        result.Should().Be(new IngestResult(3, 1), "the two lines it had are not taken again");
        _store.OfMachine("qimper-gaming").Should().BeEmpty();
        _store.OfMachine("quimper-gaming").Should().ContainSingle().Which.Lines.Should().Be(3);
        Directory.Exists(NotesOf("qimper-gaming")).Should().BeFalse();
        Hits("third").Should().ContainSingle().Which.Should().StartWith("quimper-gaming/");
    }

    [Fact]
    public async Task should_keep_a_conversation_held_by_another_machine_that_is_enrolled()
    {
        Enrol("server-1");
        await _ingestor.IngestAsync(Batch("server-1", "aaaa1111", 0, Talk), Token);
        Enrol("quimper-gaming");

        await _ingestor.IngestAsync(Batch("quimper-gaming", "aaaa1111", 0, Talk), Token);

        _store.OfMachine("server-1").Should().ContainSingle("a machine that exists keeps what it sent");
        _store.OfMachine("quimper-gaming").Should().ContainSingle();
    }
}

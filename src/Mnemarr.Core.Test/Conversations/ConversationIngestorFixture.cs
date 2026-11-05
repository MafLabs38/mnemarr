using Microsoft.Extensions.Time.Testing;
using Mnemarr.Core.Accounts;
using Mnemarr.Core.Audit;
using Mnemarr.Core.Conversations;
using Mnemarr.Core.Indexing;
using Mnemarr.Core.Sources;

namespace Mnemarr.Core.Test.Conversations;

public sealed class ConversationIngestorFixture : IDisposable
{
    private readonly TestWorkspace _workspace = new();
    private readonly FakeTimeProvider _clock = new(new DateTimeOffset(2026, 11, 4, 16, 0, 0, TimeSpan.Zero));
    private readonly SqliteNoteIndex _index;
    private readonly SqliteConversationStore _store;
    private readonly SourceCatalog _catalog;
    private readonly ConversationIngestor _ingestor;

    public ConversationIngestorFixture()
    {
        var paths = _workspace.Paths();
        var locks = new SourceLocks();
        _index = new SqliteNoteIndex(_workspace.Database);
        _store = new SqliteConversationStore(_workspace.Database);
        _catalog = new SourceCatalog(new SqliteSourceStore(_workspace.Database), new SqliteAuditLog(_workspace.Database, _clock), paths, _clock);
        var indexer = new SourceIndexer(_index, paths, new ScanOutcomes(), _clock, locks);
        _ingestor = new ConversationIngestor(_catalog, locks, indexer, new RawConversations(_workspace.Options), _store, _clock);
    }

    public void Dispose() => _workspace.Dispose();

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    private static string Line(string type, string text, string at) =>
        System.Text.Json.JsonSerializer.Serialize(new { type, timestamp = at, message = new { role = type, content = text } });

    private static IngestBatch Batch(long from, params string[] lines) => new(ConversationFormats.ClaudeCode, "wacken", "Mnemarr Core", "abcdef12-3456", from, lines);

    private string NoteFile => Path.Combine(_workspace.DataFolder, "conversations", "mnemarr-core", "2026-11-04-abcdef12.md");

    private List<string> Hits(string words) => [.. _index.Search(new SearchRequest(words), SourceScope.All).Select(hit => hit.Path)];

    [Fact]
    public async Task should_write_the_conversation_as_a_note_in_a_source_of_its_own_and_make_it_searchable_at_once()
    {
        var result = await _ingestor.IngestAsync(Batch(0, Line("user", "How do I rotate the tokens?", "2026-11-04T14:03:00Z"), Line("assistant", "Use the admin page, then revoke the old key.", "2026-11-04T14:04:00Z")), Token);

        result.Should().Be(new IngestResult(2, 2));
        File.ReadAllText(NoteFile).Should().Contain("# How do I rotate the tokens?").And.Contain("revoke the old key");
        Hits("revoke").Should().Equal("mnemarr-core/2026-11-04-abcdef12.md");
        var source = _catalog.List(SourceScope.All).Single();
        source.Should().Match<Source>(s => s.Kind == SourceKind.Conversations && s.Managed && s.Mode == SourceMode.ReadOnly);
    }

    [Fact]
    public async Task should_take_a_batch_that_is_sent_again_once_and_only_what_is_new_of_one_that_overlaps()
    {
        var first = new[] { Line("user", "one", "2026-11-04T14:03:00Z"), Line("assistant", "two", "2026-11-04T14:04:00Z") };
        await _ingestor.IngestAsync(Batch(0, first), Token);

        var again = await _ingestor.IngestAsync(Batch(0, first), Token);
        var overlap = await _ingestor.IngestAsync(Batch(1, first[1], Line("user", "three", "2026-11-04T14:05:00Z")), Token);

        again.Should().Be(new IngestResult(2, 0));
        overlap.Should().Be(new IngestResult(3, 1));
        File.ReadAllText(NoteFile).Split("## ").Should().HaveCount(4, "the header and three turns, none twice");
    }

    [Fact]
    public async Task should_say_where_to_resume_from_when_a_batch_starts_after_what_it_has()
    {
        await _ingestor.IngestAsync(Batch(0, Line("user", "one", "2026-11-04T14:03:00Z")), Token);

        var hole = await _ingestor.IngestAsync(Batch(5, Line("user", "six", "2026-11-04T14:09:00Z")), Token);

        hole.Should().Be(new IngestResult(1, 0));
        File.ReadAllText(NoteFile).Should().NotContain("six");
    }

    [Fact]
    public async Task should_mask_a_secret_again_before_it_stores_or_indexes_anything()
    {
        var secret = "ghp_" + new string('a', 36);

        await _ingestor.IngestAsync(Batch(0, Line("user", $"my token is {secret} ok", "2026-11-04T14:03:00Z")), Token);

        File.ReadAllText(NoteFile).Should().Contain("[masked:github-token]").And.NotContain(secret);
        Hits("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa").Should().BeEmpty();
        Directory.EnumerateFiles(Path.Combine(_workspace.DataFolder, "raw"), "*", SearchOption.AllDirectories).Select(file => RawText(file)).Should().OnlyContain(text => !text.Contains(secret));
    }

    private static string RawText(string file)
    {
        using var stream = new System.IO.Compression.GZipStream(File.OpenRead(file), System.IO.Compression.CompressionMode.Decompress);
        return new StreamReader(stream).ReadToEnd();
    }

    [Fact]
    public async Task should_keep_the_raw_lines_apart_from_the_notes_so_that_the_notes_can_be_written_again()
    {
        await _ingestor.IngestAsync(Batch(0, Line("user", "one", "2026-11-04T14:03:00Z")), Token);
        await _ingestor.IngestAsync(Batch(1, Line("assistant", "two", "2026-11-04T14:04:00Z")), Token);

        var raw = Directory.EnumerateFiles(Path.Combine(_workspace.DataFolder, "raw", "wacken", "abcdef12-3456")).Select(Path.GetFileName).Order().ToList();

        raw.Should().Equal("000000000000.jsonl.gz", "000000000001.jsonl.gz");
    }

    [Fact]
    public async Task should_not_write_a_note_for_a_conversation_that_has_nothing_said_in_it()
    {
        var result = await _ingestor.IngestAsync(Batch(0, "{\"type\":\"summary\",\"summary\":\"x\"}"), Token);

        result.Should().Be(new IngestResult(1, 1));
        File.Exists(NoteFile).Should().BeFalse();
        _store.Find("wacken", "abcdef12-3456")!.Lines.Should().Be(1);
    }

    [Theory]
    [InlineData("../escape", "abcdef12")]
    [InlineData("wacken", ".hidden")]
    [InlineData("wa cken", "abcdef12")]
    [InlineData("wacken", "a/b")]
    public async Task should_refuse_a_machine_or_a_session_that_is_not_a_plain_name(string machine, string session)
    {
        var refusal = await Assert.ThrowsAsync<RejectedException>(() => _ingestor.IngestAsync(new IngestBatch(ConversationFormats.ClaudeCode, machine, "app", session, 0, [Line("user", "x", "2026-11-04T14:03:00Z")]), Token));

        refusal.Problem.Should().Be(Problem.Invalid);
    }

    [Fact]
    public async Task should_refuse_a_format_it_does_not_read_an_empty_batch_and_a_negative_position()
    {
        await Assert.ThrowsAsync<RejectedException>(() => _ingestor.IngestAsync(new IngestBatch("other", "wacken", "app", "s1", 0, ["x"]), Token));
        await Assert.ThrowsAsync<RejectedException>(() => _ingestor.IngestAsync(Batch(0), Token));
        await Assert.ThrowsAsync<RejectedException>(() => _ingestor.IngestAsync(Batch(-1, "x"), Token));
    }

    [Theory]
    [InlineData("My App", "my-app")]
    [InlineData("../..", "unknown")]
    [InlineData("  ", "unknown")]
    [InlineData("API v2 (beta)", "api-v2-beta")]
    public void should_name_the_folder_of_an_application_with_plain_characters_only(string application, string folder) =>
        ConversationIngestor.Slug(application).Should().Be(folder);

    [Fact]
    public async Task should_list_the_machines_that_sent_something_and_when()
    {
        await _ingestor.IngestAsync(Batch(0, Line("user", "one", "2026-11-04T14:03:00Z"), Line("assistant", "two", "2026-11-04T14:04:00Z")), Token);

        _store.Machines().Should().ContainSingle().Which.Should().Be(new MachineActivity("wacken", 1, 2, _clock.GetUtcNow()));
    }

    [Fact]
    public void should_not_let_anyone_declare_the_source_of_conversations_by_hand_nor_change_or_forget_it()
    {
        var declare = () => _catalog.Add("mine", SourceKind.Conversations, SourceMode.ReadOnly, _workspace.Folder("x"), SourceScope.All, "alice");
        declare.Should().Throw<RejectedException>();

        var source = _catalog.EnsureConversations();
        _catalog.EnsureConversations().Id.Should().Be(source.Id, "there is one");
        var forget = () => _catalog.Remove(source.Id, SourceScope.All, "alice");
        forget.Should().Throw<RejectedException>();
    }
}

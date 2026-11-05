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
    private readonly SourceLocks _locks = new();
    private readonly FakeTimeProvider _clock = new(new DateTimeOffset(2026, 11, 4, 16, 0, 0, TimeSpan.Zero));
    private readonly SqliteNoteIndex _index;
    private readonly SqliteConversationStore _store;
    private readonly SourceCatalog _catalog;
    private readonly ConversationIngestor _ingestor;

    public ConversationIngestorFixture()
    {
        var paths = _workspace.Paths();
        _index = new SqliteNoteIndex(_workspace.Database);
        _store = new SqliteConversationStore(_workspace.Database);
        _catalog = new SourceCatalog(new SqliteSourceStore(_workspace.Database), new SqliteAuditLog(_workspace.Database, _clock), paths, _clock);
        var indexer = new SourceIndexer(_index, paths, new ScanOutcomes(), _clock, _locks);
        _ingestor = new ConversationIngestor(_catalog, _locks, indexer, new RawConversations(_workspace.Options), _store, _clock);
    }

    public void Dispose() => _workspace.Dispose();

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    private static string Line(string type, string text, string at) =>
        System.Text.Json.JsonSerializer.Serialize(new { type, timestamp = at, message = new { role = type, content = text } });

    private static IngestBatch Batch(long from, params string[] lines) => new(ConversationFormats.ClaudeCode, "wacken", "Mnemarr Core", "abcdef12-3456", from, lines);

    private string NoteFile => Path.Combine(_workspace.DataFolder, "conversations", "wacken", "mnemarr-core", "2026-11-04-abcdef12-3456.md");

    private List<string> Hits(string words) => [.. _index.Search(new SearchRequest(words), SourceScope.All).Select(hit => hit.Path)];

    [Fact]
    public async Task should_write_the_conversation_as_a_note_in_a_source_of_its_own_and_make_it_searchable_at_once()
    {
        var result = await _ingestor.IngestAsync(Batch(0, Line("user", "How do I rotate the tokens?", "2026-11-04T14:03:00Z"), Line("assistant", "Use the admin page, then revoke the old key.", "2026-11-04T14:04:00Z")), Token);

        result.Should().Be(new IngestResult(2, 2));
        File.ReadAllText(NoteFile).Should().Contain("# How do I rotate the tokens?").And.Contain("revoke the old key");
        Hits("revoke").Should().Equal("wacken/mnemarr-core/2026-11-04-abcdef12-3456.md");
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
        Directory.EnumerateFiles(Path.Combine(_workspace.DataFolder, "raw"), "*.jsonl.gz", SearchOption.AllDirectories).Select(file => RawText(file)).Should().OnlyContain(text => !text.Contains(secret));
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

        var raw = Directory.EnumerateFiles(Path.Combine(_workspace.DataFolder, "raw", "wacken", "abcdef12-3456"), "*.jsonl.gz").Select(Path.GetFileName).Order().ToList();

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

    private List<string> Turns() => [.. File.ReadAllText(NoteFile).Split("\n## ").Skip(1).Select(section => section.Split('\n')[0])];

    [Fact]
    public async Task should_add_new_turns_to_the_end_of_the_note_and_give_the_same_note_as_writing_it_whole()
    {
        await _ingestor.IngestAsync(Batch(0, Line("user", "How do I rotate the tokens?", "2026-11-04T14:03:00Z")), Token);
        await _ingestor.IngestAsync(Batch(1, Line("assistant", "Use the admin page.", "2026-11-04T14:04:00Z"), Line("user", "And then?", "2026-11-04T14:05:00Z")), Token);

        var whole = ConversationMarkdown.Render(
            ConversationFormats.ClaudeCode, "wacken", "mnemarr-core", "abcdef12-3456", new DateTimeOffset(2026, 11, 4, 14, 3, 0, TimeSpan.Zero),
            ConversationFormats.Turns(ConversationFormats.ClaudeCode, [Line("user", "How do I rotate the tokens?", "2026-11-04T14:03:00Z"), Line("assistant", "Use the admin page.", "2026-11-04T14:04:00Z"), Line("user", "And then?", "2026-11-04T14:05:00Z")]));

        File.ReadAllText(NoteFile).Should().Be(whole);
        Hits("admin").Should().Equal("wacken/mnemarr-core/2026-11-04-abcdef12-3456.md");
    }

    [Fact]
    public async Task should_cut_off_what_a_failed_attempt_left_at_the_end_of_the_note_before_it_adds_to_it()
    {
        await _ingestor.IngestAsync(Batch(0, Line("user", "first thing", "2026-11-04T14:03:00Z")), Token);
        File.AppendAllText(NoteFile, "## User\n\nhalf of a turn that was never recorded");

        await _ingestor.IngestAsync(Batch(1, Line("assistant", "second thing", "2026-11-04T14:04:00Z")), Token);

        File.ReadAllText(NoteFile).Should().NotContain("half of a turn").And.Contain("second thing");
        Turns().Should().HaveCount(2);
    }

    [Fact]
    public async Task should_write_the_note_again_whole_from_the_raw_lines_when_it_is_not_what_was_recorded()
    {
        await _ingestor.IngestAsync(Batch(0, Line("user", "first thing", "2026-11-04T14:03:00Z")), Token);
        File.Delete(NoteFile);

        await _ingestor.IngestAsync(Batch(1, Line("assistant", "second thing", "2026-11-04T14:04:00Z")), Token);

        File.ReadAllText(NoteFile).Should().Contain("first thing").And.Contain("second thing");
    }

    [Fact]
    public async Task should_keep_two_sessions_that_start_alike_in_two_notes()
    {
        await _ingestor.IngestAsync(new IngestBatch(ConversationFormats.ClaudeCode, "wacken", "app", "session-1", 0, [Line("user", "alpha story here", "2026-11-04T14:03:00Z")]), Token);
        await _ingestor.IngestAsync(new IngestBatch(ConversationFormats.ClaudeCode, "wacken", "app", "session-2", 0, [Line("user", "beta story here", "2026-11-04T14:03:00Z")]), Token);

        Hits("alpha").Should().Equal("wacken/app/2026-11-04-session-1.md");
        Hits("beta").Should().Equal("wacken/app/2026-11-04-session-2.md");
    }

    [Fact]
    public async Task should_keep_the_application_a_conversation_was_first_given()
    {
        await _ingestor.IngestAsync(Batch(0, Line("user", "one thing", "2026-11-04T14:03:00Z")), Token);
        await _ingestor.IngestAsync(new IngestBatch(ConversationFormats.ClaudeCode, "wacken", "Something Else", "abcdef12-3456", 1, [Line("assistant", "two things", "2026-11-04T14:04:00Z")]), Token);

        _store.Find("wacken", "abcdef12-3456")!.Should().Match<ConversationRecord>(record => record.Application == "mnemarr-core" && record.NotePath.StartsWith("wacken/mnemarr-core/", StringComparison.Ordinal));
        File.ReadAllText(NoteFile).Should().Contain("two things");
    }

    [Fact]
    public async Task should_refuse_a_batch_with_too_many_lines_or_a_line_that_is_too_long()
    {
        var tooMany = Enumerable.Repeat("{}", ConversationIngestor.MaxLines + 1).ToArray();
        var tooLong = new string('x', ConversationIngestor.MaxLineChars + 1);

        await Assert.ThrowsAsync<RejectedException>(() => _ingestor.IngestAsync(Batch(0, tooMany), Token));
        await Assert.ThrowsAsync<RejectedException>(() => _ingestor.IngestAsync(Batch(0, tooLong), Token));
    }

    [Fact]
    public async Task should_wait_for_the_lock_of_the_conversations_and_then_take_the_batch()
    {
        var source = _catalog.EnsureConversations();
        var gate = _locks.Of(source.Id);
        await gate.WaitAsync(Token);
        Task<IngestResult> waiting;
        try
        {
            waiting = _ingestor.IngestAsync(Batch(0, Line("user", "one", "2026-11-04T14:03:00Z")), Token);
            await Task.Delay(200, Token);

            waiting.IsCompleted.Should().BeFalse("the lock is held elsewhere");
            File.Exists(NoteFile).Should().BeFalse("nothing is written while the lock is held elsewhere");
        }
        finally
        {
            gate.Release();
        }

        (await waiting).Should().Be(new IngestResult(1, 1));
        File.Exists(NoteFile).Should().BeTrue();
    }

    [Fact]
    public async Task should_stop_waiting_for_the_lock_when_the_request_is_cancelled()
    {
        var source = _catalog.EnsureConversations();
        var gate = _locks.Of(source.Id);
        await gate.WaitAsync(Token);
        try
        {
            using var cancel = CancellationTokenSource.CreateLinkedTokenSource(Token);
            var waiting = _ingestor.IngestAsync(Batch(0, Line("user", "one", "2026-11-04T14:03:00Z")), cancel.Token);
            await cancel.CancelAsync();

            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => waiting);
            File.Exists(NoteFile).Should().BeFalse();
        }
        finally
        {
            gate.Release();
        }
    }

    [Fact]
    public void should_not_let_a_folder_that_is_or_holds_what_mnemarr_keeps_for_itself_be_declared_as_a_source()
    {
        var paths = _workspace.Paths(_workspace.DataFolder);
        var catalog = new SourceCatalog(new SqliteSourceStore(_workspace.Database), new SqliteAuditLog(_workspace.Database, _clock), paths, _clock);
        Directory.CreateDirectory(paths.ConversationsFolder);
        Directory.CreateDirectory(paths.RawFolder);

        foreach (var folder in new[] { paths.ConversationsFolder, Path.Combine(paths.ConversationsFolder, "wacken"), paths.RawFolder, _workspace.DataFolder })
        {
            var declare = () => catalog.Add("mine-" + Guid.NewGuid().ToString("N")[..6], SourceKind.Knowledge, SourceMode.ReadOnly, folder, SourceScope.All, "alice");
            declare.Should().Throw<RejectedException>(folder);
        }
    }

    [Fact]
    public void should_keep_the_name_of_the_conversations_for_itself_and_hide_them_from_a_scope_that_forgets()
    {
        var declare = () => _catalog.Add("Conversations", SourceKind.Knowledge, SourceMode.ReadOnly, _workspace.Folder("x"), SourceScope.All, "alice");
        declare.Should().Throw<RejectedException>();

        new SourceScope([Guid.NewGuid()]).ConversationsHidden.Should().BeTrue("a scope that does not say otherwise hides the conversations");
        SourceScope.All.ConversationsHidden.Should().BeFalse("Mnemarr itself sees everything");
    }

    [Fact]
    public async Task should_take_the_same_batch_sent_twice_at_once_only_one_time()
    {
        var batch = Batch(0, Line("user", "one thing", "2026-11-04T14:03:00Z"), Line("assistant", "two things", "2026-11-04T14:04:00Z"));
        var gate = _locks.Of(_catalog.EnsureConversations().Id);
        await gate.WaitAsync(Token);

        // Both calls are queued on the lock before either can run: the protection is tried, not left to the order of two tasks.
        var first = _ingestor.IngestAsync(batch, Token);
        var second = _ingestor.IngestAsync(batch, Token);
        gate.Release();
        var results = await Task.WhenAll(first, second);

        results.Sum(result => result.Accepted).Should().Be(2);
        results.Select(result => result.Next).Should().OnlyContain(next => next == 2);
        File.ReadAllText(NoteFile).Split("\n## ").Should().HaveCount(3, "the header and two turns, none twice");
    }

    [Fact]
    public async Task should_say_in_the_raw_folder_which_format_and_application_the_lines_are()
    {
        await _ingestor.IngestAsync(Batch(0, Line("user", "one", "2026-11-04T14:03:00Z")), Token);
        await _ingestor.IngestAsync(Batch(1, Line("assistant", "two", "2026-11-04T14:04:00Z")), Token);

        var info = new RawConversations(_workspace.Options).ReadInfo("wacken", "abcdef12-3456");

        info.Should().Be(new RawInfo(ConversationFormats.ClaudeCode, "mnemarr-core", new DateTimeOffset(2026, 11, 4, 14, 3, 0, TimeSpan.Zero)));
    }

    [Fact]
    public async Task should_refuse_lines_of_another_format_for_a_conversation_it_already_holds()
    {
        // A conversation held under another format (a second assistant will make this real): its lines are not read by the wrong parser.
        var at = new DateTimeOffset(2026, 11, 4, 14, 3, 0, TimeSpan.Zero);
        _store.Save(new ConversationRecord("wacken", "abcdef12-3456", "mnemarr-core", "another-format", 1, at, at, "wacken/mnemarr-core/2026-11-04-abcdef12-3456.md"));

        var refused = await Assert.ThrowsAsync<RejectedException>(() => _ingestor.IngestAsync(Batch(1, Line("user", "two", "2026-11-04T14:04:00Z")), Token));

        refused.Problem.Should().Be(Problem.Conflict);
    }

    [Fact]
    public async Task should_give_the_source_another_name_when_an_operator_already_used_the_one_it_keeps()
    {
        new SqliteSourceStore(_workspace.Database).Add(new Source(Guid.NewGuid(), "conversations", SourceKind.Knowledge, SourceMode.ReadOnly, _workspace.Folder("mine"), _clock.GetUtcNow()));

        var result = await _ingestor.IngestAsync(Batch(0, Line("user", "one thing here", "2026-11-04T14:03:00Z")), Token);

        result.Accepted.Should().Be(1);
        _catalog.List(SourceScope.All).Should().Contain(source => source.Kind == SourceKind.Conversations && source.Name == "conversations-collected");
    }
}

using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using Mnemarr.Core.Accounts;
using Mnemarr.Core.Audit;
using Mnemarr.Core.Conversations;
using Mnemarr.Core.Indexing;
using Mnemarr.Core.Sources;
using Mnemarr.Core.Titles;

namespace Mnemarr.Core.Test.Titles;

/// <summary>A model that gives what a test says, and remembers what it was asked.</summary>
internal sealed class FakeTitleWriter : ITitleWriter
{
    public Func<string, GeneratedTitle?> Answer { get; set; } = _ => new GeneratedTitle("A model's title", "A model's summary.");

    public List<string> Asked { get; } = [];

    public Task<GeneratedTitle?> WriteAsync(string transcript, CancellationToken cancellationToken)
    {
        Asked.Add(transcript);
        return Task.FromResult(Answer(transcript));
    }
}

/// <summary>What gives a conversation its title (ADR 0028), against a real database, real files and a real index.</summary>
public sealed class ConversationTitlesFixture : IDisposable
{
    private readonly TestWorkspace _workspace = new();
    private readonly FakeTimeProvider _clock = new(new DateTimeOffset(2026, 11, 4, 16, 0, 0, TimeSpan.Zero));
    private readonly SqliteNoteIndex _index;
    private readonly SqliteConversationStore _store;
    private readonly ConversationIngestor _ingestor;
    private readonly ConversationTitles _titles;
    private readonly FakeTitleWriter _writer = new();
    private readonly TitleWriting _writing;
    private readonly TitleOptions _options = new() { Enabled = true, ApiKeyFile = "/key", IdleMinutes = 10, PerPass = 5, MaxTries = 3 };

    public ConversationTitlesFixture()
    {
        var paths = _workspace.Paths();
        var locks = new SourceLocks();
        _index = new SqliteNoteIndex(_workspace.Database);
        _store = new SqliteConversationStore(_workspace.Database);
        var catalog = new SourceCatalog(new SqliteSourceStore(_workspace.Database), new SqliteAuditLog(_workspace.Database, _clock), paths, _clock);
        var indexer = new SourceIndexer(_index, paths, new ScanOutcomes(), _clock, locks);
        var raw = new RawConversations(_workspace.Options);
        _ingestor = new ConversationIngestor(catalog, locks, indexer, raw, _store, _clock);
        _titles = new ConversationTitles(_store, raw, catalog, locks, indexer);
        _writing = new TitleWriting(_titles, _store, raw, _writer, Options.Create(_options), _clock);
    }

    public void Dispose() => _workspace.Dispose();

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    private static string Line(string type, string text, string at) =>
        System.Text.Json.JsonSerializer.Serialize(new { type, timestamp = at, message = new { role = type, content = text } });

    private async Task IngestAsync(string session, long from, params string[] lines) =>
        await _ingestor.IngestAsync(new IngestBatch(ConversationFormats.ClaudeCode, "pc", "Mnemarr", session, from, lines), Token);

    private string NoteOf(string session) => File.ReadAllText(Path.Combine(_workspace.DataFolder, "conversations", _store.Find("pc", session)!.NotePath));

    private async Task TalkAsync(string session = "s1") => await IngestAsync(session, 0, Line("user", "How do I rotate the tokens of the service accounts?", "2026-11-04T14:03:00Z"), Line("assistant", "Create a new key, then revoke the old one.", "2026-11-04T14:04:00Z"));

    [Fact]
    public async Task should_start_a_conversation_with_the_first_sentence_as_its_title_and_remember_it()
    {
        await TalkAsync();

        var record = _store.Find("pc", "s1")!;

        record.Title.Should().Be("How do I rotate the tokens of the service accounts?");
        record.TitleSource.Should().Be(TitleSources.FirstSentence);
        record.Summary.Should().BeNull();
        NoteOf("s1").Should().Contain("# How do I rotate the tokens of the service accounts?");
    }

    [Fact]
    public async Task should_write_the_note_again_with_the_title_a_person_typed_and_find_it_by_that_title_at_once()
    {
        await TalkAsync();

        var changed = await _titles.SetAsync("pc", "s1", "Rotation of the keys", TitleSources.Person, null, Token);

        changed!.TitleSource.Should().Be(TitleSources.Person);
        NoteOf("s1").Should().StartWith("---\n").And.Contain("\n# Rotation of the keys\n").And.Contain("Create a new key, then revoke the old one.");
        NoteOf("s1").Should().NotContain("# How do I rotate");
        _index.Search(new SearchRequest("Rotation"), SourceScope.All).Select(hit => hit.Path).Should().Contain(_store.Find("pc", "s1")!.NotePath);
        _store.Find("pc", "s1")!.NoteBytes.Should().Be(new FileInfo(Path.Combine(_workspace.DataFolder, "conversations", _store.Find("pc", "s1")!.NotePath)).Length);
    }

    [Fact]
    public async Task should_keep_the_title_when_more_is_said_whether_the_turns_are_added_or_the_note_is_written_whole_again()
    {
        await TalkAsync();
        await _titles.SetAsync("pc", "s1", "A title that stays", TitleSources.Person, null, Token);

        await IngestAsync("s1", 2, Line("user", "And what about the audit log?", "2026-11-04T14:05:00Z"));

        NoteOf("s1").Should().Contain("# A title that stays").And.Contain("And what about the audit log?");
        // The note is not what was recorded (somebody edited the file): it is written whole again, with the title the conversation has.
        File.AppendAllText(Path.Combine(_workspace.DataFolder, "conversations", _store.Find("pc", "s1")!.NotePath), "\nsomething added by hand\n");
        File.WriteAllText(Path.Combine(_workspace.DataFolder, "conversations", _store.Find("pc", "s1")!.NotePath), "damaged");
        await IngestAsync("s1", 3, Line("assistant", "The audit log keeps every change.", "2026-11-04T14:06:00Z"));

        NoteOf("s1").Should().Contain("# A title that stays").And.Contain("The audit log keeps every change.").And.NotContain("# How do I rotate");
    }

    [Fact]
    public async Task should_show_the_summary_a_model_wrote_under_the_title_as_a_quote()
    {
        await TalkAsync();

        await _titles.SetAsync("pc", "s1", "Rotating service keys", TitleSources.Summary, "Asked how to rotate keys. Create one and revoke the old.", Token);

        NoteOf("s1").Should().Contain("# Rotating service keys\n\n> Asked how to rotate keys. Create one and revoke the old.\n\n## User");
        _store.Find("pc", "s1")!.Summary.Should().Be("Asked how to rotate keys. Create one and revoke the old.");
    }

    [Fact]
    public async Task should_never_let_a_model_replace_the_title_of_a_person_but_let_a_person_replace_a_model_and_keep_its_summary()
    {
        await TalkAsync();
        await _titles.SetAsync("pc", "s1", "Mine", TitleSources.Person, null, Token);

        var refused = await _titles.SetAsync("pc", "s1", "A model's", TitleSources.Summary, "S", Token);

        refused.Should().BeNull();
        _store.Find("pc", "s1")!.Title.Should().Be("Mine");

        await TalkAsync("s2");
        await _titles.SetAsync("pc", "s2", "From the model", TitleSources.Summary, "What it was about.", Token);
        await _titles.SetAsync("pc", "s2", "My own words", TitleSources.Person, null, Token);

        var record = _store.Find("pc", "s2")!;
        (record.Title, record.TitleSource, record.Summary).Should().Be(("My own words", TitleSources.Person, "What it was about."));
        NoteOf("s2").Should().Contain("# My own words\n\n> What it was about.");
    }

    [Fact]
    public async Task should_refuse_a_title_with_nothing_in_it_a_source_that_is_not_one_and_a_conversation_that_does_not_exist()
    {
        await TalkAsync();

        (await Assert.ThrowsAsync<RejectedException>(() => _titles.SetAsync("pc", "s1", "[[]]", TitleSources.Person, null, Token))).Problem.Should().Be(Problem.Invalid);
        (await Assert.ThrowsAsync<RejectedException>(() => _titles.SetAsync("pc", "s1", "T", TitleSources.FirstSentence, null, Token))).Problem.Should().Be(Problem.Invalid);
        (await Assert.ThrowsAsync<RejectedException>(() => _titles.SetAsync("pc", "nope", "T", TitleSources.Person, null, Token))).Problem.Should().Be(Problem.NotFound);
    }

    [Fact]
    public async Task should_clean_what_a_title_says_so_that_it_cannot_make_a_link_or_a_heading_in_the_note()
    {
        await TalkAsync();

        await _titles.SetAsync("pc", "s1", "[[secret]] #tag\n## injected", TitleSources.Person, null, Token);

        NoteOf("s1").Should().Contain("\n# secret tag injected\n").And.NotContain("[[secret]]");
    }

    [Fact]
    public async Task should_not_ask_a_model_when_it_is_off_and_ask_for_the_conversations_left_alone_long_enough_when_it_is_on()
    {
        await TalkAsync("old");
        _clock.Advance(TimeSpan.FromMinutes(30));
        await TalkAsync("recent");

        _options.Enabled = false;
        (await _writing.RunOnceAsync(Token)).Should().Be(0);
        _writer.Asked.Should().BeEmpty();
        _options.Enabled = true;

        (await _writing.RunOnceAsync(Token)).Should().Be(1, "only the conversation that was left alone for ten minutes");

        _store.Find("pc", "old")!.TitleSource.Should().Be(TitleSources.Summary);
        _store.Find("pc", "old")!.Title.Should().Be("A model's title");
        _store.Find("pc", "recent")!.TitleSource.Should().Be(TitleSources.FirstSentence);
        _writer.Asked.Should().ContainSingle().Which.Should().Contain("[Person] How do I rotate");
        NoteOf("old").Should().Contain("# A model's title\n\n> A model's summary.");
        (await _writing.RunOnceAsync(Token)).Should().Be(0, "a conversation that has a title from a model is not asked again");
    }

    [Fact]
    public async Task should_stop_trying_after_a_few_failures_and_never_ask_for_a_conversation_of_one_line()
    {
        await TalkAsync("bad");
        await IngestAsync("short", 0, Line("user", "Only one thing", "2026-11-04T14:03:00Z"));
        _clock.Advance(TimeSpan.FromMinutes(30));
        _writer.Answer = _ => null;

        for (var i = 0; i < 5; i++)
        {
            await _writing.RunOnceAsync(Token);
        }

        _store.Find("pc", "bad")!.TitleTries.Should().Be(3, "three tries, then it is left with its first sentence");
        _writer.Asked.Should().HaveCount(3);
        _store.Find("pc", "short")!.TitleTries.Should().Be(3);
        _store.Find("pc", "bad")!.TitleSource.Should().Be(TitleSources.FirstSentence);
    }

    [Fact]
    public async Task should_count_a_try_when_the_model_cannot_be_asked_and_go_on_with_the_others()
    {
        await TalkAsync("a");
        await TalkAsync("b");
        _clock.Advance(TimeSpan.FromMinutes(30));
        var calls = 0;
        _writer.Answer = _ => ++calls == 1 ? throw new TitleWriterException("down") : new GeneratedTitle("Fine", "Fine.");

        (await _writing.RunOnceAsync(Token)).Should().Be(1);

        _store.TitleCounts().Should().Be(new TitleCounts(1, 1, 0));
    }

    [Fact]
    public async Task should_bound_a_pass_to_a_few_conversations_so_that_its_cost_is_bounded()
    {
        _options.PerPass = 2;
        for (var i = 0; i < 4; i++)
        {
            await TalkAsync($"s{i}");
        }

        _clock.Advance(TimeSpan.FromMinutes(30));

        (await _writing.RunOnceAsync(Token)).Should().Be(2);
        (await _writing.RunOnceAsync(Token)).Should().Be(2);
        (await _writing.RunOnceAsync(Token)).Should().Be(0);
    }

    [Fact]
    public async Task should_rewrite_one_title_on_request_even_when_it_was_a_persons_and_say_so_when_it_is_off()
    {
        await TalkAsync();
        await _titles.SetAsync("pc", "s1", "Mine", TitleSources.Person, null, Token);
        var path = _store.Find("pc", "s1")!.NotePath;

        var rewritten = await _writing.RewriteAsync(path, Token);

        (rewritten.Title, rewritten.TitleSource).Should().Be(("A model's title", TitleSources.Summary));
        NoteOf("s1").Should().Contain("# A model's title");

        _options.Enabled = false;
        (await Assert.ThrowsAsync<RejectedException>(() => _writing.RewriteAsync(path, Token))).Problem.Should().Be(Problem.Invalid);
        _options.Enabled = true;
        (await Assert.ThrowsAsync<RejectedException>(() => _writing.RewriteAsync("pc/none.md", Token))).Problem.Should().Be(Problem.NotFound);
    }

    [Fact]
    public async Task should_put_conversations_back_to_a_first_sentence_so_that_the_model_writes_them_again_and_keep_what_persons_typed()
    {
        await TalkAsync("model");
        await TalkAsync("person");
        await _titles.SetAsync("pc", "model", "By a model", TitleSources.Summary, "S", Token);
        await _titles.SetAsync("pc", "person", "By a person", TitleSources.Person, null, Token);

        _store.RequestAgain(includePerson: false).Should().Be(1);

        _store.TitleCounts().Should().Be(new TitleCounts(1, 0, 1));
        _store.RequestAgain(includePerson: true).Should().Be(1);
        _store.TitleCounts().Should().Be(new TitleCounts(2, 0, 0));
    }

    [Fact]
    public async Task should_give_the_first_sentence_as_the_title_of_a_conversation_taken_before_titles_were_kept()
    {
        await TalkAsync();
        using (var connection = _workspace.Database.Open())
        {
            using var command = connection.CreateCommand();
            command.CommandText = "UPDATE conversations SET title = NULL";
            command.ExecuteNonQuery();
        }

        _titles.TitleOf(_store.Find("pc", "s1")!).Should().Be("How do I rotate the tokens of the service accounts?");
    }
}

using Microsoft.Extensions.Time.Testing;
using Mnemarr.Core.Accounts;
using Mnemarr.Core.Indexing;
using Mnemarr.Core.Sources;
using Mnemarr.Core.Storage;

namespace Mnemarr.Core.Test.Indexing;

public sealed class SourceIndexerFixture : IDisposable
{
    private readonly TestWorkspace _workspace = new();
    private readonly FakeTimeProvider _clock = new(new DateTimeOffset(2026, 11, 3, 9, 0, 0, TimeSpan.Zero));
    private readonly SqliteNoteIndex _index;
    private readonly ScanOutcomes _outcomes = new();
    private readonly SqliteSourceStore _sources;
    private readonly SourceIndexer _subject;

    public SourceIndexerFixture()
    {
        _index = new SqliteNoteIndex(_workspace.Database);
        _sources = new SqliteSourceStore(_workspace.Database);
        _subject = new SourceIndexer(_index, _workspace.Paths(), _outcomes, _clock);
    }

    public void Dispose() => _workspace.Dispose();

    private Source Declare(string name, string folder, SourceKind kind = SourceKind.Knowledge)
    {
        var source = new Source(Guid.NewGuid(), name, kind, SourceMode.ReadWrite, folder, _clock.GetUtcNow());
        _sources.Add(source);
        return source;
    }

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    [Fact]
    public async Task should_index_every_note_of_a_source_and_find_them_by_their_words()
    {
        var folder = _workspace.Folder("cours");
        TestWorkspace.Write(folder, "02 - DevOps/docker.md", "# Docker\n## Images\nUne image est faite de couches.\n");
        TestWorkspace.Write(folder, "04 - Angular/signals.md", "# Signals\nLes signals remplacent zone.js.\n");
        var source = Declare("cours", folder);

        var report = await _subject.IndexAsync(source, Token);

        report.Should().Be(new IndexReport(Added: 2, Updated: 0, Removed: 0, Unchanged: 0));
        _index.Counts(source.Id).Should().Match<IndexCounts>(counts => counts.Notes == 2 && counts.Chunks == 2 && counts.LastIndexedAt == _clock.GetUtcNow());
        var hits = _index.Search(new SearchRequest("couches"), SourceScope.All);
        hits.Should().ContainSingle().Which.Should().Match<SearchHit>(hit =>
            hit.SourceName == "cours" && hit.Path == "02 - DevOps/docker.md" && hit.Topic == "02 - DevOps" && hit.Title == "Docker" && hit.Heading == "Docker > Images");
    }

    [Fact]
    public async Task should_leave_alone_a_note_whose_content_did_not_change()
    {
        var folder = _workspace.Folder("cours");
        TestWorkspace.Write(folder, "a.md", "# A\ntext");
        var source = Declare("cours", folder);
        await _subject.IndexAsync(source, Token);
        _clock.Advance(TimeSpan.FromHours(1));

        var report = await _subject.IndexAsync(source, Token);

        report.Should().Be(new IndexReport(Added: 0, Updated: 0, Removed: 0, Unchanged: 1));
        _index.Counts(source.Id).LastIndexedAt.Should().Be(new DateTimeOffset(2026, 11, 3, 9, 0, 0, TimeSpan.Zero));
    }

    [Fact]
    public async Task should_replace_a_changed_note_and_forget_its_old_words()
    {
        var folder = _workspace.Folder("cours");
        TestWorkspace.Write(folder, "a.md", "# A\nancien contenu sur redis");
        var source = Declare("cours", folder);
        await _subject.IndexAsync(source, Token);
        TestWorkspace.Write(folder, "a.md", "# A\nnouveau contenu sur postgres");

        var report = await _subject.IndexAsync(source, Token);

        report.Should().Be(new IndexReport(Added: 0, Updated: 1, Removed: 0, Unchanged: 0));
        _index.Search(new SearchRequest("redis"), SourceScope.All).Should().BeEmpty();
        _index.Search(new SearchRequest("postgres"), SourceScope.All).Should().ContainSingle();
        _index.Counts(source.Id).Should().Match<IndexCounts>(counts => counts.Notes == 1 && counts.Chunks == 1);
    }

    [Fact]
    public async Task should_remove_a_note_that_is_gone_and_its_words()
    {
        var folder = _workspace.Folder("cours");
        TestWorkspace.Write(folder, "a.md", "# A\nkafka");
        TestWorkspace.Write(folder, "b.md", "# B\nrabbitmq");
        var source = Declare("cours", folder);
        await _subject.IndexAsync(source, Token);
        File.Delete(Path.Combine(folder, "a.md"));

        var report = await _subject.IndexAsync(source, Token);

        report.Should().Be(new IndexReport(Added: 0, Updated: 0, Removed: 1, Unchanged: 1));
        _index.Search(new SearchRequest("kafka"), SourceScope.All).Should().BeEmpty();
        _index.Search(new SearchRequest("rabbitmq"), SourceScope.All).Should().ContainSingle();
    }

    [Fact]
    public async Task should_find_a_word_whatever_its_accents_or_its_case()
    {
        var folder = _workspace.Folder("cours");
        TestWorkspace.Write(folder, "net.md", "# Réseau\nLe Réseau privé virtuel.");
        var source = Declare("cours", folder);
        await _subject.IndexAsync(source, Token);

        _index.Search(new SearchRequest("reseau"), SourceScope.All).Should().ContainSingle();
        _index.Search(new SearchRequest("RÉSEAU PRIVÉ"), SourceScope.All).Should().ContainSingle();
    }

    [Fact]
    public async Task should_rank_the_note_that_has_more_of_the_words_first()
    {
        var folder = _workspace.Folder("cours");
        TestWorkspace.Write(folder, "one.md", "# One\ningress");
        TestWorkspace.Write(folder, "both.md", "# Both\ningress controller et ingress class pour un ingress");
        var source = Declare("cours", folder);
        await _subject.IndexAsync(source, Token);

        var hits = _index.Search(new SearchRequest("ingress controller"), SourceScope.All);

        hits.Select(hit => hit.Path).Should().Equal("both.md", "one.md");
        hits[0].Score.Should().BeGreaterThan(hits[1].Score);
    }

    [Fact]
    public async Task should_keep_a_search_to_one_source_or_one_topic_when_asked()
    {
        var courses = _workspace.Folder("cours");
        var memory = _workspace.Folder("memoire");
        TestWorkspace.Write(courses, "02 - DevOps/a.md", "# A\nvault secret store");
        TestWorkspace.Write(courses, "04 - Angular/b.md", "# B\nvault of components");
        TestWorkspace.Write(memory, "c.md", "# C\nvault decision");
        var coursesSource = Declare("cours", courses);
        var memorySource = Declare("memoire", memory, SourceKind.Memory);
        await _subject.IndexAsync(coursesSource, Token);
        await _subject.IndexAsync(memorySource, Token);

        _index.Search(new SearchRequest("vault"), SourceScope.All).Should().HaveCount(3);
        _index.Search(new SearchRequest("vault", SourceId: memorySource.Id), SourceScope.All).Should().ContainSingle().Which.SourceName.Should().Be("memoire");
        _index.Search(new SearchRequest("vault", SourceId: coursesSource.Id, Topic: "04 - Angular"), SourceScope.All).Should().ContainSingle().Which.Path.Should().Be("04 - Angular/b.md");
    }

    [Fact]
    public async Task should_give_nothing_for_a_question_without_a_word_and_honour_the_limit()
    {
        var folder = _workspace.Folder("cours");
        foreach (var i in Enumerable.Range(0, 5))
        {
            TestWorkspace.Write(folder, $"n{i}.md", $"# N{i}\nsame words here");
        }

        await _subject.IndexAsync(Declare("cours", folder), Token);

        _index.Search(new SearchRequest("?!"), SourceScope.All).Should().BeEmpty();
        _index.Search(new SearchRequest("words", Limit: 2), SourceScope.All).Should().HaveCount(2);
        _index.Search(new SearchRequest("words", Limit: 0), SourceScope.All).Should().HaveCount(1);
    }

    [Fact]
    public async Task should_forget_the_notes_of_a_source_that_is_removed()
    {
        var folder = _workspace.Folder("cours");
        TestWorkspace.Write(folder, "a.md", "# A\nelasticsearch");
        var source = Declare("cours", folder);
        await _subject.IndexAsync(source, Token);

        _sources.Remove(source.Id).Should().BeTrue();

        _index.Search(new SearchRequest("elasticsearch"), SourceScope.All).Should().BeEmpty();
        _index.Counts(source.Id).Should().Be(new IndexCounts(0, 0, null));
    }

    [Fact]
    public async Task should_refuse_a_folder_that_does_not_exist()
    {
        var source = Declare("gone", Path.Combine(_workspace.SourcesFolder, "gone"));

        var index = () => _subject.IndexAsync(source, Token);

        (await index.Should().ThrowAsync<RejectedException>()).Which.Problem.Should().Be(Problem.Conflict);
        _outcomes.Of(source.Id).Should().Be(new ScanOutcome(_clock.GetUtcNow(), ScanOutcome.FolderMissing));
    }

    [Fact]
    public async Task should_keep_how_the_last_pass_ended_and_clear_a_failure_when_the_next_one_goes_well()
    {
        var folder = _workspace.Folder("cours");
        var source = Declare("cours", folder);
        Directory.Delete(folder);
        await FluentActions.Awaiting(() => _subject.IndexAsync(source, Token)).Should().ThrowAsync<RejectedException>();
        Directory.CreateDirectory(folder);
        _clock.Advance(TimeSpan.FromMinutes(5));

        await _subject.IndexAsync(source, Token);

        _outcomes.Of(source.Id).Should().Be(new ScanOutcome(_clock.GetUtcNow(), null));
        _outcomes.Of(Guid.NewGuid()).Should().BeNull();
    }

    [Fact]
    public async Task should_refuse_a_source_that_is_not_inside_the_roots_any_more_and_say_why()
    {
        var folder = _workspace.Folder("cours");
        TestWorkspace.Write(folder, "a.md", "# A\ntext");
        var source = Declare("cours", folder);
        await _subject.IndexAsync(source, Token);
        var narrowed = new SourceIndexer(_index, _workspace.Paths(_workspace.Folder("elsewhere")), _outcomes, _clock);

        var index = () => narrowed.IndexAsync(source, Token);

        (await index.Should().ThrowAsync<RejectedException>()).Which.Problem.Should().Be(Problem.Conflict);
        _outcomes.Of(source.Id)!.Error.Should().Be(ScanOutcome.OutsideRoots);
    }

    [Fact]
    public async Task should_cut_again_a_note_that_was_cut_by_another_version_of_the_chunker()
    {
        var folder = _workspace.Folder("cours");
        TestWorkspace.Write(folder, "a.md", "# A\ntext");
        var source = Declare("cours", folder);
        var hash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(Path.Combine(folder, "a.md"))));
        _index.Upsert(source.Id, new ScannedFile("a.md", Path.Combine(folder, "a.md")), "A", hash, chunker: MarkdownChunker.Version - 1, [new Chunk("A", "text")], _clock.GetUtcNow());

        var report = await _subject.IndexAsync(source, Token);

        report.Should().Be(new IndexReport(Added: 0, Updated: 1, Removed: 0, Unchanged: 0));
        (await _subject.IndexAsync(source, Token)).Should().Be(new IndexReport(Added: 0, Updated: 0, Removed: 0, Unchanged: 1));
    }

    [Fact]
    public async Task should_cut_every_note_again_when_a_rebuild_is_asked_for()
    {
        var folder = _workspace.Folder("cours");
        TestWorkspace.Write(folder, "a.md", "# A\ntext");
        var source = Declare("cours", folder);
        await _subject.IndexAsync(source, Token);

        var report = await _subject.IndexAsync(source, rebuild: true, Token);

        report.Should().Be(new IndexReport(Added: 0, Updated: 1, Removed: 0, Unchanged: 0));
    }

    [Fact]
    public async Task should_read_the_title_of_a_note_that_starts_with_a_byte_order_mark()
    {
        var folder = _workspace.Folder("cours");
        File.WriteAllText(Path.Combine(folder, "windows.md"), "# From Windows\nbody text", new System.Text.UTF8Encoding(encoderShouldEmitUTF8Identifier: true));
        await _subject.IndexAsync(Declare("cours", folder), Token);

        var hit = _index.Search(new SearchRequest("body"), SourceScope.All).Should().ContainSingle().Subject;

        hit.Should().Match<SearchHit>(h => h.Title == "From Windows" && h.Heading == "From Windows");
    }

    [Fact]
    public async Task should_drop_the_chunks_of_a_note_that_was_edited_to_nothing()
    {
        var folder = _workspace.Folder("cours");
        TestWorkspace.Write(folder, "a.md", "# A\nredis");
        var source = Declare("cours", folder);
        await _subject.IndexAsync(source, Token);
        TestWorkspace.Write(folder, "a.md", string.Empty);

        var report = await _subject.IndexAsync(source, Token);

        report.Should().Be(new IndexReport(Added: 0, Updated: 1, Removed: 0, Unchanged: 0));
        _index.Counts(source.Id).Should().Match<IndexCounts>(counts => counts.Notes == 1 && counts.Chunks == 0);
        _index.Search(new SearchRequest("redis"), SourceScope.All).Should().BeEmpty();
    }

    [Fact]
    public async Task should_never_return_more_than_fifty_hits_whatever_the_limit_asked()
    {
        var folder = _workspace.Folder("cours");
        foreach (var i in Enumerable.Range(0, 60))
        {
            TestWorkspace.Write(folder, $"n{i}.md", $"# N{i}\nshared words");
        }

        await _subject.IndexAsync(Declare("cours", folder), Token);

        _index.Search(new SearchRequest("shared", Limit: 1000), SourceScope.All).Should().HaveCount(50);
    }

    [Fact]
    public async Task should_stop_when_cancelled()
    {
        var folder = _workspace.Folder("cours");
        TestWorkspace.Write(folder, "a.md", "# A\ntext");
        var source = Declare("cours", folder);
        using var cancelled = new CancellationTokenSource();
        await cancelled.CancelAsync();

        var index = () => _subject.IndexAsync(source, cancelled.Token);

        await index.Should().ThrowAsync<OperationCanceledException>();
    }

    [Fact]
    public async Task should_index_a_note_that_has_no_text_without_a_chunk()
    {
        var folder = _workspace.Folder("cours");
        TestWorkspace.Write(folder, "empty.md", string.Empty);
        var source = Declare("cours", folder);

        await _subject.IndexAsync(source, Token);

        _index.Counts(source.Id).Should().Match<IndexCounts>(counts => counts.Notes == 1 && counts.Chunks == 0);
    }

    [Fact]
    public async Task should_search_only_the_sources_a_caller_may_touch_and_none_for_an_empty_scope()
    {
        var first = _workspace.Folder("one");
        var second = _workspace.Folder("two");
        TestWorkspace.Write(first, "a.md", "# A\nvault of the first");
        TestWorkspace.Write(second, "b.md", "# B\nvault of the second");
        var one = Declare("one", first);
        var two = Declare("two", second, SourceKind.Memory);
        await _subject.IndexAsync(one, Token);
        await _subject.IndexAsync(two, Token);

        _index.Search(new SearchRequest("vault"), new SourceScope([two.Id])).Should().ContainSingle().Which.SourceName.Should().Be("two");
        _index.Search(new SearchRequest("vault"), new SourceScope([])).Should().BeEmpty();
        _index.Search(new SearchRequest("vault"), SourceScope.All).Should().HaveCount(2);
    }
}

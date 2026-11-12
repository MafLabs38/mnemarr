using System.Runtime.Versioning;
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
    public async Task should_index_a_long_conversation_of_several_megabytes_and_find_what_is_at_its_end()
    {
        var folder = _workspace.Folder("chats");
        var filler = string.Concat(Enumerable.Repeat("The people talked about the cluster and the backups for a while.\n", 50_000));
        TestWorkspace.Write(folder, "long.md", "# A long conversation\n" + filler + "\n## The end\nAnd finally zymurgy was mentioned.\n");
        new FileInfo(Path.Combine(folder, "long.md")).Length.Should().BeGreaterThan(MarkdownFileScanner.MaxFileBytes, "the old limit would have left it out");
        var source = Declare("chats", folder);

        var report = await _subject.IndexAsync(source, Token);

        report.Should().Be(new IndexReport(Added: 1, Updated: 0, Removed: 0, Unchanged: 0));
        _index.Search(new SearchRequest("zymurgy"), SourceScope.All).Should().ContainSingle().Which.Path.Should().Be("long.md");
    }

    [Fact]
    public async Task should_give_a_note_at_most_its_share_of_the_hits_so_that_a_long_conversation_does_not_fill_the_answer()
    {
        var folder = _workspace.Folder("chats");
        TestWorkspace.Write(folder, "long.md", "# Long\n" + string.Concat(Enumerable.Range(0, 20).Select(index => $"## Part {index}\nThe backups were discussed again, number {index}.\n")));
        TestWorkspace.Write(folder, "other.md", "# Other\nOne word about the backups.\n");
        var source = Declare("chats", folder);
        await _subject.IndexAsync(source, Token);

        var shared = _index.Search(new SearchRequest("backups", Limit: 10), SourceScope.All);
        var one = _index.Search(new SearchRequest("backups", Limit: 10) { PerNote = 1 }, SourceScope.All);
        var all = _index.Search(new SearchRequest("backups", Limit: 50) { PerNote = 50 }, SourceScope.All);

        shared.Count(hit => hit.Path == "long.md").Should().Be(SearchRequest.DefaultPerNote);
        shared.Should().Contain(hit => hit.Path == "other.md");
        one.Select(hit => hit.Path).Should().BeEquivalentTo("long.md", "other.md");
        all.Count(hit => hit.Path == "long.md").Should().Be(20, "the share can be lifted");
    }

    [Fact]
    public async Task should_stay_within_some_topics_and_leave_others_out_and_leave_a_source_out()
    {
        var first = _workspace.Folder("first");
        TestWorkspace.Write(first, "ops/a.md", "# A\nThe alpha is here.\n");
        TestWorkspace.Write(first, "dev/b.md", "# B\nThe alpha is here.\n");
        TestWorkspace.Write(first, "docs/c.md", "# C\nThe alpha is here.\n");
        var second = _workspace.Folder("second");
        TestWorkspace.Write(second, "ops/d.md", "# D\nThe alpha is here.\n");
        var one = Declare("first", first);
        var two = Declare("second", second);
        await _subject.IndexAsync(one, Token);
        await _subject.IndexAsync(two, Token);

        IEnumerable<string> Paths(SearchRequest request) => _index.Search(request, SourceScope.All).Select(hit => hit.Path);

        Paths(new SearchRequest("alpha") { Topics = ["ops", "dev"] }).Should().BeEquivalentTo("ops/a.md", "dev/b.md", "ops/d.md");
        Paths(new SearchRequest("alpha", Topic: "docs") { Topics = ["dev"] }).Should().BeEquivalentTo("docs/c.md", "dev/b.md");
        Paths(new SearchRequest("alpha") { ExcludeTopics = ["dev", "docs"] }).Should().BeEquivalentTo("ops/a.md", "ops/d.md");
        Paths(new SearchRequest("alpha") { ExcludeSources = [two.Id] }).Should().BeEquivalentTo("ops/a.md", "dev/b.md", "docs/c.md");
        Paths(new SearchRequest("alpha") { Topics = ["ops"], ExcludeSources = [one.Id] }).Should().Equal("ops/d.md");
        Paths(new SearchRequest("alpha")).Should().HaveCount(4, "nothing asked, nothing left out");
    }

    [Fact]
    public async Task should_keep_what_was_indexed_of_a_note_that_grew_past_the_limit_and_say_that_it_is_too_large()
    {
        var folder = _workspace.Folder("chats");
        TestWorkspace.Write(folder, "talk.md", "# Talk\nThe word is zymurgy.\n");
        var source = Declare("chats", folder);
        var narrow = new SourceIndexer(_index, _workspace.Paths(), _outcomes, _clock, options: Microsoft.Extensions.Options.Options.Create(new IndexingOptions { MaxNoteMegabytes = 1 }));
        await narrow.IndexAsync(source, Token);

        File.AppendAllText(Path.Combine(folder, "talk.md"), new string('x', 1024 * 1024 + 10));
        var report = await narrow.IndexAsync(source, Token);

        report.Should().Be(new IndexReport(Added: 0, Updated: 0, Removed: 0, Unchanged: 0, TooLarge: 1));
        _index.Search(new SearchRequest("zymurgy"), SourceScope.All).Should().ContainSingle("a note that is too large to read is not a note that was deleted");
        _outcomes.Of(source.Id).Should().Be(new ScanOutcome(_clock.GetUtcNow(), null, TooLarge: 1));
    }

    [Fact]
    public async Task should_index_every_note_of_a_source_and_find_them_by_their_words()
    {
        var folder = _workspace.Folder("courses");
        TestWorkspace.Write(folder, "02 - DevOps/docker.md", "# Docker\n## Images\nUne image est faite de couches.\n");
        TestWorkspace.Write(folder, "04 - Angular/signals.md", "# Signals\nLes signals remplacent zone.js.\n");
        var source = Declare("courses", folder);

        var report = await _subject.IndexAsync(source, Token);

        report.Should().Be(new IndexReport(Added: 2, Updated: 0, Removed: 0, Unchanged: 0));
        _index.Counts(source.Id).Should().Match<IndexCounts>(counts => counts.Notes == 2 && counts.Chunks == 2 && counts.LastIndexedAt == _clock.GetUtcNow());
        var hits = _index.Search(new SearchRequest("couches"), SourceScope.All);
        hits.Should().ContainSingle().Which.Should().Match<SearchHit>(hit =>
            hit.SourceName == "courses" && hit.Path == "02 - DevOps/docker.md" && hit.Topic == "02 - DevOps" && hit.Title == "Docker" && hit.Heading == "Docker > Images");
    }

    [Fact]
    public async Task should_leave_alone_a_note_whose_content_did_not_change()
    {
        var folder = _workspace.Folder("courses");
        TestWorkspace.Write(folder, "a.md", "# A\ntext");
        var source = Declare("courses", folder);
        await _subject.IndexAsync(source, Token);
        _clock.Advance(TimeSpan.FromHours(1));

        var report = await _subject.IndexAsync(source, Token);

        report.Should().Be(new IndexReport(Added: 0, Updated: 0, Removed: 0, Unchanged: 1));
        _index.Counts(source.Id).LastIndexedAt.Should().Be(new DateTimeOffset(2026, 11, 3, 9, 0, 0, TimeSpan.Zero));
    }

    [Fact]
    public async Task should_replace_a_changed_note_and_forget_its_old_words()
    {
        var folder = _workspace.Folder("courses");
        TestWorkspace.Write(folder, "a.md", "# A\nancien contenu sur redis");
        var source = Declare("courses", folder);
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
        var folder = _workspace.Folder("courses");
        TestWorkspace.Write(folder, "a.md", "# A\nkafka");
        TestWorkspace.Write(folder, "b.md", "# B\nrabbitmq");
        var source = Declare("courses", folder);
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
        var folder = _workspace.Folder("courses");
        TestWorkspace.Write(folder, "net.md", "# Réseau\nLe Réseau privé virtuel.");
        var source = Declare("courses", folder);
        await _subject.IndexAsync(source, Token);

        _index.Search(new SearchRequest("reseau"), SourceScope.All).Should().ContainSingle();
        _index.Search(new SearchRequest("RÉSEAU PRIVÉ"), SourceScope.All).Should().ContainSingle();
    }

    [Fact]
    public async Task should_rank_the_note_that_has_more_of_the_words_first()
    {
        var folder = _workspace.Folder("courses");
        TestWorkspace.Write(folder, "one.md", "# One\ningress");
        TestWorkspace.Write(folder, "both.md", "# Both\ningress controller et ingress class pour un ingress");
        var source = Declare("courses", folder);
        await _subject.IndexAsync(source, Token);

        var hits = _index.Search(new SearchRequest("ingress controller"), SourceScope.All);

        hits.Select(hit => hit.Path).Should().Equal("both.md", "one.md");
        hits[0].Score.Should().BeGreaterThan(hits[1].Score);
    }

    [Fact]
    public async Task should_keep_a_search_to_one_source_or_one_topic_when_asked()
    {
        var courses = _workspace.Folder("courses");
        var memory = _workspace.Folder("memoire");
        TestWorkspace.Write(courses, "02 - DevOps/a.md", "# A\nvault secret store");
        TestWorkspace.Write(courses, "04 - Angular/b.md", "# B\nvault of components");
        TestWorkspace.Write(memory, "c.md", "# C\nvault decision");
        var coursesSource = Declare("courses", courses);
        var memorySource = Declare("memoire", memory, SourceKind.Memory);
        await _subject.IndexAsync(coursesSource, Token);
        await _subject.IndexAsync(memorySource, Token);

        _index.Search(new SearchRequest("vault"), SourceScope.All).Should().HaveCount(3);
        _index.Search(new SearchRequest("vault", SourceId: memorySource.Id), SourceScope.All).Should().ContainSingle().Which.SourceName.Should().Be("memoire");
        _index.Search(new SearchRequest("vault", SourceId: coursesSource.Id, Topic: "04 - Angular"), SourceScope.All).Should().ContainSingle().Which.Path.Should().Be("04 - Angular/b.md");
    }

    [Theory]
    [InlineData(2, 2)]
    [InlineData(0, 1)]
    [InlineData(1000, 50)]
    public async Task should_honour_the_limit_asked_for_within_one_and_fifty(int asked, int expected)
    {
        var folder = _workspace.Folder("courses");
        foreach (var i in Enumerable.Range(0, 60))
        {
            TestWorkspace.Write(folder, $"n{i}.md", $"# N{i}\nshared words");
        }

        await _subject.IndexAsync(Declare("courses", folder), Token);

        _index.Search(new SearchRequest("shared", Limit: asked), SourceScope.All).Should().HaveCount(expected);
    }

    [Fact]
    public async Task should_forget_the_notes_of_a_source_that_is_removed()
    {
        var folder = _workspace.Folder("courses");
        TestWorkspace.Write(folder, "a.md", "# A\nelasticsearch");
        var source = Declare("courses", folder);
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
        var folder = _workspace.Folder("courses");
        var source = Declare("courses", folder);
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
        var folder = _workspace.Folder("courses");
        TestWorkspace.Write(folder, "a.md", "# A\ntext");
        var source = Declare("courses", folder);
        await _subject.IndexAsync(source, Token);
        var narrowed = new SourceIndexer(_index, _workspace.Paths(_workspace.Folder("elsewhere")), _outcomes, _clock);

        var index = () => narrowed.IndexAsync(source, Token);

        (await index.Should().ThrowAsync<RejectedException>()).Which.Problem.Should().Be(Problem.Conflict);
        _outcomes.Of(source.Id)!.Error.Should().Be(ScanOutcome.OutsideRoots);
    }

    [Fact]
    public async Task should_cut_again_a_note_that_was_cut_by_another_version_of_the_chunker()
    {
        var folder = _workspace.Folder("courses");
        TestWorkspace.Write(folder, "a.md", "# A\ntext");
        var source = Declare("courses", folder);
        var hash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(Path.Combine(folder, "a.md"))));
        _index.Upsert(source.Id, new ScannedFile("a.md", Path.Combine(folder, "a.md")), new IndexedNote("A", hash, MarkdownChunker.Version - 1, [new Chunk("A", "text")], NoteMeta.Empty, MarkdownNotes.Version), _clock.GetUtcNow());

        var report = await _subject.IndexAsync(source, Token);

        report.Should().Be(new IndexReport(Added: 0, Updated: 1, Removed: 0, Unchanged: 0));
        (await _subject.IndexAsync(source, Token)).Should().Be(new IndexReport(Added: 0, Updated: 0, Removed: 0, Unchanged: 1));
    }

    [Fact]
    public async Task should_cut_every_note_again_when_a_rebuild_is_asked_for()
    {
        var folder = _workspace.Folder("courses");
        TestWorkspace.Write(folder, "a.md", "# A\ntext");
        var source = Declare("courses", folder);
        await _subject.IndexAsync(source, Token);

        var report = await _subject.IndexAsync(source, rebuild: true, Token);

        report.Should().Be(new IndexReport(Added: 0, Updated: 1, Removed: 0, Unchanged: 0));
    }

    [Fact]
    public async Task should_read_the_title_of_a_note_that_starts_with_a_byte_order_mark()
    {
        var folder = _workspace.Folder("courses");
        File.WriteAllText(Path.Combine(folder, "windows.md"), "# From Windows\nbody text", new System.Text.UTF8Encoding(encoderShouldEmitUTF8Identifier: true));
        await _subject.IndexAsync(Declare("courses", folder), Token);

        var hit = _index.Search(new SearchRequest("body"), SourceScope.All).Should().ContainSingle().Subject;

        hit.Should().Match<SearchHit>(h => h.Title == "From Windows" && h.Heading == "From Windows");
    }

    [Fact]
    public async Task should_drop_the_chunks_of_a_note_that_was_edited_to_nothing()
    {
        var folder = _workspace.Folder("courses");
        TestWorkspace.Write(folder, "a.md", "# A\nredis");
        var source = Declare("courses", folder);
        await _subject.IndexAsync(source, Token);
        TestWorkspace.Write(folder, "a.md", string.Empty);

        var report = await _subject.IndexAsync(source, Token);

        report.Should().Be(new IndexReport(Added: 0, Updated: 1, Removed: 0, Unchanged: 0));
        _index.Counts(source.Id).Should().Match<IndexCounts>(counts => counts.Notes == 1 && counts.Chunks == 0);
        _index.Search(new SearchRequest("redis"), SourceScope.All).Should().BeEmpty();
    }

    [Fact]
    [SupportedOSPlatform("linux")]
    public async Task should_refuse_a_folder_that_was_swapped_for_a_link_after_it_was_declared_and_read_nothing_through_it()
    {
        Assert.SkipUnless(OperatingSystem.IsLinux(), "Creating a link needs no privilege on Linux only.");
        var folder = _workspace.Folder("courses");
        var source = Declare("courses", folder);
        var outside = Directory.CreateTempSubdirectory("mnemarr-outside-");
        try
        {
            TestWorkspace.Write(outside.FullName, "secret.md", "# Secret\nleakedword");
            Directory.Delete(folder);
            Directory.CreateSymbolicLink(folder, outside.FullName);

            var index = () => _subject.IndexAsync(source, Token);

            (await index.Should().ThrowAsync<RejectedException>()).Which.Problem.Should().Be(Problem.Conflict);
            _outcomes.Of(source.Id)!.Error.Should().Be(ScanOutcome.OutsideRoots);
            _index.Search(new SearchRequest("leakedword"), SourceScope.All).Should().BeEmpty();
        }
        finally
        {
            File.Delete(folder);
            outside.Delete(recursive: true);
        }
    }

    [Fact]
    public async Task should_not_read_again_a_note_whose_size_and_time_did_not_change_unless_asked_to_rebuild()
    {
        var folder = _workspace.Folder("courses");
        var file = Path.Combine(folder, "a.md");
        TestWorkspace.Write(folder, "a.md", "# A\nfirstword");
        var source = Declare("courses", folder);
        await _subject.IndexAsync(source, Token);
        var written = File.GetLastWriteTimeUtc(file);

        File.WriteAllText(file, "# A\nsecondwrd");
        File.SetLastWriteTimeUtc(file, written);
        var skipped = await _subject.IndexAsync(source, Token);
        var rebuilt = await _subject.IndexAsync(source, rebuild: true, Token);

        skipped.Should().Be(new IndexReport(0, 0, 0, Unchanged: 1), "the size and the time are those of the note that was cut: it is not read");
        rebuilt.Updated.Should().Be(1);
        _index.Search(new SearchRequest("secondwrd"), SourceScope.All).Should().ContainSingle();
    }

    [Fact]
    public async Task should_remember_the_new_time_of_a_note_that_was_touched_without_changing_so_that_it_is_read_once()
    {
        var folder = _workspace.Folder("courses");
        var file = Path.Combine(folder, "a.md");
        TestWorkspace.Write(folder, "a.md", "# A\nfirstword");
        var source = Declare("courses", folder);
        await _subject.IndexAsync(source, Token);
        using var gated = new GatedIndex(_index);
        gated.Release.Set();
        var subject = new SourceIndexer(gated, _workspace.Paths(), _outcomes, _clock);

        var touchedAt = File.GetLastWriteTimeUtc(file).AddHours(1);
        File.SetLastWriteTimeUtc(file, touchedAt);
        var touched = await subject.IndexAsync(source, Token);
        File.WriteAllText(file, "# A\nsecondwrd");
        File.SetLastWriteTimeUtc(file, touchedAt);
        var again = await subject.IndexAsync(source, Token);

        touched.Unchanged.Should().Be(1);
        gated.Touched.Should().Be(1);
        again.Unchanged.Should().Be(1, "the touched time was kept: the same time and size are not read again");
    }

    [Fact]
    public async Task should_let_a_source_be_indexed_while_the_pass_over_another_one_is_running()
    {
        var running = _workspace.Folder("busy");
        TestWorkspace.Write(running, "a.md", "# A\ntext");
        var busy = Declare("busy", running);
        var idle = Declare("idle", _workspace.Folder("idle"));
        using var gated = new GatedIndex(_index);
        var subject = new SourceIndexer(gated, _workspace.Paths(), _outcomes, _clock);
        var pass = Task.Run(() => subject.IndexAsync(busy, Token), Token);
        gated.Entered.Wait(Token);

        var other = await subject.IndexAsync(idle, rebuild: false, waitForTurn: false, Token);
        var same = () => subject.IndexAsync(busy, rebuild: false, waitForTurn: false, Token);

        other.Should().Be(new IndexReport(0, 0, 0, 0));
        (await same.Should().ThrowAsync<RejectedException>()).Which.Problem.Should().Be(Problem.Busy);
        gated.Release.Set();
        await pass;
    }

    [Fact]
    public async Task should_record_a_pass_that_fails_in_the_index_so_that_the_source_does_not_look_healthy()
    {
        var folder = _workspace.Folder("courses");
        TestWorkspace.Write(folder, "a.md", "# A\ntext");
        var source = Declare("courses", folder);
        var subject = new SourceIndexer(new BrokenIndex(), _workspace.Paths(), _outcomes, _clock);

        var run = () => subject.IndexAsync(source, Token);

        await run.Should().ThrowAsync<InvalidOperationException>();
        _outcomes.Of(source.Id)!.Error.Should().Be(ScanOutcome.IndexFailed);
    }

    [Fact]
    [SupportedOSPlatform("linux")]
    public void should_see_a_link_in_a_folder_above_a_file_not_only_in_the_file()
    {
        Assert.SkipUnless(OperatingSystem.IsLinux(), "Creating a link needs no privilege on Linux only.");
        var folder = _workspace.Folder("courses");
        var outside = Directory.CreateTempSubdirectory("mnemarr-outside-");
        try
        {
            TestWorkspace.Write(outside.FullName, "secret.md", "# Secret\nleakedword");
            TestWorkspace.Write(folder, "plain.md", "# Plain");
            Directory.CreateSymbolicLink(Path.Combine(folder, "swapped"), outside.FullName);

            SourcePaths.CrossesLink(Path.Combine(folder, "swapped", "secret.md"), folder).Should().BeTrue();
            SourcePaths.CrossesLink(Path.Combine(folder, "plain.md"), folder).Should().BeFalse();
        }
        finally
        {
            outside.Delete(recursive: true);
        }
    }

    [Fact]
    [SupportedOSPlatform("linux")]
    public async Task should_skip_a_note_that_cannot_be_read_and_index_the_others()
    {
        Assert.SkipUnless(OperatingSystem.IsLinux() && !Environment.IsPrivilegedProcess, "A file is unreadable to its owner only when the process is not privileged.");
        var folder = _workspace.Folder("courses");
        TestWorkspace.Write(folder, "locked.md", "# Locked\nhidden");
        TestWorkspace.Write(folder, "open.md", "# Open\nvisibleword");
        File.SetUnixFileMode(Path.Combine(folder, "locked.md"), UnixFileMode.None);
        var source = Declare("courses", folder);

        var report = await _subject.IndexAsync(source, Token);

        report.Added.Should().Be(1);
        _index.Search(new SearchRequest("visibleword"), SourceScope.All).Should().ContainSingle();
        _outcomes.Of(source.Id)!.Failed.Should().BeFalse("one file that cannot be read is not a failure of the pass");
    }

    [Fact]
    public async Task should_refuse_to_wait_for_a_pass_that_is_running_when_asked_not_to_wait()
    {
        var folder = _workspace.Folder("courses");
        TestWorkspace.Write(folder, "a.md", "# A\ntext");
        var source = Declare("courses", folder);
        using var gated = new GatedIndex(_index);
        var subject = new SourceIndexer(gated, _workspace.Paths(), _outcomes, _clock);
        var running = Task.Run(() => subject.IndexAsync(source, Token), Token);
        gated.Entered.Wait(Token);

        var refused = () => subject.IndexAsync(source, rebuild: false, waitForTurn: false, Token);

        (await refused.Should().ThrowAsync<RejectedException>()).Which.Should().Match<RejectedException>(e => e.Problem == Problem.Busy && e.RetryAfter != null);
        gated.Release.Set();
        (await running).Added.Should().Be(1);
    }

    /// <summary>An index that fails whatever it is asked.</summary>
    private sealed class BrokenIndex : INoteIndex
    {
        public IReadOnlyDictionary<string, NoteState> Notes(Guid sourceId) => throw new InvalidOperationException("the index is broken");

        public void Upsert(Guid sourceId, ScannedFile file, IndexedNote note, DateTimeOffset at) => throw new InvalidOperationException();

        public void Touch(Guid sourceId, ScannedFile file) => throw new InvalidOperationException();

        public void UpdateMeta(Guid sourceId, ScannedFile file, NoteMeta meta, int metaVersion) => throw new InvalidOperationException();

        public IReadOnlySet<string> Images(Guid sourceId) => throw new InvalidOperationException();

        public void ReplaceImages(Guid sourceId, IReadOnlyCollection<string> paths) => throw new InvalidOperationException();

        public void Remove(Guid sourceId, string path) => throw new InvalidOperationException();

        public IndexCounts Counts(Guid sourceId) => throw new InvalidOperationException();

        public IReadOnlyList<SearchHit> Search(SearchRequest request, SourceScope scope) => throw new InvalidOperationException();
    }

    /// <summary>An index whose writes wait for the test: a pass that is running, for as long as the test wants.</summary>
    private sealed class GatedIndex(INoteIndex inner) : INoteIndex, IDisposable
    {
        public ManualResetEventSlim Entered { get; } = new();

        public ManualResetEventSlim Release { get; } = new();

        public void Dispose()
        {
            Entered.Dispose();
            Release.Dispose();
        }

        public IReadOnlyDictionary<string, NoteState> Notes(Guid sourceId) => inner.Notes(sourceId);

        public void Upsert(Guid sourceId, ScannedFile file, IndexedNote note, DateTimeOffset at)
        {
            Entered.Set();
            Release.Wait(TimeSpan.FromSeconds(10));
            inner.Upsert(sourceId, file, note, at);
        }

        public int Touched { get; private set; }

        public void Touch(Guid sourceId, ScannedFile file)
        {
            Touched++;
            inner.Touch(sourceId, file);
        }

        public void UpdateMeta(Guid sourceId, ScannedFile file, NoteMeta meta, int metaVersion) => inner.UpdateMeta(sourceId, file, meta, metaVersion);

        public IReadOnlySet<string> Images(Guid sourceId) => inner.Images(sourceId);

        public void ReplaceImages(Guid sourceId, IReadOnlyCollection<string> paths) => inner.ReplaceImages(sourceId, paths);

        public void Remove(Guid sourceId, string path) => inner.Remove(sourceId, path);

        public IndexCounts Counts(Guid sourceId) => inner.Counts(sourceId);

        public IReadOnlyList<SearchHit> Search(SearchRequest request, SourceScope scope) => inner.Search(request, scope);
    }

    [Fact]
    public async Task should_stop_when_cancelled()
    {
        var folder = _workspace.Folder("courses");
        TestWorkspace.Write(folder, "a.md", "# A\ntext");
        var source = Declare("courses", folder);
        using var cancelled = new CancellationTokenSource();
        await cancelled.CancelAsync();

        var index = () => _subject.IndexAsync(source, cancelled.Token);

        await index.Should().ThrowAsync<OperationCanceledException>();
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

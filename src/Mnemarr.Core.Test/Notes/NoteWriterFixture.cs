using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using Mnemarr.Core.Accounts;
using Mnemarr.Core.Audit;
using Mnemarr.Core.Git;
using Mnemarr.Core.Indexing;
using Mnemarr.Core.Notes;
using Mnemarr.Core.Sources;
using Mnemarr.Core.Test.Git;

namespace Mnemarr.Core.Test.Notes;

public sealed class NoteWriterFixture : IDisposable
{
    private static readonly NoteActor Alice = new(SourceScope.All, "alice");
    private readonly TestWorkspace _workspace = new();
    private readonly FakeTimeProvider _clock = new(new DateTimeOffset(2026, 11, 4, 9, 0, 0, TimeSpan.Zero));
    private readonly FakeGitEngine _engine = new();
    private readonly SourceLocks _locks = new();
    private readonly SyncOutcomes _syncs = new();
    private readonly SqliteSourceStore _store;
    private readonly SqliteNoteIndex _index;
    private readonly SqliteAuditLog _audit;
    private readonly NoteWriter _writer;
    private readonly NoteReader _reader;

    public NoteWriterFixture()
    {
        _store = new SqliteSourceStore(_workspace.Database);
        _index = new SqliteNoteIndex(_workspace.Database);
        _audit = new SqliteAuditLog(_workspace.Database, _clock);
        var paths = _workspace.Paths();
        var options = Options.Create(new GitOptions { Hosts = ["github.example"], Credentials = { ["github"] = new GitCredentialOptions { Host = "github.example", Token = "secret-token" } } });
        var policy = new GitPolicy(options, new GitCredentials(options));
        var indexer = new SourceIndexer(_index, paths, new ScanOutcomes(), _clock, _locks);
        var browser = new SqliteNoteBrowser(_workspace.Database);
        var catalog = new SourceCatalog(_store, _audit, paths, _clock, policy);
        _writer = new NoteWriter(catalog, paths, _locks, indexer, new SourceSync(_engine, policy, indexer, paths, _locks, _syncs, _clock), browser, _audit);
        _reader = new NoteReader(_store, browser, paths);
    }

    public void Dispose() => _workspace.Dispose();

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    private Source Declare(string name, SourceMode mode = SourceMode.ReadWrite, bool git = false, params (string Path, string Text)[] notes) => Declare(name, SourceKind.Knowledge, mode, git, notes);

    private Source Declare(string name, SourceKind kind, SourceMode mode, bool git, params (string Path, string Text)[] notes)
    {
        var folder = _workspace.Folder(name);
        foreach (var (path, text) in notes)
        {
            TestWorkspace.Write(folder, path, text);
        }

        var source = new Source(Guid.NewGuid(), name, kind, mode, folder, _clock.GetUtcNow(), Repository: git ? new GitRepository("https://github.example/org/notes", "main", "github") : null);
        _store.Add(source);
        return source;
    }

    private async Task<Source> DeclareIndexedAsync(string name, SourceMode mode = SourceMode.ReadWrite, bool git = false, params (string Path, string Text)[] notes)
    {
        var source = Declare(name, mode, git, notes);
        var indexer = new SourceIndexer(_index, _workspace.Paths(), new ScanOutcomes(), _clock, _locks);
        await indexer.IndexAsync(source, Token);
        return source;
    }

    private string Read(Source source, string path) => File.ReadAllText(Path.Combine(source.Path, path));

    private List<string> Hits(string words) => [.. _index.Search(new SearchRequest(words), SourceScope.All).Select(hit => hit.Path)];

    [Fact]
    public async Task should_create_a_note_on_disk_and_in_the_index_at_once_and_audit_it_without_its_text()
    {
        var source = Declare("vault");

        var result = await _writer.CreateAsync(Alice, source.Id, "Topic/Docker.md", "# Docker\nImages are made of layers.\n", Token);

        Read(source, "Topic/Docker.md").Should().Be("# Docker\nImages are made of layers.\n");
        Hits("layers").Should().Equal("Topic/Docker.md");
        result.Version.Should().Be(NoteWriter.VersionOf(System.Text.Encoding.UTF8.GetBytes("# Docker\nImages are made of layers.\n")));
        result.Git.Should().BeNull();
        var events = _audit.Recent(10, "note.created");
        events.Should().ContainSingle().Which.Should().Match<AuditEvent>(e => e.Actor == "alice" && e.Target == "vault/Topic/Docker.md");
        events.Single().Detail.Should().NotContain("layers");
        Directory.EnumerateFiles(source.Path, "*", SearchOption.AllDirectories).Should().NotContain(file => file.EndsWith(".tmp", StringComparison.Ordinal), "a note is written whole, never through a file left behind");
    }

    [Fact]
    public async Task should_refuse_to_create_over_a_note_that_is_there()
    {
        var source = Declare("vault", notes: ("a.md", "# A"));

        var refusal = await Assert.ThrowsAsync<RejectedException>(() => _writer.CreateAsync(Alice, source.Id, "a.md", "# New", Token));

        refusal.Problem.Should().Be(Problem.Conflict);
        Read(source, "a.md").Should().Be("# A");
    }

    [Fact]
    public async Task should_replace_a_note_that_is_still_the_version_that_was_read_and_refuse_one_that_changed()
    {
        var source = await DeclareIndexedAsync("vault", notes: ("a.md", "# A\nold words\n"));
        var version = _reader.Read(source.Id, "a.md", SourceScope.All).Version;

        var updated = await _writer.UpdateAsync(Alice, source.Id, "a.md", "# A\nnew words\n", version, Token);
        var stale = await Assert.ThrowsAsync<RejectedException>(() => _writer.UpdateAsync(Alice, source.Id, "a.md", "# A\nlost update\n", version, Token));

        Read(source, "a.md").Should().Be("# A\nnew words\n");
        stale.Problem.Should().Be(Problem.Conflict);
        updated.Version.Should().NotBe(version);
        Hits("new").Should().Equal("a.md");
        Hits("old").Should().BeEmpty();
        _reader.Read(source.Id, "a.md", SourceScope.All).Version.Should().Be(updated.Version, "the version that comes back is the one the next read gives");
    }

    [Fact]
    public async Task should_not_update_a_note_that_does_not_exist()
    {
        var source = Declare("vault");

        var refusal = await Assert.ThrowsAsync<RejectedException>(() => _writer.UpdateAsync(Alice, source.Id, "nope.md", "x", "v", Token));

        refusal.Problem.Should().Be(Problem.NotFound);
    }

    [Fact]
    public async Task should_append_text_on_a_new_line_whatever_the_version_and_keep_the_front_matter()
    {
        var source = await DeclareIndexedAsync("vault", notes: ("a.md", "---\ntags: [x]\n---\n# A\nfirst"));

        await _writer.AppendAsync(Alice, source.Id, "a.md", "- second\n", Token);

        Read(source, "a.md").Should().Be("---\ntags: [x]\n---\n# A\nfirst\n- second\n");
        Hits("second").Should().Equal("a.md");
    }

    [Theory]
    [InlineData("../outside.md")]
    [InlineData("a/../b.md")]
    [InlineData(".hidden/x.md")]
    [InlineData(".x.md")]
    [InlineData("notes.txt")]
    [InlineData("noextension")]
    [InlineData("/absolute.md")]
    [InlineData("a\\b.md")]
    [InlineData("a//b.md")]
    [InlineData("")]
    [InlineData("a\0b.md")]
    public async Task should_refuse_a_path_that_is_not_a_plain_relative_markdown_path(string path)
    {
        var source = Declare("vault");

        var refusal = await Assert.ThrowsAsync<RejectedException>(() => _writer.CreateAsync(Alice, source.Id, path, "x", Token));

        refusal.Problem.Should().Be(Problem.Invalid);
        Directory.EnumerateFileSystemEntries(source.Path, "*", SearchOption.AllDirectories).Should().BeEmpty();
        Directory.EnumerateFiles(Path.GetDirectoryName(source.Path)!, "outside.md").Should().BeEmpty();
    }

    [Fact]
    public async Task should_refuse_a_path_that_goes_through_a_link_and_never_write_where_it_leads()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        var source = Declare("vault");
        var elsewhere = Directory.CreateDirectory(Path.Combine(_workspace.Root, "elsewhere")).FullName;
        Directory.CreateSymbolicLink(Path.Combine(source.Path, "linked"), elsewhere);

        var refusal = await Assert.ThrowsAsync<RejectedException>(() => _writer.CreateAsync(Alice, source.Id, "linked/x.md", "x", Token));

        refusal.Problem.Should().Be(Problem.Invalid);
        Directory.EnumerateFileSystemEntries(elsewhere).Should().BeEmpty();
    }

    [Theory]
    [InlineData("A note with a NUL \0 in it")]
    public async Task should_refuse_a_text_that_is_not_a_note(string content)
    {
        var source = Declare("vault");

        (await Assert.ThrowsAsync<RejectedException>(() => _writer.CreateAsync(Alice, source.Id, "a.md", content, Token))).Problem.Should().Be(Problem.Invalid);
        (await Assert.ThrowsAsync<RejectedException>(() => _writer.CreateAsync(Alice, source.Id, "b.md", new string('x', (int)MarkdownFileScanner.MaxFileBytes + 1), Token))).Problem.Should().Be(Problem.Invalid);
    }

    [Fact]
    public async Task should_not_write_in_a_source_that_is_read_only_and_treat_one_out_of_scope_as_absent()
    {
        var readOnly = Declare("ro", SourceMode.ReadOnly);
        var mine = Declare("mine");
        var scoped = new NoteActor(new SourceScope([mine.Id]), "key");

        var closed = await Assert.ThrowsAsync<RejectedException>(() => _writer.CreateAsync(Alice, readOnly.Id, "a.md", "x", Token));
        var hidden = await Assert.ThrowsAsync<RejectedException>(() => _writer.CreateAsync(scoped, readOnly.Id, "a.md", "x", Token));
        var missing = await Assert.ThrowsAsync<RejectedException>(() => _writer.CreateAsync(Alice, Guid.NewGuid(), "a.md", "x", Token));
        await _writer.CreateAsync(scoped, mine.Id, "a.md", "x", Token);

        closed.Problem.Should().Be(Problem.Forbidden);
        hidden.Problem.Should().Be(Problem.NotFound);
        missing.Problem.Should().Be(Problem.NotFound);
        Directory.EnumerateFiles(readOnly.Path).Should().BeEmpty();
    }

    [Fact]
    public async Task should_never_write_the_documentation_of_an_application_even_if_its_source_is_declared_read_write()
    {
        var docs = Declare("portarr", SourceKind.ApplicationDocs, SourceMode.ReadWrite, git: false);

        var refusal = await Assert.ThrowsAsync<RejectedException>(() => _writer.CreateAsync(Alice, docs.Id, "a.md", "x", Token));

        refusal.Problem.Should().Be(Problem.Forbidden);
        refusal.Message.Should().Contain("repository");
        Directory.EnumerateFiles(docs.Path).Should().BeEmpty();
    }

    [Fact]
    public async Task should_write_the_memory_vault_when_it_is_open_for_writing()
    {
        var memory = Declare("memory", SourceKind.Memory, SourceMode.ReadWrite, git: false);

        await _writer.CreateAsync(Alice, memory.Id, "decisions/one.md", "# One", Token);

        File.Exists(Path.Combine(memory.Path, "decisions/one.md")).Should().BeTrue();
    }

    [Fact]
    public async Task should_wait_for_the_lock_of_the_source_so_that_it_never_changes_a_folder_a_pass_or_a_sync_is_using()
    {
        var source = Declare("vault");
        var gate = _locks.Of(source.Id);
        await gate.WaitAsync(Token);

        var writing = _writer.CreateAsync(Alice, source.Id, "a.md", "x", Token);
        await Task.Delay(150, Token);
        var early = writing.IsCompleted;
        gate.Release();
        await writing;

        early.Should().BeFalse();
        File.Exists(Path.Combine(source.Path, "a.md")).Should().BeTrue();
        gate.CurrentCount.Should().Be(1);
    }

    [Fact]
    public async Task should_move_a_note_and_rewrite_the_links_that_lead_to_it_in_the_same_source_and_count_the_ones_it_leaves()
    {
        var courses = await DeclareIndexedAsync(
            "courses",
            notes:
            [
                ("devops/docker.md", "# Docker\nSee [sibling](sibling.md)."),
                ("devops/sibling.md", "# Sibling"),
                ("index.md", "# Index\n[[docker]] and [[devops/docker|the tool]] and [x](devops/docker.md#images)\n```\n[[docker]]\n```"),
                ("other.md", "# Other\n[[Docker]]"),
            ]);
        var work = await DeclareIndexedAsync("work", SourceMode.ReadOnly, notes: ("w.md", "# W\n[[docker]]"));

        var result = await _writer.RenameAsync(Alice, courses.Id, "devops/docker.md", "ops/engine.md", Token);

        File.Exists(Path.Combine(courses.Path, "devops/docker.md")).Should().BeFalse();
        Read(courses, "ops/engine.md").Should().Be("# Docker\nSee [sibling](../devops/sibling.md).", "the links of the moved note keep leading where they led");
        Read(courses, "index.md").Should().Be("# Index\n[[engine]] and [[ops/engine|the tool]] and [x](ops/engine.md#images)\n```\n[[docker]]\n```");
        Read(courses, "other.md").Should().Be("# Other\n[[engine]]");
        Read(work, "w.md").Should().Be("# W\n[[docker]]", "a note of another source is not changed");
        result.Should().Match<RenameResult>(r => r.Path == "ops/engine.md" && r.NotesRewritten == 2 && r.LinksRewritten == 4 && r.NotesNotRewritten == 1);
        Hits("Docker").Should().Contain("ops/engine.md").And.NotContain("devops/docker.md", "the index follows the move");
        _reader.Read(courses.Id, "ops/engine.md", SourceScope.All).Detail.Backlinks.Select(b => b.Path).Should().BeEquivalentTo(["index.md", "other.md"]);
        _audit.Recent(10, "note.renamed").Should().ContainSingle();
    }

    [Fact]
    public async Task should_not_leave_empty_folders_behind_when_a_note_moves_or_goes_but_keep_the_ones_that_hold_something()
    {
        var source = await DeclareIndexedAsync("vault", git: true, notes: [("a/b/one.md", "# One"), ("a/two.md", "# Two"), ("c/three.md", "# Three")]);

        await _writer.RenameAsync(Alice, source.Id, "a/b/one.md", "moved.md", Token);
        await _writer.DeleteAsync(Alice, source.Id, "c/three.md", null, Token);

        Directory.Exists(Path.Combine(source.Path, "a/b")).Should().BeFalse();
        Directory.Exists(Path.Combine(source.Path, "a")).Should().BeTrue("it still holds a note");
        Directory.Exists(Path.Combine(source.Path, "c")).Should().BeFalse();
        Directory.Exists(source.Path).Should().BeTrue("the folder of the source stays");
    }

    [Fact]
    public async Task should_refuse_to_move_a_note_over_another_or_to_a_path_that_is_not_one()
    {
        var source = await DeclareIndexedAsync("vault", notes: [("a.md", "# A"), ("b.md", "# B")]);

        var taken = await Assert.ThrowsAsync<RejectedException>(() => _writer.RenameAsync(Alice, source.Id, "a.md", "b.md", Token));
        var outside = await Assert.ThrowsAsync<RejectedException>(() => _writer.RenameAsync(Alice, source.Id, "a.md", "../b.md", Token));
        var missing = await Assert.ThrowsAsync<RejectedException>(() => _writer.RenameAsync(Alice, source.Id, "nope.md", "c.md", Token));

        taken.Problem.Should().Be(Problem.Conflict);
        outside.Problem.Should().Be(Problem.Invalid);
        missing.Problem.Should().Be(Problem.NotFound);
        Read(source, "a.md").Should().Be("# A");
    }

    [Fact]
    public async Task should_keep_a_removed_note_in_a_hidden_trash_folder_of_a_plain_source_and_forget_it_in_the_index()
    {
        var source = await DeclareIndexedAsync("vault", notes: ("Topic/a.md", "# A\nvanishing words"));
        var version = _reader.Read(source.Id, "Topic/a.md", SourceScope.All).Version;

        var stale = await Assert.ThrowsAsync<RejectedException>(() => _writer.DeleteAsync(Alice, source.Id, "Topic/a.md", "not-the-version", Token));
        await _writer.DeleteAsync(Alice, source.Id, "Topic/a.md", version, Token);

        stale.Problem.Should().Be(Problem.Conflict);
        File.Exists(Path.Combine(source.Path, "Topic/a.md")).Should().BeFalse();
        Hits("vanishing").Should().BeEmpty();
        var kept = Directory.EnumerateFiles(Path.Combine(source.Path, NoteWriter.TrashFolder), "a.md", SearchOption.AllDirectories).Should().ContainSingle().Subject;
        File.ReadAllText(kept).Should().Contain("vanishing words");
        MarkdownFileScanner.Scan(source.Path).Should().BeEmpty("the trash is hidden: nothing reads it back");
        _audit.Recent(10, "note.removed").Should().ContainSingle();
    }

    [Fact]
    public async Task should_commit_and_push_a_change_to_a_git_source_as_the_account_that_made_it()
    {
        var source = Declare("repo", git: true);

        var result = await _writer.CreateAsync(Alice, source.Id, "Topic/a.md", "# A", Token);

        result.Git.Should().Be(new GitPublish(true, null));
        _engine.Commits.Should().ContainSingle().Which.Should().Match<(string Folder, string[] Paths, string Message, GitAuthor Author)>(c =>
            c.Folder == source.Path && c.Paths.SequenceEqual(new[] { "Topic/a.md" }) && c.Message == "Add Topic/a.md" && c.Author.Name == "alice" && c.Author.Email == "mnemarr@localhost");
        _engine.Pushes.Should().Be(1);
    }

    [Fact]
    public async Task should_keep_the_note_and_the_commit_when_the_push_fails_and_say_why_in_fixed_words()
    {
        var source = Declare("repo", git: true);
        _engine.OnPush = () => throw new GitSyncException(GitFailure.PushRejected, "remote said: protected branch /srv/x");

        var result = await _writer.CreateAsync(Alice, source.Id, "a.md", "# A", Token);

        result.Git.Should().Be(new GitPublish(false, GitFailure.PushRejected));
        File.Exists(Path.Combine(source.Path, "a.md")).Should().BeTrue();
        _engine.Commits.Should().HaveCount(1);
        _syncs.Of(source.Id)!.Error.Should().Be(GitFailure.PushRejected);
    }

    [Fact]
    public async Task should_commit_every_path_a_rename_touched_in_one_commit_and_a_removal_by_its_own()
    {
        var source = await DeclareIndexedAsync("repo", git: true, notes: [("a.md", "# A"), ("index.md", "[[a]]")]);

        await _writer.RenameAsync(Alice, source.Id, "a.md", "b.md", Token);
        await _writer.DeleteAsync(Alice, source.Id, "b.md", null, Token);

        _engine.Commits.Select(c => c.Message).Should().Equal("Rename a.md to b.md", "Remove b.md");
        _engine.Commits[0].Paths.Should().BeEquivalentTo(["a.md", "b.md", "index.md"]);
        File.Exists(Path.Combine(source.Path, NoteWriter.TrashFolder)).Should().BeFalse("a Git source keeps what it removes in its history, not in a folder");
        Directory.Exists(Path.Combine(source.Path, NoteWriter.TrashFolder)).Should().BeFalse();
    }

    [Fact]
    public async Task should_bring_a_git_source_up_to_date_before_it_writes()
    {
        var source = Declare("repo", git: true);
        var order = new List<string>();
        _engine.During = () => order.Add("sync");
        _engine.OnPush = () => order.Add("push");

        await _writer.CreateAsync(Alice, source.Id, "a.md", "# A", Token);

        order.Should().Equal("sync", "push");
    }

    [Fact]
    public async Task should_refuse_to_write_in_a_checkout_that_has_diverged_instead_of_piling_commits_on_it()
    {
        var source = Declare("repo", git: true);
        _engine.Result = () => throw new GitSyncException(GitFailure.Diverged, "diverged: /srv/x");

        var write = () => _writer.CreateAsync(Alice, source.Id, "a.md", "# A", Token);

        (await write.Should().ThrowAsync<RejectedException>()).Which.Problem.Should().Be(Problem.Conflict);
        File.Exists(Path.Combine(source.Path, "a.md")).Should().BeFalse();
        _engine.Commits.Should().BeEmpty();
        _syncs.Of(source.Id)!.Error.Should().Be(GitFailure.Diverged);
    }

    [Fact]
    public async Task should_write_and_commit_locally_when_the_host_cannot_be_reached()
    {
        var source = Declare("repo", git: true);
        Directory.CreateDirectory(Path.Combine(source.Path, ".git"));
        _engine.Result = () => throw new GitSyncException(GitFailure.Unreachable, "no route to host");

        var result = await _writer.CreateAsync(Alice, source.Id, "a.md", "# A", Token);

        File.Exists(Path.Combine(source.Path, "a.md")).Should().BeTrue();
        _engine.Commits.Should().ContainSingle();
        result.Git.Should().NotBeNull();
    }

    [Fact]
    public async Task should_refuse_to_write_in_a_git_source_that_was_never_cloned()
    {
        var source = Declare("repo", git: true);
        _engine.Result = () => throw new GitSyncException(GitFailure.Unreachable, "no route to host");

        var write = () => _writer.CreateAsync(Alice, source.Id, "a.md", "# A", Token);

        (await write.Should().ThrowAsync<RejectedException>()).Which.Problem.Should().Be(Problem.Conflict);
        File.Exists(Path.Combine(source.Path, "a.md")).Should().BeFalse("a note in the empty folder would stop the first clone for good");
    }

    [Fact]
    public async Task should_read_what_a_pull_before_the_write_brought_in()
    {
        var source = Declare("repo", git: true);
        _engine.Result = () =>
        {
            TestWorkspace.Write(source.Path, "from-remote.md", "# From remote\nuniqueremoteword\n");
            return new GitSyncResult(GitSyncAction.Updated, "def456");
        };

        await _writer.CreateAsync(Alice, source.Id, "a.md", "# A", Token);

        Hits("uniqueremoteword").Should().Equal("from-remote.md");
    }

    [Fact]
    public async Task should_say_in_fixed_words_when_the_push_fails_in_a_way_that_has_no_code()
    {
        var source = Declare("repo", git: true);
        _engine.OnPush = () => throw new InvalidOperationException("/srv/secret/path exploded");

        var result = await _writer.CreateAsync(Alice, source.Id, "a.md", "# A", Token);

        result.Git.Should().Be(new GitPublish(false, GitFailure.Failed));
        File.Exists(Path.Combine(source.Path, "a.md")).Should().BeTrue();
    }

    [Fact]
    public async Task should_keep_the_line_endings_of_a_note_written_with_crlf_when_the_editor_sends_lf()
    {
        var source = await DeclareIndexedAsync("vault", notes: ("a.md", "# A\r\nold\r\n"));
        var version = NoteWriter.VersionOf(File.ReadAllBytes(Path.Combine(source.Path, "a.md")));

        await _writer.UpdateAsync(Alice, source.Id, "a.md", "# A\nnew\n", version, Token);

        File.ReadAllText(Path.Combine(source.Path, "a.md")).Should().Be("# A\r\nnew\r\n");
    }

    [Fact]
    public async Task should_rewrite_every_link_of_a_rename_even_past_the_two_hundred_a_note_shows()
    {
        var notes = Enumerable.Range(0, 230).Select(i => ($"n{i:000}.md", "# N\n[[hub]]")).Append(("hub.md", "# Hub")).ToArray();
        var source = await DeclareIndexedAsync("vault", notes: notes);

        var result = await _writer.RenameAsync(Alice, source.Id, "hub.md", "centre.md", Token);

        result.NotesRewritten.Should().Be(230);
        Read(source, "n229.md").Should().Be("# N\n[[centre]]");
    }
}

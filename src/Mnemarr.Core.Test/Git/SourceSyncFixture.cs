using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using Mnemarr.Core.Git;
using Mnemarr.Core.Indexing;
using Mnemarr.Core.Sources;

namespace Mnemarr.Core.Test.Git;

public sealed class SourceSyncFixture : IDisposable
{
    private readonly TestWorkspace _workspace = new();
    private readonly FakeTimeProvider _clock = new(new DateTimeOffset(2026, 11, 4, 9, 0, 0, TimeSpan.Zero));
    private readonly FakeGitEngine _engine = new();
    private readonly SyncOutcomes _outcomes = new();
    private readonly SourceLocks _locks = new();
    private readonly SqliteNoteIndex _index;
    private readonly SourceSync _subject;
    private readonly GitOptions _options = new() { Credentials = { ["github"] = new GitCredentialOptions { Host = "github.example", Token = "secret-token" } } };

    public SourceSyncFixture()
    {
        _index = new SqliteNoteIndex(_workspace.Database);
        var paths = _workspace.Paths();
        var indexer = new SourceIndexer(_index, paths, new ScanOutcomes(), _clock, _locks);
        _subject = new SourceSync(_engine, new GitCredentials(Options.Create(_options)), indexer, paths, _locks, _outcomes, _clock);
    }

    public void Dispose() => _workspace.Dispose();

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    private Source Git(string folder, string url = "https://github.example/org/notes", string? branch = "main", string? credential = "github", string name = "notes")
    {
        var source = new Source(Guid.NewGuid(), name, SourceKind.Knowledge, SourceMode.ReadOnly, folder, _clock.GetUtcNow(), Repository: new GitRepository(url, branch, credential));
        new SqliteSourceStore(_workspace.Database).Add(source);
        return source;
    }

    [Fact]
    public async Task should_sync_with_the_secret_of_the_credential_for_its_host_and_read_what_came_in()
    {
        var folder = _workspace.Folder("notes");
        TestWorkspace.Write(folder, "a.md", "# Alpha\nWords about docker.\n");
        var source = Git(folder);
        _engine.Result = () => new GitSyncResult(GitSyncAction.Updated, "def456");

        var report = await _subject.SyncAsync(source, waitForTurn: false, Token);

        report.Action.Should().Be(GitSyncAction.Updated);
        report.Head.Should().Be("def456");
        report.Index.Should().Be(new IndexReport(Added: 1, Updated: 0, Removed: 0, Unchanged: 0));
        _engine.Calls.Should().ContainSingle().Which.Should().Be(("https://github.example/org/notes", "main", folder, new GitSecret("x-access-token", "secret-token")));
        _outcomes.Of(source.Id).Should().Be(new SyncOutcome(_clock.GetUtcNow(), null, "def456"));
    }

    [Fact]
    public async Task should_not_read_the_notes_again_when_nothing_was_new()
    {
        var folder = _workspace.Folder("notes");
        TestWorkspace.Write(folder, "a.md", "# Alpha\n");
        var source = Git(folder);

        var report = await _subject.SyncAsync(source, waitForTurn: false, Token);

        report.Action.Should().Be(GitSyncAction.UpToDate);
        report.Index.Should().BeNull();
        _index.Counts(source.Id).Notes.Should().Be(0);
    }

    [Fact]
    public async Task should_sync_a_public_repository_without_a_secret()
    {
        var source = Git(_workspace.Folder("notes"), credential: null);

        await _subject.SyncAsync(source, waitForTurn: false, Token);

        _engine.Calls.Single().Secret.Should().BeNull();
    }

    [Fact]
    public async Task should_refuse_a_source_that_is_a_folder()
    {
        var plain = new Source(Guid.NewGuid(), "plain", SourceKind.Knowledge, SourceMode.ReadOnly, _workspace.Folder("plain"), _clock.GetUtcNow());

        var refusal = await Assert.ThrowsAsync<RejectedException>(() => _subject.SyncAsync(plain, waitForTurn: false, Token));

        refusal.Problem.Should().Be(Problem.Invalid);
        _engine.Calls.Should().BeEmpty();
    }

    [Theory]
    [InlineData(GitFailure.AuthFailed)]
    [InlineData(GitFailure.Unreachable)]
    [InlineData(GitFailure.Diverged)]
    [InlineData(GitFailure.LocalChanges)]
    [InlineData(GitFailure.BranchNotFound)]
    [InlineData(GitFailure.RepositoryNotFound)]
    [InlineData(GitFailure.FolderNotEmpty)]
    [InlineData(GitFailure.DifferentRepository)]
    [InlineData(GitFailure.Failed)]
    public async Task should_record_the_code_of_a_failure_and_answer_a_conflict_in_fixed_words(string code)
    {
        var source = Git(_workspace.Folder("notes"));
        _engine.Result = () => throw new GitSyncException(code, "detail from the engine that must not be shown: /srv/secret/path https://github.example/org/notes");

        var refusal = await Assert.ThrowsAsync<RejectedException>(() => _subject.SyncAsync(source, waitForTurn: false, Token));

        refusal.Problem.Should().Be(Problem.Conflict);
        refusal.Message.Should().Be(SourceSync.MessageOf(code)).And.NotContain("/srv").And.NotContain("github.example");
        _outcomes.Of(source.Id).Should().Be(new SyncOutcome(_clock.GetUtcNow(), code, null));
    }

    [Fact]
    public async Task should_keep_the_last_commit_when_a_later_sync_fails()
    {
        var source = Git(_workspace.Folder("notes"));
        _engine.Result = () => new GitSyncResult(GitSyncAction.UpToDate, "abc123");
        await _subject.SyncAsync(source, waitForTurn: false, Token);
        _engine.Result = () => throw new GitSyncException(GitFailure.Unreachable, "down");

        await Assert.ThrowsAsync<RejectedException>(() => _subject.SyncAsync(source, waitForTurn: false, Token));

        _outcomes.Of(source.Id)!.Head.Should().Be("abc123");
        _outcomes.Of(source.Id)!.Failed.Should().BeTrue();
    }

    [Fact]
    public async Task should_record_a_disk_that_refuses_as_a_failure_without_its_path()
    {
        var source = Git(_workspace.Folder("notes"));
        _engine.Result = () => throw new UnauthorizedAccessException("/srv/secret/path is read-only");

        var refusal = await Assert.ThrowsAsync<RejectedException>(() => _subject.SyncAsync(source, waitForTurn: false, Token));

        refusal.Message.Should().NotContain("/srv");
        _outcomes.Of(source.Id)!.Error.Should().Be(GitFailure.Failed);
    }

    [Fact]
    public async Task should_not_send_a_credential_to_a_host_that_is_not_its_own_even_if_a_row_says_so()
    {
        var source = Git(_workspace.Folder("notes"), url: "https://elsewhere.example/org/notes");

        await Assert.ThrowsAsync<RejectedException>(() => _subject.SyncAsync(source, waitForTurn: false, Token));

        _engine.Calls.Should().BeEmpty("the secret of a credential is for the host it is bound to, whatever the row says");
        _outcomes.Of(source.Id)!.Error.Should().Be(GitFailure.CredentialMissing);
    }

    [Fact]
    public async Task should_say_credential_missing_when_the_credential_was_removed_from_the_settings()
    {
        var source = Git(_workspace.Folder("notes"));
        _options.Credentials.Clear();

        await Assert.ThrowsAsync<RejectedException>(() => _subject.SyncAsync(source, waitForTurn: false, Token));

        _outcomes.Of(source.Id)!.Error.Should().Be(GitFailure.CredentialMissing);
    }

    [Fact]
    public async Task should_refuse_a_folder_that_is_outside_the_roots_now_or_an_address_that_is_not_https()
    {
        var outside = Git(Path.Combine(Path.GetTempPath(), "outside-of-the-roots"));
        var plain = Git(_workspace.Folder("notes"), url: "http://github.example/org/notes", name: "plain");

        await Assert.ThrowsAsync<RejectedException>(() => _subject.SyncAsync(outside, waitForTurn: false, Token));
        await Assert.ThrowsAsync<RejectedException>(() => _subject.SyncAsync(plain, waitForTurn: false, Token));

        _engine.Calls.Should().BeEmpty();
        _outcomes.Of(outside.Id)!.Error.Should().Be(GitFailure.OutsideRoots);
        _outcomes.Of(plain.Id)!.Error.Should().Be(GitFailure.OutsideRoots);
    }

    [Fact]
    public async Task should_answer_busy_while_a_sync_or_a_pass_of_the_same_source_runs_and_work_again_after()
    {
        var source = Git(_workspace.Folder("notes"));
        using var held = new SemaphoreSlim(0);
        using var release = new SemaphoreSlim(0);
        _engine.During = () =>
        {
            held.Release();
            release.Wait(TimeSpan.FromSeconds(10));
        };
        var first = _subject.SyncAsync(source, waitForTurn: false, Token);
        (await held.WaitAsync(TimeSpan.FromSeconds(10), Token)).Should().BeTrue();

        var refusal = await Assert.ThrowsAsync<RejectedException>(() => _subject.SyncAsync(source, waitForTurn: false, Token));
        release.Release();
        await first;
        _engine.During = null;
        var again = await _subject.SyncAsync(source, waitForTurn: false, Token);

        refusal.Problem.Should().Be(Problem.Busy);
        again.Action.Should().Be(GitSyncAction.UpToDate);
    }

    [Fact]
    public async Task should_hold_the_lock_of_the_source_while_the_checkout_changes_so_that_a_pass_cannot_read_it()
    {
        var source = Git(_workspace.Folder("notes"));
        var lockedDuring = false;
        _engine.During = () => lockedDuring = _locks.Of(source.Id).CurrentCount == 0;

        await _subject.SyncAsync(source, waitForTurn: false, Token);

        lockedDuring.Should().BeTrue();
        _locks.Of(source.Id).CurrentCount.Should().Be(1);
    }

    [Fact]
    public async Task should_release_the_lock_when_the_sync_fails()
    {
        var source = Git(_workspace.Folder("notes"));
        _engine.Result = () => throw new GitSyncException(GitFailure.Unreachable, "down");

        await Assert.ThrowsAsync<RejectedException>(() => _subject.SyncAsync(source, waitForTurn: false, Token));

        _locks.Of(source.Id).CurrentCount.Should().Be(1);
    }

    [Theory]
    [InlineData("an-unknown-code")]
    public void should_have_words_for_every_code_and_a_default(string unknown)
    {
        SourceSync.MessageOf(unknown).Should().Be("The repository could not be synchronised.");
        foreach (var code in new[] { GitFailure.AuthFailed, GitFailure.RepositoryNotFound, GitFailure.Unreachable, GitFailure.BranchNotFound, GitFailure.Diverged, GitFailure.LocalChanges, GitFailure.FolderNotEmpty, GitFailure.DifferentRepository, GitFailure.CredentialMissing, GitFailure.OutsideRoots })
        {
            SourceSync.MessageOf(code).Should().NotBe(SourceSync.MessageOf(unknown), code);
        }
    }
}

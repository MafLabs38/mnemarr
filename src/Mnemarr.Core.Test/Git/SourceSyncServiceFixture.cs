using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using Mnemarr.Core.Git;
using Mnemarr.Core.Indexing;
using Mnemarr.Core.Sources;

namespace Mnemarr.Core.Test.Git;

public sealed class SourceSyncServiceFixture : IDisposable
{
    private readonly TestWorkspace _workspace = new();
    private readonly FakeTimeProvider _clock = new(new DateTimeOffset(2026, 11, 4, 9, 0, 0, TimeSpan.Zero));
    private readonly FakeGitEngine _engine = new();
    private readonly SyncOutcomes _outcomes = new();
    private readonly SqliteSourceStore _sources;
    private readonly SourceSyncService _subject;
    private readonly List<string> _logged = [];

    public SourceSyncServiceFixture()
    {
        _sources = new SqliteSourceStore(_workspace.Database);
        var locks = new SourceLocks();
        var paths = _workspace.Paths();
        var indexer = new SourceIndexer(new SqliteNoteIndex(_workspace.Database), paths, new ScanOutcomes(), _clock, locks);
        var options = Options.Create(new GitOptions { Hosts = ["github.example"] });
        var sync = new SourceSync(_engine, new GitPolicy(options, new GitCredentials(options)), indexer, paths, locks, _outcomes, _clock);
        _subject = new SourceSyncService(_sources, sync, _outcomes, options, _clock, new RecordingLogger(_logged));
    }

    public void Dispose() => _workspace.Dispose();

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    private Source Declare(string name, bool git)
    {
        var folder = _workspace.Folder(name);
        var source = new Source(Guid.NewGuid(), name, SourceKind.Knowledge, SourceMode.ReadOnly, folder, _clock.GetUtcNow(), Repository: git ? new GitRepository($"https://github.example/org/{name}") : null);
        _sources.Add(source);
        return source;
    }

    [Fact]
    public async Task should_sync_every_git_source_and_leave_the_folders_alone()
    {
        var one = Declare("one", git: true);
        var two = Declare("two", git: true);
        var folder = Declare("plain", git: false);

        await _subject.SyncAllAsync(Token);

        _engine.Calls.Select(call => call.Folder).Should().BeEquivalentTo([one.Path, two.Path]);
        _outcomes.Of(one.Id).Should().NotBeNull();
        _outcomes.Of(folder.Id).Should().BeNull();
    }

    [Fact]
    public async Task should_go_on_with_the_other_sources_when_one_fails_and_log_a_failure_once_until_it_changes()
    {
        var down = Declare("down", git: true);
        var fine = Declare("fine", git: true);
        _engine.Result = () => _engine.Calls[^1].Folder == down.Path ? throw new GitSyncException(GitFailure.Unreachable, "down") : new GitSyncResult(GitSyncAction.UpToDate, "abc");

        await _subject.SyncAllAsync(Token);
        await _subject.SyncAllAsync(Token);
        _engine.Result = () => _engine.Calls[^1].Folder == down.Path ? throw new GitSyncException(GitFailure.AuthFailed, "revoked") : new GitSyncResult(GitSyncAction.UpToDate, "abc");
        await _subject.SyncAllAsync(Token);

        _outcomes.Of(fine.Id)!.Failed.Should().BeFalse();
        _outcomes.Of(down.Id)!.Error.Should().Be(GitFailure.AuthFailed);
        _logged.Should().HaveCount(2).And.OnlyContain(line => line.Contains("down"));
        _logged.Should().Contain(line => line.Contains(GitFailure.Unreachable)).And.Contain(line => line.Contains(GitFailure.AuthFailed));
    }

    [Fact]
    public async Task should_log_again_when_a_source_that_recovered_fails_again()
    {
        Declare("flaky", git: true);
        var failing = true;
        _engine.Result = () => failing ? throw new GitSyncException(GitFailure.Unreachable, "down") : new GitSyncResult(GitSyncAction.UpToDate, "abc");

        await _subject.SyncAllAsync(Token);
        failing = false;
        await _subject.SyncAllAsync(Token);
        failing = true;
        await _subject.SyncAllAsync(Token);

        _logged.Should().HaveCount(2);
    }

    [Fact]
    public async Task should_drop_the_outcome_of_a_source_that_was_forgotten()
    {
        var source = Declare("gone", git: true);
        await _subject.SyncAllAsync(Token);
        _sources.Remove(source.Id);

        await _subject.SyncAllAsync(Token);

        _outcomes.Of(source.Id).Should().BeNull();
    }

    [Fact]
    public async Task should_sync_at_start_then_at_each_interval_and_stop_when_the_host_stops()
    {
        Declare("loop", git: true);

        await _subject.StartAsync(Token);
        await WaitUntilAsync(() => _engine.Calls.Count >= 1);
        _clock.Advance(TimeSpan.FromSeconds(299));
        await Task.Delay(200, Token);
        var beforeTheInterval = _engine.Calls.Count;
        _clock.Advance(TimeSpan.FromSeconds(2));
        await WaitUntilAsync(() => _engine.Calls.Count >= 2);
        await _subject.StopAsync(Token);

        beforeTheInterval.Should().Be(1, "nothing is synchronised before the interval has passed");
    }

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (!condition() && DateTime.UtcNow < deadline)
        {
            await Task.Delay(20, Token);
        }

        condition().Should().BeTrue("the service should have synchronised by now");
    }

    private sealed class RecordingLogger(List<string> lines) : ILogger<SourceSyncService>
    {
        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            lock (lines)
            {
                lines.Add(formatter(state, exception));
            }
        }
    }
}

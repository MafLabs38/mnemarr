using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using Mnemarr.Core.Indexing;
using Mnemarr.Core.Sources;

namespace Mnemarr.Core.Test.Indexing;

public sealed class SourceIndexingServiceFixture : IDisposable
{
    private readonly TestWorkspace _workspace = new();
    private readonly FakeTimeProvider _clock = new(new DateTimeOffset(2026, 11, 3, 9, 0, 0, TimeSpan.Zero));
    private readonly SqliteNoteIndex _index;
    private readonly ScanOutcomes _outcomes = new();
    private readonly SqliteSourceStore _sources;
    private readonly SourceIndexingService _subject;

    public SourceIndexingServiceFixture()
    {
        _index = new SqliteNoteIndex(_workspace.Database);
        _sources = new SqliteSourceStore(_workspace.Database);
        _subject = new SourceIndexingService(
            _sources, new SourceIndexer(_index, _workspace.Paths(), _outcomes, _clock), Options.Create(new IndexingOptions { ScanIntervalSeconds = 30 }), _clock,
            NullLogger<SourceIndexingService>.Instance);
    }

    public void Dispose() => _workspace.Dispose();

    private Source Declare(string name, string folder)
    {
        var source = new Source(Guid.NewGuid(), name, SourceKind.Knowledge, SourceMode.ReadWrite, folder, _clock.GetUtcNow());
        _sources.Add(source);
        return source;
    }

    [Fact]
    public async Task should_index_every_source_and_go_on_after_one_that_fails()
    {
        var healthy = _workspace.Folder("healthy");
        TestWorkspace.Write(healthy, "a.md", "# A\nhello");
        var broken = Declare("a-broken", Path.Combine(_workspace.SourcesFolder, "gone"));
        var source = Declare("b-healthy", healthy);

        await _subject.ScanAllAsync(TestContext.Current.CancellationToken);

        _index.Counts(source.Id).Notes.Should().Be(1);
        _outcomes.Of(broken.Id)!.Error.Should().Be(ScanOutcome.FolderMissing);
        _outcomes.Of(source.Id)!.Failed.Should().BeFalse();
    }

    [Fact]
    public async Task should_scan_at_start_then_again_when_the_interval_has_passed_and_stop_when_asked()
    {
        var folder = _workspace.Folder("courses");
        TestWorkspace.Write(folder, "a.md", "# A\nfirst");
        var source = Declare("courses", folder);
        var token = TestContext.Current.CancellationToken;

        await _subject.StartAsync(token);
        await AdvanceUntil(() => _index.Counts(source.Id).Notes == 1, token);

        TestWorkspace.Write(folder, "b.md", "# B\nsecond");
        await AdvanceUntil(() => _index.Counts(source.Id).Notes == 2, token);

        await _subject.StopAsync(token);
        _subject.ExecuteTask!.IsCompletedSuccessfully.Should().BeTrue();
    }

    /// <summary>
    /// Lets the scan interval pass again and again until the condition holds. Whether the service had already waited for its timer when the
    /// clock moved does not matter: a moment that is missed is made up at the next advance, and an extra pass finds nothing to do.
    /// </summary>
    private async Task AdvanceUntil(Func<bool> condition, CancellationToken token)
    {
        using var limit = CancellationTokenSource.CreateLinkedTokenSource(token);
        limit.CancelAfter(TimeSpan.FromSeconds(30));
        while (!condition())
        {
            _clock.Advance(TimeSpan.FromSeconds(30));
            await Task.Delay(10, limit.Token);
        }
    }

    [Fact]
    public async Task should_stay_up_and_do_nothing_while_the_database_cannot_be_reached()
    {
        var gone = new Mnemarr.Core.Storage.Database(Options.Create(new Mnemarr.Core.Configuration.MnemarrOptions { DataFolder = Path.Combine(_workspace.Root, "gone") }));
        var subject = new SourceIndexingService(
            new SqliteSourceStore(gone), new SourceIndexer(new SqliteNoteIndex(gone), _workspace.Paths(), _outcomes, _clock), Options.Create(new IndexingOptions()), _clock,
            NullLogger<SourceIndexingService>.Instance);

        var scan = () => subject.ScanAllAsync(TestContext.Current.CancellationToken);

        await scan.Should().NotThrowAsync();
    }
}

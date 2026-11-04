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
    private readonly SqliteSourceStore _sources;
    private readonly SourceIndexingService _subject;

    public SourceIndexingServiceFixture()
    {
        _index = new SqliteNoteIndex(_workspace.Database);
        _sources = new SqliteSourceStore(_workspace.Database);
        _subject = new SourceIndexingService(
            _sources, new SourceIndexer(_index, _clock), Options.Create(new IndexingOptions { ScanIntervalSeconds = 30 }), _clock,
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
        Declare("a-broken", Path.Combine(_workspace.SourcesFolder, "gone"));
        var source = Declare("b-healthy", healthy);

        await _subject.ScanAllAsync(TestContext.Current.CancellationToken);

        _index.Counts(source.Id).Notes.Should().Be(1);
    }

    [Fact]
    public async Task should_scan_at_start_then_again_when_the_interval_has_passed_and_stop_when_asked()
    {
        var folder = _workspace.Folder("cours");
        TestWorkspace.Write(folder, "a.md", "# A\nfirst");
        var source = Declare("cours", folder);
        var token = TestContext.Current.CancellationToken;

        await _subject.StartAsync(token);
        await WaitUntil(() => _index.Counts(source.Id).Notes == 1, token);

        TestWorkspace.Write(folder, "b.md", "# B\nsecond");
        await WaitUntil(() => _subject.ExecuteTask is { IsCompleted: false }, token);
        _clock.Advance(TimeSpan.FromSeconds(30));
        await WaitUntil(() => _index.Counts(source.Id).Notes == 2, token);

        await _subject.StopAsync(token);
        _subject.ExecuteTask!.IsCompletedSuccessfully.Should().BeTrue();
    }

    private static async Task WaitUntil(Func<bool> condition, CancellationToken token)
    {
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (!condition())
        {
            if (DateTime.UtcNow > deadline)
            {
                throw new TimeoutException("The condition was not reached in 10 seconds.");
            }

            await Task.Delay(20, token);
        }
    }

    [Fact]
    public async Task should_stay_up_and_do_nothing_while_the_database_cannot_be_reached()
    {
        var gone = new Mnemarr.Core.Storage.Database(Options.Create(new Mnemarr.Core.Configuration.MnemarrOptions { DataFolder = Path.Combine(_workspace.Root, "gone") }));
        var subject = new SourceIndexingService(
            new SqliteSourceStore(gone), new SourceIndexer(new SqliteNoteIndex(gone), _clock), Options.Create(new IndexingOptions()), _clock,
            NullLogger<SourceIndexingService>.Instance);

        var scan = () => subject.ScanAllAsync(TestContext.Current.CancellationToken);

        await scan.Should().NotThrowAsync();
    }
}

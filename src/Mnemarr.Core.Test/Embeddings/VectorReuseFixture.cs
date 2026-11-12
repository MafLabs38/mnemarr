using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using Mnemarr.Core.Embeddings;
using Mnemarr.Core.Indexing;
using Mnemarr.Core.Sources;
using Mnemarr.Core.Storage;

namespace Mnemarr.Core.Test.Embeddings;

/// <summary>
/// The model only ever reads a text once (ADR 0039): a passage that was computed is given its vector again, whichever note it comes back in, and a conversation that grows
/// costs the passages it adds. The store is the real <c>sqlite-vec</c> library (<c>MNEMARR_TEST_VECTOR_LIBRARY</c>); the model is a counter.
/// </summary>
public sealed class VectorReuseFixture : IDisposable
{
    private static readonly string? Library = Environment.GetEnvironmentVariable("MNEMARR_TEST_VECTOR_LIBRARY");

    private readonly TestWorkspace _workspace;
    private readonly FakeTimeProvider _clock = new(new DateTimeOffset(2026, 11, 12, 9, 0, 0, TimeSpan.Zero));
    private readonly EmbeddingWakeUp _wake = new();
    private readonly SqliteNoteIndex _index;
    private readonly SourceIndexer _indexer;
    private readonly CountingEmbeddings _provider = new();
    private readonly SqliteVectorStore _store;
    private readonly EmbeddingIndexer _embedder;
    private readonly Source _source;
    private readonly string _folder;

    public VectorReuseFixture()
    {
        Assert.SkipUnless(Library is not null, "No sqlite-vec library given.");
        _workspace = new TestWorkspace();
        var options = new EmbeddingOptions { ModelFolder = "unused", ExtensionPath = Library, BatchSize = 4, CacheDays = 30 };
        _index = new SqliteNoteIndex(_workspace.Database, _wake);
        _indexer = new SourceIndexer(_index, _workspace.Paths(), new ScanOutcomes(), _clock);
        _store = new SqliteVectorStore(_workspace.Database, Options.Create(options), _clock);
        _embedder = new EmbeddingIndexer(_provider, _store, Options.Create(options), NullLogger<EmbeddingIndexer>.Instance, _wake);
        _folder = _workspace.Folder("notes");
        _source = new Source(Guid.NewGuid(), "notes", SourceKind.Knowledge, SourceMode.ReadWrite, _folder, _clock.GetUtcNow());
        new SqliteSourceStore(_workspace.Database).Add(_source);
    }

    public void Dispose() => _workspace.Dispose();

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    private async Task ScanAsync() => await _indexer.IndexAsync(_source, Token);

    private async Task<int> ComputeAsync()
    {
        var before = _provider.Computed;
        await _embedder.PassAsync(tidy: true, Token);
        return _provider.Computed - before;
    }

    [Fact]
    public async Task should_compute_a_text_once_when_it_is_in_two_notes()
    {
        TestWorkspace.Write(_folder, "a.md", "# Same\nthe same passage about docker");
        TestWorkspace.Write(_folder, "b.md", "# Same\nthe same passage about docker");
        await ScanAsync();

        var computed = await ComputeAsync();

        computed.Should().Be(1);
        _store.Progress(_provider.ModelId).Should().Be(new EmbeddingProgress(2, 2));
    }

    [Fact]
    public async Task should_not_compute_again_a_note_that_was_renamed_or_moved()
    {
        TestWorkspace.Write(_folder, "a.md", "# Backup\nthe nightly copy of the data");
        await ScanAsync();
        (await ComputeAsync()).Should().Be(1);

        File.Delete(Path.Combine(_folder, "a.md"));
        TestWorkspace.Write(_folder, "moved/b.md", "# Backup\nthe nightly copy of the data");
        await ScanAsync();

        (await ComputeAsync()).Should().Be(0);
        _store.Progress(_provider.ModelId).Should().Be(new EmbeddingProgress(1, 1));
    }

    [Fact]
    public async Task should_not_compute_again_a_note_that_was_removed_and_comes_back()
    {
        TestWorkspace.Write(_folder, "a.md", "# Backup\nthe nightly copy of the data");
        await ScanAsync();
        await ComputeAsync();
        File.Delete(Path.Combine(_folder, "a.md"));
        await ScanAsync();
        await ComputeAsync();

        TestWorkspace.Write(_folder, "a.md", "# Backup\nthe nightly copy of the data");
        await ScanAsync();

        (await ComputeAsync()).Should().Be(0);
    }

    [Fact]
    public async Task should_compute_only_what_a_conversation_adds_as_it_grows()
    {
        var text = "# Chat\n\n";
        const int turns = 40;
        for (var turn = 0; turn < turns; turn++)
        {
            text += $"## Turn {turn}\n\nsomething said at step {turn} about subject {turn * 7}\n\n";
            TestWorkspace.Write(_folder, "chat.md", text);
            await _indexer.IndexNoteAsync(_source, "chat.md", Token);
            await ComputeAsync();
        }

        var chunks = _store.Progress(_provider.ModelId);
        chunks.Embedded.Should().Be(chunks.Chunks);
        _provider.Computed.Should().BeLessThanOrEqualTo(chunks.Chunks + 2, "each step computes its new passage and nothing it computed before");
    }

    [Fact]
    public async Task should_compute_a_note_again_when_its_title_changes_and_not_when_it_changes_back()
    {
        TestWorkspace.Write(_folder, "a.md", "# First title\nsome passage about docker");
        await ScanAsync();
        await ComputeAsync();

        TestWorkspace.Write(_folder, "a.md", "# Second title\nsome passage about docker");
        await ScanAsync();
        (await ComputeAsync()).Should().Be(1, "the model reads the title with the passage");

        TestWorkspace.Write(_folder, "a.md", "# First title\nsome passage about docker");
        await ScanAsync();
        (await ComputeAsync()).Should().Be(0);
        _store.Progress(_provider.ModelId).Should().Be(new EmbeddingProgress(1, 1));
    }

    [Fact]
    public async Task should_forget_a_computed_vector_that_was_not_used_for_a_while()
    {
        TestWorkspace.Write(_folder, "a.md", "# Backup\nthe nightly copy of the data");
        await ScanAsync();
        await ComputeAsync();
        File.Delete(Path.Combine(_folder, "a.md"));
        await ScanAsync();
        await ComputeAsync();

        _clock.Advance(TimeSpan.FromDays(31));
        await ComputeAsync();
        TestWorkspace.Write(_folder, "a.md", "# Backup\nthe nightly copy of the data");
        await ScanAsync();

        (await ComputeAsync()).Should().Be(1);
    }

    [Fact]
    public async Task should_wake_the_indexer_when_the_index_has_work_and_when_chunks_went()
    {
        TestWorkspace.Write(_folder, "a.md", "# A\nsome passage");
        await ScanAsync();
        (await _wake.WaitAsync(TimeSpan.FromSeconds(5), Token)).Should().BeTrue("a new chunk tells the indexer");
        (await _wake.WaitAsync(TimeSpan.FromMilliseconds(50), Token)).Should().BeFalse("nothing else was said");

        File.Delete(Path.Combine(_folder, "a.md"));
        await ScanAsync();

        (await _wake.WaitAsync(TimeSpan.FromSeconds(5), Token)).Should().BeTrue("a removal tells it too");
        _wake.TakeRemoved().Should().BeTrue();
        _wake.TakeRemoved().Should().BeFalse();
    }

    [Fact]
    public async Task should_tell_once_for_many_changes_before_the_indexer_looks()
    {
        var wake = new EmbeddingWakeUp();
        wake.Notify();
        wake.Notify();
        wake.Notify();

        (await wake.WaitAsync(TimeSpan.FromSeconds(5), Token)).Should().BeTrue();
        (await wake.WaitAsync(TimeSpan.FromMilliseconds(50), Token)).Should().BeFalse();
        wake.TakeRemoved().Should().BeFalse();
    }

    [Fact]
    public async Task should_move_past_a_window_of_known_texts_and_compute_the_one_that_is_new()
    {
        string Sections(int count) => "# Moved\n\n" + string.Concat(Enumerable.Range(0, count).Select(index => $"## Part {index}\n\nthe text of part {index} about subject {index * 11}\n\n"));
        TestWorkspace.Write(_folder, "a.md", Sections(10));
        await ScanAsync();
        var first = await ComputeAsync();
        first.Should().BeGreaterThan(4, "more than one window of the batch size");

        File.Delete(Path.Combine(_folder, "a.md"));
        TestWorkspace.Write(_folder, "moved/b.md", Sections(10) + "## Added\n\na passage the model has not read\n\n");
        await ScanAsync();

        (await ComputeAsync()).Should().Be(1, "only the new passage is read, though the known ones fill more than a window");
        var progress = _store.Progress(_provider.ModelId);
        progress.Embedded.Should().Be(progress.Chunks);
    }

    [Fact]
    public async Task should_keep_a_vector_that_is_used_from_expiring_while_it_is_in_use()
    {
        TestWorkspace.Write(_folder, "a.md", "# Backup\nthe nightly copy of the data");
        await ScanAsync();
        await ComputeAsync();

        _clock.Advance(TimeSpan.FromDays(20));
        File.Delete(Path.Combine(_folder, "a.md"));
        TestWorkspace.Write(_folder, "b.md", "# Backup\nthe nightly copy of the data");
        await ScanAsync();
        (await ComputeAsync()).Should().Be(0);

        _clock.Advance(TimeSpan.FromDays(20));
        File.Delete(Path.Combine(_folder, "b.md"));
        await ScanAsync();
        await ComputeAsync();
        TestWorkspace.Write(_folder, "c.md", "# Backup\nthe nightly copy of the data");
        await ScanAsync();

        (await ComputeAsync()).Should().Be(0, "the vector was used at day 20: day 40 is not its expiry");
    }

    private sealed class CountingEmbeddings : IEmbeddingProvider
    {
        public int Computed { get; private set; }

        public string ModelId => "counting";

        public int Dimension => 4;

        public double DefaultMaxDistance => 0.3;

        public EmbeddingStatus? Known() => Status();

        public EmbeddingStatus Status() => new(EmbeddingState.Ready);

        public float[] EmbedQuery(string text) => Embed(text);

        public IReadOnlyList<float[]> EmbedPassages(IReadOnlyList<string> texts)
        {
            Computed += texts.Count;
            return [.. texts.Select(Embed)];
        }

        private static float[] Embed(string text)
        {
            var seed = text.Length;
            return [1f, seed % 7 / 7f, seed % 5 / 5f, 0.5f];
        }
    }
}

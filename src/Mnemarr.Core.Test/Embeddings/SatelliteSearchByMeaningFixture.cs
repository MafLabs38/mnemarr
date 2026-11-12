using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using Mnemarr.Core.Accounts;
using Mnemarr.Core.Audit;
using Mnemarr.Core.Collectors;
using Mnemarr.Core.Consolidation;
using Mnemarr.Core.Conversations;
using Mnemarr.Core.Embeddings;
using Mnemarr.Core.Indexing;
using Mnemarr.Core.Sources;

namespace Mnemarr.Core.Test.Embeddings;

/// <summary>
/// The point of keeping a satellite's conversations raw (ADR 0041): they are not consolidated, and they are still indexed for the search by meaning. The store is the real
/// <c>sqlite-vec</c> library (<c>MNEMARR_TEST_VECTOR_LIBRARY</c>); the model is a counter.
/// </summary>
public sealed class SatelliteSearchByMeaningFixture : IDisposable
{
    private static readonly string? Library = Environment.GetEnvironmentVariable("MNEMARR_TEST_VECTOR_LIBRARY");

    private readonly TestWorkspace _workspace;
    private readonly FakeTimeProvider _clock = new(new DateTimeOffset(2026, 11, 12, 9, 0, 0, TimeSpan.Zero));
    private readonly ConversationIngestor _ingestor;
    private readonly SqliteVectorStore _store;
    private readonly EmbeddingIndexer _embedder;
    private readonly SqliteConsolidationStore _consolidation;
    private readonly CountingProvider _provider = new();

    public SatelliteSearchByMeaningFixture()
    {
        Assert.SkipUnless(Library is not null, "No sqlite-vec library given.");
        _workspace = new TestWorkspace();
        var paths = _workspace.Paths();
        var wake = new EmbeddingWakeUp();
        var index = new SqliteNoteIndex(_workspace.Database, wake);
        var conversations = new SqliteConversationStore(_workspace.Database);
        var locks = new SourceLocks();
        var catalog = new SourceCatalog(new SqliteSourceStore(_workspace.Database), new SqliteAuditLog(_workspace.Database, _clock), paths, _clock);
        var indexer = new SourceIndexer(index, paths, new ScanOutcomes(), _clock, locks);
        var raw = new RawConversations(_workspace.Options);
        _ingestor = new ConversationIngestor(catalog, locks, indexer, raw, conversations, new ConversationRelocator(catalog, locks, indexer, raw, conversations, new SqliteMachineStore(_workspace.Database)), _clock);
        var options = Options.Create(new EmbeddingOptions { ModelFolder = "unused", ExtensionPath = Library, BatchSize = 4 });
        _store = new SqliteVectorStore(_workspace.Database, options, _clock);
        _embedder = new EmbeddingIndexer(_provider, _store, options, NullLogger<EmbeddingIndexer>.Instance, wake);
        _consolidation = new SqliteConsolidationStore(_workspace.Database);
    }

    public void Dispose() => _workspace.Dispose();

    [Fact]
    public async Task should_index_for_the_search_by_meaning_a_conversation_that_is_never_consolidated()
    {
        var lines = new[]
        {
            System.Text.Json.JsonSerializer.Serialize(new { v = 1, meta = new { title = "A sketch", source = "web", id = "x" } }),
            System.Text.Json.JsonSerializer.Serialize(new { v = 1, role = "user", at = (string?)null, text = "Could the nightly copy be split across two disks?" }),
            System.Text.Json.JsonSerializer.Serialize(new { v = 1, role = "assistant", at = (string?)null, text = "Yes, one copy to each disk, and a check of both once a week." }),
        };
        await _ingestor.IngestAsync(new IngestBatch(ConversationFormats.ChatTurns, "gemini", "gemini", "gemini-x", 0, lines), TestContext.Current.CancellationToken);
        _clock.Advance(TimeSpan.FromHours(2));

        await _embedder.PassAsync(tidy: false, TestContext.Current.CancellationToken);

        var progress = _store.Progress(_provider.ModelId);
        progress.Chunks.Should().BeGreaterThan(0);
        progress.Embedded.Should().Be(progress.Chunks, "the conversation is found by its meaning");
        _consolidation.Pending(_clock.GetUtcNow(), maxTries: 3, limit: 10).Should().BeEmpty("and is not turned into notes");
        _consolidation.Counts(_clock.GetUtcNow().Date, 3).Waiting.Should().Be(0);
    }

    private sealed class CountingProvider : IEmbeddingProvider
    {
        public string ModelId => "counting";

        public int Dimension => 4;

        public double DefaultMaxDistance => 0.3;

        public EmbeddingStatus? Known() => Status();

        public EmbeddingStatus Status() => new(EmbeddingState.Ready);

        public float[] EmbedQuery(string text) => Embed(text);

        public IReadOnlyList<float[]> EmbedPassages(IReadOnlyList<string> texts) => [.. texts.Select(Embed)];

        private static float[] Embed(string text) => [1f, text.Length % 7 / 7f, text.Length % 5 / 5f, 0.5f];
    }
}

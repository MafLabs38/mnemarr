using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Mnemarr.Core.Accounts;
using Mnemarr.Core.Embeddings;
using Mnemarr.Core.Indexing;

namespace Mnemarr.Core.Test.Embeddings;

/// <summary>How the two answers are merged, on canned lists: no model, no library, no database, so it runs everywhere.</summary>
public sealed class NoteSearchFixture
{
    private static readonly Guid Source = Guid.NewGuid();

    private static SearchHit Hit(long chunk, string match = SearchMatch.Keywords) => new(Source, "s", $"{chunk}.md", string.Empty, $"T{chunk}", "H", "snippet", 0, chunk, match);

    private static NoteSearch Search(StubIndex index, StubProvider provider, StubStore store) =>
        new(index, index, provider, store, Options.Create(new EmbeddingOptions { ModelFolder = "/model", ExtensionPath = "x" }), NullLogger<NoteSearch>.Instance);

    [Fact]
    public void should_rank_by_the_places_in_the_two_lists_and_say_which_chunk_matched_both_ways()
    {
        var index = new StubIndex { Keywords = [Hit(1), Hit(2)], Chunks = [Hit(2, SearchMatch.Meaning), Hit(3, SearchMatch.Meaning)] };
        var store = new StubStore { Matches = [new VectorMatch(2, 0.1), new VectorMatch(3, 0.15)] };

        var hits = Search(index, new StubProvider(), store).Search(new SearchRequest("sauvegarde nuit"), SourceScope.All);

        hits.Select(hit => (hit.ChunkId, hit.Match)).Should().Equal((2L, SearchMatch.Both), (1L, SearchMatch.Keywords), (3L, SearchMatch.Meaning));
    }

    [Fact]
    public void should_weigh_the_words_of_a_long_question_less_than_its_meaning_and_not_those_of_a_short_one()
    {
        var index = new StubIndex { Keywords = [Hit(1)], Chunks = [Hit(5, SearchMatch.Meaning)] };
        var store = new StubStore { Matches = [new VectorMatch(5, 0.1)] };
        var search = Search(index, new StubProvider(), store);

        var long_ = search.Search(new SearchRequest("quelle protection contre la perte de mes données dimanche"), SourceScope.All);
        var short_ = search.Search(new SearchRequest("sauvegarde nuit"), SourceScope.All);

        long_.Select(hit => hit.ChunkId).Should().Equal(5, 1);
        short_.Select(hit => hit.ChunkId).Should().Equal(1, 5);
    }

    [Fact]
    public void should_read_a_very_long_question_only_up_to_a_limit_and_cut_the_answer_to_the_limit_asked()
    {
        var provider = new StubProvider();
        var index = new StubIndex { Keywords = [.. Enumerable.Range(1, 20).Select(id => Hit(id))] };

        var hits = Search(index, provider, new StubStore()).Search(new SearchRequest(new string('a', 5000), Limit: 3), SourceScope.All);

        provider.LastQueryLength.Should().Be(1000);
        hits.Should().HaveCount(3);
    }

    [Theory]
    [InlineData(0, 1)]
    [InlineData(-4, 1)]
    [InlineData(500, 50)]
    public void should_clamp_the_limit_of_a_search(int asked, int given)
    {
        var index = new StubIndex { Keywords = [.. Enumerable.Range(1, 60).Select(id => Hit(id))] };

        Search(index, new StubProvider(), new StubStore()).Search(new SearchRequest("sauvegarde", Limit: asked), SourceScope.All).Should().HaveCount(given);
    }

    [Fact]
    public void should_answer_by_words_when_the_store_or_the_model_fails_in_any_way_and_when_the_question_is_empty()
    {
        var index = new StubIndex { Keywords = [Hit(1)] };

        Search(index, new StubProvider(), new StubStore { NearestFails = new ArgumentException("odd") }).Search(new SearchRequest("sauvegarde"), SourceScope.All).Should().ContainSingle();
        Search(index, new StubProvider { Fails = new System.Text.RegularExpressions.RegexMatchTimeoutException() }, new StubStore()).Search(new SearchRequest("sauvegarde"), SourceScope.All).Should().ContainSingle();
        var idle = new StubProvider();
        Search(new StubIndex(), idle, new StubStore()).Search(new SearchRequest("   "), SourceScope.All).Should().BeEmpty("nothing is asked");
        idle.LastQueryLength.Should().Be(0, "the model is not asked about nothing");
    }

    [Fact]
    public void should_not_show_a_chunk_that_is_not_near_enough()
    {
        var index = new StubIndex { Keywords = [], Chunks = [Hit(7, SearchMatch.Meaning)] };
        var store = new StubStore { Matches = [new VectorMatch(7, 0.5)] };

        Search(index, new StubProvider(), store).Search(new SearchRequest("sauvegarde"), SourceScope.All).Should().BeEmpty("0.5 is beyond the distance measured for the model");
    }

    [Fact]
    public async Task should_answer_by_its_words_a_question_that_does_not_get_its_turn_and_give_the_turn_back()
    {
        var gate = new ManualResetEventSlim();
        var provider = new StubProvider { Hold = gate };
        var index = new StubIndex { Keywords = [Hit(1)] };
        var search = Search(index, provider, new StubStore());
        // Four questions fill the turns and wait for the model.
        var held = Enumerable.Range(0, 4).Select(_ => Task.Run(() => search.Search(new SearchRequest("sauvegarde"), SourceScope.All), TestContext.Current.CancellationToken)).ToList();
        while (provider.Waiting < 4)
        {
            await Task.Delay(10, TestContext.Current.CancellationToken);
        }

        var fifth = search.Search(new SearchRequest("sauvegarde"), SourceScope.All);

        fifth.Should().ContainSingle().Which.Match.Should().Be(SearchMatch.Keywords, "no turn in 300 ms: answered by its words, and not held");
        gate.Set();
        await Task.WhenAll(held);
        provider.Hold = null;
        // The turns were given back (also by a question that failed): the next one is read by the model again.
        provider.Fails = new EmbeddingException("on purpose");
        search.Search(new SearchRequest("sauvegarde"), SourceScope.All);
        provider.Fails = null;
        provider.Waiting = 0;
        var again = Enumerable.Range(0, 4).Select(_ => Task.Run(() => search.Search(new SearchRequest("sauvegarde"), SourceScope.All), TestContext.Current.CancellationToken)).ToList();
        await Task.WhenAll(again);
        provider.Reads.Should().BeGreaterThanOrEqualTo(8);
    }

    private sealed class StubProvider : IEmbeddingProvider
    {
        private int _waiting;
        private int _reads;

        public ManualResetEventSlim? Hold { get; set; }

        public int Waiting
        {
            get => _waiting;
            set => _waiting = value;
        }

        public int Reads => _reads;

        public int LastQueryLength { get; private set; }

        public Exception? Fails { get; set; }

        public string ModelId => "stub";

        public int Dimension => 2;

        public double DefaultMaxDistance => 0.2;

        public EmbeddingStatus? Known() => new(EmbeddingState.Ready);

        public EmbeddingStatus Status() => new(EmbeddingState.Ready);

        public float[] EmbedQuery(string text)
        {
            LastQueryLength = text.Length;
            Interlocked.Increment(ref _reads);
            if (Hold is { } gate)
            {
                Interlocked.Increment(ref _waiting);
                gate.Wait();
            }

            return Fails is { } failure ? throw failure : [1f, 0f];
        }

        public IReadOnlyList<float[]> EmbedPassages(IReadOnlyList<string> texts) => [.. texts.Select(_ => new[] { 1f, 0f })];
    }

    private sealed class StubStore : IVectorStore
    {
        public IReadOnlyList<VectorMatch> Matches { get; set; } = [];

        public Exception? NearestFails { get; set; }

        public bool TryPrepare(out string? reason)
        {
            reason = null;
            return true;
        }

        public IReadOnlyList<ChunkToEmbed> Missing(string model, int dimension, int limit, IReadOnlySet<long> skipped) => [];

        public void Save(string model, int dimension, IReadOnlyList<ComputedVector> items)
        {
        }

        public IReadOnlyList<VectorMatch> Nearest(string model, int dimension, float[] query, int k) => NearestFails is { } failure ? throw failure : Matches;

        public EmbeddingProgress Progress(string model, SourceScope? scope = null) => new(0, 0);

        public int Forgotten { get; private set; }

        public void ForgetComputed() => Forgotten++;

        public void Tidy(string model, int dimension)
        {
        }
    }

    private sealed class StubIndex : INoteIndex, IChunkHits
    {
        public IReadOnlyList<SearchHit> Keywords { get; set; } = [];

        public IReadOnlyList<SearchHit> Chunks { get; set; } = [];

        public IReadOnlyList<SearchHit> Search(SearchRequest request, SourceScope scope) => [.. Keywords.Take(request.Limit)];

        IReadOnlyList<SearchHit> IChunkHits.Chunks(IReadOnlyCollection<long> chunkIds, SearchRequest request, SourceScope scope) => [.. Chunks.Where(hit => chunkIds.Contains(hit.ChunkId))];

        public IReadOnlyDictionary<string, NoteState> Notes(Guid sourceId) => throw new NotSupportedException();

        public void Upsert(Guid sourceId, ScannedFile file, IndexedNote note, DateTimeOffset at) => throw new NotSupportedException();

        public void Touch(Guid sourceId, ScannedFile file) => throw new NotSupportedException();

        public void UpdateMeta(Guid sourceId, ScannedFile file, NoteMeta meta, int metaVersion) => throw new NotSupportedException();

        public void Remove(Guid sourceId, string path) => throw new NotSupportedException();

        public IReadOnlySet<string> Images(Guid sourceId) => throw new NotSupportedException();

        public void ReplaceImages(Guid sourceId, IReadOnlyCollection<string> paths) => throw new NotSupportedException();

        public IndexCounts Counts(Guid sourceId) => throw new NotSupportedException();
    }
}

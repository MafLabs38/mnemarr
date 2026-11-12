using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using Mnemarr.Core.Accounts;
using Mnemarr.Core.Embeddings;
using Mnemarr.Core.Health;
using Mnemarr.Core.Indexing;
using Mnemarr.Core.Sources;

namespace Mnemarr.Core.Test.Embeddings;

/// <summary>What the search by meaning says of itself, and how search behaves when it cannot work: none of this needs the model or the library.</summary>
public sealed class EmbeddingReportFixture : IDisposable
{
    private readonly TestWorkspace _workspace = new();

    public void Dispose() => _workspace.Dispose();

    private static IOptions<EmbeddingOptions> Wanted(string library = "/nowhere/vec0.so") => Options.Create(new EmbeddingOptions { ModelFolder = "/model", ExtensionPath = library });

    private static EmbeddingIndexer Indexer(IEmbeddingProvider provider, IVectorStore store, IOptions<EmbeddingOptions>? options = null) =>
        new(provider, store, options ?? Wanted(), NullLogger<EmbeddingIndexer>.Instance);

    [Fact]
    public void should_say_it_is_off_when_nobody_gave_a_model_folder()
    {
        var off = Indexer(new FakeProvider(null), new FakeStore(), Options.Create(new EmbeddingOptions()));

        off.Report().State.Should().Be(EmbeddingState.Off);
    }

    [Fact]
    public void should_say_it_is_starting_until_the_model_is_known_and_never_load_it_to_say_so()
    {
        var provider = new FakeProvider(null);

        Indexer(provider, new FakeStore()).Report().State.Should().Be(EmbeddingState.Starting);
        provider.Loads.Should().Be(0);
    }

    [Fact]
    public void should_say_why_it_failed_when_the_model_cannot_be_loaded()
    {
        var report = Indexer(new FakeProvider(new EmbeddingStatus(EmbeddingState.Failed, "no model")), new FakeStore()).Report();

        report.Should().Match<EmbeddingReport>(item => item.State == EmbeddingState.Failed && item.Reason == "no model");
    }

    [Fact]
    public void should_fail_with_a_sentence_and_not_the_path_when_the_vector_library_cannot_be_loaded()
    {
        var store = new SqliteVectorStore(_workspace.Database, Wanted("/nowhere/vec0.so"));

        store.TryPrepare(out var reason).Should().BeFalse();
        reason.Should().NotBeNull().And.NotContain("/nowhere");
        Indexer(new FakeProvider(new EmbeddingStatus(EmbeddingState.Ready)), store).Report().State.Should().Be(EmbeddingState.Failed);
    }

    [Theory]
    [InlineData(EmbeddingState.Failed, "embeddings-failing", HealthStatus.Degraded)]
    [InlineData(EmbeddingState.Starting, null, HealthStatus.Healthy)]
    [InlineData(EmbeddingState.Off, null, HealthStatus.Healthy)]
    public async Task should_map_what_the_indexer_says_to_a_health_status(EmbeddingState state, string? reason, HealthStatus expected)
    {
        var provider = new FakeProvider(state == EmbeddingState.Failed ? new EmbeddingStatus(EmbeddingState.Failed, "no model") : null);
        var options = state == EmbeddingState.Off ? Options.Create(new EmbeddingOptions()) : Wanted();

        var result = await new EmbeddingsHealthCheck(Indexer(provider, new FakeStore(), options)).CheckHealthAsync(new HealthCheckContext(), TestContext.Current.CancellationToken);

        result.Status.Should().Be(expected);
        result.Data.GetValueOrDefault(HealthReasons.Key).Should().Be(reason);
    }

    [Fact]
    public async Task should_say_how_far_the_model_is_while_it_reads_and_that_it_is_up_to_date_after()
    {
        var provider = new FakeProvider(new EmbeddingStatus(EmbeddingState.Ready));
        var store = new FakeStore { Progress = new EmbeddingProgress(472, 120) };

        var catchingUp = await new EmbeddingsHealthCheck(Indexer(provider, store)).CheckHealthAsync(new HealthCheckContext(), TestContext.Current.CancellationToken);
        store.Progress = new EmbeddingProgress(472, 472);
        var done = await new EmbeddingsHealthCheck(Indexer(provider, store)).CheckHealthAsync(new HealthCheckContext(), TestContext.Current.CancellationToken);

        catchingUp.Should().Match<HealthCheckResult>(result => result.Status == HealthStatus.Healthy && result.Description!.Contains("120 of 472", StringComparison.Ordinal));
        done.Description.Should().Contain("up to date");
    }

    [Fact]
    public async Task should_go_on_after_a_pass_that_failed_for_any_reason_and_say_it_without_a_path()
    {
        var provider = new FakeProvider(new EmbeddingStatus(EmbeddingState.Ready));
        var store = new FakeStore { Pending = [new ChunkToEmbed(1, "a")], FailWith = new IndexOutOfRangeException("/secret/path") };
        var indexer = Indexer(provider, store);

        await indexer.PassAsync(tidy: false, TestContext.Current.CancellationToken);

        indexer.Report().Should().Match<EmbeddingReport>(report => report.State == EmbeddingState.Failed && !report.Reason!.Contains("secret", StringComparison.Ordinal));
        store.FailWith = null;
        await indexer.PassAsync(tidy: false, TestContext.Current.CancellationToken);
        indexer.Report().State.Should().Be(EmbeddingState.Ready, "the next pass that works clears the reason");
    }

    [Fact]
    public async Task should_not_leave_passages_out_because_the_store_failed_and_try_them_again_at_the_next_pass()
    {
        var provider = new FakeProvider(new EmbeddingStatus(EmbeddingState.Ready));
        var store = new FakeStore { Pending = [new ChunkToEmbed(1, "a"), new ChunkToEmbed(2, "b")], SaveFails = new InvalidOperationException("database is locked") };
        var indexer = Indexer(provider, store);

        await indexer.PassAsync(tidy: false, TestContext.Current.CancellationToken);
        indexer.Report().State.Should().Be(EmbeddingState.Failed, "a store that fails is a failure of the pass");
        store.SaveFails = null;
        await indexer.PassAsync(tidy: false, TestContext.Current.CancellationToken);

        store.Saved.Should().BeEquivalentTo([1L, 2L], "nothing was left out for good");
        indexer.Report().State.Should().Be(EmbeddingState.Ready);
    }

    [Fact]
    public async Task should_leave_out_a_passage_that_the_model_cannot_read_and_read_the_others()
    {
        var provider = new FakeProvider(new EmbeddingStatus(EmbeddingState.Ready)) { Unreadable = "bad" };
        var store = new FakeStore { Pending = [new ChunkToEmbed(1, "good"), new ChunkToEmbed(2, "bad"), new ChunkToEmbed(3, "also good")] };
        var indexer = Indexer(provider, store);

        var computed = await indexer.RunAsync(TestContext.Current.CancellationToken);

        computed.Should().Be(2);
        store.Saved.Should().BeEquivalentTo([1L, 3L]);
        (await indexer.RunAsync(TestContext.Current.CancellationToken)).Should().Be(0, "the passage that failed is not tried at every pass");
    }

    [Theory]
    [InlineData("unknown", "ExtensionPath", 2, 8, 512, 15, 0.2, false)]
    [InlineData("granite-embedding-97m-multilingual-r2", null, 2, 8, 512, 15, 0.2, false)]
    [InlineData("granite-embedding-97m-multilingual-r2", "ExtensionPath", 0, 8, 512, 15, 0.2, false)]
    [InlineData("granite-embedding-97m-multilingual-r2", "ExtensionPath", 65, 8, 512, 15, 0.2, false)]
    [InlineData("granite-embedding-97m-multilingual-r2", "ExtensionPath", 2, 0, 512, 15, 0.2, false)]
    [InlineData("granite-embedding-97m-multilingual-r2", "ExtensionPath", 2, 8, 15, 15, 0.2, false)]
    [InlineData("granite-embedding-97m-multilingual-r2", "ExtensionPath", 2, 8, 8193, 15, 0.2, false)]
    [InlineData("granite-embedding-97m-multilingual-r2", "ExtensionPath", 2, 8, 512, 0, 0.2, false)]
    [InlineData("granite-embedding-97m-multilingual-r2", "ExtensionPath", 2, 8, 512, 15, 0.0, false)]
    [InlineData("granite-embedding-97m-multilingual-r2", "ExtensionPath", 2, 8, 512, 15, 2.01, false)]
    [InlineData("granite-embedding-97m-multilingual-r2", "ExtensionPath", 64, 64, 8192, 3600, 2.0, true)]
    [InlineData("granite-embedding-97m-multilingual-r2", "ExtensionPath", 1, 1, 16, 1, 0.01, true)]
    public void should_check_every_setting_of_the_search_by_meaning(string model, string? library, int threads, int batch, int tokens, int idle, double distance, bool valid)
    {
        var options = new EmbeddingOptions { Model = model, ModelFolder = "/model", ExtensionPath = library, Threads = threads, BatchSize = batch, MaxTokens = tokens, IdleSeconds = idle, MaxDistance = distance };

        new EmbeddingOptionsValidator().Validate(null, options).Succeeded.Should().Be(valid);
    }

    [Fact]
    public void should_need_no_extension_when_the_feature_is_off_and_use_the_distance_of_the_model_when_none_is_set()
    {
        new EmbeddingOptionsValidator().Validate(null, new EmbeddingOptions()).Succeeded.Should().BeTrue();
        new EmbeddingOptions().MaxDistance.Should().BeNull();
        KnownModels.TryGet(KnownModels.Default, out var model).Should().BeTrue();
        model!.MaxDistance.Should().Be(0.2);
    }

    [Fact]
    public async Task should_answer_by_words_when_the_model_is_not_loaded_yet_or_fails_on_the_question()
    {
        var index = new SqliteNoteIndex(_workspace.Database);
        var folder = _workspace.Folder("courses");
        TestWorkspace.Write(folder, "a.md", "# A\nUne sauvegarde chaque nuit.");
        var source = new Source(Guid.NewGuid(), "courses", SourceKind.Knowledge, SourceMode.ReadWrite, folder, DateTimeOffset.UnixEpoch);
        new SqliteSourceStore(_workspace.Database).Add(source);
        await new SourceIndexer(index, _workspace.Paths(), new ScanOutcomes(), new FakeTimeProvider()).IndexAsync(source, TestContext.Current.CancellationToken);
        var starting = new NoteSearch(index, index, new FakeProvider(null), new FakeStore(), Wanted(), NullLogger<NoteSearch>.Instance);
        var failing = new NoteSearch(index, index, new FakeProvider(new EmbeddingStatus(EmbeddingState.Ready)) { QueryFails = true }, new FakeStore(), Wanted(), NullLogger<NoteSearch>.Instance);

        starting.Search(new SearchRequest("sauvegarde"), SourceScope.All).Should().ContainSingle().Which.Match.Should().Be(SearchMatch.Keywords);
        failing.Search(new SearchRequest("sauvegarde"), SourceScope.All).Should().ContainSingle().Which.Match.Should().Be(SearchMatch.Keywords, "a model that fails on a question is a log line, not an error for the person who asked");
    }

    private sealed class FakeProvider(EmbeddingStatus? known) : IEmbeddingProvider
    {
        public int Loads { get; private set; }

        public bool QueryFails { get; set; }

        public string? Unreadable { get; set; }

        public int LastQueryLength { get; private set; }

        public string ModelId => "fake";

        public int Dimension => 2;

        public double DefaultMaxDistance => 0.2;

        public EmbeddingStatus? Known() => known;

        public EmbeddingStatus Status()
        {
            Loads++;
            return known ?? new EmbeddingStatus(EmbeddingState.Ready);
        }

        public float[] EmbedQuery(string text)
        {
            LastQueryLength = text.Length;
            return QueryFails ? throw new EmbeddingException("on purpose") : [1f, 0f];
        }

        public IReadOnlyList<float[]> EmbedPassages(IReadOnlyList<string> texts) =>
            texts.Contains(Unreadable) ? throw new EmbeddingException("cannot read") : [.. texts.Select(_ => new[] { 1f, 0f })];
    }

    private sealed class FakeStore : IVectorStore
    {
        private readonly HashSet<long> _done = [];

        public EmbeddingProgress Progress { get; set; } = new(0, 0);

        public IReadOnlyList<ChunkToEmbed> Pending { get; set; } = [];

        public IReadOnlyList<VectorMatch> Matches { get; set; } = [];

        public List<long> Saved { get; } = [];

        public Exception? FailWith { get; set; }

        public Exception? NearestFails { get; set; }

        public Exception? SaveFails { get; set; }

        public bool TryPrepare(out string? reason)
        {
            reason = null;
            return true;
        }

        public IReadOnlyList<ChunkToEmbed> Missing(string model, int dimension, int limit, IReadOnlySet<long> skipped) =>
            FailWith is { } failure ? throw failure : [.. Pending.Where(chunk => !_done.Contains(chunk.ChunkId) && !skipped.Contains(chunk.ChunkId)).Take(limit)];

        public void Save(string model, int dimension, IReadOnlyList<ComputedVector> items)
        {
            if (SaveFails is { } failure)
            {
                throw failure;
            }

            foreach (var (chunkId, _, _) in items)
            {
                _done.Add(chunkId);
                Saved.Add(chunkId);
            }
        }

        public IReadOnlyList<VectorMatch> Nearest(string model, int dimension, float[] query, int k) => NearestFails is { } failure ? throw failure : Matches;

        EmbeddingProgress IVectorStore.Progress(string model, SourceScope? scope) => Progress;

        public void Tidy(string model, int dimension)
        {
        }
    }
}

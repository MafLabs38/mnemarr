using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using Mnemarr.Core.Accounts;
using Mnemarr.Core.Embeddings;
using Mnemarr.Core.Indexing;
using Mnemarr.Core.Sources;
using Mnemarr.Core.Storage;

namespace Mnemarr.Core.Test.Embeddings;

/// <summary>
/// The search by meaning, whole: the notes are indexed, the vectors computed in the background, and a question that shares no word with a note still finds it. The
/// vectors are the ones of a model that is small enough to read (four themes, by word lists), and the store is the real <c>sqlite-vec</c> library: the test runs when
/// <c>MNEMARR_TEST_VECTOR_LIBRARY</c> names it (<c>tools/native/fetch-vector-library.sh</c>) and says it skipped otherwise.
/// </summary>
public sealed class SemanticSearchFixture : IDisposable
{
    private static readonly string? Library = Environment.GetEnvironmentVariable("MNEMARR_TEST_VECTOR_LIBRARY");

    private readonly TestWorkspace _workspace;
    private readonly FakeTimeProvider _clock = new(new DateTimeOffset(2026, 11, 7, 9, 0, 0, TimeSpan.Zero));
    private readonly SqliteNoteIndex _index;
    private readonly SqliteSourceStore _sources;
    private readonly SourceIndexer _indexer;
    private readonly EmbeddingOptions _options;
    private readonly ThemeEmbeddings _provider = new();
    private readonly SqliteVectorStore _store;
    private readonly EmbeddingIndexer _embedder;
    private readonly NoteSearch _search;

    public SemanticSearchFixture()
    {
        Assert.SkipUnless(Library is not null, "No sqlite-vec library given.");
        _workspace = new TestWorkspace();
        _options = new EmbeddingOptions { ModelFolder = "unused", ExtensionPath = Library, BatchSize = 2, MaxDistance = 0.3 };
        _index = new SqliteNoteIndex(_workspace.Database);
        _sources = new SqliteSourceStore(_workspace.Database);
        _indexer = new SourceIndexer(_index, _workspace.Paths(), new ScanOutcomes(), _clock);
        _store = new SqliteVectorStore(_workspace.Database, Options.Create(_options));
        _embedder = new EmbeddingIndexer(_provider, _store, Options.Create(_options), NullLogger<EmbeddingIndexer>.Instance);
        _search = new NoteSearch(_index, _index, _provider, _store, Options.Create(_options), NullLogger<NoteSearch>.Instance);
    }

    public void Dispose() => _workspace.Dispose();

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    private async Task<Source> IndexAsync(string name, SourceKind kind, params (string Path, string Text)[] notes)
    {
        // The conversations are kept in the folder Mnemarr made for them; any other source is under the sources folder.
        var folder = kind == SourceKind.Conversations ? Directory.CreateDirectory(Path.Combine(_workspace.DataFolder, "conversations")).FullName : _workspace.Folder(name);
        foreach (var (path, text) in notes)
        {
            TestWorkspace.Write(folder, path, text);
        }

        var source = new Source(Guid.NewGuid(), name, kind, SourceMode.ReadWrite, folder, _clock.GetUtcNow());
        _sources.Add(source);
        await _indexer.IndexAsync(source, Token);
        return source;
    }

    [Fact]
    public async Task should_find_a_note_by_its_meaning_when_the_question_shares_no_word_with_it()
    {
        await IndexAsync("courses", SourceKind.Knowledge, ("backup.md", "# Sauvegardes\nLe dossier backup est copié chaque nuit."), ("cooking.md", "# Cuisine\nLa recette demande du beurre."));
        await _embedder.RunAsync(Token);

        var hits = _search.Search(new SearchRequest("comment protéger mes données contre la perte"), SourceScope.All);

        hits.Should().ContainSingle().Which.Should().Match<SearchHit>(hit => hit.Path == "backup.md" && hit.Match == SearchMatch.Meaning);
    }

    [Fact]
    public async Task should_say_that_a_chunk_matched_by_both_its_words_and_its_meaning_and_rank_it_first()
    {
        await IndexAsync("courses", SourceKind.Knowledge, ("backup.md", "# Sauvegardes\nLe dossier backup est copié chaque nuit."), ("other.md", "# Autre\nUne promenade de nuit."));
        await _embedder.RunAsync(Token);

        var hits = _search.Search(new SearchRequest("backup nuit"), SourceScope.All);

        hits[0].Match.Should().Be(SearchMatch.Both);
        hits[0].Path.Should().Be("backup.md");
    }

    [Fact]
    public async Task should_rank_a_note_that_means_what_a_long_question_asks_above_one_that_shares_a_single_word_with_it()
    {
        await IndexAsync("courses", SourceKind.Knowledge, ("backup.md", "# Sauvegardes\nLe dossier backup est copié chaque nuit."), ("walk.md", "# Balade\nUne protection solaire pour la marche, un dimanche."));
        await _embedder.RunAsync(Token);

        var hits = _search.Search(new SearchRequest("quelle protection contre la perte de mes données dimanche"), SourceScope.All);

        hits.Select(hit => hit.Path).Should().Equal("backup.md", "walk.md");
        hits[0].Match.Should().Be(SearchMatch.Meaning);
    }

    [Fact]
    public async Task should_not_show_as_an_answer_a_chunk_that_is_not_near_the_question()
    {
        await IndexAsync("courses", SourceKind.Knowledge, ("cooking.md", "# Cuisine\nLa recette demande du beurre."));
        await _embedder.RunAsync(Token);

        _search.Search(new SearchRequest("protéger mes données contre une défaillance"), SourceScope.All).Should().BeEmpty("cooking is another theme: nearest is not the same as near");
    }

    [Fact]
    public async Task should_keep_the_scope_of_the_caller_for_the_search_by_meaning_as_for_the_words()
    {
        var mine = await IndexAsync("mine", SourceKind.Knowledge, ("a.md", "# A\nSauvegarde de la maison."));
        await IndexAsync("theirs", SourceKind.Knowledge, ("b.md", "# B\nSauvegarde du bureau."));
        await IndexAsync("talks", SourceKind.Conversations, ("c.md", "# C\nSauvegarde discutée avec l'assistant."));
        await _embedder.RunAsync(Token);

        var hits = _search.Search(new SearchRequest("protéger contre la perte"), new SourceScope([mine.Id]));
        var everything = _search.Search(new SearchRequest("protéger contre la perte"), SourceScope.All with { ConversationsHidden = true });
        everything.Select(hit => hit.Path).Should().BeEquivalentTo(["a.md", "b.md"], "conversations are hidden from a caller who holds no right to read them");

        hits.Select(hit => hit.Path).Should().Equal("a.md");
    }

    [Fact]
    public async Task should_still_fill_the_answer_with_what_the_caller_may_see_when_many_nearer_chunks_are_hidden_from_them()
    {
        var talks = Enumerable.Range(0, 60).Select(index => ($"talk{index}.md", $"# T{index}\nSauvegarde {index}.")).ToArray();
        await IndexAsync("talks", SourceKind.Conversations, talks);
        await IndexAsync("mine", SourceKind.Knowledge, ("a.md", "# A\nSauvegarde de la maison."));
        await _embedder.RunAsync(Token);

        var hits = _search.Search(new SearchRequest("protéger contre la perte"), SourceScope.All with { ConversationsHidden = true });

        hits.Select(hit => hit.Path).Should().Equal("a.md");
    }

    [Fact]
    public async Task should_keep_the_filters_of_the_question_for_the_search_by_meaning()
    {
        var first = await IndexAsync("first", SourceKind.Knowledge, ("a.md", "# A\nSauvegarde de la maison."), ("notes/b.md", "# B\nSauvegarde du bureau."));
        await IndexAsync("second", SourceKind.Knowledge, ("c.md", "# C\nSauvegarde du jardin."));
        await _embedder.RunAsync(Token);

        _search.Search(new SearchRequest("protéger contre la perte", first.Id), SourceScope.All).Select(hit => hit.Path).Should().BeEquivalentTo("a.md", "notes/b.md");
        _search.Search(new SearchRequest("protéger contre la perte", first.Id, "notes"), SourceScope.All).Select(hit => hit.Path).Should().Equal("notes/b.md");
    }

    [Fact]
    public async Task should_count_the_progress_among_the_sources_the_caller_may_see()
    {
        var mine = await IndexAsync("mine", SourceKind.Knowledge, ("a.md", "# A\nSauvegarde."));
        await IndexAsync("talks", SourceKind.Conversations, ("c.md", "# C\nSauvegarde discutée."));
        await _embedder.RunAsync(Token);

        _store.Progress(_provider.ModelId).Should().Be(new EmbeddingProgress(2, 2));
        _store.Progress(_provider.ModelId, new SourceScope([mine.Id])).Should().Be(new EmbeddingProgress(1, 1), "the other source is not the caller's");
        _store.Progress(_provider.ModelId, SourceScope.All with { ConversationsHidden = true }).Should().Be(new EmbeddingProgress(1, 1), "conversations are hidden");
    }

    [Fact]
    public async Task should_compute_only_what_is_missing_and_again_for_a_chunk_that_changed()
    {
        var source = await IndexAsync("courses", SourceKind.Knowledge, ("a.md", "# A\nSauvegarde."), ("b.md", "# B\nRecette de cuisine."));

        (await _embedder.RunAsync(Token)).Should().Be(2);
        (await _embedder.RunAsync(Token)).Should().Be(0, "nothing is missing");
        TestWorkspace.Write(_workspace.Folder("courses"), "a.md", "# A\nSauvegarde, et une règle de plus sur les copies.");
        await _indexer.IndexAsync(source, Token);

        (await _embedder.RunAsync(Token)).Should().Be(1, "only the note that changed has new chunks");
        _store.Progress(_provider.ModelId).Should().Be(new EmbeddingProgress(2, 2));
    }

    [Fact]
    public async Task should_forget_the_vectors_of_a_note_that_was_removed_and_count_what_is_left()
    {
        var source = await IndexAsync("courses", SourceKind.Knowledge, ("a.md", "# A\nSauvegarde."), ("b.md", "# B\nRecette de cuisine."));
        await _embedder.RunAsync(Token);
        File.Delete(Path.Combine(_workspace.Folder("courses"), "a.md"));
        await _indexer.IndexAsync(source, Token);

        // Before the cleanup the table still holds the row of the removed note: it is not a match, whatever its place in the table.
        _store.Nearest(_provider.ModelId, _provider.Dimension, ThemeEmbeddings.Vector(ThemeEmbeddings.Protection), 10).Should().HaveCount(1);
        await _embedder.TidyAsync(Token);

        _store.Progress(_provider.ModelId).Should().Be(new EmbeddingProgress(1, 1));
        _store.Nearest(_provider.ModelId, _provider.Dimension, ThemeEmbeddings.Vector(ThemeEmbeddings.Protection), 10).Should().HaveCount(1, "the vector of the removed note is gone");
    }

    [Fact]
    public async Task should_compute_again_every_chunk_when_the_model_changes_and_drop_the_table_of_the_old_one()
    {
        await IndexAsync("courses", SourceKind.Knowledge, ("a.md", "# A\nSauvegarde."), ("b.md", "# B\nRecette de cuisine."));
        await _embedder.RunAsync(Token);
        var other = new ThemeEmbeddings("another-model");
        var embedder = new EmbeddingIndexer(other, _store, Options.Create(_options), NullLogger<EmbeddingIndexer>.Instance);

        (await embedder.RunAsync(Token)).Should().Be(2);
        await embedder.TidyAsync(Token);

        _store.Progress("another-model").Should().Be(new EmbeddingProgress(2, 2));
        _store.Nearest(_provider.ModelId, _provider.Dimension, ThemeEmbeddings.Vector(ThemeEmbeddings.Protection), 10).Should().BeEmpty("the table of the old model was dropped");
    }

    [Fact]
    public async Task should_keep_the_vectors_in_a_snapshot_of_the_database_that_is_restored()
    {
        await IndexAsync("courses", SourceKind.Knowledge, ("a.md", "# A\nSauvegarde."), ("b.md", "# B\nRecette de cuisine."));
        await _embedder.RunAsync(Token);
        var restored = Directory.CreateDirectory(Path.Combine(_workspace.Root, "restored")).FullName;
        using (var connection = _workspace.Database.Open())
        {
            SnapshotFiles.Write(connection, Path.Combine(restored, "mnemarr.db"));
        }

        var database = new Database(Options.Create(new Mnemarr.Core.Configuration.MnemarrOptions { DataFolder = restored }));
        var store = new SqliteVectorStore(database, Options.Create(_options));

        store.Nearest(_provider.ModelId, _provider.Dimension, ThemeEmbeddings.Vector(ThemeEmbeddings.Protection), 10).Should().HaveCount(2, "the vectors are in the copy, and found by the restored database");
        store.Progress(_provider.ModelId).Should().Be(new EmbeddingProgress(2, 2));
    }

    [Fact]
    public async Task should_answer_by_words_when_the_model_is_not_ready()
    {
        await IndexAsync("courses", SourceKind.Knowledge, ("backup.md", "# Sauvegardes\nLe dossier backup est copié chaque nuit."));
        await _embedder.RunAsync(Token);
        _provider.Broken = true;

        var hits = _search.Search(new SearchRequest("backup"), SourceScope.All);

        hits.Should().ContainSingle().Which.Match.Should().Be(SearchMatch.Keywords);
        _embedder.Report().State.Should().Be(EmbeddingState.Failed);
    }

    /// <summary>A model of four themes: a text is the sum of the themes whose words it holds. The same meaning with other words has the same vector.</summary>
    private sealed class ThemeEmbeddings(string id = "themes") : IEmbeddingProvider
    {
        public const int Protection = 0;

        private static readonly string[][] Themes =
        [
            ["backup", "sauvegarde", "protéger", "perte", "copie", "copiés", "copié"],
            ["recette", "cuisine", "beurre"],
            ["docker", "image", "conteneur"],
            ["angular", "signal", "composant"],
        ];

        public bool Broken { get; set; }

        public string ModelId => id;

        public int Dimension => Themes.Length;

        public static float[] Vector(int theme) => [.. Enumerable.Range(0, Themes.Length).Select(index => index == theme ? 1f : 0f)];

        public double DefaultMaxDistance => 0.3;

        public EmbeddingStatus? Known() => Status();

        public EmbeddingStatus Status() => Broken ? new EmbeddingStatus(EmbeddingState.Failed, "broken on purpose") : new EmbeddingStatus(EmbeddingState.Ready);

        public float[] EmbedQuery(string text) => Embed(text);

        public IReadOnlyList<float[]> EmbedPassages(IReadOnlyList<string> texts) => [.. texts.Select(Embed)];

        private static float[] Embed(string text)
        {
            var lower = text.ToLowerInvariant();
            var vector = Themes.Select(words => (float)words.Count(word => lower.Contains(word, StringComparison.Ordinal))).ToArray();
            var norm = MathF.Sqrt(vector.Sum(value => value * value));
            return norm == 0 ? [.. Enumerable.Repeat(0.001f, Themes.Length)] : [.. vector.Select(value => value / norm)];
        }
    }
}

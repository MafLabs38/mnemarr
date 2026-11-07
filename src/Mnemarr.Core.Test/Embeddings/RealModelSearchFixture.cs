using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using Mnemarr.Core.Accounts;
using Mnemarr.Core.Embeddings;
using Mnemarr.Core.Indexing;
using Mnemarr.Core.Sources;

namespace Mnemarr.Core.Test.Embeddings;

/// <summary>
/// The whole chain with the real model and the real library, on real notes (the documentation of the project, copied into a source): questions that use other words than
/// the notes still find them, and a question that nothing answers finds nothing. It needs the model (390 MB), the library and a folder of notes, named by
/// <c>MNEMARR_TEST_MODEL</c>, <c>MNEMARR_TEST_VECTOR_LIBRARY</c> and <c>MNEMARR_TEST_DOCS</c>; it says it skipped otherwise. The notes are not in the test: any folder will do,
/// and the questions that fit them are the ones asked here.
/// </summary>
public sealed class RealModelSearchFixture : IDisposable
{
    private static readonly string? Model = Environment.GetEnvironmentVariable("MNEMARR_TEST_MODEL");
    private static readonly string? Library = Environment.GetEnvironmentVariable("MNEMARR_TEST_VECTOR_LIBRARY");
    private static readonly string? Docs = Environment.GetEnvironmentVariable("MNEMARR_TEST_DOCS");
    private static readonly string? Out = Environment.GetEnvironmentVariable("MNEMARR_TEST_OUT");

    private readonly TestWorkspace _workspace = new();
    private readonly FakeTimeProvider _clock = new(new DateTimeOffset(2026, 11, 7, 9, 0, 0, TimeSpan.Zero));
    private readonly OnnxEmbeddingProvider _provider;
    private readonly SqliteVectorStore _store;
    private readonly NoteSearch _search;
    private readonly EmbeddingIndexer _embedder;

    public RealModelSearchFixture()
    {
        Assert.SkipUnless(Model is not null && Library is not null && Docs is not null, "No model, library or notes given.");
        var options = Options.Create(new EmbeddingOptions { ModelFolder = Model, ExtensionPath = Library, Threads = 2, MaxDistance = 0.2 });
        var index = new SqliteNoteIndex(_workspace.Database);
        _provider = new OnnxEmbeddingProvider(options, NullLogger<OnnxEmbeddingProvider>.Instance);
        _store = new SqliteVectorStore(_workspace.Database, options);
        _embedder = new EmbeddingIndexer(_provider, _store, options, NullLogger<EmbeddingIndexer>.Instance);
        _search = new NoteSearch(index, index, _provider, _store, options, NullLogger<NoteSearch>.Instance);
        var folder = _workspace.Folder("docs");
        foreach (var file in Directory.EnumerateFiles(Docs!, "*.md", SearchOption.AllDirectories))
        {
            File.Copy(file, Path.Combine(folder, Path.GetRelativePath(Docs!, file).Replace('/', '_')));
        }

        var source = new Source(Guid.NewGuid(), "docs", SourceKind.Knowledge, SourceMode.ReadOnly, folder, _clock.GetUtcNow());
        new SqliteSourceStore(_workspace.Database).Add(source);
        new SourceIndexer(index, _workspace.Paths(), new ScanOutcomes(), _clock).IndexAsync(source, TestContext.Current.CancellationToken).GetAwaiter().GetResult();
    }

    public void Dispose()
    {
        _provider.Dispose();
        _workspace.Dispose();
    }

    [Fact]
    public async Task should_find_the_notes_that_answer_questions_asked_in_other_words()
    {
        await _embedder.RunAsync(TestContext.Current.CancellationToken);
        var questions = new (string Question, string[] Expected)[]
        {
            ("comment le collecteur se met-il à jour tout seul ?", ["0032", "collector"]),
            ("how is the history of a conversation kept after a restore of the database", ["runbook", "0029"]),
            ("qui peut valider une pull request fusionnée par l'agent", ["0014", "CLAUDE", "PROTOCOL"]),
            ("pourquoi ne pas stocker les vecteurs dans une base dédiée", ["0009", "0012", "VISION"]),
            ("quel modèle calcule le sens des phrases", ["0012", "0009", "0034", "configuration", "SPEC"]),
            ("what happens when a machine is installed again under another name", ["0031", "collector"]),
            ("comment changer le mot de passe de l'administrateur", ["configuration", "runbook", "SECURITY"]),
            ("how do I publish a new version of the container image", ["releasing", "0013"]),
            ("où sont rangés les fichiers de sauvegarde", ["runbook", "0029", "configuration"]),
            ("what must a pull request contain before it is merged", ["CLAUDE", "PROTOCOL", "0014", "TESTING"]),
        };
        var report = new List<string>();
        var failures = new List<string>();
        foreach (var (question, expected) in questions)
        {
            var hits = _search.Search(new SearchRequest(question, Limit: 5), SourceScope.All);
            var near = _store.Nearest(_provider.ModelId, _provider.Dimension, _provider.EmbedQuery(question), 8);
            report.Add($"{question} -> {string.Join(" | ", hits.Select(hit => $"{hit.Match}:{hit.Path}"))} :: {string.Join(" ", near.Select(match => match.Distance.ToString("0.000", System.Globalization.CultureInfo.InvariantCulture)))}");
            if (Out is not null)
            {
                File.WriteAllLines(Path.Combine(Out, "real-search.txt"), report);
            }

            failures.AddRange(hits.Take(3).Any(hit => expected.Any(name => hit.Path.Contains(name, StringComparison.Ordinal))) ? [] : [question]);
        }

        failures.Should().BeEmpty("each question is answered by one of the notes expected, among the first three");
    }

    [Fact]
    public async Task should_find_nothing_by_meaning_for_a_question_that_no_note_answers()
    {
        await _embedder.RunAsync(TestContext.Current.CancellationToken);
        var nonsense = new[]
        {
            "recette de la tarte aux pommes", "météo à Paris demain", "who won the football match yesterday", "combien de calories dans un croissant",
            "résultat du match de rugby", "horoscope du jour pour les poissons", "how tall is mount everest",
        };
        var report = new List<string>();
        foreach (var question in nonsense)
        {
            var query = _provider.EmbedQuery(question);
            var near = _store.Nearest(_provider.ModelId, _provider.Dimension, query, 3);
            report.Add($"{question} -> {string.Join(" ", near.Select(match => match.Distance.ToString("0.000", System.Globalization.CultureInfo.InvariantCulture)))}");
        }

        if (Out is not null)
        {
            File.WriteAllLines(Path.Combine(Out, "real-distances.txt"), report);
        }

        foreach (var question in nonsense)
        {
            _search.Search(new SearchRequest(question), SourceScope.All).Where(hit => hit.Match == SearchMatch.Meaning).Should().BeEmpty($"nothing answers '{question}'");
        }
    }
}

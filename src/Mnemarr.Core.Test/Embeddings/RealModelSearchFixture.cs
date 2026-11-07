using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using Mnemarr.Core.Accounts;
using Mnemarr.Core.Embeddings;
using Mnemarr.Core.Indexing;
using Mnemarr.Core.Sources;

namespace Mnemarr.Core.Test.Embeddings;

/// <summary>
/// The notes of the real-model tests, read once for all of them: the documentation of the project is copied into a source, indexed, and every passage computed by the real
/// model and kept by the real library. It exists when the model, the library and a folder of notes are given (<c>MNEMARR_TEST_MODEL</c>, <c>MNEMARR_TEST_VECTOR_LIBRARY</c>,
/// <c>MNEMARR_TEST_DOCS</c>); otherwise nothing is made, and the tests say they skipped.
/// </summary>
public sealed class RealNotes : IAsyncLifetime, IDisposable
{
    private static readonly string? Model = Environment.GetEnvironmentVariable("MNEMARR_TEST_MODEL");
    private static readonly string? Library = Environment.GetEnvironmentVariable("MNEMARR_TEST_VECTOR_LIBRARY");
    private static readonly string? Docs = Environment.GetEnvironmentVariable("MNEMARR_TEST_DOCS");

    private TestWorkspace? _workspace;
    private OnnxEmbeddingProvider? _provider;

    public static bool Available => Model is not null && Library is not null && Docs is not null;

    public NoteSearch Search { get; private set; } = null!;

    public SqliteVectorStore Store { get; private set; } = null!;

    public IEmbeddingProvider Provider => _provider!;

    public async ValueTask InitializeAsync()
    {
        if (!Available)
        {
            return;
        }

        _workspace = new TestWorkspace();
        var clock = new FakeTimeProvider(new DateTimeOffset(2026, 11, 7, 9, 0, 0, TimeSpan.Zero));
        var options = Options.Create(new EmbeddingOptions { ModelFolder = Model, ExtensionPath = Library, Threads = 2 });
        var index = new SqliteNoteIndex(_workspace.Database);
        _provider = new OnnxEmbeddingProvider(options, NullLogger<OnnxEmbeddingProvider>.Instance);
        Store = new SqliteVectorStore(_workspace.Database, options);
        Search = new NoteSearch(index, index, _provider, Store, options, NullLogger<NoteSearch>.Instance);
        var folder = _workspace.Folder("docs");
        foreach (var file in Directory.EnumerateFiles(Docs!, "*.md", SearchOption.AllDirectories))
        {
            File.Copy(file, Path.Combine(folder, Path.GetRelativePath(Docs!, file).Replace(Path.DirectorySeparatorChar, '_')));
        }

        var source = new Source(Guid.NewGuid(), "docs", SourceKind.Knowledge, SourceMode.ReadOnly, folder, clock.GetUtcNow());
        new SqliteSourceStore(_workspace.Database).Add(source);
        await new SourceIndexer(index, _workspace.Paths(), new ScanOutcomes(), clock).IndexAsync(source, TestContext.Current.CancellationToken);
        await new EmbeddingIndexer(_provider, Store, options, NullLogger<EmbeddingIndexer>.Instance).RunAsync(TestContext.Current.CancellationToken);
    }

    public ValueTask DisposeAsync()
    {
        Dispose();
        return ValueTask.CompletedTask;
    }

    public void Dispose()
    {
        _provider?.Dispose();
        _workspace?.Dispose();
    }
}

/// <summary>
/// The whole chain with the real model and the real library, on real notes: questions that use other words than the notes still find them, and a question that nothing
/// answers finds nothing by meaning. The questions are those that fit the documentation of the project (a folder of other notes needs other questions); they are
/// expected to be answered by one of the notes named, among the first three, and a failure here after the documentation changed means: ask again what the notes answer
/// (and measure the distances again, ADR 0034).
/// </summary>
public sealed class RealModelSearchFixture : IClassFixture<RealNotes>
{
    private readonly RealNotes _notes;

    public RealModelSearchFixture(RealNotes notes)
    {
        _notes = notes;
    }

    [Fact]
    public void should_find_the_notes_that_answer_questions_asked_in_other_words()
    {
        Assert.SkipUnless(RealNotes.Available, "No model, library or notes given.");
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

        var unanswered = questions
            .Where(item => !_notes.Search.Search(new SearchRequest(item.Question, Limit: 5), SourceScope.All).Take(3).Any(hit => item.Expected.Any(name => hit.Path.Contains(name, StringComparison.Ordinal))))
            .Select(item => item.Question);

        unanswered.Should().BeEmpty("each question is answered by one of the notes expected, among the first three");
    }

    [Fact]
    public void should_find_nothing_by_meaning_for_a_question_that_no_note_answers()
    {
        Assert.SkipUnless(RealNotes.Available, "No model, library or notes given.");
        var nonsense = new[]
        {
            "recette de la tarte aux pommes", "météo à Paris demain", "who won the football match yesterday", "combien de calories dans un croissant",
            "résultat du match de rugby", "horoscope du jour pour les poissons", "how tall is mount everest",
        };

        foreach (var question in nonsense)
        {
            _notes.Search.Search(new SearchRequest(question), SourceScope.All).Where(hit => hit.Match == SearchMatch.Meaning).Should().BeEmpty($"nothing answers '{question}'");
        }
    }
}

using Microsoft.Extensions.Time.Testing;
using Mnemarr.Core.Accounts;
using Mnemarr.Core.Indexing;
using Mnemarr.Core.Notes;
using Mnemarr.Core.Sources;

namespace Mnemarr.Core.Test.Notes;

/// <summary>Sources with real folders, indexed for real, to ask the reader questions about.</summary>
public abstract class NotesFixtureBase : IDisposable
{
    private readonly FakeTimeProvider _clock = new(new DateTimeOffset(2026, 11, 4, 9, 0, 0, TimeSpan.Zero));
    private readonly SqliteSourceStore _store;
    private readonly SourceIndexer _indexer;

    protected NotesFixtureBase()
    {
        Workspace = new TestWorkspace();
        _store = new SqliteSourceStore(Workspace.Database);
        _indexer = new SourceIndexer(new SqliteNoteIndex(Workspace.Database), Workspace.Paths(), new ScanOutcomes(), _clock);
        Browser = new SqliteNoteBrowser(Workspace.Database);
        Reader = new NoteReader(_store, Browser, Workspace.Paths());
    }

    internal TestWorkspace Workspace { get; }

    protected SqliteNoteBrowser Browser { get; }

    protected NoteReader Reader { get; }

    protected static CancellationToken Token => TestContext.Current.CancellationToken;

    public void Dispose() => Workspace.Dispose();

    protected Task<IndexReport> ReindexAsync(Source source) => _indexer.IndexAsync(source, Token);

    /// <summary>Declares a source whose folder holds the given notes, and indexes it.</summary>
    protected async Task<Source> SourceWithAsync(string name, params (string Path, string Text)[] notes)
    {
        var folder = Workspace.Folder(name);
        foreach (var (path, text) in notes)
        {
            TestWorkspace.Write(folder, path, text);
        }

        var source = new Source(Guid.NewGuid(), name, SourceKind.Knowledge, SourceMode.ReadOnly, folder, _clock.GetUtcNow());
        _store.Add(source);
        await _indexer.IndexAsync(source, Token);
        return source;
    }
}

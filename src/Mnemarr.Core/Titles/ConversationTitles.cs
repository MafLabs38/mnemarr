using Mnemarr.Core.Conversations;
using Mnemarr.Core.Indexing;
using Mnemarr.Core.Sources;

namespace Mnemarr.Core.Titles;

/// <summary>
/// Gives a conversation its title (ADR 0028). The title is the conversation's own, stored apart from its note: the note is written again from the raw
/// lines, with the new title, and read into the index again. Nothing is collected again. What a person typed is never replaced by what a model wrote.
/// </summary>
public sealed class ConversationTitles
{
    private static readonly TimeSpan LockWait = TimeSpan.FromSeconds(10);
    private readonly IConversationStore _store;
    private readonly RawConversations _raw;
    private readonly SourceCatalog _catalog;
    private readonly SourceLocks _locks;
    private readonly SourceIndexer _indexer;

    public ConversationTitles(IConversationStore store, RawConversations raw, SourceCatalog catalog, SourceLocks locks, SourceIndexer indexer)
    {
        _store = store;
        _raw = raw;
        _catalog = catalog;
        _locks = locks;
        _indexer = indexer;
    }

    /// <summary>The title a conversation has: the one kept with it, or the first sentence of it when it was never kept (a conversation taken before titles were kept).</summary>
    public string TitleOf(ConversationRecord record) =>
        record.Title ?? ConversationMarkdown.Title(ConversationFormats.Turns(record.Format, _raw.Read(record.Machine, record.Session, record.Lines)), record.Session);

    /// <summary>
    /// Sets the title (and the summary) of a conversation and writes its note again. Null, and nothing changed, when a model's title would replace a
    /// person's. <paramref name="source"/> is <see cref="TitleSources.Person"/> or <see cref="TitleSources.Summary"/>.
    /// </summary>
    public async Task<ConversationRecord?> SetAsync(string machine, string session, string title, string source, string? summary, CancellationToken cancellationToken)
    {
        if (source is not (TitleSources.Person or TitleSources.Summary))
        {
            throw new RejectedException(Problem.Invalid, "A title is typed by a person or written by a model.");
        }

        var clean = TitleText.Clean(title) ?? throw new RejectedException(Problem.Invalid, "A title is a few words.");
        var cleanSummary = source == TitleSources.Person ? null : TitleText.CleanSummary(summary);
        var found = _store.Find(machine, session) ?? throw new RejectedException(Problem.NotFound, "There is no such conversation.");
        var conversations = _catalog.EnsureConversations();
        var gate = _locks.Of(conversations.Id);
        if (!await gate.WaitAsync(LockWait, cancellationToken))
        {
            throw new RejectedException(Problem.Busy, "The conversations are being read: try again in a moment.", TimeSpan.FromSeconds(5));
        }

        try
        {
            // Read again under the lock: a batch or another title may have come in meanwhile.
            found = _store.Find(machine, session) ?? found;
            if (source != TitleSources.Person && found.TitleSource == TitleSources.Person)
            {
                return null;
            }

            // A person's title keeps the summary a model wrote, if any: the person changed the title, not what the conversation is about.
            var keptSummary = source == TitleSources.Person ? found.Summary : cleanSummary;
            var turns = ConversationFormats.Turns(found.Format, _raw.Read(machine, session, found.Lines));
            var heading = new ConversationHeading(found.Format, machine, found.Application, session, found.StartedAt);
            var bytes = NoteFiles.Write(conversations, found.NotePath, ConversationMarkdown.Render(heading, turns, clean, keptSummary));
            await _indexer.IndexNoteAsync(conversations, found.NotePath, cancellationToken);
            _store.SetTitle(machine, session, clean, source, keptSummary, bytes);
            return found with { Title = clean, TitleSource = source, Summary = keptSummary, NoteBytes = bytes, TitleTries = 0 };
        }
        finally
        {
            gate.Release();
        }
    }
}

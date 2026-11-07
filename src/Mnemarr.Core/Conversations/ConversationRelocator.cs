using Mnemarr.Core.Collectors;
using Mnemarr.Core.Indexing;
using Mnemarr.Core.Sources;

namespace Mnemarr.Core.Conversations;

/// <summary>What became of the conversations of a machine that were moved: how many were filed under the other, and how many were already there and were not made twice.</summary>
public sealed record Relocation(int Moved, int AlreadyThere);

/// <summary>
/// Files conversations under another machine, or removes them (ADR 0031): a machine renamed or merged into another, a conversation that a renamed computer
/// takes back, a machine deleted with what it sent. A conversation is a row, its raw lines and a note: the three move or go together, and the note is
/// written again whole from the raw lines (it names its machine), under its new place, with the title it has. A conversation that the other machine holds
/// already is not made twice: the longer copy stays.
/// </summary>
public sealed class ConversationRelocator
{
    private static readonly TimeSpan LockWait = TimeSpan.FromSeconds(30);
    private readonly SourceCatalog _catalog;
    private readonly SourceLocks _locks;
    private readonly SourceIndexer _indexer;
    private readonly RawConversations _raw;
    private readonly IConversationStore _store;
    private readonly IMachineStore _machines;

    public ConversationRelocator(SourceCatalog catalog, SourceLocks locks, SourceIndexer indexer, RawConversations raw, IConversationStore store, IMachineStore machines)
    {
        _machines = machines;
        _catalog = catalog;
        _locks = locks;
        _indexer = indexer;
        _raw = raw;
        _store = store;
    }

    /// <summary>What each name that conversations came from has sent.</summary>
    public IReadOnlyList<MachineActivity> Machines() => _store.Machines();

    /// <summary>Whether conversations are held under this name.</summary>
    public bool Holds(string machine) => _store.OfMachine(machine).Count > 0;

    /// <summary>
    /// A conversation held under names that are not machines (a computer that was installed again under another name) goes to the machine that sends it
    /// now, and goes on from where it was, instead of being made a second time; the other copies of it under such names are dropped. The caller holds the
    /// lock of the source. True when one was taken over.
    /// </summary>
    internal async Task<bool> AdoptAsync(Source source, string session, string machine, CancellationToken cancellationToken)
    {
        var orphans = _store.OfSession(session).Where(held => !string.Equals(held.Machine, machine, StringComparison.Ordinal) && _machines.Get(held.Machine) is null).OrderByDescending(held => held.Lines).ToList();
        if (orphans.Count == 0)
        {
            return false;
        }

        // The longest copy is taken over. The others are the same conversation, with the same lines or fewer, under names that are not machines: a
        // conversation is stored once, so they go (nothing that was in them is lost).
        var taken = await MoveHeldAsync(source, orphans[0], machine, cancellationToken);
        foreach (var copy in orphans.Skip(1))
        {
            Drop(source, copy);
        }

        return taken;
    }

    /// <summary>Files every conversation of a machine under another.</summary>
    public async Task<Relocation> MoveAllAsync(string from, string to, CancellationToken cancellationToken)
    {
        var source = _catalog.EnsureConversations();
        var gate = _locks.Of(source.Id);
        if (!await gate.WaitAsync(LockWait, cancellationToken))
        {
            throw new RejectedException(Problem.Busy, "The conversations are being read: try again in a moment.", TimeSpan.FromSeconds(5));
        }

        try
        {
            int moved = 0, already = 0;
            foreach (var conversation in _store.OfMachine(from))
            {
                if (await MoveHeldAsync(source, conversation, to, cancellationToken))
                {
                    moved++;
                }
                else
                {
                    already++;
                }
            }

            return new Relocation(moved, already);
        }
        finally
        {
            gate.Release();
        }
    }

    /// <summary>Deletes every conversation of a machine: its note, its raw lines and its row, and what the index knew of the note. Returns how many.</summary>
    public async Task<int> EraseAsync(string machine, CancellationToken cancellationToken)
    {
        var source = _catalog.EnsureConversations();
        var gate = _locks.Of(source.Id);
        if (!await gate.WaitAsync(LockWait, cancellationToken))
        {
            throw new RejectedException(Problem.Busy, "The conversations are being read: try again in a moment.", TimeSpan.FromSeconds(5));
        }

        try
        {
            var all = _store.OfMachine(machine);
            foreach (var conversation in all)
            {
                Drop(source, conversation);
            }

            return all.Count;
        }
        finally
        {
            gate.Release();
        }
    }

    /// <summary>
    /// Files one conversation under another machine; the caller holds the lock of the source. False when the other machine holds this session already:
    /// the copy that is there stays, and this one is dropped.
    /// </summary>
    internal async Task<bool> MoveHeldAsync(Source source, ConversationRecord conversation, string to, CancellationToken cancellationToken)
    {
        if (_store.Find(to, conversation.Session) is { } existing)
        {
            // Both hold this conversation: the longer copy is the one kept, so that no line is lost (the shorter is the same file, read earlier).
            if (conversation.Lines <= existing.Lines)
            {
                Drop(source, conversation);
                return false;
            }

            Drop(source, existing);
        }

        var turns = ConversationFormats.Turns(conversation.Format, _raw.Read(conversation.Machine, conversation.Session, conversation.Lines));
        var title = conversation.Title ?? ConversationMarkdown.Title(turns, conversation.Session);
        var path = ConversationMarkdown.NotePath(to, conversation.Application, conversation.StartedAt, conversation.Session);
        var heading = new ConversationHeading(conversation.Format, to, conversation.Application, conversation.Session, conversation.StartedAt);
        var bytes = NoteFiles.Write(source, path, ConversationMarkdown.Render(heading, turns, title, conversation.Summary));
        _raw.Move(conversation.Machine, conversation.Session, to);
        _store.Move(conversation.Machine, conversation.Session, to, path, bytes);
        RemoveNote(source, conversation.NotePath);
        await _indexer.IndexNoteAsync(source, path, cancellationToken);
        return true;
    }

    private void Drop(Source source, ConversationRecord conversation)
    {
        RemoveNote(source, conversation.NotePath);
        _raw.Delete(conversation.Machine, conversation.Session);
        _store.Delete(conversation.Machine, conversation.Session);
    }

    private void RemoveNote(Source source, string relativePath)
    {
        _indexer.RemoveNote(source, relativePath);
        var full = Path.Combine(source.Path, relativePath);
        if (File.Exists(full))
        {
            File.Delete(full);
        }

        // The folders that the note leaves empty (its application, then its machine) go too, up to the source itself.
        var root = Path.GetFullPath(source.Path);
        for (var folder = Path.GetDirectoryName(Path.GetFullPath(full)); folder is not null && folder.Length > root.Length && Directory.Exists(folder) && !Directory.EnumerateFileSystemEntries(folder).Any(); folder = Path.GetDirectoryName(folder))
        {
            Directory.Delete(folder);
        }
    }
}

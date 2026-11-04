using System.Security.Cryptography;
using System.Text;
using Mnemarr.Core.Accounts;
using Mnemarr.Core.Audit;
using Mnemarr.Core.Git;
using Mnemarr.Core.Indexing;
using Mnemarr.Core.Sources;

namespace Mnemarr.Core.Notes;

/// <summary>Who changes a note, and what they may touch: the scope of their key or account, and the name the change is written under (the audit log, the Git commit).</summary>
public sealed record NoteActor(SourceScope Scope, string Name);

/// <summary>A note that was written: where it is, the version of what is on disk now (to send back with the next change), and what became of the commit when the source is a Git repository.</summary>
public sealed record WriteResult(string Path, string Version, GitPublish? Git);

/// <summary>A note that was moved, and what that did to the notes that link to it.</summary>
public sealed record RenameResult(string Path, string Version, int LinksRewritten, int NotesRewritten, int NotesNotRewritten, GitPublish? Git);

/// <summary>
/// Writes the notes of the sources that are open for writing (D-35, ADR 0015, ADR 0023): creates, changes, moves and removes a note, on disk
/// first (atomically: a note is never half written), then in the index at once, then, for a Git source, as one commit pushed to its remote.
/// Everything happens under the lock of the source, so that a scan or a sync never sees a folder that is changing, and a change is refused (409)
/// when the note is no longer the one the caller read (its version is the hash of the file). What may be written is a note of a source that is
/// declared read-write, inside its folder, never behind a link, never hidden. A note that is removed is kept: in the Git history of its
/// repository, or, for a plain folder, in a hidden folder of the source that nothing reads.
/// </summary>
public sealed class NoteWriter
{
    /// <summary>The hidden folder of a plain source where a removed note goes.</summary>
    public const string TrashFolder = ".mnemarr-trash";

    private const int MaxPathLength = 300;
    private static readonly TimeSpan LockWait = TimeSpan.FromSeconds(10);

    private readonly SourceCatalog _catalog;
    private readonly SourcePaths _paths;
    private readonly SourceLocks _locks;
    private readonly SourceIndexer _indexer;
    private readonly SourceSync _sync;
    private readonly INoteBrowser _browser;
    private readonly IAuditLog _audit;

    public NoteWriter(SourceCatalog catalog, SourcePaths paths, SourceLocks locks, SourceIndexer indexer, SourceSync sync, INoteBrowser browser, IAuditLog audit)
    {
        _catalog = catalog;
        _paths = paths;
        _locks = locks;
        _indexer = indexer;
        _sync = sync;
        _browser = browser;
        _audit = audit;
    }

    /// <summary>The version of a note's content: what a change must send back to say which note it is changing.</summary>
    public static string VersionOf(byte[] content) => Convert.ToHexString(SHA256.HashData(content)).ToLowerInvariant();

    public Task<WriteResult> CreateAsync(NoteActor actor, Guid sourceId, string path, string content, CancellationToken cancellationToken) =>
        InLockAsync(actor, sourceId, cancellationToken, async (source, folder) =>
        {
            var full = Resolve(folder, path);
            var bytes = Encode(content);
            if (File.Exists(full) || Directory.Exists(full))
            {
                throw new RejectedException(Problem.Conflict, "There is already a note at this path.");
            }

            WriteAtomic(full, bytes, overwrite: false);
            await _indexer.IndexNoteAsync(source, path, cancellationToken);
            _audit.Append(actor.Name, "note.created", Target(source, path));
            return new WriteResult(path, VersionOf(bytes), Publish(source, $"Add {path}", actor, [path], cancellationToken));
        });

    /// <summary>Replaces the content of a note, if it is still the version the caller read.</summary>
    public Task<WriteResult> UpdateAsync(NoteActor actor, Guid sourceId, string path, string content, string expectedVersion, CancellationToken cancellationToken) =>
        InLockAsync(actor, sourceId, cancellationToken, async (source, folder) =>
        {
            var full = Resolve(folder, path);
            var current = Existing(full);
            if (!string.Equals(VersionOf(current), expectedVersion, StringComparison.OrdinalIgnoreCase))
            {
                throw new RejectedException(Problem.Conflict, "The note changed since it was read: read it again before changing it.");
            }

            var bytes = Encode(content);
            WriteAtomic(full, bytes, overwrite: true);
            await _indexer.IndexNoteAsync(source, path, cancellationToken);
            _audit.Append(actor.Name, "note.updated", Target(source, path));
            return new WriteResult(path, VersionOf(bytes), Publish(source, $"Update {path}", actor, [path], cancellationToken));
        });

    /// <summary>Adds text at the end of a note, whatever its version: what an assistant jotting something down needs, with no read first.</summary>
    public Task<WriteResult> AppendAsync(NoteActor actor, Guid sourceId, string path, string text, CancellationToken cancellationToken) =>
        InLockAsync(actor, sourceId, cancellationToken, async (source, folder) =>
        {
            var full = Resolve(folder, path);
            var current = Encoding.UTF8.GetString(Existing(full));
            var separator = current.Length == 0 || current.EndsWith('\n') ? string.Empty : "\n";
            var bytes = Encode(current + separator + text);
            WriteAtomic(full, bytes, overwrite: true);
            await _indexer.IndexNoteAsync(source, path, cancellationToken);
            _audit.Append(actor.Name, "note.updated", Target(source, path));
            return new WriteResult(path, VersionOf(bytes), Publish(source, $"Append to {path}", actor, [path], cancellationToken));
        });

    /// <summary>
    /// Moves a note to another path, and rewrites the links that lead to it, in the notes of the same source (a note of another source is
    /// not changed: it may be read-only, or not the caller's, and is counted in the result), so that a rename does not break the vault.
    /// </summary>
    public Task<RenameResult> RenameAsync(NoteActor actor, Guid sourceId, string from, string to, CancellationToken cancellationToken) =>
        InLockAsync(actor, sourceId, cancellationToken, async (source, folder) =>
        {
            var oldFull = Resolve(folder, from);
            var newFull = Resolve(folder, to);
            var content = Existing(oldFull);
            if (File.Exists(newFull) || Directory.Exists(newFull))
            {
                throw new RejectedException(Problem.Conflict, "There is already a note at the new path.");
            }

            var backlinks = _browser.Find(source.Id, from, SourceScope.All);
            var movedText = LinkRetargeter.Rebase(Encoding.UTF8.GetString(content), from, to).Text;
            Directory.CreateDirectory(Path.GetDirectoryName(newFull)!);
            WriteAtomic(newFull, Encode(movedText), overwrite: false);
            File.Delete(oldFull);
            PruneEmptyFolders(Path.GetDirectoryName(oldFull)!, folder);
            var touched = new List<string> { from, to };
            var (links, notes) = RewriteBacklinks(source, folder, backlinks, from, to, touched);
            _indexer.RemoveNote(source, from);
            foreach (var path in touched.Skip(1))
            {
                await _indexer.IndexNoteAsync(source, path, cancellationToken);
            }

            _audit.Append(actor.Name, "note.renamed", Target(source, from), Target(source, to));
            var published = Publish(source, $"Rename {from} to {to}", actor, touched, cancellationToken);
            var others = (backlinks?.BacklinksTotal ?? 0) - notes;
            return new RenameResult(to, VersionOf(File.ReadAllBytes(newFull)), links, notes, Math.Max(others, 0), published);
        });

    /// <summary>Removes a note: from a Git repository by a commit (its history keeps it), from a plain folder into the hidden trash folder of the source.</summary>
    public Task<GitPublish?> DeleteAsync(NoteActor actor, Guid sourceId, string path, string? expectedVersion, CancellationToken cancellationToken) =>
        InLockAsync(actor, sourceId, cancellationToken, (source, folder) =>
        {
            var full = Resolve(folder, path);
            var current = Existing(full);
            if (expectedVersion is not null && !string.Equals(VersionOf(current), expectedVersion, StringComparison.OrdinalIgnoreCase))
            {
                throw new RejectedException(Problem.Conflict, "The note changed since it was read: read it again before removing it.");
            }

            if (source.Repository is null)
            {
                var trash = Path.Combine(folder, TrashFolder);
                var kept = Path.GetFullPath(Path.Combine(trash, DateTime.UtcNow.ToString("yyyyMMddHHmmss", System.Globalization.CultureInfo.InvariantCulture), path));
                if (!kept.StartsWith(trash + Path.DirectorySeparatorChar, StringComparison.Ordinal))
                {
                    throw new RejectedException(Problem.Invalid, "The path of a note stays inside its source, and never goes through a link.");
                }

                Directory.CreateDirectory(Path.GetDirectoryName(kept)!);
                File.Move(full, kept);
            }
            else
            {
                File.Delete(full);
            }

            PruneEmptyFolders(Path.GetDirectoryName(full)!, folder);
            _indexer.RemoveNote(source, path);
            _audit.Append(actor.Name, "note.removed", Target(source, path));
            return Task.FromResult(Publish(source, $"Remove {path}", actor, [path], cancellationToken));
        });

    private async Task<T> InLockAsync<T>(NoteActor actor, Guid sourceId, CancellationToken cancellationToken, Func<Source, string, Task<T>> change)
    {
        // A source out of the caller's scope is as absent as one that does not exist.
        var source = _catalog.Get(sourceId, actor.Scope);
        // The permission was checked by the caller; what is checked here is what belongs to the source: its kind, then its write switch.
        var rights = NoteRights.Evaluate(source.Kind, source.Mode, mayWriteNotes: true);
        if (!rights.CanWrite)
        {
            throw new RejectedException(Problem.Forbidden, rights.Refusal);
        }

        string folder;
        try
        {
            folder = _paths.Allowed(source.Path);
        }
        catch (RejectedException)
        {
            throw new RejectedException(Problem.Conflict, "The folder of this source is not available.");
        }

        var gate = _locks.Of(source.Id);
        if (!await gate.WaitAsync(LockWait, cancellationToken))
        {
            throw new RejectedException(Problem.Busy, "This source is being synchronised or read: try again in a moment.", TimeSpan.FromSeconds(5));
        }

        try
        {
            if (source.Repository is not null)
            {
                await _sync.BringUpToDateAsync(source, cancellationToken);
            }

            return await change(source, folder);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // What the disk said (a path, a name) is not for the caller.
            throw new RejectedException(Problem.Conflict, "The note could not be written: try again in a moment.");
        }
        finally
        {
            gate.Release();
        }
    }

    private GitPublish? Publish(Source source, string message, NoteActor actor, IReadOnlyCollection<string> paths, CancellationToken cancellationToken) =>
        source.Repository is null ? null : _sync.Publish(source, message, actor.Name, paths, cancellationToken);

    private static (int Links, int Notes) RewriteBacklinks(Source source, string folder, NoteDetail? detail, string from, string to, List<string> touched)
    {
        var links = 0;
        var notes = 0;
        foreach (var path in (detail?.Backlinks ?? []).Where(backlink => backlink.SourceId == source.Id && backlink.Path != from).Select(backlink => backlink.Path))
        {
            var changed = RewriteLinksOf(folder, path, from, to);
            if (changed > 0)
            {
                touched.Add(path);
                links += changed;
                notes++;
            }
        }

        return (links, notes);
    }

    /// <summary>Points the links of one note at the new path of a note that moved; how many were changed (0 when the note cannot be read or has none).</summary>
    private static int RewriteLinksOf(string folder, string path, string from, string to)
    {
        string full;
        try
        {
            full = Resolve(folder, path);
        }
        catch (RejectedException)
        {
            return 0;
        }

        if (!File.Exists(full))
        {
            return 0;
        }

        var (text, changed) = LinkRetargeter.Retarget(Encoding.UTF8.GetString(File.ReadAllBytes(full)), path, from, to);
        if (changed > 0)
        {
            WriteAtomic(full, Encode(text), overwrite: true);
        }

        return changed;
    }

    /// <summary>Removes the folders a move or a removal left empty, up to (not including) the folder of the source.</summary>
    private static void PruneEmptyFolders(string folder, string root)
    {
        for (var current = folder; current.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.Ordinal) && !Directory.EnumerateFileSystemEntries(current).Any(); current = Path.GetDirectoryName(current)!)
        {
            Directory.Delete(current);
        }
    }

    private static string Target(Source source, string path) => $"{source.Name}/{path}";

    private static byte[] Encode(string content)
    {
        if (content.Contains('\0', StringComparison.Ordinal))
        {
            throw new RejectedException(Problem.Invalid, "A note is text: it holds no NUL character.");
        }

        return Encoding.UTF8.GetByteCount(content) > MarkdownFileScanner.MaxFileBytes
            ? throw new RejectedException(Problem.Invalid, $"A note is at most {MarkdownFileScanner.MaxFileBytes / (1024 * 1024)} MB.")
            : Encoding.UTF8.GetBytes(content);
    }

    private static byte[] Existing(string full)
    {
        if (!File.Exists(full))
        {
            throw new RejectedException(Problem.NotFound, "There is no such note.");
        }

        var bytes = File.ReadAllBytes(full);
        return bytes.Length > MarkdownFileScanner.MaxFileBytes ? throw new RejectedException(Problem.NotFound, "There is no such note.") : bytes;
    }

    /// <summary>The full path of a note of a source, or a refusal: a Markdown file, relative, inside the folder, not hidden, not behind a link.</summary>
    private static string Resolve(string folder, string relative)
    {
        var parts = relative.Split('/');
        if (relative.Length is 0 or > MaxPathLength || relative.Any(char.IsControl) || relative.Contains('\\', StringComparison.Ordinal) || Path.IsPathRooted(relative)
            || parts.Any(part => part.Length is 0 or > 200 || part is ".." or "." || part.StartsWith('.'))
            || !MarkdownNotes.MarkdownExtensions.Any(extension => relative.EndsWith(extension, StringComparison.OrdinalIgnoreCase)))
        {
            throw new RejectedException(Problem.Invalid, "The path of a note is relative to its source, made of plain names (none hidden), and ends in .md.");
        }

        var full = Path.GetFullPath(Path.Combine(folder, relative));
        return full.StartsWith(folder + Path.DirectorySeparatorChar, StringComparison.Ordinal) && !SourcePaths.CrossesLink(full, folder)
            ? full
            : throw new RejectedException(Problem.Invalid, "The path of a note stays inside its source, and never goes through a link.");
    }

    /// <summary>Writes beside the note and moves into place: a reader sees the old note or the new one, and a crash leaves the old one.</summary>
    private static void WriteAtomic(string full, byte[] bytes, bool overwrite)
    {
        var directory = Path.GetDirectoryName(full)!;
        Directory.CreateDirectory(directory);
        var temporary = Path.Combine(directory, $".mnemarr-{Guid.NewGuid():N}.tmp");
        try
        {
            File.WriteAllBytes(temporary, bytes);
            File.Move(temporary, full, overwrite);
        }
        finally
        {
            if (File.Exists(temporary))
            {
                File.Delete(temporary);
            }
        }
    }
}

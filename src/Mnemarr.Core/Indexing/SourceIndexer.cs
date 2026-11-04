using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using Mnemarr.Core.Sources;

namespace Mnemarr.Core.Indexing;

/// <summary>What one pass over a source did.</summary>
public sealed record IndexReport(int Added, int Updated, int Removed, int Unchanged);

/// <summary>
/// Brings the index of a source in line with its folder (F-IDX-01): a note whose content hash and cut did not change is left alone,
/// a new or changed one is cut again and replaced, one that has gone is removed. Only one pass runs at a time for a source, so that a source that hangs (a share that does not answer) holds back only itself. The folder is checked
/// against the roots before every pass, and how the pass ended is kept for the source.
/// </summary>
public sealed class SourceIndexer
{
    private readonly INoteIndex _index;
    private readonly SourcePaths _paths;
    private readonly ScanOutcomes _outcomes;
    private readonly TimeProvider _clock;
    private readonly ConcurrentDictionary<Guid, SemaphoreSlim> _passes = new();

    public SourceIndexer(INoteIndex index, SourcePaths paths, ScanOutcomes outcomes, TimeProvider clock)
    {
        _index = index;
        _paths = paths;
        _outcomes = outcomes;
        _clock = clock;
    }

    /// <param name="source">The source to bring up to date.</param>
    /// <param name="rebuild">Cut every note again, even those that did not change: the index is derived and can always be rebuilt (D-03).</param>
    /// <param name="waitForTurn">Wait for the pass that is running (the background scan does); otherwise refuse with <see cref="Problem.Busy"/>, so that requests cannot queue without limit.</param>
    /// <param name="cancellationToken">Stops the pass.</param>
    public async Task<IndexReport> IndexAsync(Source source, bool rebuild, bool waitForTurn, CancellationToken cancellationToken)
    {
        var onePass = _passes.GetOrAdd(source.Id, _ => new SemaphoreSlim(1, 1));
        if (!await onePass.WaitAsync(waitForTurn ? Timeout.InfiniteTimeSpan : TimeSpan.Zero, cancellationToken))
        {
            throw new RejectedException(Problem.Busy, "A pass over this source is running: try again in a moment.", TimeSpan.FromSeconds(5));
        }

        try
        {
            // After the wait, not before: the roots or the folder can change while a pass ahead of this one runs.
            CheckFolder(source);
            var report = await PassAsync(source, rebuild, cancellationToken);
            _outcomes.Succeeded(source.Id, _clock.GetUtcNow());
            return report;
        }
        catch (Exception ex) when (ex is not (OperationCanceledException or RejectedException))
        {
            // The disk (IOException, UnauthorizedAccessException) or the index (SqliteException): either way the pass did not finish, and the
            // source must not look healthy. A refusal already recorded its own code, and a cancellation is not a failure.
            _outcomes.Failed(source.Id, _clock.GetUtcNow(), ex is IOException or UnauthorizedAccessException ? ScanOutcome.Unreadable : ScanOutcome.IndexFailed);
            throw;
        }
        finally
        {
            onePass.Release();
        }
    }

    public Task<IndexReport> IndexAsync(Source source, CancellationToken cancellationToken) => IndexAsync(source, rebuild: false, waitForTurn: true, cancellationToken);

    public Task<IndexReport> IndexAsync(Source source, bool rebuild, CancellationToken cancellationToken) => IndexAsync(source, rebuild, waitForTurn: true, cancellationToken);

    /// <summary>Forgets what is kept of the sources that are not declared any more: how their last pass ended, and their lock.</summary>
    public void Retain(IReadOnlyCollection<Guid> declared)
    {
        _outcomes.Retain(declared);
        foreach (var id in _passes.Keys.Where(id => !declared.Contains(id)))
        {
            _passes.TryRemove(id, out _);
        }
    }

    /// <summary>The folder is still one a source may be in, and is still there: the roots can be narrowed, and a folder can go or become a link, after the declaration.</summary>
    private void CheckFolder(Source source)
    {
        try
        {
            _paths.Allowed(source.Path);
        }
        catch (RejectedException)
        {
            throw Failed(source, ScanOutcome.OutsideRoots, $"The folder of the source '{source.Name}' is not inside the folders the operator allows for sources any more.");
        }

        if (!Directory.Exists(source.Path))
        {
            throw Failed(source, ScanOutcome.FolderMissing, $"The folder of the source '{source.Name}' does not exist any more.");
        }
    }

    private RejectedException Failed(Source source, string code, string message)
    {
        _outcomes.Failed(source.Id, _clock.GetUtcNow(), code);
        return new RejectedException(Problem.Conflict, message);
    }

    private async Task<IndexReport> PassAsync(Source source, bool rebuild, CancellationToken cancellationToken)
    {
        var known = _index.Notes(source.Id);
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var counts = new Dictionary<FileResult, int>();

        foreach (var file in MarkdownFileScanner.Scan(source.Path))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (SourcePaths.CrossesLink(file.FullPath, source.Path))
            {
                // The file, or a folder above it, was swapped for a link since the listing (a pull can do it): not a note any more, and what it led to is not read.
                continue;
            }

            known.TryGetValue(file.RelativePath, out var previous);
            var result = await IndexFileAsync(source, file, previous, rebuild, cancellationToken);
            if (result != FileResult.Gone)
            {
                seen.Add(file.RelativePath);
            }

            counts[result] = counts.GetValueOrDefault(result) + 1;
        }

        var removed = 0;
        foreach (var path in known.Keys.Where(path => !seen.Contains(path)))
        {
            _index.Remove(source.Id, path);
            removed++;
        }

        return new IndexReport(counts.GetValueOrDefault(FileResult.Added), counts.GetValueOrDefault(FileResult.Updated), removed, counts.GetValueOrDefault(FileResult.Unchanged));
    }

    private enum FileResult
    {
        /// <summary>Gone since the scan: it is removed from the index.</summary>
        Gone,

        /// <summary>There, but it cannot be read now: it keeps what it had.</summary>
        Kept,
        Unchanged,
        Added,
        Updated,
    }

    private async Task<FileResult> IndexFileAsync(Source source, ScannedFile file, NoteState? previous, bool rebuild, CancellationToken cancellationToken)
    {
        if (!rebuild && previous is { } same && same.Chunker == MarkdownChunker.Version && same.Length == file.Length && same.ModifiedTicks == file.ModifiedTicks)
        {
            // Same size and same time as when it was read: not read again, so that a pass costs what changed and not the size of the vault.
            return FileResult.Unchanged;
        }

        if (await ReadAsync(file, cancellationToken) is not { } bytes)
        {
            return File.Exists(file.FullPath) ? FileResult.Kept : FileResult.Gone;
        }

        var hash = Convert.ToHexString(SHA256.HashData(bytes));
        if (!rebuild && previous is { } state && state.ContentHash == hash && state.Chunker == MarkdownChunker.Version)
        {
            // Touched, not changed: the content is what was cut, so only what the next pass compares with is brought up to date.
            _index.Touch(source.Id, file);
            return FileResult.Unchanged;
        }

        var text = Decode(bytes);
        _index.Upsert(source.Id, file, MarkdownChunker.Title(text, file.RelativePath), hash, MarkdownChunker.Version, MarkdownChunker.Split(text), _clock.GetUtcNow());
        return previous is null ? FileResult.Added : FileResult.Updated;
    }

    /// <summary>
    /// The content of a note, or null when it cannot be read now: removed since the scan, locked, not allowed. A file that grew since the
    /// scan is read no further than the limit, and is not a note.
    /// </summary>
    private static async Task<byte[]?> ReadAsync(ScannedFile file, CancellationToken cancellationToken)
    {
        try
        {
            await using var stream = new FileStream(file.FullPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            var buffer = new byte[Math.Min(stream.Length, MarkdownFileScanner.MaxFileBytes + 1)];
            await stream.ReadExactlyAsync(buffer, cancellationToken);
            return buffer.Length > MarkdownFileScanner.MaxFileBytes ? null : buffer;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>UTF-8 text without its byte order mark: a Windows editor writes one, and it would hide the first heading of the note.</summary>
    private static string Decode(byte[] bytes)
    {
        var skip = bytes.AsSpan().StartsWith(Encoding.UTF8.Preamble) ? Encoding.UTF8.Preamble.Length : 0;
        return Encoding.UTF8.GetString(bytes, skip, bytes.Length - skip);
    }
}

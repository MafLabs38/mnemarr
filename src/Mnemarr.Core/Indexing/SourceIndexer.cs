using System.Security.Cryptography;
using System.Text;
using Mnemarr.Core.Sources;

namespace Mnemarr.Core.Indexing;

/// <summary>What one pass over a source did.</summary>
public sealed record IndexReport(int Added, int Updated, int Removed, int Unchanged);

/// <summary>
/// Brings the index of a source in line with its folder (F-IDX-01): a note whose content hash and cut did not change is left alone,
/// a new or changed one is cut again and replaced, one that has gone is removed. Only one pass runs at a time. The folder is checked
/// against the roots before every pass, and how the pass ended is kept for the source.
/// </summary>
public sealed class SourceIndexer
{
    private readonly INoteIndex _index;
    private readonly SourcePaths _paths;
    private readonly ScanOutcomes _outcomes;
    private readonly TimeProvider _clock;
    private readonly SemaphoreSlim _onePass = new(1, 1);

    public SourceIndexer(INoteIndex index, SourcePaths paths, ScanOutcomes outcomes, TimeProvider clock)
    {
        _index = index;
        _paths = paths;
        _outcomes = outcomes;
        _clock = clock;
    }

    /// <param name="source">The source to bring up to date.</param>
    /// <param name="rebuild">Cut every note again, even those that did not change: the index is derived and can always be rebuilt (D-03).</param>
    /// <param name="cancellationToken">Stops the pass.</param>
    public async Task<IndexReport> IndexAsync(Source source, bool rebuild, CancellationToken cancellationToken)
    {
        CheckFolder(source);
        await _onePass.WaitAsync(cancellationToken);
        try
        {
            var report = await PassAsync(source, rebuild, cancellationToken);
            _outcomes.Succeeded(source.Id, _clock.GetUtcNow());
            return report;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _outcomes.Failed(source.Id, _clock.GetUtcNow(), ScanOutcome.Unreadable);
            throw;
        }
        finally
        {
            _onePass.Release();
        }
    }

    public Task<IndexReport> IndexAsync(Source source, CancellationToken cancellationToken) => IndexAsync(source, rebuild: false, cancellationToken);

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
        int added = 0, updated = 0, unchanged = 0;

        foreach (var file in MarkdownFileScanner.Scan(source.Path))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (await ReadAsync(file, cancellationToken) is not { } bytes)
            {
                // Gone since the scan: it is removed below. One that is there but cannot be read keeps what it had.
                if (File.Exists(file.FullPath))
                {
                    seen.Add(file.RelativePath);
                }

                continue;
            }

            seen.Add(file.RelativePath);
            var hash = Convert.ToHexString(SHA256.HashData(bytes));
            known.TryGetValue(file.RelativePath, out var previous);
            if (!rebuild && previous is { } state && state.ContentHash == hash && state.Chunker == MarkdownChunker.Version)
            {
                unchanged++;
                continue;
            }

            var text = Decode(bytes);
            _index.Upsert(source.Id, file, MarkdownChunker.Title(text, file.RelativePath), hash, MarkdownChunker.Version, MarkdownChunker.Split(text), _clock.GetUtcNow());
            if (previous is null)
            {
                added++;
            }
            else
            {
                updated++;
            }
        }

        var removed = 0;
        foreach (var path in known.Keys.Where(path => !seen.Contains(path)))
        {
            _index.Remove(source.Id, path);
            removed++;
        }

        return new IndexReport(added, updated, removed, unchanged);
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

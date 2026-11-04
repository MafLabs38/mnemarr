using System.Security.Cryptography;
using System.Text;
using Mnemarr.Core.Sources;

namespace Mnemarr.Core.Indexing;

/// <summary>What one pass over a source did.</summary>
public sealed record IndexReport(int Added, int Updated, int Removed, int Unchanged);

/// <summary>
/// Brings the index of a source in line with its folder (F-IDX-01): a note whose content hash did not change is left alone,
/// a new or changed one is cut again and replaced, one that has gone is removed. Only one pass runs at a time.
/// </summary>
public sealed class SourceIndexer
{
    private readonly INoteIndex _index;
    private readonly TimeProvider _clock;
    private readonly SemaphoreSlim _onePass = new(1, 1);

    public SourceIndexer(INoteIndex index, TimeProvider clock)
    {
        _index = index;
        _clock = clock;
    }

    public async Task<IndexReport> IndexAsync(Source source, CancellationToken cancellationToken)
    {
        if (!Directory.Exists(source.Path))
        {
            throw new RejectedException(Problem.Conflict, $"The folder of the source '{source.Name}' does not exist any more.");
        }

        await _onePass.WaitAsync(cancellationToken);
        try
        {
            return await PassAsync(source, cancellationToken);
        }
        finally
        {
            _onePass.Release();
        }
    }

    private async Task<IndexReport> PassAsync(Source source, CancellationToken cancellationToken)
    {
        var known = _index.Hashes(source.Id);
        var seen = new HashSet<string>(StringComparer.Ordinal);
        int added = 0, updated = 0, unchanged = 0;

        foreach (var file in MarkdownFileScanner.Scan(source.Path))
        {
            cancellationToken.ThrowIfCancellationRequested();
            seen.Add(file.RelativePath);
            var bytes = await File.ReadAllBytesAsync(file.FullPath, cancellationToken);
            var hash = Convert.ToHexString(SHA256.HashData(bytes));
            if (known.TryGetValue(file.RelativePath, out var previous) && previous == hash)
            {
                unchanged++;
                continue;
            }

            var text = Encoding.UTF8.GetString(bytes);
            _index.Upsert(source.Id, file, MarkdownChunker.Title(text, file.RelativePath), hash, MarkdownChunker.Split(text), _clock.GetUtcNow());
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
}

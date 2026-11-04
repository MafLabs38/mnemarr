using System.Collections.Concurrent;

namespace Mnemarr.Core.Sources;

/// <summary>
/// One lock per source, so that what changes the folder of a source (a sync of its repository) and what reads it (a pass of the indexer) do not
/// run at once on the same source, and that a source that hangs (a share, a host) holds back only itself.
/// </summary>
public sealed class SourceLocks
{
    private readonly ConcurrentDictionary<Guid, SemaphoreSlim> _locks = new();

    public SemaphoreSlim Of(Guid sourceId) => _locks.GetOrAdd(sourceId, _ => new SemaphoreSlim(1, 1));

    /// <summary>Forgets the locks of the sources that are not declared any more.</summary>
    public void Retain(IReadOnlyCollection<Guid> declared)
    {
        foreach (var id in _locks.Keys.Where(id => !declared.Contains(id)))
        {
            _locks.TryRemove(id, out _);
        }
    }
}

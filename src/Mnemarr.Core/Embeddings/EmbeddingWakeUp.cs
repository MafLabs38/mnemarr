namespace Mnemarr.Core.Embeddings;

/// <summary>
/// How the index tells the embedding indexer that there is work (ADR 0039): a chunk is new, or chunks are gone. The indexer sleeps until it is told or until its safety timer ends,
/// so that an idle host does not look at every chunk every few seconds to find that nothing changed.
/// </summary>
public sealed class EmbeddingWakeUp
{
    private readonly SemaphoreSlim _signal = new(0, 1);
    private int _removed;

    /// <summary>Chunks were added or replaced: the indexer should look.</summary>
    public void Notify()
    {
        try
        {
            _ = _signal.Release();
        }
        catch (SemaphoreFullException)
        {
            // Already told: one look covers every change before it.
        }
    }

    /// <summary>Chunks are gone: their vectors are to be forgotten at the next look.</summary>
    public void NotifyRemoved()
    {
        Volatile.Write(ref _removed, 1);
        Notify();
    }

    /// <summary>Whether chunks went since the last time this was asked.</summary>
    public bool TakeRemoved() => Interlocked.Exchange(ref _removed, 0) == 1;

    /// <summary>Waits to be told, for at most <paramref name="timeout"/>: true when it was told, false when the time ran out. Throws when cancelled.</summary>
    public Task<bool> WaitAsync(TimeSpan timeout, CancellationToken cancellationToken) =>
        _signal.WaitAsync(timeout, cancellationToken);
}

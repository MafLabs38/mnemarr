using System.Collections.Concurrent;

namespace Mnemarr.Core.Git;

/// <summary>How the last sync of a source ended: when, the commit it is at, and the failure code when it failed (never an address, a path or a message from the host).</summary>
public sealed record SyncOutcome(DateTimeOffset At, string? Error, string? Head)
{
    public bool Failed => Error is not null;
}

/// <summary>
/// The last outcome of the sync of each Git source, for the source resource and for readiness (F-HLT-01): a checkout that cannot be brought
/// up to date keeps its old notes and stays searchable, so the failure must be somewhere other than the log. Held in memory: it describes the running process.
/// </summary>
public sealed class SyncOutcomes
{
    private readonly ConcurrentDictionary<Guid, SyncOutcome> _outcomes = new();

    public SyncOutcome? Of(Guid sourceId) => _outcomes.GetValueOrDefault(sourceId);

    public void Succeeded(Guid sourceId, DateTimeOffset at, string head) => _outcomes[sourceId] = new SyncOutcome(at, null, head);

    public void Failed(Guid sourceId, DateTimeOffset at, string error) => _outcomes[sourceId] = new SyncOutcome(at, error, _outcomes.GetValueOrDefault(sourceId)?.Head);

    public void Retain(IReadOnlyCollection<Guid> declared)
    {
        foreach (var id in _outcomes.Keys.Where(id => !declared.Contains(id)))
        {
            _outcomes.TryRemove(id, out _);
        }
    }
}

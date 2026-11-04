using System.Collections.Concurrent;

namespace Mnemarr.Core.Indexing;

/// <summary>How the last pass over a source ended: when, and the reason code when it failed (never a path or a message from the disk).</summary>
public sealed record ScanOutcome(DateTimeOffset At, string? Error)
{
    public const string FolderMissing = "folder-missing";
    public const string OutsideRoots = "outside-roots";
    public const string Unreadable = "unreadable";

    public bool Failed => Error is not null;
}

/// <summary>
/// The last outcome of the pass over each source. A source whose folder was unmounted keeps its old notes and stays searchable, so the
/// failure must be somewhere other than the log: here, for the source resource and for the readiness probe (F-HLT-01). Held in memory:
/// it describes the running process, and the next pass rewrites it.
/// </summary>
public sealed class ScanOutcomes
{
    private readonly ConcurrentDictionary<Guid, ScanOutcome> _outcomes = new();

    public ScanOutcome? Of(Guid sourceId) => _outcomes.GetValueOrDefault(sourceId);

    public void Succeeded(Guid sourceId, DateTimeOffset at) => _outcomes[sourceId] = new ScanOutcome(at, null);

    public void Failed(Guid sourceId, DateTimeOffset at, string error) => _outcomes[sourceId] = new ScanOutcome(at, error);
}

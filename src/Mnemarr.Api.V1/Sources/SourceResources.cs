using Mnemarr.Core.Indexing;
using Mnemarr.Core.Sources;

namespace Mnemarr.Api.V1.Sources;

/// <summary>
/// A declared source, with what is known of its index and of its last pass: <c>lastError</c> is a stable code (<c>folder-missing</c>,
/// <c>outside-roots</c>, <c>unreadable</c>) when the last pass failed, and the notes of the source are then those of the pass before.
/// <c>managed</c>: a configuration document owns it, and it is changed there.</summary>
public sealed record SourceResource(
    Guid Id, string Name, string Kind, string Mode, string Path, DateTimeOffset CreatedAt, int Notes, int Chunks, DateTimeOffset? LastIndexedAt,
    DateTimeOffset? LastScanAt, string? LastError, bool Managed)
{
    public static SourceResource From(Source source, IndexCounts counts, ScanOutcome? scan) =>
        new(
            source.Id, source.Name, source.Kind.ToString(), source.Mode.ToString(), source.Path, source.CreatedAt, counts.Notes, counts.Chunks,
            counts.LastIndexedAt, scan?.At, scan?.Error, source.Managed);
}

public sealed record CreateSourceRequest(string Name, string Kind, string Mode, string Path);

/// <summary>What can change about a source: not its kind, which is the rule it lives under.</summary>
public sealed record UpdateSourceRequest(string Name, string Mode, string Path);

/// <summary>A kind of source, the rule behind it and how many are declared: the base the interface shows, with what is missing.</summary>
public sealed record SourceKindResource(string Kind, bool Required, bool Unique, int Count, bool Satisfied, string DefaultMode)
{
    public static SourceKindResource From(KindStatus status) =>
        new(status.Rule.Kind.ToString(), status.Rule.Required, status.Rule.Unique, status.Count, status.Satisfied, status.Rule.DefaultMode.ToString());
}

/// <summary>What one pass over a source did.</summary>
public sealed record IndexReportResource(int Added, int Updated, int Removed, int Unchanged)
{
    public static IndexReportResource From(IndexReport report) => new(report.Added, report.Updated, report.Removed, report.Unchanged);
}

/// <summary>A chunk of a note that matches a search, with where it comes from.</summary>
public sealed record SearchHitResource(Guid SourceId, string SourceName, string Path, string Topic, string Title, string Heading, string Snippet, double Score)
{
    public static SearchHitResource From(SearchHit hit) =>
        new(hit.SourceId, hit.SourceName, hit.Path, hit.Topic, hit.Title, hit.Heading, hit.Snippet, hit.Score);
}

using Mnemarr.Core.Embeddings;
using Mnemarr.Core.Git;
using Mnemarr.Core.Indexing;
using Mnemarr.Core.Sources;

namespace Mnemarr.Api.V1.Sources;

/// <summary>
/// A declared source, with what is known of its index and of its last pass: <c>lastError</c> is a stable code (<c>folder-missing</c>,
/// <c>outside-roots</c>, <c>unreadable</c>, <c>index-failed</c>) when the last pass failed, and the notes of the source are then those of the pass before.
/// <c>managed</c>: a configuration document owns it, and it is changed there. A source that is the checkout of a Git repository has its
/// <c>repository</c> (never a secret: the name of the credential only) and how its last sync ended (<c>lastSyncError</c> is a stable code).</summary>
public sealed record SourceResource(
    Guid Id, string Name, string Kind, string Mode, string Path, DateTimeOffset CreatedAt, int Notes, int Chunks, DateTimeOffset? LastIndexedAt,
    DateTimeOffset? LastScanAt, string? LastError, bool Managed, GitRepositoryResource? Repository = null, DateTimeOffset? LastSyncAt = null,
    string? LastSyncError = null, string? Head = null)
{
    public static SourceResource From(Source source, IndexCounts counts, ScanOutcome? scan, SyncOutcome? sync = null) =>
        new(
            source.Id, source.Name, source.Kind.ToString(), source.Mode.ToString(), source.Path, source.CreatedAt, counts.Notes, counts.Chunks,
            counts.LastIndexedAt, scan?.At, scan?.Error, source.Managed, GitRepositoryResource.From(source.Repository), sync?.At, sync?.Error, sync?.Head);
}

/// <summary>The repository a source is the checkout of: its address, its branch (the default one when null) and the name of the credential it uses.</summary>
public sealed record GitRepositoryResource(string Url, string? Branch, string? Credential)
{
    public static GitRepositoryResource? From(GitRepository? repository) => repository is null ? null : new(repository.Url, repository.Branch, repository.Credential);

    public GitRepository ToModel() => new(Url, Branch, Credential);
}

/// <summary>A source is a folder, or, with a <c>repository</c>, the checkout of one: the folder is then where it is cloned.</summary>
public sealed record CreateSourceRequest(string Name, string Kind, string Mode, string Path, GitRepositoryResource? Repository = null);

/// <summary>What can change about a source: not its kind, which is the rule it lives under.</summary>
public sealed record UpdateSourceRequest(string Name, string Mode, string Path, GitRepositoryResource? Repository = null);

/// <summary>What a sync of a Git source did: how its checkout changed (<c>Cloned</c>, <c>Updated</c> or <c>UpToDate</c>), the commit it is at, and what reading it found.</summary>
public sealed record SyncReportResource(string Action, string Head, IndexReportResource? Index)
{
    public static SyncReportResource From(SourceSyncReport report) =>
        new(report.Action.ToString(), report.Head, report.Index is null ? null : IndexReportResource.From(report.Index));
}

/// <summary>A kind of source, the rule behind it and how many are declared: the base the interface shows, with what is missing.</summary>
public sealed record SourceKindResource(string Kind, bool Required, bool Unique, int Count, bool Satisfied, string DefaultMode, bool Declarable)
{
    public static SourceKindResource From(KindStatus status) =>
        new(status.Rule.Kind.ToString(), status.Rule.Required, status.Rule.Unique, status.Count, status.Satisfied, status.Rule.DefaultMode.ToString(), status.Rule.Declarable);
}

/// <summary>What one pass over a source did.</summary>
public sealed record IndexReportResource(int Added, int Updated, int Removed, int Unchanged)
{
    public static IndexReportResource From(IndexReport report) => new(report.Added, report.Updated, report.Removed, report.Unchanged);
}

/// <summary>A chunk of a note that matches a search, with where it comes from.</summary>
public sealed record SearchHitResource(Guid SourceId, string SourceName, string Path, string Topic, string Title, string Heading, string Snippet, double Score, string Match)
{
    public static SearchHitResource From(SearchHit hit) =>
        new(hit.SourceId, hit.SourceName, hit.Path, hit.Topic, hit.Title, hit.Heading, hit.Snippet, hit.Score, hit.Match);
}

/// <summary>The state of the search by meaning (ADR 0034): <c>off</c>, <c>starting</c>, <c>ready</c> or <c>failed</c>, with the model, the passages computed of those that exist, and why it failed.</summary>
/// <summary><c>Device</c> says where the passages were computed last: <c>cpu</c>, <c>cuda</c>, or an embedding server and its device (ADR 0040).</summary>
public sealed record SemanticSearchResource(string State, string Model, int Passages, int Computed, string? Reason, string Device)
{
    public static SemanticSearchResource From(EmbeddingReport report) =>
        new(report.State.ToString().ToLowerInvariant(), report.Model, report.Progress?.Chunks ?? 0, report.Progress?.Embedded ?? 0, report.Reason, report.Device);
}

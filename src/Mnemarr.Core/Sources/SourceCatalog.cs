using Mnemarr.Core.Accounts;
using Mnemarr.Core.Audit;

namespace Mnemarr.Core.Sources;

/// <summary>
/// Declares sources. A source is a folder Mnemarr is allowed to read, so its path must be one that the operator put at its
/// disposal: inside one of the roots of <see cref="SourcesOptions"/> (by default the <c>sources</c> folder of the data folder),
/// and not a link that could lead out of it.
/// </summary>
public sealed class SourceCatalog
{
    private const int MaxNameLength = 100;

    /// <summary>One change to the sources at a time: the rules count, and a count is only true if nobody else changes it meanwhile.</summary>
    internal Lock Gate { get; } = new();

    private readonly ISourceStore _store;
    private readonly IAuditLog _audit;
    private readonly SourcePaths _paths;
    private readonly TimeProvider _clock;

    public SourceCatalog(ISourceStore store, IAuditLog audit, SourcePaths paths, TimeProvider clock)
    {
        _store = store;
        _audit = audit;
        _paths = paths;
        _clock = clock;
    }

    /// <summary>The folders a source may be in.</summary>
    public IReadOnlyList<string> Roots() => _paths.Roots();

    /// <summary>The sources a caller may touch, by name.</summary>
    public IReadOnlyList<Source> List(SourceScope scope) => [.. _store.List().Where(source => scope.CanAccess(source.Id))];

    /// <summary>A source the caller may touch. One that exists but is out of the caller's scope is as absent as one that does not exist.</summary>
    public Source Get(Guid id, SourceScope scope) =>
        _store.Get(id) is { } source && scope.CanAccess(id) ? source : throw new RejectedException(Problem.NotFound, "There is no such source.");

    /// <summary>
    /// Each kind with its rule and how many sources of it the caller can see: what the interface shows as the base and what is missing.
    /// A source out of the caller's scope is as absent as one that does not exist, so it is not counted either.
    /// </summary>
    public IReadOnlyList<KindStatus> Kinds(SourceScope scope)
    {
        var sources = List(scope);
        return [.. SourceKinds.All.Select(rule => new KindStatus(rule, sources.Count(source => source.Kind == rule.Kind)))];
    }

    /// <summary>The kinds and their counts over every source: what readiness is about, not a caller.</summary>
    public IReadOnlyList<KindStatus> Kinds() => Kinds(SourceScope.All);

    /// <summary>
    /// Declares a source. A caller limited to some sources cannot: the new source would be outside what it may touch, and a folder is
    /// how a caller asks Mnemarr to read it.
    /// </summary>
    public Source Add(string name, SourceKind kind, SourceMode mode, string path, SourceScope scope, string actor)
    {
        RefuseIfScoped(scope, "declare a source");
        name = CheckedName(name);
        lock (Gate)
        {
            var rule = SourceKinds.Of(kind);
            if (rule.Max is { } max && _store.List().Count(source => source.Kind == kind) >= max)
            {
                // The name of the one that exists is not given: a caller limited to some sources may not see it.
                throw new RejectedException(Problem.Conflict, $"There is already a source of the kind {kind}: there can be only {max}. Change its folder instead.");
            }

            var source = new Source(Guid.NewGuid(), name, kind, mode, CheckedPath(path), _clock.GetUtcNow());
            _store.Add(source);
            _audit.Append(actor, "source.added", source.Name, $"{kind}, {mode}");
            return source;
        }
    }

    /// <summary>
    /// Changes the name, the mode and the folder of a source. Its kind never changes: the rule behind it is the business of the kind, and a
    /// source of another kind is another source. The notes of the old folder are replaced by those of the new one at the next pass.
    /// A caller limited to some sources changes the mode only: the name and the folder say which part of the disk is read, and repointing
    /// a source at another source's folder would give the caller that folder's notes.
    /// </summary>
    public Source Update(Guid id, string name, SourceMode mode, string path, SourceScope scope, string actor)
    {
        name = CheckedName(name);
        lock (Gate)
        {
            var current = Get(id, scope);
            RefuseIfManaged(current);
            // Before any look at the disk: a caller who may not change the folder must not learn, from the kind of refusal, what is there.
            if (scope.Sources is not null && (name != current.Name || Path.TrimEndingDirectorySeparator(Path.GetFullPath(path)) != current.Path))
            {
                throw new RejectedException(Problem.Forbidden, "A key limited to some sources can change the mode of a source, not its name or its folder.");
            }

            var changed = current with { Name = name, Mode = mode, Path = scope.Sources is null ? CheckedPath(path) : current.Path };

            if (!_store.Update(changed))
            {
                throw new RejectedException(Problem.NotFound, "There is no such source.");
            }

            _audit.Append(actor, "source.changed", changed.Name, $"{changed.Mode}, {changed.Path}");
            return changed;
        }
    }

    /// <summary>Forgets a source, with its notes and chunks. Its folder is not touched. A source the base requires cannot be forgotten.</summary>
    public void Remove(Guid id, SourceScope scope, string actor)
    {
        lock (Gate)
        {
            var source = Get(id, scope);
            RefuseIfManaged(source);
            if (SourceKinds.Of(source.Kind).Min >= _store.List().Count(other => other.Kind == source.Kind))
            {
                throw new RejectedException(Problem.Conflict, $"The {source.Kind} source is required: change its folder instead of removing it.");
            }

            _store.Remove(id);
            _audit.Append(actor, "source.removed", source.Name);
        }
    }

    private static void RefuseIfScoped(SourceScope scope, string what)
    {
        if (scope.Sources is not null)
        {
            throw new RejectedException(Problem.Forbidden, $"A key limited to some sources cannot {what}.");
        }
    }

    /// <summary>A source a configuration document owns is changed by changing the document: the document is the truth, and a hand edit would be undone at the next application.</summary>
    private static void RefuseIfManaged(Source source)
    {
        if (source.Managed)
        {
            throw new RejectedException(Problem.Conflict, $"The source '{source.Name}' is managed by configuration: change the configuration instead.");
        }
    }

    internal static string CheckedName(string name)
    {
        name = name.Trim();
        return name.Length is 0 or > MaxNameLength
            ? throw new RejectedException(Problem.Invalid, $"The name of a source is 1 to {MaxNameLength} characters.")
            : name;
    }

    /// <summary>A folder a source may be in (see <see cref="SourcePaths"/>), that exists.</summary>
    internal string CheckedPath(string path)
    {
        var full = _paths.Allowed(path);
        return Directory.Exists(full) ? full : throw new RejectedException(Problem.Invalid, "The folder of the source does not exist.");
    }
}

/// <summary>A kind of source with its rule and how many sources of it exist.</summary>
public sealed record KindStatus(SourceKindRule Rule, int Count)
{
    /// <summary>Whether the rule is met: a required kind needs at least its minimum.</summary>
    public bool Satisfied => Count >= Rule.Min;
}

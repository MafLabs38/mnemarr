using Microsoft.Extensions.Options;
using Mnemarr.Core.Accounts;
using Mnemarr.Core.Audit;
using Mnemarr.Core.Configuration;

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
    private readonly IOptions<MnemarrOptions> _options;
    private readonly IOptions<SourcesOptions> _sources;
    private readonly TimeProvider _clock;

    public SourceCatalog(ISourceStore store, IAuditLog audit, IOptions<MnemarrOptions> options, IOptions<SourcesOptions> sources, TimeProvider clock)
    {
        _store = store;
        _audit = audit;
        _options = options;
        _sources = sources;
        _clock = clock;
    }

    /// <summary>The folders a source may be in.</summary>
    public IReadOnlyList<string> Roots() =>
        _sources.Value.Roots.Length > 0
            ? [.. _sources.Value.Roots.Select(Path.GetFullPath)]
            : [Path.GetFullPath(Path.Combine(_options.Value.DataFolder, "sources"))];

    /// <summary>The sources a caller may touch, by name.</summary>
    public IReadOnlyList<Source> List(SourceScope scope) => [.. _store.List().Where(source => scope.CanAccess(source.Id))];

    /// <summary>A source the caller may touch. One that exists but is out of the caller's scope is as absent as one that does not exist.</summary>
    public Source Get(Guid id, SourceScope scope) =>
        _store.Get(id) is { } source && scope.CanAccess(id) ? source : throw new RejectedException(Problem.NotFound, "There is no such source.");

    /// <summary>Each kind with its rule and how many sources of it are declared: what the interface shows as the base and what is missing.</summary>
    public IReadOnlyList<KindStatus> Kinds()
    {
        var sources = _store.List();
        return [.. SourceKinds.All.Select(rule => new KindStatus(rule, sources.Count(source => source.Kind == rule.Kind)))];
    }

    public Source Add(string name, SourceKind kind, SourceMode mode, string path, string actor)
    {
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
    /// </summary>
    public Source Update(Guid id, string name, SourceMode mode, string path, SourceScope scope, string actor)
    {
        var current = Get(id, scope);
        RefuseIfManaged(current);
        var changed = current with { Name = CheckedName(name), Mode = mode, Path = CheckedPath(path) };
        lock (Gate)
        {
            if (!_store.Update(changed))
            {
                throw new RejectedException(Problem.NotFound, "There is no such source.");
            }
        }

        _audit.Append(actor, "source.changed", changed.Name, $"{changed.Mode}, {changed.Path}");
        return changed;
    }

    /// <summary>Forgets a source, with its notes and chunks. Its folder is not touched. A source the base requires cannot be forgotten.</summary>
    public void Remove(Guid id, SourceScope scope, string actor)
    {
        var source = Get(id, scope);
        RefuseIfManaged(source);
        lock (Gate)
        {
            if (SourceKinds.Of(source.Kind).Min >= _store.List().Count(other => other.Kind == source.Kind))
            {
                throw new RejectedException(Problem.Conflict, $"The {SourceKinds.Of(source.Kind).Title.ToLowerInvariant()} is required: change its folder instead of removing it.");
            }

            _store.Remove(id);
        }

        _audit.Append(actor, "source.removed", source.Name);
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

    internal string CheckedPath(string path)
    {
        if (!Path.IsPathFullyQualified(path))
        {
            throw new RejectedException(Problem.Invalid, "The path of a source must be an absolute path.");
        }

        var full = Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar);
        if (!Roots().Any(root => IsInside(root, full)))
        {
            throw new RejectedException(Problem.Invalid, "The path of a source must be inside one of the folders the operator allows for sources.");
        }

        var directory = new DirectoryInfo(full);
        if (!directory.Exists)
        {
            throw new RejectedException(Problem.Invalid, "The folder of the source does not exist.");
        }

        if (directory.LinkTarget is not null)
        {
            throw new RejectedException(Problem.Invalid, "The folder of a source cannot be a link.");
        }

        return full;
    }

    private static bool IsInside(string root, string path)
    {
        var relative = Path.GetRelativePath(root, path);
        return relative != ".." && !relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal) && !Path.IsPathRooted(relative);
    }
}

/// <summary>A kind of source with its rule and how many sources of it exist.</summary>
public sealed record KindStatus(SourceKindRule Rule, int Count)
{
    /// <summary>Whether the rule is met: a required kind needs at least its minimum.</summary>
    public bool Satisfied => Count >= Rule.Min;
}

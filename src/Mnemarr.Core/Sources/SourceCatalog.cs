using Mnemarr.Core.Accounts;
using Mnemarr.Core.Audit;
using Mnemarr.Core.Git;

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
    private readonly GitPolicy? _git;

    public SourceCatalog(ISourceStore store, IAuditLog audit, SourcePaths paths, TimeProvider clock, GitPolicy? git = null)
    {
        _git = git;
        _store = store;
        _audit = audit;
        _paths = paths;
        _clock = clock;
    }

    /// <summary>The folders a source may be in.</summary>
    public IReadOnlyList<string> Roots() => _paths.Roots();

    /// <summary>The sources a caller may touch, by name.</summary>
    public IReadOnlyList<Source> List(SourceScope scope) => [.. _store.List().Where(scope.CanSee)];

    /// <summary>A source the caller may touch. One that exists but is out of the caller's scope is as absent as one that does not exist.</summary>
    public Source Get(Guid id, SourceScope scope) =>
        _store.Get(id) is { } source && scope.CanSee(source) ? source : throw new RejectedException(Problem.NotFound, "There is no such source.");

    /// <summary>
    /// Each kind with its rule and how many sources of it the caller can see: what the interface shows as the base and what is missing.
    /// A source out of the caller's scope is as absent as one that does not exist, so it is not counted either.
    /// </summary>
    public IReadOnlyList<KindStatus> Kinds(SourceScope scope)
    {
        var sources = List(scope);
        // The conversations are not a kind a caller who may not read them is told about.
        return [.. SourceKinds.All.Where(rule => !(scope.ConversationsHidden && rule.Kind == SourceKind.Conversations)).Select(rule => new KindStatus(rule, sources.Count(source => source.Kind == rule.Kind)))];
    }

    /// <summary>The kinds and their counts over every source: what readiness is about, not a caller.</summary>
    public IReadOnlyList<KindStatus> Kinds() => Kinds(SourceScope.All);

    /// <summary>
    /// Declares a source. A caller limited to some sources cannot: the new source would be outside what it may touch, and a folder is
    /// how a caller asks Mnemarr to read it.
    /// </summary>
    public Source Add(string name, SourceKind kind, SourceMode mode, string path, SourceScope scope, string actor, GitRepository? repository = null)
    {
        RefuseIfScoped(scope, "declare a source");
        if (!SourceKinds.Of(kind).Declarable)
        {
            throw new RejectedException(Problem.Invalid, "A source of this kind is created by Mnemarr, not declared.");
        }

        name = CheckedName(name);
        lock (Gate)
        {
            var checkedRepository = CheckedRepository(repository);
            var source = new Source(Guid.NewGuid(), name, kind, mode, checkedRepository is null ? CheckedPath(path) : CheckedCheckoutFolder(path, null), _clock.GetUtcNow(), Repository: checkedRepository);
            Insert(source, actor);
            return source;
        }
    }

    /// <summary>
    /// The source that holds the conversations the collectors send (ADR 0024): created by Mnemarr the first time it is needed, in its own folder,
    /// and owned by Mnemarr (nobody edits or forgets it by hand). The one that exists is returned.
    /// </summary>
    public Source EnsureConversations()
    {
        lock (Gate)
        {
            if (_store.List().FirstOrDefault(source => source.Kind == SourceKind.Conversations) is { } existing)
            {
                return existing;
            }

            var folder = _paths.ConversationsFolder;
            Directory.CreateDirectory(folder);
            var source = new Source(Guid.NewGuid(), ConversationsName, SourceKind.Conversations, SourceMode.ReadOnly, folder, _clock.GetUtcNow(), Managed: true);
            Insert(source, "mnemarr");
            return source;
        }
    }

    /// <summary>
    /// The writes of configuration as code (ADR 0020). They are the same writes as the hand path, with the same rules and the same audit
    /// events, so that whatever a change of a source brings later is written once. The caller holds <see cref="Gate"/>, has checked the
    /// document, and passes a name and a folder that were checked.
    /// </summary>
    internal void AddManaged(string name, SourceKind kind, SourceMode mode, string path, GitRepository? repository, string actor) =>
        Insert(new Source(Guid.NewGuid(), name, kind, mode, path, _clock.GetUtcNow(), Managed: true, Repository: repository), actor);

    internal void ChangeManaged(Source current, string name, SourceMode mode, string path, GitRepository? repository, string actor)
    {
        var changed = current with { Name = name, Mode = mode, Path = path, Managed = true, Repository = repository };
        if (!_store.Update(changed))
        {
            throw new RejectedException(Problem.NotFound, "There is no such source.");
        }

        _audit.Append(actor, "source.changed", changed.Name, $"{changed.Mode}, {changed.Path}{Describe(changed.Repository)}");
    }

    internal void RemoveManaged(Source source, string actor)
    {
        _store.Remove(source.Id);
        _audit.Append(actor, "source.removed", source.Name);
    }

    private void Insert(Source source, string actor)
    {
        var rule = SourceKinds.Of(source.Kind);
        if (rule.Max is { } max && _store.List().Count(other => other.Kind == source.Kind) >= max)
        {
            // The name of the one that exists is not given: a caller limited to some sources may not see it.
            throw new RejectedException(Problem.Conflict, "There is already a source of this kind: there can be only {max}. Change its folder instead.".Replace("{max}", max.ToString(System.Globalization.CultureInfo.InvariantCulture), StringComparison.Ordinal));
        }

        _store.Add(source);
        _audit.Append(actor, "source.added", source.Name, $"{source.Kind}, {source.Mode}{Describe(source.Repository)}");
    }

    /// <summary>
    /// Changes the name, the mode and the folder of a source. Its kind never changes: the rule behind it is the business of the kind, and a
    /// source of another kind is another source. The notes of the old folder are replaced by those of the new one at the next pass.
    /// A caller limited to some sources changes the mode only: the name and the folder say which part of the disk is read, and repointing
    /// a source at another source's folder would give the caller that folder's notes.
    /// </summary>
    public Source Update(Guid id, string name, SourceMode mode, string path, SourceScope scope, string actor, GitRepository? repository = null)
    {
        name = CheckedName(name);
        lock (Gate)
        {
            var current = Get(id, scope);
            RefuseIfManaged(current);
            // Before any look at the disk: a caller who may not change the folder must not learn, from the kind of refusal, what is there.
            if (scope.Sources is not null && ChangesIdentity(current, name, path, repository))
            {
                throw new RejectedException(Problem.Forbidden, "A key limited to some sources can change the mode of a source, not its name, its folder or its repository.");
            }

            var checkedRepository = scope.Sources is null ? CheckedRepository(repository) : current.Repository;
            var folder = current.Path;
            if (scope.Sources is null)
            {
                folder = checkedRepository is null ? CheckedPath(path) : CheckedCheckoutFolder(path, id);
            }

            var changed = current with { Name = name, Mode = mode, Path = folder, Repository = checkedRepository };

            if (!_store.Update(changed))
            {
                throw new RejectedException(Problem.NotFound, "There is no such source.");
            }

            _audit.Append(actor, "source.changed", changed.Name, $"{changed.Mode}, {changed.Path}{Describe(changed.Repository)}");
            return changed;
        }
    }

    /// <summary>Forgets a source, with its notes and chunks. Its folder is not touched. A source the base requires cannot be forgotten.</summary>
    public void Remove(Guid id, SourceScope scope, string actor)
    {
        lock (Gate)
        {
            var source = Get(id, scope);
            RefuseIfScoped(scope, "forget a source");
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

    /// <summary>The name of the source that holds the conversations the collectors send.</summary>
    public const string ConversationsName = "conversations";

    internal static string CheckedName(string name)
    {
        name = name.Trim();
        if (name.Length is 0 or > MaxNameLength || name.Any(char.IsControl))
        {
            throw new RejectedException(Problem.Invalid, $"The name of a source is 1 to {MaxNameLength} characters, with no control character: it is written in logs and in the audit trail.");
        }

        // The name Mnemarr gives to what it collects: a source declared under it would clash with it at the first conversation, and keep it from being taken.
        return string.Equals(name, ConversationsName, StringComparison.OrdinalIgnoreCase)
            ? throw new RejectedException(Problem.Invalid, $"The name '{ConversationsName}' is reserved.")
            : name;
    }

    /// <summary>A folder a source may be in (see <see cref="SourcePaths"/>), that exists.</summary>
    internal string CheckedPath(string path)
    {
        var full = _paths.Allowed(path);
        return Directory.Exists(full) ? full : throw new RejectedException(Problem.Invalid, "The folder of the source does not exist.");
    }

    /// <summary>A folder a source may be in, that need not exist yet: the folder of a Git source is where its repository will be cloned.</summary>
    internal string CheckedFolder(string path) => _paths.Allowed(path);

    /// <summary>
    /// The folder a repository is cloned into: it is not inside the folder of another source, and holds none (what an outside repository
    /// brings would otherwise appear as notes of that source, the memory vault included).
    /// </summary>
    internal string CheckedCheckoutFolder(string path, Guid? self, string? sameName = null)
    {
        var folder = CheckedFolder(path);
        return _store.List().Where(other => other.Id != self && other.Name != sameName).Any(other => Contains(other.Path, folder) || Contains(folder, other.Path))
            ? throw new RejectedException(Problem.Invalid, "A Git checkout cannot be inside the folder of another source, nor contain one.")
            : folder;
    }

    private static bool Contains(string parent, string child) =>
        string.Equals(parent, child, StringComparison.Ordinal) || child.StartsWith(parent.TrimEnd(System.IO.Path.DirectorySeparatorChar) + System.IO.Path.DirectorySeparatorChar, StringComparison.Ordinal);

    /// <summary>Whether a change touches what makes a source this one: its name, its folder or its repository (compared as they are stored, not as typed).</summary>
    private static bool ChangesIdentity(Source current, string name, string path, GitRepository? repository) =>
        name != current.Name || SourcePaths.Normalize(path) != current.Path || !GitPolicy.Same(repository, current.Repository);

    /// <summary>The repository in its normal form, or a refusal. None is allowed (a source that is a plain folder).</summary>
    internal GitRepository? CheckedRepository(GitRepository? repository) =>
        repository is null ? null : (_git ?? throw new RejectedException(Problem.Invalid, "Git sources are not available.")).Check(repository);

    private static string Describe(GitRepository? repository) => repository is null ? string.Empty : $", git {repository.Url}";
}

/// <summary>A kind of source with its rule and how many sources of it exist.</summary>
public sealed record KindStatus(SourceKindRule Rule, int Count)
{
    /// <summary>Whether the rule is met: a required kind needs at least its minimum.</summary>
    public bool Satisfied => Count >= Rule.Min;
}

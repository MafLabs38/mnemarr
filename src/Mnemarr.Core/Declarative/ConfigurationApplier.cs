using Mnemarr.Core.Accounts;
using Mnemarr.Core.Audit;
using Mnemarr.Core.Git;
using Mnemarr.Core.Sources;

namespace Mnemarr.Core.Declarative;

/// <summary>
/// Brings the sources to what a configuration document says (ADR 0020). Everything is checked before anything is changed: a document with
/// a single error changes nothing. What the document owns is marked as managed, and only that is ever removed by it. The steps then run
/// one after the other under the lock of the catalog, so nothing else changes the sources meanwhile; if the database fails halfway, the
/// document is not fully applied and nothing says it was, and applying it again (the file service does) finishes the job: it is idempotent.
/// </summary>
public sealed class ConfigurationApplier
{
    public const string MemoryName = "memory";
    private readonly SourceCatalog _catalog;
    private readonly ISourceStore _store;
    private readonly IAuditLog _audit;

    public ConfigurationApplier(SourceCatalog catalog, ISourceStore store, IAuditLog audit)
    {
        _catalog = catalog;
        _store = store;
        _audit = audit;
    }

    /// <summary>The sources a caller may touch, as a document.</summary>
    public string Export(SourceScope scope) => ConfigurationYaml.Export(_store.List().Where(scope.CanSee));

    /// <summary>What the document would do, without doing it.</summary>
    public ConfigPlan Plan(ConfigurationDocument document)
    {
        lock (_catalog.Gate)
        {
            return Build(document, string.Empty).Plan;
        }
    }

    /// <summary>Does what the document says, or nothing at all when it has an error. Idempotent: applying the same document again changes nothing.</summary>
    public ConfigPlan Apply(ConfigurationDocument document, string actor)
    {
        lock (_catalog.Gate)
        {
            var (plan, steps) = Build(document, actor);
            if (!plan.Valid)
            {
                return plan;
            }

            foreach (var step in steps)
            {
                step();
            }

            var changes = plan.Items.Where(item => item.Action != PlanAction.Keep).ToList();
            if (changes.Count > 0)
            {
                _audit.Append(actor, "config.applied", "sources", AuditDetail(changes));
            }

            return plan;
        }
    }

    /// <summary>"1 create, 2 remove (a, b)": what was done, and the names of what was removed, which is what an audit asks of configuration as code.</summary>
    private static string AuditDetail(List<PlanItem> changes)
    {
        var counts = string.Join(", ", changes.GroupBy(item => item.Action).Select(group => $"{group.Count()} {group.Key.ToString().ToLowerInvariant()}"));
        var removed = changes.Where(item => item.Action == PlanAction.Remove).Select(item => item.Name).ToList();
        return removed.Count == 0 ? counts : $"{counts}; removed: {string.Join(", ", removed)}";
    }

    private (ConfigPlan Plan, List<Action> Steps) Build(ConfigurationDocument document, string actor)
    {
        var errors = new List<string>();
        var wanted = WantedSources(document, errors);
        var current = _store.List();
        var items = new List<PlanItem>();
        var steps = new Steps();

        // What the document owns in a section it describes, and does not list any more, is removed. Known first: the names it frees
        // can be taken again by the same document.
        var kept = wanted.Select(w => Find(current, w)?.Id).OfType<Guid>().ToHashSet();
        var gone = SectionsPresent(document)
            .Where(kind => kind != SourceKind.Memory)
            .SelectMany(kind => current.Where(source => source.Kind == kind && source.Managed && !kept.Contains(source.Id)))
            .ToList();
        var freed = gone.Select(source => source.Id).ToHashSet();

        foreach (var w in wanted)
        {
            PlanOne(w, current, freed, items, steps, errors, actor);
        }

        foreach (var source in gone)
        {
            items.Add(new PlanItem(PlanAction.Remove, source.Kind, source.Name, null, null));
            steps.Removals.Add(() => _catalog.RemoveManaged(source, actor));
        }

        // Removals first (they free names), then the changes to what exists, then what is new.
        return (new ConfigPlan(items, errors), [.. steps.Removals, .. steps.Changes, .. steps.Creations]);
    }

    /// <summary>The source an entry is about. The memory vault is one: it is the one that exists, whatever its name. Any other is found by its name.</summary>
    private static Source? Find(IReadOnlyList<Source> current, Wanted w) =>
        w.Kind == SourceKind.Memory
            ? current.FirstOrDefault(source => source.Kind == SourceKind.Memory)
            : current.FirstOrDefault(source => source.Kind == w.Kind && source.Name == w.Name);

    private void PlanOne(Wanted w, IReadOnlyList<Source> current, HashSet<Guid> freed, List<PlanItem> items, Steps steps, List<string> errors, string actor)
    {
        var existing = Find(current, w);
        if (current.FirstOrDefault(source => source.Name == w.Name && source.Id != existing?.Id && !freed.Contains(source.Id)) is { } owner)
        {
            errors.Add($"The name '{w.Name}' is already taken by a source of the kind {owner.Kind}.");
            return;
        }

        if (existing is null)
        {
            items.Add(new PlanItem(PlanAction.Create, w.Kind, w.Name, w.Path, w.Mode));
            steps.Creations.Add(() => _catalog.AddManaged(w.Name, w.Kind, w.Mode, w.Path, w.Repository, actor));
            return;
        }

        var action = ActionFor(existing, w);
        items.Add(new PlanItem(action, w.Kind, w.Name, w.Path, w.Mode));
        if (action != PlanAction.Keep)
        {
            steps.Changes.Add(() => _catalog.ChangeManaged(existing, w.Name, w.Mode, w.Path, w.Repository, actor));
        }
    }

    /// <summary>A source made by hand is always taken over; one the document already owns changes only if it differs.</summary>
    private static PlanAction ActionFor(Source existing, Wanted w)
    {
        if (!existing.Managed)
        {
            return PlanAction.Adopt;
        }

        return existing.Name == w.Name && existing.Path == w.Path && existing.Mode == w.Mode && existing.Repository == w.Repository ? PlanAction.Keep : PlanAction.Update;
    }

    private sealed class Steps
    {
        public List<Action> Removals { get; } = [];

        public List<Action> Changes { get; } = [];

        public List<Action> Creations { get; } = [];
    }

    private static IEnumerable<SourceKind> SectionsPresent(ConfigurationDocument document)
    {
        if (document.Memory is not null)
        {
            yield return SourceKind.Memory;
        }

        if (document.Knowledge is not null)
        {
            yield return SourceKind.Knowledge;
        }

        if (document.ApplicationDocs is not null)
        {
            yield return SourceKind.ApplicationDocs;
        }
    }

    private List<Wanted> WantedSources(ConfigurationDocument document, List<string> errors)
    {
        var wanted = new List<Wanted>();
        if (document.Memory is not null)
        {
            Add(wanted, errors, SourceKind.Memory, document.Memory, "memory", MemoryName);
        }

        AddAll(wanted, errors, SourceKind.Knowledge, document.Knowledge, "knowledge");
        AddAll(wanted, errors, SourceKind.ApplicationDocs, document.ApplicationDocs, "applicationDocs");
        foreach (var duplicate in wanted.GroupBy(w => w.Name, StringComparer.Ordinal).Where(group => group.Count() > 1))
        {
            errors.Add($"The name '{duplicate.Key}' is used by more than one entry: a name is one source.");
        }

        return wanted;
    }

    private void AddAll(List<Wanted> wanted, List<string> errors, SourceKind kind, List<SourceEntry>? entries, string section)
    {
        if (entries is null)
        {
            return;
        }

        for (var i = 0; i < entries.Count; i++)
        {
            Add(wanted, errors, kind, entries[i], $"{section}[{i}]", null);
        }
    }

    private void Add(List<Wanted> wanted, List<string> errors, SourceKind kind, SourceEntry? entry, string where, string? defaultName)
    {
        if (entry is null)
        {
            errors.Add($"{where}: it is empty.");
            return;
        }

        var name = string.IsNullOrWhiteSpace(entry.Name) ? defaultName : entry.Name;
        var problems = new List<string>();
        if (name is null)
        {
            problems.Add("it has no name");
        }

        if (string.IsNullOrWhiteSpace(entry.Path))
        {
            problems.Add("it has no path");
        }

        var mode = SourceKinds.Of(kind).DefaultMode;
        if (entry.Mode is not null && (!Enum.TryParse(entry.Mode, ignoreCase: false, out mode) || !Enum.IsDefined(mode)))
        {
            problems.Add("its mode is ReadOnly or ReadWrite");
        }

        if (problems.Count > 0)
        {
            errors.Add($"{where}: {string.Join(", ", problems)}.");
            return;
        }

        try
        {
            var repository = entry.Repository is null ? null : _catalog.CheckedRepository(new GitRepository(entry.Repository.Url ?? string.Empty, entry.Repository.Branch, entry.Repository.Credential));
            var folder = repository is null ? _catalog.CheckedPath(entry.Path!) : _catalog.CheckedCheckoutFolder(entry.Path!, null, name);
            wanted.Add(new Wanted(kind, SourceCatalog.CheckedName(name!), folder, mode, repository));
        }
        catch (RejectedException ex)
        {
            errors.Add($"{where}: {ex.Message}");
        }
    }

    private sealed record Wanted(SourceKind Kind, string Name, string Path, SourceMode Mode, GitRepository? Repository);
}

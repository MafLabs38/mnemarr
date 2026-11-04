using Mnemarr.Core.Accounts;
using Mnemarr.Core.Audit;
using Mnemarr.Core.Sources;

namespace Mnemarr.Core.Declarative;

/// <summary>
/// Brings the sources to what a configuration document says (ADR 0020). Everything is checked before anything is changed: a document with
/// a single error changes nothing. What the document owns is marked as managed, and only that is ever removed by it.
/// </summary>
public sealed class ConfigurationApplier
{
    public const string MemoryName = "memory";
    private readonly SourceCatalog _catalog;
    private readonly ISourceStore _store;
    private readonly IAuditLog _audit;
    private readonly TimeProvider _clock;

    public ConfigurationApplier(SourceCatalog catalog, ISourceStore store, IAuditLog audit, TimeProvider clock)
    {
        _catalog = catalog;
        _store = store;
        _audit = audit;
        _clock = clock;
    }

    /// <summary>The sources a caller may touch, as a document.</summary>
    public string Export(SourceScope scope) => ConfigurationYaml.Export(_store.List().Where(source => scope.CanAccess(source.Id)));

    /// <summary>What the document would do, without doing it.</summary>
    public ConfigPlan Plan(ConfigurationDocument document)
    {
        lock (_catalog.Gate)
        {
            return Build(document).Plan;
        }
    }

    /// <summary>Does what the document says, or nothing at all when it has an error. Idempotent: applying the same document again changes nothing.</summary>
    public ConfigPlan Apply(ConfigurationDocument document, string actor)
    {
        lock (_catalog.Gate)
        {
            var (plan, steps) = Build(document);
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
                _audit.Append(actor, "config.applied", "sources", string.Join(", ", changes.GroupBy(item => item.Action).Select(group => $"{group.Count()} {group.Key.ToString().ToLowerInvariant()}")));
            }

            return plan;
        }
    }

    private (ConfigPlan Plan, List<Action> Steps) Build(ConfigurationDocument document)
    {
        var errors = new List<string>();
        var wanted = WantedSources(document, errors);
        var current = _store.List();
        var items = new List<PlanItem>();
        var steps = new Steps();
        var matched = new HashSet<Guid>();

        foreach (var w in wanted)
        {
            PlanOne(w, current, matched, items, steps, errors);
        }

        foreach (var kind in SectionsPresent(document).Where(kind => kind != SourceKind.Memory))
        {
            foreach (var gone in current.Where(source => source.Kind == kind && source.Managed && !matched.Contains(source.Id)))
            {
                items.Add(new PlanItem(PlanAction.Remove, gone.Kind, gone.Name, null, null));
                steps.Removals.Add(() => _store.Remove(gone.Id));
            }
        }

        // Removals first (they free names), then the changes to what exists, then what is new.
        return (new ConfigPlan(items, errors), [.. steps.Removals, .. steps.Changes, .. steps.Creations]);
    }

    private void PlanOne(Wanted w, IReadOnlyList<Source> current, HashSet<Guid> matched, List<PlanItem> items, Steps steps, List<string> errors)
    {
        // The memory vault is one: it is the one that exists, whatever its name. Any other is found by its name.
        var existing = w.Kind == SourceKind.Memory
            ? current.FirstOrDefault(source => source.Kind == SourceKind.Memory)
            : current.FirstOrDefault(source => source.Kind == w.Kind && source.Name == w.Name);
        if (current.FirstOrDefault(source => source.Name == w.Name && source.Id != existing?.Id) is { } owner)
        {
            errors.Add($"The name '{w.Name}' is already taken by a source of the kind {owner.Kind}.");
            return;
        }

        if (existing is null)
        {
            items.Add(new PlanItem(PlanAction.Create, w.Kind, w.Name, w.Path, w.Mode));
            steps.Creations.Add(() => _store.Add(new Source(Guid.NewGuid(), w.Name, w.Kind, w.Mode, w.Path, _clock.GetUtcNow(), Managed: true)));
            return;
        }

        matched.Add(existing.Id);
        var action = ActionFor(existing, w);
        items.Add(new PlanItem(action, w.Kind, w.Name, w.Path, w.Mode));
        if (action != PlanAction.Keep)
        {
            steps.Changes.Add(() => _store.Update(existing with { Name = w.Name, Path = w.Path, Mode = w.Mode, Managed = true }));
        }
    }

    /// <summary>A source made by hand is always taken over; one the document already owns changes only if it differs.</summary>
    private static PlanAction ActionFor(Source existing, Wanted w)
    {
        if (!existing.Managed)
        {
            return PlanAction.Adopt;
        }

        return existing.Name == w.Name && existing.Path == w.Path && existing.Mode == w.Mode ? PlanAction.Keep : PlanAction.Update;
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
        for (var i = 0; i < (entries?.Count ?? 0); i++)
        {
            Add(wanted, errors, kind, entries![i], $"{section}[{i}]", null);
        }
    }

    private void Add(List<Wanted> wanted, List<string> errors, SourceKind kind, SourceEntry entry, string where, string? defaultName)
    {
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
            wanted.Add(new Wanted(kind, SourceCatalog.CheckedName(name!), _catalog.CheckedPath(entry.Path!), mode));
        }
        catch (RejectedException ex)
        {
            errors.Add($"{where}: {ex.Message}");
        }
    }

    private sealed record Wanted(SourceKind Kind, string Name, string Path, SourceMode Mode);
}

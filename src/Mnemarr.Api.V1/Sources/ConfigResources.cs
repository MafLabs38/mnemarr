using Mnemarr.Core.Declarative;

namespace Mnemarr.Api.V1.Sources;

/// <summary>What a configuration document does, or would do, to one source.</summary>
public sealed record PlanItemResource(string Action, string Kind, string Name, string? Path, string? Mode)
{
    public static PlanItemResource From(PlanItem item) =>
        new(item.Action.ToString(), item.Kind.ToString(), item.Name, item.Path, item.Mode?.ToString());
}

/// <summary>The outcome of applying a configuration document: every change, and whether it was made (false for a preview).</summary>
public sealed record ConfigPlanResource(bool Applied, IReadOnlyList<PlanItemResource> Items)
{
    public static ConfigPlanResource From(ConfigPlan plan, bool applied) => new(applied, [.. plan.Items.Select(PlanItemResource.From)]);
}

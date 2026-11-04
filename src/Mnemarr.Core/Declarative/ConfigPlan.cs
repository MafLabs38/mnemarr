using Mnemarr.Core.Sources;

namespace Mnemarr.Core.Declarative;

/// <summary>What applying a document does to one source.</summary>
public enum PlanAction
{
    /// <summary>The source is declared.</summary>
    Create,

    /// <summary>The source exists and is managed: its folder, its mode or its name change.</summary>
    Update,

    /// <summary>The source was made by hand: the document takes it over (it is changed to what the document says, and becomes managed).</summary>
    Adopt,

    /// <summary>The source is managed and the document no longer lists it.</summary>
    Remove,

    /// <summary>Nothing to do.</summary>
    Keep,
}

public sealed record PlanItem(PlanAction Action, SourceKind Kind, string Name, string? Path, SourceMode? Mode);

/// <summary>What a document would do, or did: every change, and every reason it cannot be applied. A document with a reason is applied not at all.</summary>
public sealed record ConfigPlan(IReadOnlyList<PlanItem> Items, IReadOnlyList<string> Errors)
{
    public bool Valid => Errors.Count == 0;
}

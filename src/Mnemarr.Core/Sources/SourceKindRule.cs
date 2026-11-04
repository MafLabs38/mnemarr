namespace Mnemarr.Core.Sources;

/// <summary>
/// What a kind of source is for, and how many there can be: the business rule behind the kind (D-05, D-14, D-35). A kind with a minimum
/// is part of the fixed base: it is there, to be filled, and cannot be removed. A kind with a maximum of one cannot be declared twice.
/// The mode of a source is its write switch (D-35), and it is off by default for every kind: an operator turns it on, source by source.
/// </summary>
public sealed record SourceKindRule(SourceKind Kind, int Min, int? Max, SourceMode DefaultMode)
{
    public bool Required => Min > 0;

    public bool Unique => Max == 1;
}

/// <summary>The kinds of source and their rules: a closed list in code, like the roles. A new kind is a decision.</summary>
public static class SourceKinds
{
    public static IReadOnlyList<SourceKindRule> All { get; } =
    [
        new(SourceKind.Memory, Min: 1, Max: 1, SourceMode.ReadOnly),
        new(SourceKind.Knowledge, Min: 0, Max: null, SourceMode.ReadOnly),
        new(SourceKind.ApplicationDocs, Min: 0, Max: null, SourceMode.ReadOnly),
    ];

    public static SourceKindRule Of(SourceKind kind) => All.Single(rule => rule.Kind == kind);
}

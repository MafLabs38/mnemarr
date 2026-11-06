namespace Mnemarr.Core.Sources;

/// <summary>
/// What a kind of source is for, and how many there can be: the business rule behind the kind (D-05, D-14, D-35). A kind with a minimum
/// is part of the fixed base: it is there, to be filled, and cannot be removed. A kind with a maximum of one cannot be declared twice.
/// The mode of a source is its write switch (D-35), and it is off by default for every kind: an operator turns it on, source by source.
/// </summary>
public sealed record SourceKindRule(SourceKind Kind, int Min, int? Max, SourceMode DefaultMode, bool NotesWritable = true, bool Declarable = true)
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
        // The documentation of an application lives in that application's repository, which Mnemarr does not own: it reads it, and never writes it here.
        new(SourceKind.ApplicationDocs, Min: 0, Max: null, SourceMode.ReadOnly, NotesWritable: false),
        // The conversations are written by Mnemarr from what the collectors send: the source is created by Mnemarr, and nobody edits a conversation by hand.
        new(SourceKind.Conversations, Min: 0, Max: 1, SourceMode.ReadOnly, NotesWritable: false, Declarable: false),
    ];

    public static SourceKindRule Of(SourceKind kind) => All.Single(rule => rule.Kind == kind);
}

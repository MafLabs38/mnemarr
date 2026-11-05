using Mnemarr.Core.Sources;

namespace Mnemarr.Core.Notes;

/// <summary>Why a person may or may not write a note: a stable code the interface explains in words.</summary>
public static class NoteRightsReason
{
    /// <summary>The person's role may write notes and the source is open for writing.</summary>
    public const string Writable = "writable";

    /// <summary>The source is declared read-only (D-35): whoever manages sources turns writing on.</summary>
    public const string SourceReadOnly = "source-read-only";

    /// <summary>The kind of the source is never written by hand here (the documentation of an application is written in its repository, conversations come from the collectors).</summary>
    public const string KindNotWritable = "kind-not-writable";

    /// <summary>The person's role (or key) does not hold <c>notes.write</c>.</summary>
    public const string NoPermission = "no-permission";
}

/// <summary>What a caller may do to the notes of a source, and why: one rule for the interface, the API and the MCP tools.</summary>
public sealed record NoteRights(bool CanWrite, string Reason)
{
    /// <summary>
    /// The rights of a caller on the notes of a source of this kind and mode. The permission comes first (a caller who may not write is told so,
    /// whatever the source), then the rule of the kind, then the mode of the source (its write switch).
    /// </summary>
    public static NoteRights Evaluate(SourceKind kind, SourceMode mode, bool mayWriteNotes)
    {
        if (!mayWriteNotes)
        {
            return new NoteRights(false, NoteRightsReason.NoPermission);
        }

        if (!SourceKinds.Of(kind).NotesWritable)
        {
            return new NoteRights(false, NoteRightsReason.KindNotWritable);
        }

        return mode == SourceMode.ReadWrite ? new NoteRights(true, NoteRightsReason.Writable) : new NoteRights(false, NoteRightsReason.SourceReadOnly);
    }

    /// <summary>The refusal a write gets, in fixed words.</summary>
    public string Refusal => Reason switch
    {
        NoteRightsReason.SourceReadOnly => "This source is read-only: whoever manages sources turns writing on for it.",
        NoteRightsReason.KindNotWritable => "This kind of source is not written here: the documentation of an application changes in its repository, and conversations come from the collectors.",
        NoteRightsReason.NoPermission => "Your role may not write notes.",
        _ => string.Empty,
    };
}

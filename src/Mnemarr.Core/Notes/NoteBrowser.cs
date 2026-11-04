using Mnemarr.Core.Accounts;

namespace Mnemarr.Core.Notes;

/// <summary>A note of a source, as the tree of the reader lists it.</summary>
public sealed record NoteEntry(Guid SourceId, string SourceName, string Path, string Title, string Topic);

/// <summary>The notes the caller may read, and how many there are: a tree is cut at a limit, and a cut list says so.</summary>
public sealed record NoteListing(IReadOnlyList<NoteEntry> Notes, int Total);

/// <summary>Where a note is: enough to link to it.</summary>
public sealed record NoteRef(Guid SourceId, string SourceName, string Path, string Title);

/// <summary>
/// A link of a note and the note it leads to, or null when no note of the sources the caller may read has that name. <paramref name="Target"/>
/// is the link as the note wrote it (normalised): what the interface finds in the text.
/// </summary>
public sealed record ResolvedLink(string Target, NoteRef? Note);

/// <summary>What the index knows of a note besides its text: its tags, where its links lead, and which notes link to it.</summary>
public sealed record NoteDetail(NoteRef Note, IReadOnlyList<string> Tags, IReadOnlyList<ResolvedLink> Links, IReadOnlyList<NoteRef> Backlinks);

/// <summary>A tag and how many notes carry it.</summary>
public sealed record TagCount(string Tag, int Count);

/// <summary>The tags of the notes the caller may read, most used first, and how many tags there are in all.</summary>
public sealed record TagListing(IReadOnlyList<TagCount> Tags, int Total);

/// <summary>
/// The notes of the sources as a reader needs them (D-37): the tree, a note with its tags, links and backlinks, and the tags. Every question
/// takes the scope of the caller, so a note of a source that a caller may not read is neither listed, nor linked, nor counted.
/// </summary>
public interface INoteBrowser
{
    /// <summary>The notes the caller may read, by source and path, optionally of one source or carrying one tag.</summary>
    NoteListing List(SourceScope scope, Guid? sourceId, string? tag);

    /// <summary>A note with its tags, links and backlinks, or null when it is not indexed or the caller may not read its source.</summary>
    NoteDetail? Find(Guid sourceId, string path, SourceScope scope);

    TagListing Tags(SourceScope scope);
}

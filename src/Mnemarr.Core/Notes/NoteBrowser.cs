using Mnemarr.Core.Accounts;

namespace Mnemarr.Core.Notes;

/// <summary>A note of a source, as the tree of the reader lists it.</summary>
public sealed record NoteEntry(Guid SourceId, string SourceName, string Path, string Title, string Topic);

/// <summary>The notes the caller may read, and how many there are: a tree is cut at a limit, and a cut list says so.</summary>
public sealed record NoteListing(IReadOnlyList<NoteEntry> Notes, int Total);

/// <summary>Where a note is: enough to link to it.</summary>
public sealed record NoteRef(Guid SourceId, string SourceName, string Path, string Title);

/// <summary>A link of a note (as it is stored: lower case, from the root of the source) and the note it leads to, or null when no note of the sources the caller may read has that name.</summary>
public sealed record ResolvedLink(string Target, NoteRef? Note);

/// <summary>What the index knows of a note besides its text: its tags, where its links lead, and which notes link to it (the first of them, and how many there are in all).</summary>
public sealed record NoteDetail(NoteRef Note, IReadOnlyList<string> Tags, IReadOnlyList<ResolvedLink> Links, IReadOnlyList<NoteRef> Backlinks, int BacklinksTotal);

/// <summary>
/// What the links of one note ask the index, all at once: the links (as stored, to be resolved from the source of the note), the embedded
/// image names, the paths of the notes that ordinary relative links name, and the paths of the images they name.
/// </summary>
public sealed record LinkQuestions(IReadOnlyCollection<string> Targets, IReadOnlyCollection<string> Embeds, IReadOnlyCollection<string> NotePaths, IReadOnlyCollection<string> ImagePaths);

/// <summary>
/// The answers, by what was asked: the note a link leads to (null when none the caller may read), the path of the image an embed names (null
/// when none), the note at a path (null when none), and which of the image paths the last pass found.
/// </summary>
public sealed record LinkAnswers(
    IReadOnlyDictionary<string, NoteRef?> Targets, IReadOnlyDictionary<string, string?> Embeds, IReadOnlyDictionary<string, NoteRef?> NotePaths, IReadOnlySet<string> ImagePaths);

/// <summary>A tag and how many notes carry it.</summary>
public sealed record TagCount(string Tag, int Count);

/// <summary>The tags of the notes the caller may read, most used first, and how many tags there are in all.</summary>
public sealed record TagListing(IReadOnlyList<TagCount> Tags, int Total);

/// <summary>
/// The notes of the sources as a reader needs them (D-37): the tree, a note with its tags, links and backlinks, and the tags. Every question
/// takes the scope of the caller, so a note of a source that a caller may not read is neither listed, nor linked, nor counted.
/// </summary>
/// <summary>The notes whose links lead to a note: the paths of those of its own source (all of them, with no cap) and how many are in other sources.</summary>
public sealed record BacklinkSet(IReadOnlyList<string> InSource, int InOtherSources);

public interface INoteBrowser
{
    /// <summary>
    /// Every note of the same source that links to the note, however many, for a rename that must rewrite them all (a note shown to a person is
    /// capped; this is not), and the count of those in other sources. An unknown note has none.
    /// </summary>
    BacklinkSet BacklinksOf(Guid sourceId, string path);

    /// <summary>The notes the caller may read, by source and path, optionally of one source or carrying one tag.</summary>
    NoteListing List(SourceScope scope, Guid? sourceId, string? tag);

    /// <summary>A note with its tags, links and backlinks, or null when it is not indexed or the caller may not read its source.</summary>
    NoteDetail? Find(Guid sourceId, string path, SourceScope scope);

    TagListing Tags(SourceScope scope);

    /// <summary>Everything the links of one note lead to, answered in one call (one connection): see <see cref="LinkQuestions"/>.</summary>
    LinkAnswers Answer(Guid sourceId, string notePath, LinkQuestions questions, SourceScope scope);

    /// <summary>Whether the image file is one the last pass found in the source: only those are served.</summary>
    bool HasImage(Guid sourceId, string path);
}

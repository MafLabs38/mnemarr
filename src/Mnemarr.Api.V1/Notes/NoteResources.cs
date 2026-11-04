using Mnemarr.Core.Notes;

namespace Mnemarr.Api.V1.Notes;

/// <summary>A note in the tree of the reader.</summary>
public sealed record NoteEntryResource(Guid SourceId, string SourceName, string Path, string Title, string Topic)
{
    public static NoteEntryResource From(NoteEntry entry) => new(entry.SourceId, entry.SourceName, entry.Path, entry.Title, entry.Topic);
}

/// <summary>Where a note is: enough to open it.</summary>
public sealed record NoteRefResource(Guid SourceId, string SourceName, string Path, string Title)
{
    public static NoteRefResource From(NoteRef note) => new(note.SourceId, note.SourceName, note.Path, note.Title);
}

/// <summary>A <c>[[wikilink]]</c> of a note: what it names (lower case, as it is matched) and the note it leads to, if the caller may read one.</summary>
public sealed record NoteLinkResource(string Target, NoteRefResource? Note);

/// <summary>
/// A note to read: its Markdown without the front matter, its tags, where its links lead, the notes that link to it, and where the files it
/// embeds (<c>![[plan.png]]</c>, by the name written in the note) are in its source, to be fetched from <c>/api/v1/files</c>.
/// </summary>
public sealed record NoteResource(
    Guid SourceId, string SourceName, string Path, string Title, string Markdown, IReadOnlyList<string> Tags, IReadOnlyList<NoteLinkResource> Links,
    IReadOnlyList<NoteRefResource> Backlinks, IReadOnlyDictionary<string, string> Embeds)
{
    public static NoteResource From(NoteContent content) =>
        new(
            content.Detail.Note.SourceId, content.Detail.Note.SourceName, content.Detail.Note.Path, content.Detail.Note.Title, content.Markdown,
            content.Detail.Tags,
            [.. content.Detail.Links.Select(link => new NoteLinkResource(link.Target, link.Note is null ? null : NoteRefResource.From(link.Note)))],
            [.. content.Detail.Backlinks.Select(NoteRefResource.From)],
            content.Embeds);
}

public sealed record TagResource(string Tag, int Count);

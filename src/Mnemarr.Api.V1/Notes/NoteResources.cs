using Mnemarr.Core.Notes;

namespace Mnemarr.Api.V1.Notes;

/// <summary>A note in the tree of the reader.</summary>
public sealed record NoteEntryResource(Guid SourceId, string SourceName, string Path, string Title, string Topic)
{
    public static NoteEntryResource From(NoteEntry entry) => new(entry.SourceId, entry.SourceName, entry.Path, entry.Title, entry.Topic);
}

/// <summary>The notes of the tree and how many there are in all: when the tree is cut, <c>total</c> is larger than the list.</summary>
public sealed record NoteListResource(IReadOnlyList<NoteEntryResource> Notes, int Total)
{
    public static NoteListResource From(NoteListing listing) => new([.. listing.Notes.Select(NoteEntryResource.From)], listing.Total);
}

/// <summary>Where a note is: enough to open it.</summary>
public sealed record NoteRefResource(Guid SourceId, string SourceName, string Path, string Title)
{
    public static NoteRefResource From(NoteRef note) => new(note.SourceId, note.SourceName, note.Path, note.Title);
}

/// <summary>A <c>[[wikilink]]</c> of a note: what it names (lower case, no extension, from the root of the source) and the note it leads to, if the caller may read one.</summary>
public sealed record NoteLinkResource(string Target, NoteRefResource? Note);

/// <summary>
/// A note to read: its Markdown as written (without the front matter), the same Markdown with its wikilinks and embeds rewritten into
/// links and images that lead where the caller may go (<c>mnemarr-note:{source}/{path}</c>, <c>mnemarr-file:{path}</c> for an image of the
/// source of the note, <c>mnemarr-unresolved:</c> for what leads nowhere the caller may read), its tags, where its links lead, and the notes
/// that link to it. An image is fetched from <c>/api/v1/sources/{id}/files/{path}</c>.
/// </summary>
public sealed record NoteResource(
    Guid SourceId, string SourceName, string Path, string Title, string Markdown, string Rendered, IReadOnlyList<string> Tags,
    IReadOnlyList<NoteLinkResource> Links, IReadOnlyList<NoteRefResource> Backlinks, int BacklinksTotal)
{
    public static NoteResource From(NoteContent content) =>
        new(
            content.Detail.Note.SourceId, content.Detail.Note.SourceName, content.Detail.Note.Path, content.Detail.Note.Title, content.Markdown, content.Rendered,
            content.Detail.Tags,
            [.. content.Detail.Links.Select(link => new NoteLinkResource(link.Target, link.Note is null ? null : NoteRefResource.From(link.Note)))],
            [.. content.Detail.Backlinks.Select(NoteRefResource.From)], content.Detail.BacklinksTotal);
}

public sealed record TagResource(string Tag, int Count);

/// <summary>The tags, most used first, and how many tags there are in all.</summary>
public sealed record TagListResource(IReadOnlyList<TagResource> Tags, int Total)
{
    public static TagListResource From(TagListing listing) => new([.. listing.Tags.Select(tag => new TagResource(tag.Tag, tag.Count))], listing.Total);
}

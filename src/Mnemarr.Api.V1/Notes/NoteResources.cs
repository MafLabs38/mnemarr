using Mnemarr.Core.Git;
using Mnemarr.Core.Notes;
using Mnemarr.Core.Sources;

namespace Mnemarr.Api.V1.Notes;

/// <summary>A note in the tree of the reader.</summary>
public sealed record NoteEntryResource(Guid SourceId, string SourceName, string Path, string Title, string Topic)
{
    public static NoteEntryResource From(NoteEntry entry) => new(entry.SourceId, entry.SourceName, entry.Path, entry.Title, entry.Topic);
}

/// <summary>What a caller may do to the notes of a source, and why: <c>reason</c> is <c>writable</c>, <c>source-read-only</c>, <c>kind-not-writable</c> or <c>no-permission</c>; <c>role</c> is the caller's, to say it in words.</summary>
public sealed record NoteRightsResource(bool CanWrite, string Reason, string Role)
{
    public static NoteRightsResource For(SourceKind kind, SourceMode mode, bool mayWrite, string role)
    {
        var rights = NoteRights.Evaluate(kind, mode, mayWrite);
        return new(rights.CanWrite, rights.Reason, role);
    }
}

/// <summary>A source of notes the caller may read, as the tree draws it: its kind, its mode, and what the caller may do there.</summary>
public sealed record NoteSourceResource(Guid Id, string Name, string Kind, string Mode, NoteRightsResource Rights);

/// <summary>The notes of the tree and how many there are in all (when the tree is cut, <c>total</c> is larger than the list), and the sources they are in, with what the caller may do in each.</summary>
public sealed record NoteListResource(IReadOnlyList<NoteEntryResource> Notes, int Total, IReadOnlyList<NoteSourceResource> Sources)
{
    public static NoteListResource From(NoteListing listing, IEnumerable<Source> sources, bool mayWrite, string role) =>
        new(
            [.. listing.Notes.Select(NoteEntryResource.From)], listing.Total,
            [.. sources.Select(source => new NoteSourceResource(source.Id, source.Name, source.Kind.ToString(), source.Mode.ToString(), NoteRightsResource.For(source.Kind, source.Mode, mayWrite, role)))]);
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
    IReadOnlyList<NoteLinkResource> Links, IReadOnlyList<NoteRefResource> Backlinks, int BacklinksTotal, string Raw, string Version, bool Writable,
    string SourceKind, string SourceMode, NoteRightsResource Rights)
{
    /// <summary>The note, for a caller who may or may not write: <c>writable</c> is true when the caller may write notes and the source is open for writing.</summary>
    public static NoteResource From(NoteContent content, bool mayWrite, string role) =>
        From(content, NoteRightsResource.For(content.Kind, content.Mode, mayWrite, role));

    private static NoteResource From(NoteContent content, NoteRightsResource rights) =>
        new(
            content.Detail.Note.SourceId, content.Detail.Note.SourceName, content.Detail.Note.Path, content.Detail.Note.Title, content.Markdown, content.Rendered,
            content.Detail.Tags,
            [.. content.Detail.Links.Select(link => new NoteLinkResource(link.Target, link.Note is null ? null : NoteRefResource.From(link.Note)))],
            [.. content.Detail.Backlinks.Select(NoteRefResource.From)], content.Detail.BacklinksTotal, content.Raw, content.Version, rights.CanWrite,
            content.Kind.ToString(), content.Mode.ToString(), rights);
}

/// <summary>The whole text of a new note (front matter included).</summary>
public sealed record CreateNoteRequest(string Content);

/// <summary>The whole new text of a note, and the version it replaces (what was read): a note that changed since is not overwritten.</summary>
public sealed record UpdateNoteRequest(string Content, string Version);

public sealed record RenameNoteRequest(string From, string To);

/// <summary>A text and where it would be saved, to see it drawn as a note of that source.</summary>
public sealed record PreviewNoteRequest(string Path, string Content);

public sealed record PreviewResource(string Rendered);

/// <summary>What became of the commit of a change to a Git source: pushed, or the code of why not (the commit is kept and a later sync pushes it).</summary>
public sealed record GitPublishResource(bool Pushed, string? Error)
{
    public static GitPublishResource? From(GitPublish? publish) => publish is null ? null : new(publish.Pushed, publish.Error);
}

/// <summary>A note that was written: its path, the version to send back with the next change, and the fate of its commit when the source is a Git repository.</summary>
public sealed record WriteResultResource(string Path, string Version, GitPublishResource? Git)
{
    public static WriteResultResource From(WriteResult result) => new(result.Path, result.Version, GitPublishResource.From(result.Git));
}

/// <summary>A note that was moved: the links that were rewritten in the notes of the same source, and how many notes that link to it were left as they were.</summary>
public sealed record RenameResultResource(string Path, string Version, int LinksRewritten, int NotesRewritten, int NotesNotRewritten, GitPublishResource? Git)
{
    public static RenameResultResource From(RenameResult result) =>
        new(result.Path, result.Version, result.LinksRewritten, result.NotesRewritten, result.NotesNotRewritten, GitPublishResource.From(result.Git));
}

public sealed record DeleteResultResource(GitPublishResource? Git);

public sealed record TagResource(string Tag, int Count);

/// <summary>The tags, most used first, and how many tags there are in all.</summary>
public sealed record TagListResource(IReadOnlyList<TagResource> Tags, int Total)
{
    public static TagListResource From(TagListing listing) => new([.. listing.Tags.Select(tag => new TagResource(tag.Tag, tag.Count))], listing.Total);
}

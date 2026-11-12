using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Mnemarr.Api.V1.Security;
using Mnemarr.Core.Accounts;
using Mnemarr.Core.Notes;
using Mnemarr.Core.Sources;

namespace Mnemarr.Api.V1.Notes;

/// <summary>
/// The notes reader (D-37): the tree of the notes a caller may read, a note with its links and backlinks, the tags. Read-only. A source out
/// of the scope of the caller is as absent as one that does not exist.
/// </summary>
[ApiController]
[Route("api/v1/notes")]
[Authorize(Policy = Permissions.NotesRead)]
[RequestSizeLimit(NotesController.MaxRequestBytes)]
public sealed class NotesController : ControllerBase
{
    /// <summary>A note is at most 2 MB; the request that carries one (and its JSON) is not allowed to be much more, whatever the server would otherwise take.</summary>
    public const int MaxRequestBytes = 4 * 1024 * 1024;

    private readonly NoteReader _reader;
    private readonly NoteWriter _writer;
    private readonly SourceCatalog _catalog;

    public NotesController(NoteReader reader, NoteWriter writer, SourceCatalog catalog)
    {
        _reader = reader;
        _writer = writer;
        _catalog = catalog;
    }

    private bool MayWrite => User.HasClaim(MnemarrClaims.Permission, Permissions.NotesWrite);

    private string Role => User.FindFirst(System.Security.Claims.ClaimTypes.Role)?.Value ?? string.Empty;

    private NoteActor Actor => new(User.Scope(), User.ActorName());

    /// <summary>The notes, by source and path; of one source or carrying one tag when asked. A very large vault is cut short, and <c>total</c> says by how much: the search is the way in beyond it.</summary>
    [HttpGet]
    public NoteListResource List([FromQuery] Guid? source = null, [FromQuery] string? tag = null) =>
        NoteListResource.From(_reader.List(User.Scope(), source, tag), _catalog.List(User.Scope()), MayWrite, Role);

    /// <summary>A note: its Markdown as written, the same with its links rewritten to where they lead for the caller (never trusted by whoever draws it), its tags, links and backlinks.</summary>
    [HttpGet("{sourceId:guid}/{**path}")]
    [ProducesResponseType<NoteResource>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public NoteResource Get(Guid sourceId, string path, [FromQuery] int page = 0) =>
        NoteResource.From(_reader.Read(sourceId, path, User.Scope(), page: page), MayWrite, Role);

    /// <summary>Creates a note, in a source that is open for writing: written whole and at once, read into the index at once, and, in a Git source, committed and pushed.</summary>
    [HttpPost("{sourceId:guid}/{**path}")]
    [Authorize(Policy = Permissions.NotesWrite)]
    [ProducesResponseType<WriteResultResource>(StatusCodes.Status201Created)]
    public async Task<ActionResult<WriteResultResource>> Create(Guid sourceId, string path, CreateNoteRequest request, CancellationToken cancellationToken)
    {
        var result = WriteResultResource.From(await _writer.CreateAsync(Actor, sourceId, path, request.Content, cancellationToken));
        return CreatedAtAction(nameof(Get), new { sourceId, path }, result);
    }

    /// <summary>Replaces a note's text. <c>version</c> is the one that was read: a note changed since is refused (409), never overwritten.</summary>
    [HttpPut("{sourceId:guid}/{**path}")]
    [Authorize(Policy = Permissions.NotesWrite)]
    public async Task<WriteResultResource> Update(Guid sourceId, string path, UpdateNoteRequest request, CancellationToken cancellationToken) =>
        WriteResultResource.From(await _writer.UpdateAsync(Actor, sourceId, path, request.Content, request.Version, cancellationToken));

    /// <summary>Removes a note: a commit in a Git source (its history keeps it), the hidden trash folder of the source otherwise. <c>version</c>, when given, must be the one that was read.</summary>
    [HttpDelete("{sourceId:guid}/{**path}")]
    [Authorize(Policy = Permissions.NotesWrite)]
    public async Task<DeleteResultResource> Delete(Guid sourceId, string path, [FromQuery] string? version, CancellationToken cancellationToken) =>
        new(GitPublishResource.From(await _writer.DeleteAsync(Actor, sourceId, path, version, cancellationToken)));

    /// <summary>Moves a note, and rewrites the links to it in the notes of the same source; the answer says how many, and how many notes of other sources link to it and were left as they were.</summary>
    [HttpPost("{sourceId:guid}/rename")]
    [Authorize(Policy = Permissions.NotesWrite)]
    public async Task<RenameResultResource> Rename(Guid sourceId, RenameNoteRequest request, CancellationToken cancellationToken) =>
        RenameResultResource.From(await _writer.RenameAsync(Actor, sourceId, request.From, request.To, cancellationToken));

    /// <summary>Adds text at the end of a note. The same service as the MCP tool <c>append_to_note</c>.</summary>
    [HttpPost("{sourceId:guid}/append")]
    [Authorize(Policy = Permissions.NotesWrite)]
    public async Task<WriteResultResource> Append(Guid sourceId, AppendNoteRequest request, CancellationToken cancellationToken) =>
        WriteResultResource.From(await _writer.AppendAsync(Actor, sourceId, request.Path, request.Text, cancellationToken));

    /// <summary>A text as it would be drawn once saved as a note of this source, its links resolved for the caller. Nothing is written.</summary>
    [HttpPost("{sourceId:guid}/preview")]
    public PreviewResource Preview(Guid sourceId, PreviewNoteRequest request) =>
        new(_reader.Preview(sourceId, request.Path, request.Content, User.Scope()));
}

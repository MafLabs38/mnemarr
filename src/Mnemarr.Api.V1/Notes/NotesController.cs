using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Mnemarr.Api.V1.Security;
using Mnemarr.Core.Accounts;
using Mnemarr.Core.Notes;

namespace Mnemarr.Api.V1.Notes;

/// <summary>
/// The notes reader (D-37): the tree of the notes a caller may read, a note with its links and backlinks, the tags. Read-only. A source out
/// of the scope of the caller is as absent as one that does not exist.
/// </summary>
[ApiController]
[Route("api/v1/notes")]
[Authorize(Policy = Permissions.NotesRead)]
public sealed class NotesController : ControllerBase
{
    private readonly NoteReader _reader;

    public NotesController(NoteReader reader)
    {
        _reader = reader;
    }

    /// <summary>The notes, by source and path; of one source or carrying one tag when asked. A very large vault is cut short: the search is the way in beyond it.</summary>
    [HttpGet]
    public IReadOnlyList<NoteEntryResource> List([FromQuery] Guid? source = null, [FromQuery] string? tag = null) =>
        [.. _reader.List(User.Scope(), source, tag).Select(NoteEntryResource.From)];

    /// <summary>A note: its Markdown (never rendered here, and never trusted by whoever renders it), its tags, links and backlinks.</summary>
    [HttpGet("{sourceId:guid}/{**path}")]
    [ProducesResponseType<NoteResource>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public NoteResource Get(Guid sourceId, string path) => NoteResource.From(_reader.Read(sourceId, path, User.Scope()));
}

using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Mnemarr.Api.V1.Security;
using Mnemarr.Core.Accounts;
using Mnemarr.Core.Notes;

namespace Mnemarr.Api.V1.Notes;

/// <summary>
/// The images of a source, for the notes that show them, served from the source they belong to and from nowhere else. Only a short list of
/// image formats: a note is untrusted, and what it points at must not be able to run in the page.
/// </summary>
[ApiController]
[Route("api/v1/files")]
[Authorize(Policy = Permissions.NotesRead)]
public sealed class FilesController : ControllerBase
{
    private readonly NoteReader _reader;

    public FilesController(NoteReader reader)
    {
        _reader = reader;
    }

    [HttpGet("{sourceId:guid}/{**path}")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public IActionResult Get(Guid sourceId, string path)
    {
        var file = _reader.OpenImage(sourceId, path, User.Scope());
        Response.Headers.XContentTypeOptions = "nosniff";
        Response.Headers.CacheControl = "private, max-age=60";
        return File(file.Content, file.ContentType);
    }
}

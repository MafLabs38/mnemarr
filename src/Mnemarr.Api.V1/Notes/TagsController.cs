using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Mnemarr.Api.V1.Security;
using Mnemarr.Core.Accounts;
using Mnemarr.Core.Notes;

namespace Mnemarr.Api.V1.Notes;

/// <summary>The tags of the notes the caller may read, with how many notes carry each, most used first.</summary>
[ApiController]
[Route("api/v1/notes/tags")]
[Authorize(Policy = Permissions.NotesRead)]
public sealed class TagsController : ControllerBase
{
    private readonly INoteBrowser _browser;

    public TagsController(INoteBrowser browser)
    {
        _browser = browser;
    }

    [HttpGet]
    public IReadOnlyList<TagResource> Get() => [.. _browser.Tags(User.Scope()).Select(tag => new TagResource(tag.Tag, tag.Count))];
}

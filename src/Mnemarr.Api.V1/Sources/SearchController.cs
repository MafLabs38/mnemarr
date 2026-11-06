using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Mnemarr.Api.V1.Security;
using Mnemarr.Core.Accounts;
using Mnemarr.Core.Indexing;

namespace Mnemarr.Api.V1.Sources;

/// <summary>Keyword search over the notes of the sources the caller may touch.</summary>
[ApiController]
[Route("api/v1/search")]
[Authorize(Policy = Permissions.Search)]
public sealed class SearchController : ControllerBase
{
    private readonly INoteIndex _index;

    public SearchController(INoteIndex index)
    {
        _index = index;
    }

    /// <summary>The best matching chunks first, with the source, the note and the words around the match. Several words match any of them.</summary>
    [HttpGet]
    public IReadOnlyList<SearchHitResource> Search(
        [FromQuery(Name = "q")] string query = "", [FromQuery] Guid? source = null, [FromQuery] string? topic = null, [FromQuery] int limit = 10) =>
        [.. _index.Search(new SearchRequest(query, source, topic, limit), User.Scope()).Select(SearchHitResource.From)];
}

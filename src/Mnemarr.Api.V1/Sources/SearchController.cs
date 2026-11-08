using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Mnemarr.Api.V1.Security;
using Mnemarr.Core.Accounts;
using Mnemarr.Core.Indexing;

namespace Mnemarr.Api.V1.Sources;

/// <summary>Search over the notes of the sources the caller may touch: by keywords, and by meaning when the model is ready (ADR 0034).</summary>
[ApiController]
[Route("api/v1/search")]
[Authorize(Policy = Permissions.Search)]
public sealed class SearchController : ControllerBase
{
    private readonly NoteSearch _search;

    public SearchController(NoteSearch search)
    {
        _search = search;
    }

    /// <summary>The best matching chunks first, with the source, the note, the words around the match and why it matched. Several words match any of them.</summary>
    [HttpGet]
    public IReadOnlyList<SearchHitResource> Search(
        [FromQuery(Name = "q")] string query = "", [FromQuery] Guid? source = null, [FromQuery] string? topic = null, [FromQuery] int limit = 10) =>
        [.. _search.Search(new SearchRequest(query, source, topic, limit), User.Scope()).Select(SearchHitResource.From)];
}

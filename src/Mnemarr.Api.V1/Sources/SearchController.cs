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

    /// <summary>
    /// The best matching chunks first, with the source, the note, the words around the match and why it matched. Several words match any of them.
    /// <c>topic</c> may be repeated (a note of any one of them), <c>notTopic</c> and <c>notSource</c> leave topics and sources out, and <c>perNote</c> (1 to 50, 3 by default)
    /// is how many hits one note may give, so that a long conversation does not fill the answer.
    /// </summary>
    [HttpGet]
    public IReadOnlyList<SearchHitResource> Search(
        [FromQuery(Name = "q")] string query = "", [FromQuery] Guid? source = null, [FromQuery] string[]? topic = null, [FromQuery] string[]? notTopic = null,
        [FromQuery] Guid[]? notSource = null, [FromQuery] int perNote = SearchRequest.DefaultPerNote, [FromQuery] int limit = 10) =>
        [.. _search.Search(new SearchRequest(query, source, Limit: limit) { Topics = topic ?? [], ExcludeTopics = notTopic ?? [], ExcludeSources = notSource ?? [], PerNote = perNote }, User.Scope()).Select(SearchHitResource.From)];
}

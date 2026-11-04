using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Mnemarr.Api.V1.Security;
using Mnemarr.Core;
using Mnemarr.Core.Accounts;
using Mnemarr.Core.Indexing;
using Mnemarr.Core.Sources;

namespace Mnemarr.Api.V1.Sources;

/// <summary>The sources: the folders of Markdown that Mnemarr indexes. What an operator manages, within the sources the caller may touch.</summary>
[ApiController]
[Route("api/v1/sources")]
[Authorize(Policy = Permissions.SourcesManage)]
public sealed class SourcesController : ControllerBase
{
    private readonly SourceCatalog _catalog;
    private readonly INoteIndex _index;
    private readonly SourceIndexer _indexer;

    public SourcesController(SourceCatalog catalog, INoteIndex index, SourceIndexer indexer)
    {
        _catalog = catalog;
        _index = index;
        _indexer = indexer;
    }

    private SourceResource Resource(Source source) => SourceResource.From(source, _index.Counts(source.Id));

    [HttpGet]
    public IReadOnlyList<SourceResource> GetSources() => [.. _catalog.List(User.Scope()).Select(Resource)];

    /// <summary>The folders a source may be in: what the operator put at Mnemarr's disposal.</summary>
    [HttpGet("roots")]
    public IReadOnlyList<string> GetRoots() => _catalog.Roots();

    /// <summary>The kinds of source, the rule of each (required, unique) and how many are declared: what must be there, and what is missing.</summary>
    [HttpGet("kinds")]
    public IReadOnlyList<SourceKindResource> GetKinds() => [.. _catalog.Kinds().Select(SourceKindResource.From)];

    [HttpGet("{id:guid}")]
    [ProducesResponseType<SourceResource>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public SourceResource GetSource(Guid id) => Resource(_catalog.Get(id, User.Scope()));

    [HttpPost]
    [ProducesResponseType<SourceResource>(StatusCodes.Status201Created)]
    public ActionResult<SourceResource> CreateSource(CreateSourceRequest request)
    {
        if (!Enum.TryParse<SourceKind>(request.Kind, ignoreCase: false, out var kind) || !Enum.IsDefined(kind))
        {
            return Problem(statusCode: StatusCodes.Status400BadRequest, title: "The kind of a source is Knowledge, Memory or ApplicationDocs.");
        }

        if (!Enum.TryParse<SourceMode>(request.Mode, ignoreCase: false, out var mode) || !Enum.IsDefined(mode))
        {
            return Problem(statusCode: StatusCodes.Status400BadRequest, title: "The mode of a source is ReadOnly or ReadWrite.");
        }

        var source = _catalog.Add(request.Name, kind, mode, request.Path, User.ActorName());
        return CreatedAtAction(nameof(GetSource), new { id = source.Id }, Resource(source));
    }

    /// <summary>Changes the name, the mode or the folder of a source. Its kind does not change.</summary>
    [HttpPut("{id:guid}")]
    public ActionResult<SourceResource> UpdateSource(Guid id, UpdateSourceRequest request)
    {
        if (!Enum.TryParse<SourceMode>(request.Mode, ignoreCase: false, out var mode) || !Enum.IsDefined(mode))
        {
            return Problem(statusCode: StatusCodes.Status400BadRequest, title: "The mode of a source is ReadOnly or ReadWrite.");
        }

        return Resource(_catalog.Update(id, request.Name, mode, request.Path, User.Scope(), User.ActorName()));
    }

    /// <summary>Forgets a source with its notes and chunks. The folder and its files are not touched. A source the base requires (the memory vault) cannot be forgotten: change its folder.</summary>
    [HttpDelete("{id:guid}")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    public IActionResult DeleteSource(Guid id)
    {
        _catalog.Remove(id, User.Scope(), User.ActorName());
        return NoContent();
    }

    /// <summary>Brings the index of a source in line with its folder now, without waiting for the next scan.</summary>
    [HttpPost("{id:guid}/index")]
    public async Task<IndexReportResource> IndexSource(Guid id, CancellationToken cancellationToken) =>
        IndexReportResource.From(await _indexer.IndexAsync(_catalog.Get(id, User.Scope()), cancellationToken));
}

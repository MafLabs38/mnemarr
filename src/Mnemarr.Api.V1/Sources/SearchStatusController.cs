using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Mnemarr.Core.Accounts;
using Mnemarr.Core.Embeddings;

namespace Mnemarr.Api.V1.Sources;

/// <summary>Where the search by meaning is at (ADR 0034): off, ready (and how many passages are computed of how many) or failed with the reason. The search works in every case.</summary>
[ApiController]
[Route("api/v1/search/status")]
[Authorize(Policy = Permissions.Search)]
public sealed class SearchStatusController : ControllerBase
{
    private readonly EmbeddingIndexer _embeddings;

    public SearchStatusController(EmbeddingIndexer embeddings)
    {
        _embeddings = embeddings;
    }

    [HttpGet]
    public SemanticSearchResource Status() => SemanticSearchResource.From(_embeddings.Report());
}

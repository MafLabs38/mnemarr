using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Hosting;
using Mnemarr.Core.Accounts;

namespace Mnemarr.Api.V1.Consolidation;

/// <summary>An empty JSON body, so that asking for a run is a JSON request (never a cross-site form post).</summary>
[System.Diagnostics.CodeAnalysis.SuppressMessage("Major Code Smell", "S2094:Classes should not be empty", Justification = "Its type is the contract: a body bound to it must be JSON, which a cross-site form cannot send.")]
public sealed record ConsolidationRunRequest;

/// <summary>Where the consolidation stands (ADR 0036) and a way to run it now.</summary>
[ApiController]
[Route("api/v1/consolidation")]
public sealed class ConsolidationController : ControllerBase
{
    private readonly Consolidator _consolidator;
    private readonly IHostApplicationLifetime _lifetime;

    public ConsolidationController(Consolidator consolidator, IHostApplicationLifetime lifetime)
    {
        _consolidator = consolidator;
        _lifetime = lifetime;
    }

    /// <summary>How the consolidation stands: on or off and why not, the memory vault it writes in, what waits, what is done, what failed.</summary>
    [HttpGet]
    [Authorize(Policy = Permissions.SystemRead)]
    public ConsolidationStatus Get() => _consolidator.Status();

    /// <summary>Consolidates the conversations that grew since their last consolidation, now, without waiting for them to be left alone. It runs in the background: the status says when it is done.</summary>
    /// <response code="202">A run was started.</response>
    /// <response code="409">The consolidation is off, cannot run (see the status), or is running.</response>
    [HttpPost("run")]
    [Authorize(Policy = Permissions.SourcesManage)]
    [ProducesResponseType(StatusCodes.Status202Accepted)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    public IActionResult Run(ConsolidationRunRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (!_consolidator.TryStartNow(_lifetime.ApplicationStopping))
        {
            return Conflict(new ProblemDetails { Status = StatusCodes.Status409Conflict, Title = "The consolidation cannot run now: it is off, not ready (see its status) or already running." });
        }

        return Accepted();
    }
}

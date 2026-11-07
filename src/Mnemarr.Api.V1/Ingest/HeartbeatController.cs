using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Mnemarr.Api.V1.Security;
using Mnemarr.Core.Accounts;
using Mnemarr.Core.Collectors;

namespace Mnemarr.Api.V1.Ingest;

/// <summary>What a collector says at the end of a pass: its version, how many lines it still has to send, when a reader failed a short sentence with no path and no content, and whether a person paused it.</summary>
public sealed record HeartbeatRequest(string? Version, [property: JsonRequired] long PendingLines, string? Error, bool? Paused, string? Platform = null, string? Program = null, string? ComputerId = null, int? IntervalSeconds = null, bool? CanUpdate = null);

/// <summary>The sign of life of a collector (ADR 0027 §6): sent at the end of every pass, even one that sent nothing, with the key of the machine.</summary>
[ApiController]
[Route("api/v1/ingest/heartbeat")]
public sealed class HeartbeatController : ControllerBase
{
    /// <summary>A version, a number and a short sentence.</summary>
    public const int MaxRequestBytes = 8192;

    private readonly CollectorService _collectors;

    public HeartbeatController(CollectorService collectors)
    {
        _collectors = collectors;
    }

    /// <summary>The sign of life at the end of a pass, from the machine the key belongs to.</summary>
    [HttpPost]
    [RequestSizeLimit(MaxRequestBytes)]
    [Authorize(Policy = Permissions.IngestWrite)]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    public IActionResult Beat(HeartbeatRequest request)
    {
        _collectors.Beat(User.AccountId(), new Heartbeat(request.Version ?? string.Empty, request.PendingLines, request.Error, request.Paused ?? false, request.Platform, request.Program, request.ComputerId, request.IntervalSeconds, request.CanUpdate ?? false));
        return NoContent();
    }
}

using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Mnemarr.Api.V1.Security;
using Mnemarr.Core.Accounts;
using Mnemarr.Core.Collectors;

namespace Mnemarr.Api.V1.Collectors;

public sealed record PairingResource(Guid Id, string Hostname, DateTimeOffset RequestedAt, DateTimeOffset ExpiresAt)
{
    public static PairingResource From(Pairing pairing) => new(pairing.Id, pairing.Hostname, pairing.RequestedAt, pairing.ExpiresAt);
}

public sealed record LookupPairingRequest(string? Code);

public sealed record ApprovePairingRequest(string? Machine, string? Kind);

/// <summary>The machines that ask to be linked (ADR 0030), seen and answered by a person who manages the collectors.</summary>
[ApiController]
[Route("api/v1/pairings")]
[Authorize(Policy = Permissions.CollectorsManage)]
public sealed class PairingsController : ControllerBase
{
    private readonly PairingService _pairings;

    public PairingsController(PairingService pairings)
    {
        _pairings = pairings;
    }

    private string Actor => User.ActorName();

    /// <summary>The requests that wait for a person.</summary>
    [HttpGet]
    public IReadOnlyList<PairingResource> Open() => [.. _pairings.Open().Select(PairingResource.From)];

    /// <summary>The request a machine shows the code of. A POST, so that the code is not in the address of a request.</summary>
    [HttpPost("lookup")]
    public PairingResource Lookup(LookupPairingRequest request) => PairingResource.From(_pairings.Lookup(request.Code));

    [HttpPost("{id:guid}/approve")]
    public PairingResource Approve(Guid id, ApprovePairingRequest request) => PairingResource.From(_pairings.Approve(id, request.Machine, request.Kind, Actor));

    [HttpPost("{id:guid}/reject")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    public IActionResult Reject(Guid id)
    {
        _pairings.Reject(id, Actor);
        return NoContent();
    }
}

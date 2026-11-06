using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Mnemarr.Api.V1.Collectors;
using Mnemarr.Core;
using Mnemarr.Core.Collectors;

namespace Mnemarr.Api.V1.Ingest;

/// <summary>What a machine says when it asks to be linked: its host name, which a person is shown (and does not have to keep).</summary>
public sealed record PairStartRequest(string? Hostname);

/// <summary>What the machine is given: the short code to show, the secret to ask with, the page where a person answers, and how long and how often.</summary>
public sealed record PairStartResult(string UserCode, string DeviceCode, string VerificationUrl, int ExpiresInSeconds, int IntervalSeconds);

public sealed record PairPollRequest(string? DeviceCode);

/// <summary>The answer so far: <c>pending</c>, <c>approved</c> (with the key, shown once), <c>rejected</c> or <c>expired</c>.</summary>
public sealed record PairPollResult(string State, string? Key, string? Machine, string? Kind);

/// <summary>
/// Where a machine asks to be linked and then asks what became of it (ADR 0030): under the ingest prefix, which a proxy may publish for the collectors alone.
/// Both are anonymous on purpose, and give nothing until a person who manages the collectors has said yes.
/// </summary>
[ApiController]
[Route("api/v1/ingest/pair")]
[AllowAnonymous]
public sealed class PairController : ControllerBase
{
    /// <summary>A host name or a secret: nothing anonymous is given more room than that.</summary>
    public const int MaxRequestBytes = 4096;

    private readonly PairingService _pairings;
    private readonly InstallCommands _commands;

    public PairController(PairingService pairings, InstallCommands commands)
    {
        _pairings = pairings;
        _commands = commands;
    }

    [HttpPost]
    [RequestSizeLimit(MaxRequestBytes)]
    [ProducesResponseType<PairStartResult>(StatusCodes.Status201Created)]
    public ActionResult<PairStartResult> Start(PairStartRequest request)
    {
        var server = _commands.Server(Request);
        if (!InstallScripts.IsSafeAddress(server))
        {
            throw new RejectedException(Mnemarr.Core.Problem.Invalid, "The address of this Mnemarr cannot be written in a link: set Collectors:PublicUrl.");
        }

        var started = _pairings.Start(request.Hostname);
        return StatusCode(StatusCodes.Status201Created, new PairStartResult(
            started.UserCode,
            started.DeviceCode,
            $"{server}/link?code={Uri.EscapeDataString(started.UserCode)}",
            (int)started.Life.TotalSeconds,
            (int)started.Interval.TotalSeconds));
    }

    [HttpPost("poll")]
    [RequestSizeLimit(MaxRequestBytes)]
    public PairPollResult Poll(PairPollRequest request)
    {
        var answer = _pairings.Poll(request.DeviceCode);
        return answer.Machine is { } machine
            ? new PairPollResult(answer.State, machine.Key, machine.Machine, MachineKinds.Name(machine.Kind))
            : new PairPollResult(answer.State, null, null, null);
    }
}

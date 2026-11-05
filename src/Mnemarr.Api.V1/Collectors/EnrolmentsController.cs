using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Mnemarr.Api.V1.Security;
using Mnemarr.Core;
using Mnemarr.Core.Accounts;
using Mnemarr.Core.Collectors;

namespace Mnemarr.Api.V1.Collectors;

/// <param name="Machine">The name the machine will give itself: the key it is given is bound to it.</param>
/// <param name="Kind"><c>always-on</c> (a server: its silence is an alert) or <c>intermittent</c> (a workstation: its absence is ordinary). Default: intermittent.</param>
public sealed record CreateEnrolmentRequest(string Machine, string? Kind);

/// <summary>A code that joins a machine, and where it stands: <c>pending</c> (waiting for a person), <c>approved</c> (the code works) or <c>expired</c>.</summary>
public sealed record EnrolmentResource(Guid Id, string Machine, string Kind, string State, DateTimeOffset RequestedAt, string RequestedBy, string? ApprovedBy, DateTimeOffset ExpiresAt)
{
    public static EnrolmentResource From(Enrolment enrolment, DateTimeOffset now) => new(
        enrolment.Id,
        enrolment.Machine,
        MachineKinds.Name(enrolment.Kind),
        enrolment.IsExpired(now) ? "expired" : enrolment.State.ToString().ToLowerInvariant(),
        enrolment.RequestedAt,
        enrolment.RequestedBy,
        enrolment.ApprovedBy,
        enrolment.ExpiresAt);
}

/// <summary>
/// A new request with its code, the lines to run on the machine to install the collector and join it (one per system this Mnemarr has a program for),
/// whether the address is served without TLS, and the command that only joins a machine that has the program already. The code is in this answer and nowhere else, ever.
/// </summary>
public sealed record CreatedEnrolmentResource(EnrolmentResource Enrolment, string Code, string Command, IReadOnlyList<InstallCommand> Installs, bool Insecure);

/// <summary>The codes that join machines (ADR 0027): added by a person who manages the collectors, approved or rejected by one.</summary>
[ApiController]
[Route("api/v1/enrolments")]
[Authorize(Policy = Permissions.CollectorsManage)]
public sealed class EnrolmentsController : ControllerBase
{
    private readonly EnrolmentService _enrolments;
    private readonly InstallCommands _installs;
    private readonly TimeProvider _clock;

    public EnrolmentsController(EnrolmentService enrolments, InstallCommands installs, TimeProvider clock)
    {
        _enrolments = enrolments;
        _installs = installs;
        _clock = clock;
    }

    private string Actor => User.ActorName();

    [HttpGet]
    public IReadOnlyList<EnrolmentResource> Open() => [.. _enrolments.Open().Select(enrolment => EnrolmentResource.From(enrolment, _clock.GetUtcNow()))];

    /// <summary>Adds a machine: a request approved on the spot, with its code, shown once, and the command to run on the machine.</summary>
    [HttpPost]
    [ProducesResponseType<CreatedEnrolmentResource>(StatusCodes.Status201Created)]
    public ActionResult<CreatedEnrolmentResource> Create(CreateEnrolmentRequest request)
    {
        var kind = MachineKind.Intermittent;
        if (request.Kind is not null && !MachineKinds.TryParse(request.Kind, out kind))
        {
            throw new RejectedException(Mnemarr.Core.Problem.Invalid, "The kind of a machine is always-on or intermittent.");
        }

        var created = _enrolments.Request(request.Machine, kind, approveNow: true, Actor);
        var server = _installs.Server(Request);
        var install = _installs.For(server, created.Code, request.Machine);
        return StatusCode(StatusCodes.Status201Created, new CreatedEnrolmentResource(EnrolmentResource.From(created.Enrolment, _clock.GetUtcNow()), created.Code, EnrolCommand.For(server, created.Code, request.Machine, install.Insecure), install.Commands, install.Insecure));
    }

    [HttpPost("{id:guid}/approve")]
    public EnrolmentResource Approve(Guid id) => EnrolmentResource.From(_enrolments.Approve(id, Actor), _clock.GetUtcNow());

    [HttpPost("{id:guid}/reject")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    public IActionResult Reject(Guid id)
    {
        _enrolments.Reject(id, Actor);
        return NoContent();
    }
}

using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Mnemarr.Api.V1.Security;
using Mnemarr.Core;
using Mnemarr.Core.Accounts;
using Mnemarr.Core.Collectors;

namespace Mnemarr.Api.V1.Collectors;

/// <summary>
/// What an assistant may do over REST as it does over MCP (D-19): ask for a machine to join. The request waits for a person who manages the collectors
/// to approve it (ADR 0027 §3); the code it carries is of no use before.
/// </summary>
[ApiController]
[Route("api/v1/enrolments/requests")]
[Authorize(Policy = Permissions.CollectorsRequest)]
public sealed class EnrolmentRequestsController : ControllerBase
{
    private readonly EnrolmentService _enrolments;
    private readonly InstallCommands _installs;
    private readonly TimeProvider _clock;

    public EnrolmentRequestsController(EnrolmentService enrolments, InstallCommands installs, TimeProvider clock)
    {
        _enrolments = enrolments;
        _installs = installs;
        _clock = clock;
    }

    [HttpPost]
    [ProducesResponseType<CreatedEnrolmentResource>(StatusCodes.Status201Created)]
    public ActionResult<CreatedEnrolmentResource> Create(CreateEnrolmentRequest request)
    {
        var kind = MachineKind.Intermittent;
        if (request.Kind is not null && !MachineKinds.TryParse(request.Kind, out kind))
        {
            throw new RejectedException(Mnemarr.Core.Problem.Invalid, "The kind of a machine is always-on or intermittent.");
        }

        var created = _enrolments.Request(request.Machine, kind, approveNow: false, User.ActorName());
        var server = _installs.Server(Request);
        var install = _installs.For(server, created.Code, request.Machine);
        return StatusCode(StatusCodes.Status201Created, new CreatedEnrolmentResource(EnrolmentResource.From(created.Enrolment, _clock.GetUtcNow()), created.Code, EnrolCommand.For(server, created.Code, request.Machine, install.Insecure), install.Commands, install.Insecure));
    }
}

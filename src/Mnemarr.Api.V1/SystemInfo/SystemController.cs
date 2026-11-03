using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Mnemarr.Core.Accounts;
using Mnemarr.Core.SystemInfo;

namespace Mnemarr.Api.V1.SystemInfo;

[ApiController]
[Route("api/v1/system")]
[Authorize(Policy = Permissions.SystemRead)]
public sealed class SystemController : ControllerBase
{
    private readonly ISystemStatusService _systemStatusService;

    public SystemController(ISystemStatusService systemStatusService)
    {
        _systemStatusService = systemStatusService;
    }

    /// <summary>Version, runtime and uptime of this Mnemarr.</summary>
    [HttpGet("status")]
    [ProducesResponseType<SystemStatusResource>(StatusCodes.Status200OK)]
    public SystemStatusResource GetStatus() => _systemStatusService.GetStatus().ToResource();
}

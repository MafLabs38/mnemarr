using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Mnemarr.Core.Accounts;

namespace Mnemarr.Api.V1.Accounts;

/// <summary>The audit log: who signed in, and who changed an account or a key.</summary>
[ApiController]
[Route("api/v1/audit")]
[Authorize(Policy = Permissions.AccountsManage)]
public sealed class AuditController : ControllerBase
{
    private readonly IAuditLog _audit;

    public AuditController(IAuditLog audit)
    {
        _audit = audit;
    }

    [HttpGet]
    public IReadOnlyList<AuditEventResource> GetAudit([FromQuery] int limit = 100, [FromQuery] string? action = null) =>
        [.. _audit.Recent(limit, action).Select(AuditEventResource.From)];
}

using Mnemarr.Core.Audit;

namespace Mnemarr.Api.V1.Audit;

public sealed record AuditEventResource(long Id, DateTimeOffset At, string Actor, string Action, string Target, string Detail)
{
    public static AuditEventResource From(AuditEvent e) => new(e.Id, e.At, e.Actor, e.Action, e.Target, e.Detail);
}

namespace Mnemarr.Core.Accounts;

/// <summary>Something that was done, by whom, to what. Never holds a secret.</summary>
public sealed record AuditEvent(long Id, DateTimeOffset At, string Actor, string Action, string Target, string Detail);

public interface IAuditLog
{
    void Append(string actor, string action, string target, string detail = "");

    /// <summary>The latest events, newest first, optionally of one action.</summary>
    IReadOnlyList<AuditEvent> Recent(int limit, string? action = null);
}

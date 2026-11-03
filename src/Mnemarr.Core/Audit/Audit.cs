namespace Mnemarr.Core.Audit;

/// <summary>Something that was done, by whom, to what. Never holds a secret.</summary>
public sealed record AuditEvent(long Id, DateTimeOffset At, string Actor, string Action, string Target, string Detail);

/// <summary>The actors the log names when nobody is signed in.</summary>
public static class AuditActors
{
    /// <summary>A call that no account made: a key nobody knows.</summary>
    public const string Unknown = "unknown";
}

public interface IAuditLog
{
    void Append(string actor, string action, string target, string detail = "");

    /// <summary>Forgets the events older than this moment. Returns how many.</summary>
    int Prune(DateTimeOffset before);

    /// <summary>The latest events, newest first, optionally of one action and optionally older than an event (its id): the next page.</summary>
    IReadOnlyList<AuditEvent> Recent(int limit, string? action = null, long? before = null);
}

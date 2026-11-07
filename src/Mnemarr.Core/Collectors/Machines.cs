using System.Text.RegularExpressions;

namespace Mnemarr.Core.Collectors;

/// <summary>Whether a machine is expected to be there all the time: a server is, and its silence is an alert; a workstation is not, and its absence is ordinary (ADR 0025 §5).</summary>
public enum MachineKind
{
    AlwaysOn,
    Intermittent,
}

/// <summary>What a machine is doing, told from facts (ADR 0027 §7).</summary>
public enum MachineState
{
    /// <summary>Conversations came from this name, but no enrolment made it a machine.</summary>
    Unenrolled,

    /// <summary>Its key was revoked.</summary>
    Refused,

    /// <summary>Enrolled, and never heard from.</summary>
    Waiting,

    /// <summary>Its last pass reported an error.</summary>
    Failing,

    /// <summary>Heard from recently.</summary>
    Online,

    /// <summary>A person asked the collector to be quiet: it is there, and sends nothing on purpose. Never an alert.</summary>
    Paused,

    /// <summary>An intermittent machine that is asleep or off: not an error.</summary>
    Away,

    /// <summary>An always-on machine that has not been heard from.</summary>
    Silent,
}

public static class MachineKinds
{
    public const string AlwaysOnName = "always-on";
    public const string IntermittentName = "intermittent";

    public static string Name(MachineKind kind) => kind == MachineKind.AlwaysOn ? AlwaysOnName : IntermittentName;

    public static bool TryParse(string? name, out MachineKind kind)
    {
        switch (name)
        {
            case AlwaysOnName:
                kind = MachineKind.AlwaysOn;
                return true;
            case IntermittentName:
                kind = MachineKind.Intermittent;
                return true;
            default:
                kind = default;
                return false;
        }
    }
}

/// <summary>The name of a machine as the ingest accepts it, and short enough to name its service account.</summary>
public static partial class MachineNames
{
    public const int MaxLength = 50;

    public static bool IsValid(string? name) => name is not null && Pattern().IsMatch(name);

    [GeneratedRegex(@"\A[A-Za-z0-9][A-Za-z0-9._-]{0,49}\z", RegexOptions.CultureInvariant, matchTimeoutMilliseconds: 1000)]
    private static partial Regex Pattern();
}

/// <summary>A machine that is enrolled: the account that sends for it, and what it last said about itself.</summary>
public sealed record Machine(
    string Name,
    MachineKind Kind,
    Guid AccountId,
    DateTimeOffset EnrolledAt,
    DateTimeOffset? LastSeenAt,
    string? Version,
    long PendingLines,
    string? Error,
    DateTimeOffset? RevokedAt,
    bool Paused = false,
    string? ComputerId = null,
    string? Platform = null,
    string? Program = null,
    int? IntervalSeconds = null,
    bool CanUpdate = false);

/// <summary>
/// What a collector says at the end of a pass. <paramref name="Paused"/>: a person asked it to be quiet. <paramref name="Platform"/> and <paramref name="Program"/>:
/// the system the collector is for and the hash of the program it runs. <paramref name="ComputerId"/>: the identifier of its computer.
/// <paramref name="IntervalSeconds"/>: how often it makes a pass. <paramref name="CanUpdate"/>: it was installed with the installer and can replace itself.
/// </summary>
public sealed record Heartbeat(string Version, long PendingLines, string? Error, bool Paused = false, string? Platform = null, string? Program = null, string? ComputerId = null, int? IntervalSeconds = null, bool CanUpdate = false);

public enum EnrolmentState
{
    /// <summary>Asked for (over MCP), not yet approved by a person.</summary>
    Pending,

    /// <summary>A person said yes: the code works until it expires.</summary>
    Approved,

    /// <summary>Exchanged for a key.</summary>
    Used,

    Rejected,
}

/// <summary>A code to join a machine, and where it stands. The code itself is never kept, only its hash.</summary>
public sealed record Enrolment(
    Guid Id,
    string Machine,
    MachineKind Kind,
    EnrolmentState State,
    DateTimeOffset RequestedAt,
    string RequestedBy,
    DateTimeOffset? ApprovedAt,
    string? ApprovedBy,
    DateTimeOffset ExpiresAt)
{
    /// <summary>Whether the code can no longer be used because its time is over (a used or rejected one is not "expired", it is over for another reason).</summary>
    public bool IsExpired(DateTimeOffset now) => State is EnrolmentState.Pending or EnrolmentState.Approved && ExpiresAt <= now;
}

/// <summary>A new request and its code: the only time the code is available.</summary>
public sealed record NewEnrolment(Enrolment Enrolment, string Code);

/// <summary>The machine a key belongs to, under the name it has now, and the identifier of its computer when it has one (what an ingest is filed under).</summary>
public sealed record Sender(string Machine, string? ComputerId);

/// <summary>What a machine is given when it joins: its key (shown once), its name and its kind. <paramref name="RenamedFrom"/>: the computer was known under another name, which it now has: its conversations are to be filed under the new one.</summary>
public sealed record EnrolledMachine(string Key, string Machine, MachineKind Kind, string? RenamedFrom = null);

/// <summary>A machine as the page tells it: what it is, the state computed from the facts, and what it sent.</summary>
public sealed record MachineStatus(
    string Name,
    MachineKind? Kind,
    MachineState State,
    int Conversations,
    long Lines,
    DateTimeOffset? LastSeen,
    DateTimeOffset? EnrolledAt,
    string? Version,
    long PendingLines,
    string? Error,
    string? Platform = null,
    string? Program = null,
    bool CanUpdate = false);

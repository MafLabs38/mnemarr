namespace Mnemarr.Core.Collectors;

public enum PairingState
{
    /// <summary>A machine asked to be linked, and no person has answered yet.</summary>
    Pending,

    /// <summary>A person said yes and chose the name: the machine is given its key the next time it asks.</summary>
    Approved,

    /// <summary>The machine took its key.</summary>
    Used,

    /// <summary>A person said no.</summary>
    Rejected,
}

/// <summary>
/// A request from a machine to be linked to this Mnemarr (ADR 0030): the machine made a short code and a secret of its own, and a person who manages the
/// collectors approves it by that code. Unlike an enrolment, the name of the machine is chosen by the person at the moment of approval.
/// </summary>
public sealed record Pairing(
    Guid Id,
    string Hostname,
    PairingState State,
    string? Machine,
    MachineKind? Kind,
    DateTimeOffset RequestedAt,
    DateTimeOffset ExpiresAt,
    string? ApprovedBy)
{
    /// <summary>Whether the time is over for a request that nobody took (a used or rejected one is over for another reason).</summary>
    public bool IsExpired(DateTimeOffset now) => State is PairingState.Pending or PairingState.Approved && ExpiresAt <= now;
}

/// <summary>What the machine is given when it asks: the code a person types or follows, the secret only it knows, where to go, and how it should wait.</summary>
public sealed record NewPairing(string UserCode, string DeviceCode, TimeSpan Life, TimeSpan Interval);

/// <summary>What a machine learns when it asks again: the answer so far, and its key once, when it is approved.</summary>
public sealed record PairingAnswer(string State, EnrolledMachine? Machine);

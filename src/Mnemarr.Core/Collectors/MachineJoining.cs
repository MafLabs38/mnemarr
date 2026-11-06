using Mnemarr.Core.Audit;

namespace Mnemarr.Core.Collectors;

/// <summary>
/// What happens when a machine joins, whichever way it was approved (a code a person gave it, ADR 0027 §4, or a link it asked for, ADR 0030): its service
/// account is made or taken back with the keys it had revoked, a key is made, and the machine is recorded. The caller holds <see cref="MachineAccounts.Gate"/>.
/// </summary>
public sealed class MachineJoining
{
    private readonly IMachineStore _machines;
    private readonly MachineAccounts _accounts;
    private readonly IAuditLog _audit;

    public MachineJoining(IMachineStore machines, MachineAccounts accounts, IAuditLog audit)
    {
        _machines = machines;
        _accounts = accounts;
        _audit = audit;
    }

    /// <summary>The machine joins again if it had joined: a person approved it, so the key it had is replaced, and a machine that was revoked is back.</summary>
    public EnrolledMachine Join(string machine, MachineKind kind, string approvedBy, DateTimeOffset now)
    {
        var account = _accounts.ForJoining(_machines.Get(machine), machine, approvedBy);
        var key = _accounts.NewKey(account, now, approvedBy);
        _machines.Enrol(new Machine(machine, kind, account.Id, now, null, null, 0, null, null));
        _audit.Append(AuditActors.Unknown, "collector.enrolled", machine, MachineKinds.Name(kind));
        return new EnrolledMachine(key, machine, kind);
    }
}

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

    /// <summary>The name a computer has here, when it has joined before.</summary>
    public string? NameOf(string computerId) => _machines.GetByComputer(computerId)?.Name;

    /// <summary>The hash of the identifier of a computer, as the collector sends it: 64 hexadecimal digits.</summary>
    public static bool IsComputerId(string? computerId) => computerId is { Length: 64 } && computerId.All(char.IsAsciiHexDigitLower);

    /// <summary>
    /// The machine joins again if it had joined: a person approved it, so the key it had is replaced, and a machine that was revoked is back. A computer
    /// that is known already (ADR 0031) is the machine it was, whatever it was called: it takes the name that is asked for, and what it sent is filed under it.
    /// </summary>
    public EnrolledMachine Join(string machine, MachineKind kind, string approvedBy, DateTimeOffset now, string? computerId = null)
    {
        if (computerId is not null && !IsComputerId(computerId))
        {
            throw new RejectedException(Problem.Invalid, "The identifier of the computer is not valid.");
        }

        string? renamedFrom = null;
        if (computerId is not null && _machines.GetByComputer(computerId) is { } known && !string.Equals(known.Name, machine, StringComparison.Ordinal))
        {
            if (_machines.Get(machine) is not null)
            {
                throw new RejectedException(Problem.Conflict, $"This computer is already the machine '{known.Name}', and '{machine}' is another machine: join under the name '{known.Name}', or merge the two machines first.");
            }

            _machines.Rename(known.Name, machine);
            renamedFrom = known.Name;
            _audit.Append(approvedBy, "collector.renamed", known.Name, machine);
        }

        var account = _accounts.ForJoining(_machines.Get(machine), machine, approvedBy);
        var key = _accounts.NewKey(account, now, approvedBy);
        _machines.Enrol(new Machine(machine, kind, account.Id, now, null, null, 0, null, null, ComputerId: computerId));
        _audit.Append(AuditActors.Unknown, "collector.enrolled", machine, MachineKinds.Name(kind));
        return new EnrolledMachine(key, machine, kind, renamedFrom);
    }
}

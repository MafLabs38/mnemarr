using Microsoft.Extensions.Logging;
using Mnemarr.Core.Audit;
using Mnemarr.Core.Conversations;

namespace Mnemarr.Core.Collectors;

/// <summary>
/// What happens when a machine joins, whichever way it was approved (a code a person gave it, ADR 0027 §4, or a link it asked for, ADR 0030): its service
/// account is made or taken back with the keys it had revoked, a key is made, and the machine is recorded. The caller holds <see cref="MachineAccounts.Gate"/>.
/// </summary>
public sealed class MachineJoining
{
    /// <summary>How long after its last sign of life a machine is taken to be sending: three passes of its collector, and at least this much (the pass of the default is thirty seconds).</summary>
    public static readonly TimeSpan LiveWindow = TimeSpan.FromSeconds(90);

    private readonly IMachineStore _machines;
    private readonly MachineAccounts _accounts;
    private readonly IAuditLog _audit;
    private readonly ConversationRelocator _relocator;
    private readonly ILogger<MachineJoining> _logger;
    private readonly TimeProvider _clock;

    public MachineJoining(IMachineStore machines, MachineAccounts accounts, IAuditLog audit, ConversationRelocator relocator, ILogger<MachineJoining> logger, TimeProvider clock)
    {
        _clock = clock;
        _relocator = relocator;
        _logger = logger;
        _machines = machines;
        _accounts = accounts;
        _audit = audit;
    }

    /// <summary>Whether a machine of this name is enrolled on a computer that is not this one (the machine has an identifier, and it is not the one given).</summary>
    public bool BelongsToAnotherComputer(string machine, string? computerId) =>
        _machines.Get(machine) is { RevokedAt: null, ComputerId: { } held } && !string.Equals(held, computerId, StringComparison.Ordinal);

    /// <summary>The name a computer has here, when it has joined before.</summary>
    public string? NameOf(string computerId) => _machines.GetByComputer(computerId)?.Name;

    /// <summary>
    /// What would stop this join, said before anything is changed (a person who approves is told at once, and a code is not used up by a join that cannot be
    /// done): the computer is known as another machine, and the name asked for belongs to a third.
    /// </summary>
    public void Check(string machine, string? computerId)
    {
        if (computerId is not null && !IsComputerId(computerId))
        {
            throw new RejectedException(Problem.Invalid, "The identifier of the computer is not valid.");
        }

        KnownUnder(machine, computerId, _clock.GetUtcNow());
    }

    /// <summary>The name under which this computer is known when it is not the one asked for (null when it is not known, or is known under this name).</summary>
    private string? KnownUnder(string machine, string? computerId, DateTimeOffset now)
    {
        if (computerId is null || _machines.GetByComputer(computerId) is not { } known || string.Equals(known.Name, machine, StringComparison.Ordinal))
        {
            return null;
        }

        if (_machines.Get(machine) is not null)
        {
            throw new RejectedException(Problem.Conflict, $"This computer is already the machine '{known.Name}', and '{machine}' is another machine: join under the name '{known.Name}', or merge the two machines first.");
        }

        // The identifier names a computer, it does not prove one (ADR 0031 §3): a machine that is sending right now is not taken over by whoever says its
        // identifier. Stop its collector (a minute and a half), or remove it, and join again.
        if (known.RevokedAt is null && known.LastSeenAt is { } seen && now - seen < WindowOf(known))
        {
            throw new RejectedException(Problem.Conflict, $"This computer says it is the machine '{known.Name}', which is sending right now. Stop its collector and try again in a minute and a half, or remove '{known.Name}' first.");
        }

        return known.Name;
    }

    private static TimeSpan WindowOf(Machine machine) =>
        machine.IntervalSeconds is { } seconds ? TimeSpan.FromSeconds(Math.Max(LiveWindow.TotalSeconds, 3.0 * seconds)) : LiveWindow;

    /// <summary>
    /// Files what a computer sent under the name it had before. Best effort, after the key was given: a failure (the conversations are busy) must never cost a
    /// machine its key. What is left is filed by the next batch of the same computer, or by *Merge* on the machines page.
    /// </summary>
    public async Task FileConversationsAsync(EnrolledMachine enrolled, CancellationToken cancellationToken)
    {
        if (enrolled.RenamedFrom is not { } before)
        {
            return;
        }

        try
        {
            await _relocator.MoveAllAsync(before, enrolled.Machine, cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "The conversations of {Before} could not be filed under {Machine} yet: merge them on the machines page.", before, enrolled.Machine);
        }
    }

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
        if (KnownUnder(machine, computerId, now) is { } known)
        {
            _machines.Rename(known, machine);
            renamedFrom = known;
            _audit.Append(approvedBy, "collector.renamed", known, machine);
        }

        var account = _accounts.ForJoining(_machines.Get(machine), machine, approvedBy);
        var key = _accounts.NewKey(account, now, approvedBy);
        _machines.Enrol(new Machine(machine, kind, account.Id, now, null, null, 0, null, null, ComputerId: computerId));
        _audit.Append(AuditActors.Unknown, "collector.enrolled", machine, MachineKinds.Name(kind));
        return new EnrolledMachine(key, machine, kind, renamedFrom);
    }
}

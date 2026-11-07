using Microsoft.Extensions.Options;
using Mnemarr.Core.Audit;
using Mnemarr.Core.Conversations;

namespace Mnemarr.Core.Collectors;

/// <summary>
/// The machines that joined (ADR 0027): which machine a key may send for, the sign of life it gives, the state told from the facts, and
/// the revocation of a machine.
/// </summary>
public sealed class CollectorService
{
    private const string NoSuchMachine = "There is no such machine.";
    private const string NoMachine = "This key belongs to no machine: enrol the machine with a code (mnemarr-collector enroll).";
    private const int MaxVersion = 40;
    private const int MaxError = 200;
    private const int MaxPlatform = 40;
    private readonly IMachineStore _machines;
    private readonly MachineAccounts _accounts;
    private readonly ConversationRelocator _relocator;
    private readonly IAuditLog _audit;
    private readonly TimeProvider _clock;
    private readonly IOptions<CollectorOptions> _options;
    private readonly IOptions<IngestOptions> _ingest;

    public CollectorService(IMachineStore machines, MachineAccounts accounts, ConversationRelocator relocator, IAuditLog audit, TimeProvider clock, IOptions<CollectorOptions> options, IOptions<IngestOptions> ingest)
    {
        _machines = machines;
        _accounts = accounts;
        _relocator = relocator;
        _audit = audit;
        _clock = clock;
        _options = options;
        _ingest = ingest;
    }

    /// <summary>Revokes a machine: its keys stop working and its account is disabled. The row stays, as refused.</summary>
    public void Revoke(string machine, string actor)
    {
        lock (_accounts.Gate)
        {
            var found = _machines.Get(machine);
            if (found is null || found.RevokedAt is not null)
            {
                throw new RejectedException(Problem.NotFound, "There is no such machine, or it is revoked already.");
            }

            _accounts.Disable(found.AccountId, actor);
            _machines.Revoke(machine, _clock.GetUtcNow());
            _audit.Append(actor, "collector.revoked", machine);
        }
    }

    /// <summary>
    /// Takes a revoked machine out of the list, with its (disabled) account, so that its name is free again. What it sent stays: its conversations are
    /// still notes, and the name shows as no longer enrolled while there are some.
    /// </summary>
    public void Forget(string machine, string actor)
    {
        lock (_accounts.Gate)
        {
            var found = _machines.Get(machine) ?? throw new RejectedException(Problem.NotFound, NoSuchMachine);
            if (found.RevokedAt is null)
            {
                throw new RejectedException(Problem.Conflict, "Revoke the machine first: only a machine that was revoked is taken out of the list.");
            }

            _machines.Remove(machine);
            _accounts.Delete(found.AccountId, actor);
            _audit.Append(actor, "collector.forgotten", machine);
        }
    }

    /// <summary>
    /// Takes a machine out of the list whether or not it was revoked (ADR 0031): its keys stop working, its account goes, its name is free. What it sent stays,
    /// filed under its name and shown as no longer enrolled, where a person can rename it, merge it into another machine or delete it.
    /// </summary>
    public void Remove(string machine, string actor)
    {
        lock (_accounts.Gate)
        {
            RemoveRow(machine, actor);
        }
    }

    /// <summary>Gives a machine, or the name that conversations were sent under, another name: what it sent is filed under the new one. The new name must be free.</summary>
    public async Task RenameAsync(string machine, string newName, string actor, CancellationToken cancellationToken)
    {
        if (!MachineNames.IsValid(newName))
        {
            throw new RejectedException(Problem.Invalid, $"The name of a machine is 1 to {MachineNames.MaxLength} letters, digits, dots, hyphens or underscores, and starts with a letter or a digit.");
        }

        lock (_accounts.Gate)
        {
            var exists = _machines.Get(machine) is not null;
            if (!exists && !_relocator.Holds(machine))
            {
                throw new RejectedException(Problem.NotFound, NoSuchMachine);
            }

            if (string.Equals(machine, newName, StringComparison.Ordinal))
            {
                return;
            }

            if (_machines.Get(newName) is not null || _relocator.Holds(newName))
            {
                throw new RejectedException(Problem.Conflict, $"The name '{newName}' is taken: merge the machine into it instead.");
            }

            if (exists)
            {
                _machines.Rename(machine, newName);
            }

            _audit.Append(actor, "collector.renamed", machine, newName);
        }

        await _relocator.MoveAllAsync(machine, newName, cancellationToken);
    }

    /// <summary>Files what a machine sent under another machine that is enrolled, without making a conversation twice, and takes the first out of the list.</summary>
    public async Task<Relocation> MergeAsync(string machine, string into, string actor, CancellationToken cancellationToken)
    {
        if (string.Equals(machine, into, StringComparison.Ordinal))
        {
            throw new RejectedException(Problem.Invalid, "A machine cannot be merged into itself.");
        }

        lock (_accounts.Gate)
        {
            if (_machines.Get(into) is null)
            {
                throw new RejectedException(Problem.NotFound, "There is no such machine to merge into: only a machine that is enrolled can receive.");
            }

            if (_machines.Get(machine) is null && !_relocator.Holds(machine))
            {
                throw new RejectedException(Problem.NotFound, NoSuchMachine);
            }
        }

        var report = await _relocator.MoveAllAsync(machine, into, cancellationToken);
        lock (_accounts.Gate)
        {
            if (_machines.Get(machine) is not null)
            {
                RemoveRow(machine, actor);
            }

            _audit.Append(actor, "collector.merged", machine, into);
        }

        return report;
    }

    /// <summary>
    /// Deletes a machine and everything it sent: its keys, its row, its raw lines, its conversation notes and what the search knew of them. What is
    /// computed from conversations is not here to delete (ADR 0031). Returns how many conversations went.
    /// </summary>
    public async Task<int> EraseAsync(string machine, string actor, CancellationToken cancellationToken)
    {
        lock (_accounts.Gate)
        {
            if (_machines.Get(machine) is null && !_relocator.Holds(machine))
            {
                throw new RejectedException(Problem.NotFound, NoSuchMachine);
            }

            if (_machines.Get(machine) is not null)
            {
                RemoveRow(machine, actor);
            }
        }

        var count = await _relocator.EraseAsync(machine, cancellationToken);
        _audit.Append(actor, "collector.erased", machine, count.ToString(System.Globalization.CultureInfo.InvariantCulture));
        return count;
    }

    private void RemoveRow(string machine, string actor)
    {
        var found = _machines.Get(machine) ?? throw new RejectedException(Problem.NotFound, NoSuchMachine);
        if (found.RevokedAt is null)
        {
            _accounts.Disable(found.AccountId, actor);
            _machines.Revoke(machine, _clock.GetUtcNow());
        }

        _machines.Remove(machine);
        _accounts.Delete(found.AccountId, actor);
        _audit.Append(actor, "collector.forgotten", machine);
    }

    /// <summary>
    /// Whether a key may send for a machine: the one it was given to, and no other. A key of no machine is refused unless the setting says
    /// that hand-made collector accounts are still allowed. A batch is also a sign of life. The answer is the identifier of the computer, when the machine has one.
    /// </summary>
    public string? Authorize(Guid? accountId, string claimedMachine)
    {
        var machine = accountId is { } id ? _machines.GetByAccount(id) : null;
        if (machine is null)
        {
            if (_ingest.Value.RequireEnrolment)
            {
                throw new RejectedException(Problem.Forbidden, NoMachine);
            }

            return null;
        }

        CheckNotRevoked(machine);
        if (!string.Equals(machine.Name, claimedMachine, StringComparison.Ordinal))
        {
            throw new RejectedException(Problem.Forbidden, $"This key belongs to the machine '{machine.Name}': it cannot send for another.");
        }

        _machines.Seen(machine.Name, _clock.GetUtcNow(), null);
        return machine.ComputerId;
    }

    /// <summary>The sign of life at the end of a pass. The machine is the one the key belongs to.</summary>
    public void Beat(Guid? accountId, Heartbeat beat)
    {
        var machine = (accountId is { } id ? _machines.GetByAccount(id) : null) ?? throw new RejectedException(Problem.Forbidden, NoMachine);
        CheckNotRevoked(machine);
        if (machine.ComputerId is null && MachineJoining.IsComputerId(beat.ComputerId))
        {
            // A machine that joined before computers were known learns its own: from now on a reinstall finds it, whatever the name.
            _machines.SetComputer(machine.Name, beat.ComputerId!);
        }

        _machines.Seen(machine.Name, _clock.GetUtcNow(), new Heartbeat(Short(beat.Version, MaxVersion) ?? "unknown", Math.Max(0, beat.PendingLines), Short(beat.Error, MaxError), beat.Paused, Short(beat.Platform, MaxPlatform), MachineJoining.IsComputerId(beat.Program) ? beat.Program : null, null));
    }

    /// <summary>Every machine that joined, and every name conversations came from that never joined, each with its state.</summary>
    public IReadOnlyList<MachineStatus> Machines()
    {
        var now = _clock.GetUtcNow();
        var activity = _relocator.Machines().ToDictionary(item => item.Machine, StringComparer.Ordinal);
        var list = new List<MachineStatus>();
        foreach (var machine in _machines.List())
        {
            activity.Remove(machine.Name, out var sent);
            var seen = Latest(machine.LastSeenAt, sent?.LastSeen);
            list.Add(new MachineStatus(machine.Name, machine.Kind, MachineStates.Of(machine, now, _options.Value), sent?.Conversations ?? 0, sent?.Lines ?? 0, seen, machine.EnrolledAt, machine.Version, machine.PendingLines, machine.Error, machine.Platform, machine.Program));
        }

        list.AddRange(activity.Values.Select(sent => new MachineStatus(sent.Machine, null, MachineState.Unenrolled, sent.Conversations, sent.Lines, sent.LastSeen, null, null, 0, null)));
        return [.. list.OrderBy(status => status.Name, StringComparer.Ordinal)];
    }

    /// <summary>The machines that are an alert now: always-on, silent for too long or failing.</summary>
    public IReadOnlyList<(Machine Machine, MachineState State)> Alerts()
    {
        var now = _clock.GetUtcNow();
        var options = _options.Value;
        return [.. _machines.List().Where(machine => MachineStates.IsAlert(machine, now, options)).Select(machine => (machine, MachineStates.Of(machine, now, options)))];
    }

    private static void CheckNotRevoked(Machine machine)
    {
        if (machine.RevokedAt is not null)
        {
            throw new RejectedException(Problem.Forbidden, "This machine was revoked.");
        }
    }

    private static string? Short(string? text, int max)
    {
        var trimmed = text?.Trim();
        return string.IsNullOrEmpty(trimmed) ? null : trimmed[..Math.Min(trimmed.Length, max)];
    }

    private static DateTimeOffset? Latest(DateTimeOffset? a, DateTimeOffset? b)
    {
        if (a is null || b is null)
        {
            return a ?? b;
        }

        return a > b ? a : b;
    }
}

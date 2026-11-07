using Mnemarr.Core.Audit;

namespace Mnemarr.Core.Collectors;

/// <summary>
/// The codes that join machines (ADR 0027): asked for, approved, rejected, and exchanged for the key of a machine. One lock, shared with the
/// revocation of a machine, keeps an exchange from running twice at once or while the machine is being revoked.
/// </summary>
public sealed class EnrolmentService
{
    /// <summary>A code works for this long once a person has approved it.</summary>
    public static readonly TimeSpan CodeLife = TimeSpan.FromMinutes(15);

    /// <summary>A request nobody approves is forgotten after this long.</summary>
    public static readonly TimeSpan RequestLife = TimeSpan.FromHours(24);

    /// <summary>More requests than this waiting for a person are refused: an assistant cannot fill the page.</summary>
    public const int MaxPending = 20;

    private const string NotValid = "The code is not valid: it may be wrong, used, expired or not approved yet.";
    private const int OpenListed = 50;
    private readonly IMachineStore _machines;
    private readonly IEnrolmentStore _enrolments;
    private readonly MachineAccounts _accounts;
    private readonly EnrolmentThrottle _throttle;
    private readonly MachineJoining _joining;
    private readonly IAuditLog _audit;
    private readonly TimeProvider _clock;

    public EnrolmentService(IMachineStore machines, IEnrolmentStore enrolments, MachineAccounts accounts, MachineJoining joining, EnrolmentThrottle throttle, IAuditLog audit, TimeProvider clock)
    {
        _joining = joining;
        _machines = machines;
        _enrolments = enrolments;
        _accounts = accounts;
        _throttle = throttle;
        _audit = audit;
        _clock = clock;
    }

    /// <summary>Asks for a code to join a machine. A person who may manage the collectors approves it on the spot; an assistant's request waits for one.</summary>
    public NewEnrolment Request(string machine, MachineKind kind, bool approveNow, string actor)
    {
        if (!MachineNames.IsValid(machine))
        {
            throw new RejectedException(Problem.Invalid, $"The name of a machine is 1 to {MachineNames.MaxLength} letters, digits, dots, hyphens or underscores, and starts with a letter or a digit.");
        }

        lock (_accounts.Gate)
        {
            var now = _clock.GetUtcNow();
            if (!approveNow && _machines.Get(machine) is { RevokedAt: null })
            {
                // An assistant's request must not be able to take over a machine that is working: the code would give its name to whoever holds it.
                throw new RejectedException(Problem.Conflict, "A machine with this name is enrolled already: a person who manages the collectors replaces it, or the request uses another name.");
            }

            if (!approveNow && _enrolments.CountPending(now) >= MaxPending)
            {
                throw new RejectedException(Problem.Busy, "Requests are waiting for a person to approve them: approve or reject some first.", TimeSpan.FromMinutes(5));
            }

            var code = EnrolmentCodes.New();
            var enrolment = new Enrolment(
                Guid.NewGuid(),
                machine,
                kind,
                approveNow ? EnrolmentState.Approved : EnrolmentState.Pending,
                now,
                actor,
                approveNow ? now : null,
                approveNow ? actor : null,
                now + (approveNow ? CodeLife : RequestLife));
            _enrolments.Add(enrolment, EnrolmentCodes.Hash(EnrolmentCodes.Normalise(code)!));
            _audit.Append(actor, approveNow ? "collector.enrolment-created" : "collector.enrolment-requested", machine, MachineKinds.Name(kind));
            return new NewEnrolment(enrolment, code);
        }
    }

    public Enrolment Approve(Guid id, string actor)
    {
        lock (_accounts.Gate)
        {
            var enrolment = _enrolments.Get(id) ?? throw NoSuchRequest();
            var now = _clock.GetUtcNow();
            if (enrolment.State != EnrolmentState.Pending || enrolment.IsExpired(now))
            {
                throw new RejectedException(Problem.Conflict, "This request is not waiting for approval any more.");
            }

            if (_machines.Get(enrolment.Machine) is { RevokedAt: null })
            {
                // The machine joined since the request was made: approving would give its name, and a new key, to whoever holds the code.
                throw new RejectedException(Problem.Conflict, "A machine with this name is enrolled already: reject this request, and add the machine again to replace it.");
            }

            _enrolments.Approve(id, actor, now, now + CodeLife);
            _audit.Append(actor, "collector.enrolment-approved", enrolment.Machine);
            return _enrolments.Get(id)!;
        }
    }

    public void Reject(Guid id, string actor)
    {
        lock (_accounts.Gate)
        {
            var enrolment = _enrolments.Get(id) ?? throw NoSuchRequest();
            if (!_enrolments.Reject(id))
            {
                throw new RejectedException(Problem.Conflict, "This request is over already.");
            }

            _audit.Append(actor, "collector.enrolment-rejected", enrolment.Machine);
        }
    }

    /// <summary>
    /// Whether a code is one that a person approved for this machine, without using it up: what stands before a program that carries the code is
    /// written for an anonymous caller. A code that is not is counted like a wrong code of an exchange.
    /// </summary>
    public void RequireApproved(string? code, string machine)
    {
        lock (_accounts.Gate)
        {
            var now = _clock.GetUtcNow();
            _throttle.Check(now);
            var normalised = EnrolmentCodes.Normalise(code);
            var enrolment = normalised is null ? null : _enrolments.FindByCodeHash(EnrolmentCodes.Hash(normalised));
            if (enrolment is not { State: EnrolmentState.Approved } || enrolment.IsExpired(now) || !string.Equals(enrolment.Machine, machine, StringComparison.Ordinal))
            {
                _throttle.Failed(now);
                throw new RejectedException(Problem.Invalid, NotValid);
            }
        }
    }

    /// <summary>The requests that are not over (waiting, or approved and not used), newest first.</summary>
    public IReadOnlyList<Enrolment> Open() => _enrolments.Open(OpenListed);

    /// <summary>
    /// Exchanges a code for the key of a machine. Anonymous: the code is all that is needed, so a wrong one is counted and the count is
    /// limited for the whole server, and every failure gets the same words.
    /// </summary>
    public async Task<EnrolledMachine> ExchangeAsync(string? code, string? machine, string? computerId, CancellationToken cancellationToken)
    {
        var joined = Exchange(code, machine, computerId);
        // A computer that was known under another name: what it sent is filed under the name it has now (best effort, after the key was given).
        await _joining.FileConversationsAsync(joined, cancellationToken);
        return joined;
    }

    private EnrolledMachine Exchange(string? code, string? machine, string? computerId)
    {
        lock (_accounts.Gate)
        {
            var now = _clock.GetUtcNow();
            _throttle.Check(now);
            var normalised = EnrolmentCodes.Normalise(code);
            var enrolment = normalised is null ? null : _enrolments.FindByCodeHash(EnrolmentCodes.Hash(normalised));
            if (enrolment is not { State: EnrolmentState.Approved } || enrolment.IsExpired(now) || !string.Equals(enrolment.Machine, machine, StringComparison.Ordinal))
            {
                _throttle.Failed(now);
                _audit.Append(AuditActors.Unknown, "collector.enrol-refused", MachineNames.IsValid(machine) ? machine! : "(not a valid name)");
                throw new RejectedException(Problem.Invalid, NotValid);
            }

            // The join comes first and the code is used up after it: a join that cannot be done leaves the code as it was, so that the key is not lost.
            var joined = _joining.Join(enrolment.Machine, enrolment.Kind, enrolment.RequestedBy, now, computerId);
            _enrolments.Use(enrolment.Id);
            return joined;
        }
    }

    private static RejectedException NoSuchRequest() => new(Problem.NotFound, "There is no such request.");
}

/// <summary>
/// Failed exchanges of a code, counted for the whole server: after ten in a minute no exchange is answered until the minute is over, so that a
/// code cannot be guessed. The server does not know who a caller is behind a proxy, so the count is not per caller.
/// </summary>
public sealed class EnrolmentThrottle
{
    private const int MaxFailures = 10;
    private static readonly TimeSpan Window = TimeSpan.FromMinutes(1);
    private readonly Queue<DateTimeOffset> _failures = new();
    private readonly Lock _gate = new();

    public void Check(DateTimeOffset now)
    {
        lock (_gate)
        {
            Forget(now);
            if (_failures.Count >= MaxFailures)
            {
                throw new RejectedException(Problem.Busy, "Too many wrong codes: try again in a minute.", Window);
            }
        }
    }

    public void Failed(DateTimeOffset now)
    {
        lock (_gate)
        {
            Forget(now);
            _failures.Enqueue(now);
        }
    }

    private void Forget(DateTimeOffset now)
    {
        while (_failures.Count > 0 && now - _failures.Peek() >= Window)
        {
            _failures.Dequeue();
        }
    }
}

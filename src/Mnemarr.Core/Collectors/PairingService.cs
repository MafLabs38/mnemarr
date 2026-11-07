using System.Security.Cryptography;
using Mnemarr.Core.Audit;

namespace Mnemarr.Core.Collectors;

/// <summary>
/// A machine that is linked by asking (ADR 0030): it makes a request and shows its short code, a person who manages the collectors approves it and names the
/// machine, and the machine, which keeps asking with a secret only it knows, is given its key once. The reverse of an enrolment code, which a person makes
/// and types on the machine. Nothing is trusted from the machine but the secret it asks with: it is anonymous, limited, and a person always decides.
/// </summary>
public sealed class PairingService
{
    /// <summary>How long a request waits for a person.</summary>
    public static readonly TimeSpan Life = TimeSpan.FromMinutes(10);

    /// <summary>How often the machine is told to ask again.</summary>
    public static readonly TimeSpan Interval = TimeSpan.FromSeconds(3);

    /// <summary>How many requests wait for a person at once, and how many may be made in a minute: whoever can reach the server cannot fill the page.</summary>
    public const int MaxOpen = 20;
    public const int MaxStartsPerMinute = 10;

    private const string NotValid = "The request is not valid: it may be wrong, over, or already answered.";
    private const string DeviceCodePrefix = "dvc_";
    private const int MaxHostnameLength = 50;
    private const int MaxDeviceCodeLength = 100;
    private const int OpenListed = 50;

    /// <summary>A request is forgotten a day after its time ended: long enough for a machine that was asleep to learn that it expired.</summary>
    private static readonly TimeSpan Kept = TimeSpan.FromDays(1);
    private readonly IPairingStore _pairings;
    private readonly MachineAccounts _accounts;
    private readonly MachineJoining _joining;
    private readonly EnrolmentThrottle _throttle;
    private readonly PairingStarts _starts;
    private readonly IAuditLog _audit;
    private readonly TimeProvider _clock;

    public PairingService(IPairingStore pairings, MachineAccounts accounts, MachineJoining joining, EnrolmentThrottle throttle, PairingStarts starts, IAuditLog audit, TimeProvider clock)
    {
        _pairings = pairings;
        _accounts = accounts;
        _joining = joining;
        _throttle = throttle;
        _starts = starts;
        _audit = audit;
        _clock = clock;
    }

    /// <summary>The name a machine gives itself, as text a page can show: letters, digits, dots, hyphens, underscores and spaces, and not long.</summary>
    public static string CleanHostname(string? hostname)
    {
        var kept = new string([.. (hostname ?? string.Empty).Where(c => char.IsAsciiLetterOrDigit(c) || c is '.' or '_' or '-' or ' ').Take(MaxHostnameLength)]).Trim();
        return kept.Length == 0 ? "unnamed computer" : kept;
    }

    /// <summary>A machine asks to be linked. Anonymous: it is counted, and limited for the whole server.</summary>
    public NewPairing Start(string? hostname, string? computerId = null)
    {
        if (computerId is not null && !MachineJoining.IsComputerId(computerId))
        {
            throw new RejectedException(Problem.Invalid, "The identifier of the computer is not valid.");
        }

        lock (_accounts.Gate)
        {
            var now = _clock.GetUtcNow();
            _starts.Take(now);
            _pairings.Prune(now - Kept);
            if (_pairings.CountOpen(now) >= MaxOpen)
            {
                throw new RejectedException(Problem.Busy, "Requests are waiting for a person to answer them: answer or let them expire first.", TimeSpan.FromMinutes(1));
            }

            var userCode = EnrolmentCodes.New();
            var deviceCode = DeviceCodePrefix + Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)).TrimEnd('=').Replace('+', '-').Replace('/', '_');
            var clean = CleanHostname(hostname);
            var pairing = new Pairing(Guid.NewGuid(), clean, PairingState.Pending, null, null, now, now + Life, null, computerId);
            _pairings.Add(pairing, EnrolmentCodes.Hash(EnrolmentCodes.Normalise(userCode)!), EnrolmentCodes.Hash(deviceCode));
            _audit.Append(AuditActors.Unknown, "collector.pairing-started", clean);
            return new NewPairing(userCode, deviceCode, Life, Interval);
        }
    }

    /// <summary>The requests that wait for a person, newest first.</summary>
    public IReadOnlyList<Pairing> Open() => [.. _pairings.Open(_clock.GetUtcNow(), OpenListed).Select(Known)];

    /// <summary>The name this computer has in this Mnemarr, when it has joined before (ADR 0031): the person is told, and the name is already the one to give.</summary>
    private Pairing Known(Pairing pairing) => pairing.ComputerId is { } id && _joining.NameOf(id) is { } name ? pairing with { KnownAs = name } : pairing;

    /// <summary>The request a person is looking at, by the code the machine shows.</summary>
    public Pairing Lookup(string? userCode)
    {
        var normalised = EnrolmentCodes.Normalise(userCode);
        var pairing = normalised is null ? null : _pairings.FindByUserCodeHash(EnrolmentCodes.Hash(normalised));
        return pairing is { State: PairingState.Pending } && !pairing.IsExpired(_clock.GetUtcNow())
            ? Known(pairing)
            : throw new RejectedException(Problem.NotFound, "There is no such request waiting: it may be over or already answered. Start again on the machine.");
    }

    /// <summary>A person says yes, with the code the computer shows, and names the machine; the machine takes its key the next time it asks.</summary>
    public Pairing Approve(Guid id, string? machine, string? kind, string? code, string actor)
    {
        if (!MachineNames.IsValid(machine))
        {
            throw new RejectedException(Problem.Invalid, $"The name of a machine is 1 to {MachineNames.MaxLength} letters, digits, dots, hyphens or underscores, and starts with a letter or a digit.");
        }

        if (!MachineKinds.TryParse(kind, out var parsedKind))
        {
            throw new RejectedException(Problem.Invalid, "The kind of a machine is always-on or intermittent.");
        }

        lock (_accounts.Gate)
        {
            var pairing = Waiting(id);
            // The code the computer shows is the proof that the person is looking at that computer: a request that anybody can make, with any host name, is not
            // approved from its name alone.
            var normalised = EnrolmentCodes.Normalise(code);
            if (normalised is null || _pairings.FindByUserCodeHash(EnrolmentCodes.Hash(normalised))?.Id != id)
            {
                throw new RejectedException(Problem.Invalid, "The code is not the one this computer shows: look at the computer, and type the code it shows.");
            }

            _joining.Check(machine!, pairing.ComputerId);
            _pairings.Approve(id, machine!, parsedKind, actor);
            _audit.Append(actor, "collector.pairing-approved", machine!, MachineKinds.Name(parsedKind));
            return Known(_pairings.Get(pairing.Id)!);
        }
    }

    public void Reject(Guid id, string actor)
    {
        lock (_accounts.Gate)
        {
            var pairing = Waiting(id);
            _pairings.Reject(id);
            _audit.Append(actor, "collector.pairing-rejected", pairing.Hostname);
        }
    }

    /// <summary>
    /// The machine asks what became of its request. Anonymous, with the secret it was given: a secret that is wrong is counted for the whole server, like a
    /// wrong code. The key is given once, to the first to ask after the approval.
    /// </summary>
    public async Task<PairingAnswer> PollAsync(string? deviceCode, CancellationToken cancellationToken)
    {
        var answer = Poll(deviceCode);
        if (answer.Machine is { } joined)
        {
            // A computer that was known under another name: what it sent is filed under the name it has now (best effort, after the key was given).
            await _joining.FileConversationsAsync(joined, cancellationToken);
        }

        return answer;
    }

    private PairingAnswer Poll(string? deviceCode)
    {
        lock (_accounts.Gate)
        {
            var now = _clock.GetUtcNow();
            _throttle.Check(now);
            var pairing = string.IsNullOrEmpty(deviceCode) || deviceCode.Length > MaxDeviceCodeLength ? null : _pairings.FindByDeviceHash(EnrolmentCodes.Hash(deviceCode));
            if (pairing is null)
            {
                _throttle.Failed(now);
                throw new RejectedException(Problem.Invalid, NotValid);
            }

            if (pairing.IsExpired(now))
            {
                return new PairingAnswer("expired", null);
            }

            switch (pairing.State)
            {
                case PairingState.Pending:
                    return new PairingAnswer("pending", null);
                case PairingState.Rejected:
                    return new PairingAnswer("rejected", null);
                case PairingState.Approved:
                    // The join comes first and the request is used up after it: a join that cannot be done leaves the request as it was, so that the key is not lost.
                    var joined = _joining.Join(pairing.Machine!, pairing.Kind ?? MachineKind.Intermittent, pairing.ApprovedBy ?? AuditActors.Unknown, now, pairing.ComputerId);
                    _pairings.Use(pairing.Id);
                    return new PairingAnswer("approved", joined);
                default:
                    // Already collected: the key is shown once.
                    throw new RejectedException(Problem.Invalid, NotValid);
            }
        }
    }

    private Pairing Waiting(Guid id)
    {
        var pairing = _pairings.Get(id) ?? throw new RejectedException(Problem.NotFound, "There is no such request.");
        return pairing.State == PairingState.Pending && !pairing.IsExpired(_clock.GetUtcNow())
            ? pairing
            : throw new RejectedException(Problem.Conflict, "This request is not waiting any more.");
    }
}

/// <summary>How many requests to be linked were made in the last minute, for the whole server: the server does not know who a caller is behind a proxy.</summary>
public sealed class PairingStarts
{
    private static readonly TimeSpan Window = TimeSpan.FromMinutes(1);
    private readonly Queue<DateTimeOffset> _starts = new();
    private readonly Lock _gate = new();

    /// <summary>Counts one more, or refuses it when the minute is full.</summary>
    public void Take(DateTimeOffset now)
    {
        lock (_gate)
        {
            while (_starts.Count > 0 && now - _starts.Peek() >= Window)
            {
                _starts.Dequeue();
            }

            if (_starts.Count >= PairingService.MaxStartsPerMinute)
            {
                throw new RejectedException(Problem.Busy, "Too many requests to be linked: try again in a minute.", Window);
            }

            _starts.Enqueue(now);
        }
    }
}

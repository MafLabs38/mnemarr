namespace Mnemarr.Core.Accounts;

/// <summary>
/// Locks a name out after repeated failures to sign in: five failed attempts within fifteen minutes lock it until they age out.
/// An attempt is counted <b>before</b> its password is checked and cleared when it succeeds, so a burst of parallel attempts cannot all
/// pass before the fifth failure is recorded. It counts per name, whether the account exists or not, so that it tells nothing about
/// which names exist, and it never tracks more than <see cref="MaxTracked"/> names. In memory: one instance.
/// <para>
/// At the cap, what has expired goes first, then the names that are not locked, oldest first. A lockout is never forgotten to make
/// room: when every tracked name is locked, a name that is not an account is refused, and a name that is one displaces the oldest
/// locked name that is not, so that garbage names can neither lift a lockout nor keep a real account from signing in.
/// </para>
/// </summary>
public sealed class LoginThrottle
{
    public const int MaxFailures = 5;
    public const int MaxTracked = 10_000;
    private static readonly TimeSpan Window = TimeSpan.FromMinutes(15);
    private static readonly TimeSpan NoRoomMemory = TimeSpan.FromSeconds(1);
    private readonly TimeProvider _clock;
    private readonly Dictionary<string, Entry> _entries = new(StringComparer.OrdinalIgnoreCase);
    private readonly Lock _gate = new();
    private DateTimeOffset _noRoomUntil;

    public LoginThrottle(TimeProvider clock)
    {
        _clock = clock;
    }

    /// <summary>
    /// Counts an attempt, unless the name is locked or there is no room left for it. False means the attempt is refused and not counted.
    /// <paramref name="isAccount"/> says that the name is an account: the table makes room for those.
    /// </summary>
    public bool TryReserve(string name, bool isAccount = false)
    {
        lock (_gate)
        {
            if (!_entries.TryGetValue(name, out var entry))
            {
                if (_entries.Count >= MaxTracked && !MakeRoom(isAccount))
                {
                    return false;
                }

                entry = new Entry(isAccount);
                _entries[name] = entry;
            }

            Prune(entry.Attempts);
            if (entry.Attempts.Count >= MaxFailures)
            {
                return false;
            }

            entry.Attempts.Add(_clock.GetUtcNow());
            return true;
        }
    }

    internal bool IsLocked(string name)
    {
        lock (_gate)
        {
            if (!_entries.TryGetValue(name, out var entry))
            {
                return false;
            }

            Prune(entry.Attempts);
            return entry.Attempts.Count >= MaxFailures;
        }
    }

    /// <summary>The attempt succeeded: the name starts again from nothing.</summary>
    public void Succeeded(string name)
    {
        lock (_gate)
        {
            _entries.Remove(name);
        }
    }

    private void Prune(List<DateTimeOffset> attempts)
    {
        var limit = _clock.GetUtcNow() - Window;
        attempts.RemoveAll(at => at < limit);
    }

    /// <summary>
    /// At the cap: forget what has expired, then the tenth of the names, not locked, whose last attempt is the oldest, and, for an
    /// account, the oldest locked names that are not accounts. False when nothing could be forgotten. A failure is remembered for a
    /// second, so that a flood of new names does not scan the table at every request.
    /// </summary>
    private bool MakeRoom(bool forAccount)
    {
        if (!forAccount && _clock.GetUtcNow() < _noRoomUntil)
        {
            return false;
        }

        foreach (var name in _entries.Keys.ToList())
        {
            Prune(_entries[name].Attempts);
            if (_entries[name].Attempts.Count == 0)
            {
                _entries.Remove(name);
            }
        }

        if (_entries.Count < MaxTracked || Forget(entry => entry.Attempts.Count < MaxFailures)
            || (forAccount && Forget(entry => !entry.IsAccount)))
        {
            return true;
        }

        _noRoomUntil = _clock.GetUtcNow() + NoRoomMemory;
        return false;
    }

    private bool Forget(Func<Entry, bool> candidate)
    {
        var oldest = _entries.Where(pair => candidate(pair.Value)).OrderBy(pair => pair.Value.Attempts[^1]).Take(MaxTracked / 10).Select(pair => pair.Key).ToList();
        foreach (var name in oldest)
        {
            _entries.Remove(name);
        }

        return oldest.Count > 0;
    }

    private sealed class Entry
    {
        public Entry(bool isAccount)
        {
            IsAccount = isAccount;
        }

        public bool IsAccount { get; }

        public List<DateTimeOffset> Attempts { get; } = [];
    }
}

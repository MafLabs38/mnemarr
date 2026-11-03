namespace Mnemarr.Core.Accounts;

/// <summary>
/// Locks a name out after repeated failures to sign in: five failed attempts within fifteen minutes lock it until they age out.
/// An attempt is counted <b>before</b> its password is checked and cleared when it succeeds, so a burst of parallel attempts cannot all
/// pass before the fifth failure is recorded. It counts per name, whether the account exists or not, so that it tells nothing about
/// which names exist, and it never tracks more than <see cref="MaxTracked"/> names: a name that is locked is never forgotten to make room, and
/// when every tracked name is locked a new one is refused. In memory: one instance.
/// </summary>
public sealed class LoginThrottle
{
    public const int MaxFailures = 5;
    public const int MaxTracked = 10_000;
    private static readonly TimeSpan Window = TimeSpan.FromMinutes(15);
    private readonly TimeProvider _clock;
    private readonly Dictionary<string, List<DateTimeOffset>> _attempts = new(StringComparer.OrdinalIgnoreCase);
    private readonly Lock _gate = new();

    public LoginThrottle(TimeProvider clock)
    {
        _clock = clock;
    }

    /// <summary>Counts an attempt, unless the name is locked. False means locked: the attempt is refused and not counted.</summary>
    public bool TryReserve(string name)
    {
        lock (_gate)
        {
            if (!_attempts.TryGetValue(name, out var attempts))
            {
                if (_attempts.Count >= MaxTracked && !MakeRoom())
                {
                    return false;
                }

                attempts = [];
                _attempts[name] = attempts;
            }

            Prune(attempts);
            if (attempts.Count >= MaxFailures)
            {
                return false;
            }

            attempts.Add(_clock.GetUtcNow());
            return true;
        }
    }

    internal bool IsLocked(string name)
    {
        lock (_gate)
        {
            if (!_attempts.TryGetValue(name, out var attempts))
            {
                return false;
            }

            Prune(attempts);
            return attempts.Count >= MaxFailures;
        }
    }

    /// <summary>The attempt succeeded: the name starts again from nothing.</summary>
    public void Succeeded(string name)
    {
        lock (_gate)
        {
            _attempts.Remove(name);
        }
    }

    private void Prune(List<DateTimeOffset> attempts)
    {
        var limit = _clock.GetUtcNow() - Window;
        attempts.RemoveAll(at => at < limit);
    }

    /// <summary>
    /// At the cap: forget what has expired, then, if that is not enough, the tenth of the names, not locked, whose last attempt is the
    /// oldest. False when nothing could be forgotten, which means every tracked name is locked.
    /// </summary>
    private bool MakeRoom()
    {
        foreach (var name in _attempts.Keys.ToList())
        {
            Prune(_attempts[name]);
            if (_attempts[name].Count == 0)
            {
                _attempts.Remove(name);
            }
        }

        if (_attempts.Count < MaxTracked)
        {
            return true;
        }

        var evictable = _attempts.Where(entry => entry.Value.Count < MaxFailures).OrderBy(entry => entry.Value[^1]).Take(MaxTracked / 10).Select(entry => entry.Key).ToList();
        foreach (var name in evictable)
        {
            _attempts.Remove(name);
        }

        return evictable.Count > 0;
    }
}

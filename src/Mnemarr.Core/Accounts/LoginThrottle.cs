using System.Collections.Concurrent;

namespace Mnemarr.Core.Accounts;

/// <summary>
/// Locks a name out after repeated failures to sign in: five failed attempts within fifteen minutes lock it until they age out.
/// An attempt is counted <b>before</b> its password is checked and cleared when it succeeds, so a burst of parallel attempts cannot all
/// pass before the fifth failure is recorded. It counts per name, whether the account exists or not, so that it tells nothing about
/// which names exist, and it never tracks more than <see cref="MaxTracked"/> names. In memory: one instance.
/// </summary>
public sealed class LoginThrottle
{
    public const int MaxFailures = 5;
    public const int MaxTracked = 10_000;
    private static readonly TimeSpan Window = TimeSpan.FromMinutes(15);
    private readonly TimeProvider _clock;
    private readonly ConcurrentDictionary<string, List<DateTimeOffset>> _attempts = new(StringComparer.OrdinalIgnoreCase);
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
            if (_attempts.Count >= MaxTracked && !_attempts.ContainsKey(name))
            {
                MakeRoom();
            }

            var attempts = _attempts.GetOrAdd(name, _ => []);
            Prune(attempts);
            if (attempts.Count >= MaxFailures)
            {
                return false;
            }

            attempts.Add(_clock.GetUtcNow());
            return true;
        }
    }

    public bool IsLocked(string name)
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
            _attempts.TryRemove(name, out _);
        }
    }

    private void Prune(List<DateTimeOffset> attempts)
    {
        var limit = _clock.GetUtcNow() - Window;
        attempts.RemoveAll(at => at < limit);
    }

    /// <summary>At the cap: forget what has expired, then, if that is not enough, the tenth of the names whose last attempt is the oldest.</summary>
    private void MakeRoom()
    {
        foreach (var (name, attempts) in _attempts)
        {
            Prune(attempts);
            if (attempts.Count == 0)
            {
                _attempts.TryRemove(name, out _);
            }
        }

        if (_attempts.Count < MaxTracked)
        {
            return;
        }

        foreach (var name in _attempts.OrderBy(entry => entry.Value[^1]).Take(MaxTracked / 10).Select(entry => entry.Key).ToList())
        {
            _attempts.TryRemove(name, out _);
        }
    }
}

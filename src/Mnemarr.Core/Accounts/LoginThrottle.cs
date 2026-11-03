using System.Collections.Concurrent;

namespace Mnemarr.Core.Accounts;

/// <summary>
/// Locks a name out after repeated failures to sign in: five failures within fifteen minutes lock it for fifteen minutes.
/// It counts per name, whether the account exists or not, so that it tells nothing about which names exist. In memory: one instance.
/// </summary>
public sealed class LoginThrottle
{
    private const int MaxFailures = 5;
    private const int MaxTracked = 10_000;
    private static readonly TimeSpan Window = TimeSpan.FromMinutes(15);
    private readonly TimeProvider _clock;
    private readonly ConcurrentDictionary<string, List<DateTimeOffset>> _failures = new(StringComparer.OrdinalIgnoreCase);

    public LoginThrottle(TimeProvider clock)
    {
        _clock = clock;
    }

    public bool IsLocked(string name)
    {
        if (!_failures.TryGetValue(name, out var failures))
        {
            return false;
        }

        lock (failures)
        {
            Prune(failures);
            return failures.Count >= MaxFailures;
        }
    }

    public void Failed(string name)
    {
        if (_failures.Count >= MaxTracked)
        {
            ForgetExpired();
        }

        var failures = _failures.GetOrAdd(name, _ => []);
        lock (failures)
        {
            Prune(failures);
            failures.Add(_clock.GetUtcNow());
        }
    }

    public void Succeeded(string name) => _failures.TryRemove(name, out _);

    private void Prune(List<DateTimeOffset> failures)
    {
        var limit = _clock.GetUtcNow() - Window;
        failures.RemoveAll(at => at < limit);
    }

    private void ForgetExpired()
    {
        foreach (var (name, failures) in _failures)
        {
            lock (failures)
            {
                Prune(failures);
                if (failures.Count == 0)
                {
                    _failures.TryRemove(name, out _);
                }
            }
        }
    }
}

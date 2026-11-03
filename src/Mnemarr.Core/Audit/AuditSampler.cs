namespace Mnemarr.Core.Audit;

/// <summary>
/// Keeps what an anonymous caller can make the audit log write bounded. An event is audited at most once a minute per key (the name, the
/// prefix) and at most <see cref="MaxPerMinute"/> times a minute <b>for each kind of event</b>: the log says it happens without letting a
/// flood of garbage fill the disk, and a flood of one kind cannot hide another.
/// </summary>
public sealed class AuditSampler
{
    public const int MaxPerMinute = 60;

    /// <summary>The kinds of event are a short list of constants; the cap only keeps a future caller from growing the table with user data.</summary>
    public const int MaxKinds = 32;
    private static readonly TimeSpan Minute = TimeSpan.FromMinutes(1);
    private readonly TimeProvider _clock;
    private readonly Dictionary<string, HashSet<string>> _seen = new(StringComparer.Ordinal);
    private readonly Lock _gate = new();
    private DateTimeOffset _windowStart;

    public AuditSampler(TimeProvider clock)
    {
        _clock = clock;
        _windowStart = clock.GetUtcNow();
    }

    public bool Allow(string action, string key)
    {
        var now = _clock.GetUtcNow();
        lock (_gate)
        {
            if (now - _windowStart >= Minute)
            {
                _windowStart = now;
                _seen.Clear();
            }

            if (!_seen.TryGetValue(action, out var keys))
            {
                if (_seen.Count >= MaxKinds)
                {
                    return false;
                }

                keys = new HashSet<string>(StringComparer.Ordinal);
                _seen[action] = keys;
            }

            return keys.Count < MaxPerMinute && keys.Add(key);
        }
    }
}

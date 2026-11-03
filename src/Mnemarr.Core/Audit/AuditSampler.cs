namespace Mnemarr.Core.Audit;

/// <summary>
/// Keeps what an anonymous caller can make the audit log write bounded. An event is audited at most once a minute per key (the name, the
/// prefix), at most <see cref="MaxPerMinute"/> times a minute and <see cref="MaxPerHour"/> times an hour <b>for each kind of event</b>: the log
/// says it happens without letting a flood of garbage fill the disk over the retention, and a flood of one kind cannot hide another.
/// </summary>
public sealed class AuditSampler
{
    public const int MaxPerMinute = 60;
    public const int MaxPerHour = 120;

    /// <summary>The kinds of event are a short list of constants; the cap only keeps a future caller from growing the table with user data.</summary>
    public const int MaxKinds = 32;
    private static readonly TimeSpan Minute = TimeSpan.FromMinutes(1);
    private static readonly TimeSpan Hour = TimeSpan.FromHours(1);
    private readonly TimeProvider _clock;
    private readonly Dictionary<string, HashSet<string>> _seen = new(StringComparer.Ordinal);
    private readonly Lock _gate = new();
    private readonly Dictionary<string, int> _inHour = new(StringComparer.Ordinal);
    private DateTimeOffset _windowStart;
    private DateTimeOffset _hourStart;

    public AuditSampler(TimeProvider clock)
    {
        _clock = clock;
        _windowStart = clock.GetUtcNow();
        _hourStart = _windowStart;
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

            if (now - _hourStart >= Hour)
            {
                _hourStart = now;
                _inHour.Clear();
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

            if (keys.Count >= MaxPerMinute || _inHour.GetValueOrDefault(action) >= MaxPerHour || !keys.Add(key))
            {
                return false;
            }

            _inHour[action] = _inHour.GetValueOrDefault(action) + 1;
            return true;
        }
    }
}

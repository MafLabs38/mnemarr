using System.Collections.Concurrent;

namespace Mnemarr.Core.Accounts;

/// <summary>
/// Keeps what an anonymous caller can make the audit log write bounded. A refused key or a locked name is audited at most once a minute per
/// key (the name, the prefix) and at most <see cref="MaxPerMinute"/> times a minute in all: the log says it happens without letting a flood
/// of garbage fill the disk.
/// </summary>
public sealed class AuditSampler
{
    public const int MaxPerMinute = 60;
    private const int MaxKeys = 2_000;
    private static readonly TimeSpan Minute = TimeSpan.FromMinutes(1);
    private readonly TimeProvider _clock;
    private readonly ConcurrentDictionary<string, DateTimeOffset> _last = new(StringComparer.Ordinal);
    private readonly Lock _gate = new();
    private DateTimeOffset _windowStart;
    private int _inWindow;

    public AuditSampler(TimeProvider clock)
    {
        _clock = clock;
        _windowStart = clock.GetUtcNow();
    }

    public bool Allow(string key)
    {
        var now = _clock.GetUtcNow();
        lock (_gate)
        {
            if (now - _windowStart >= Minute)
            {
                _windowStart = now;
                _inWindow = 0;
                _last.Clear();
            }

            if (_inWindow >= MaxPerMinute || _last.Count >= MaxKeys || _last.ContainsKey(key))
            {
                return false;
            }

            _last[key] = now;
            _inWindow++;
            return true;
        }
    }
}

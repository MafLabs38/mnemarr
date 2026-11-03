using System.Runtime.InteropServices;

namespace Mnemarr.Core.SystemInfo;

public sealed class SystemStatusService : ISystemStatusService
{
    private readonly TimeProvider _clock;
    private readonly DateTimeOffset _startedAt;

    public SystemStatusService(TimeProvider clock)
    {
        _clock = clock;
        _startedAt = clock.GetUtcNow();
    }

    public SystemStatus GetStatus()
    {
        var now = _clock.GetUtcNow();
        return new SystemStatus(BuildInfo.Version, RuntimeInformation.FrameworkDescription, _startedAt, now - _startedAt);
    }
}

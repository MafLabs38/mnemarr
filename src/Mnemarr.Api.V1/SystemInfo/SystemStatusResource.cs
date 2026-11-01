using Mnemarr.Core.SystemInfo;

namespace Mnemarr.Api.V1.SystemInfo;

/// <summary>Version, runtime and uptime of this Mnemarr. Nothing about the host or its folders.</summary>
public sealed record SystemStatusResource(string Version, string RuntimeVersion, DateTimeOffset StartedAt, long UptimeSeconds);

public static class SystemStatusResourceExtensions
{
    public static SystemStatusResource ToResource(this SystemStatus status) =>
        new(status.Version, status.RuntimeVersion, status.StartedAt, (long)status.Uptime.TotalSeconds);
}

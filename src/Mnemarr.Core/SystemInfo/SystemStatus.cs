namespace Mnemarr.Core.SystemInfo;

/// <summary>What the running Mnemarr can say about itself without revealing anything about its host.</summary>
public sealed record SystemStatus(string Version, string RuntimeVersion, DateTimeOffset StartedAt, TimeSpan Uptime);

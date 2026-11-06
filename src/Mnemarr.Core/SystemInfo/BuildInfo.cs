using System.Reflection;

namespace Mnemarr.Core.SystemInfo;

/// <summary>Version of the running build, set by the release pipeline (<c>-p:Version=</c>).</summary>
public static class BuildInfo
{
    public static string Version { get; } = typeof(BuildInfo).Assembly
        .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
        ?? "0.0.0-dev";
}

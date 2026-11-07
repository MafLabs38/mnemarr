using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Options;

namespace Mnemarr.Core.Collectors;

/// <summary>
/// The updates of the collector that this Mnemarr carries (ADR 0032): the folder the installer made (Velopack's <c>releases.&lt;channel&gt;.json</c>, the packages and the
/// installer) is served as it is, so that a collector that was installed asks the Mnemarr it sends to for a newer version, and an installer is offered to those
/// who have none. Only the files that the installer makes are ever served, whatever the folder holds.
/// </summary>
public sealed partial class CollectorUpdates
{
    /// <summary>The name of the folder of the feed inside the folder of the programs, when none is set.</summary>
    public const string FolderName = "updates";

    private const int MaxName = 150;

    /// <summary>The system of a machine (as its collector names it) and the channel of the installer's feed for it: one feed for each.</summary>
    private static readonly IReadOnlyDictionary<string, string> Channels = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        ["windows-x86_64"] = "win",
    };
    private readonly IOptions<CollectorOptions> _options;
    private readonly Lock _gate = new();
    private readonly Dictionary<string, (long Length, DateTime Written, string? Version)> _feeds = [];

    public CollectorUpdates(IOptions<CollectorOptions> options)
    {
        _options = options;
    }

    [GeneratedRegex(@"\A(releases\.[A-Za-z0-9][A-Za-z0-9._-]*\.json|[A-Za-z0-9][A-Za-z0-9._+-]*-(full|delta)\.nupkg|[A-Za-z0-9][A-Za-z0-9._-]*-Setup\.exe|[A-Za-z0-9][A-Za-z0-9._-]*-Portable\.zip)\z", RegexOptions.CultureInvariant, matchTimeoutMilliseconds: 1000)]
    private static partial Regex Served();

    /// <summary>The folder of the feed: the one set, or <c>updates</c> in the folder of the programs; null when there is none.</summary>
    public string? Folder
    {
        get
        {
            var options = _options.Value;
            if (!options.ServeUpdates)
            {
                return null;
            }

            if (!string.IsNullOrWhiteSpace(options.UpdateFolder))
            {
                return Path.GetFullPath(options.UpdateFolder);
            }

            return string.IsNullOrWhiteSpace(options.DownloadFolder) ? null : Path.Combine(Path.GetFullPath(options.DownloadFolder), FolderName);
        }
    }

    /// <summary>Whether a name is one of the files the installer makes (and nothing else: no folder, no other file).</summary>
    public static bool IsServed(string? name) => name is { Length: > 0 and <= MaxName } && Served().IsMatch(name);

    /// <summary>The path of a file of the feed, or null when the name is not one of those that are served or the file is not there. A link to somewhere else is not followed.</summary>
    public string? Open(string? name)
    {
        if (Folder is not { } folder || !IsServed(name))
        {
            return null;
        }

        var info = new FileInfo(Path.Combine(folder, name!));
        return info.Exists && info.LinkTarget is null ? info.FullName : null;
    }

    /// <summary>The installer for a system, when the folder has one: the file a person without a collector downloads.</summary>
    public string? InstallerName(string? platform)
    {
        if (Folder is not { } folder || !Directory.Exists(folder) || !ChannelOf(platform, out var channel))
        {
            return null;
        }

        return Directory.EnumerateFiles(folder, $"*-{channel}-Setup.exe").Select(Path.GetFileName).OfType<string>().Where(IsServed).Order(StringComparer.Ordinal).FirstOrDefault();
    }

    private static bool ChannelOf(string? platform, out string channel)
    {
        channel = string.Empty;
        return platform is not null && Channels.TryGetValue(platform, out channel!);
    }

    /// <summary>The newest version that the feed of a system carries, or null when there is none.</summary>
    public string? LatestVersion(string? platform)
    {
        if (Folder is null || !ChannelOf(platform, out var channel))
        {
            return null;
        }

        return Open($"releases.{channel}.json") is { } feed ? VersionOf(feed) : null;
    }

    /// <summary>
    /// The version a machine could update to: the newest the feed of its system carries, when the machine was installed with the installer (so it can replace itself),
    /// says a version that is one, and that version is older. Null otherwise: nobody is told something that is not known.
    /// </summary>
    public string? NewerThan(string? machineVersion, bool canUpdate, string? platform)
    {
        if (!canUpdate || !ProgramVersions.TryParse(machineVersion, out var machine) || LatestVersion(platform) is not { } latest || !ProgramVersions.TryParse(latest, out var offered))
        {
            return null;
        }

        return ProgramVersions.Compare(offered, machine) > 0 ? latest : null;
    }

    private string? VersionOf(string path)
    {
        var info = new FileInfo(path);
        lock (_gate)
        {
            if (_feeds.TryGetValue(path, out var known) && known.Length == info.Length && known.Written == info.LastWriteTimeUtc)
            {
                return known.Version;
            }

            var version = ReadNewest(path);
            _feeds[path] = (info.Length, info.LastWriteTimeUtc, version);
            return version;
        }
    }

    private static string? ReadNewest(string path)
    {
        try
        {
            using var document = JsonDocument.Parse(File.ReadAllBytes(path));
            if (!document.RootElement.TryGetProperty("Assets", out var assets) || assets.ValueKind != JsonValueKind.Array)
            {
                return null;
            }

            string? newest = null;
            SemanticVersion best = default;
            foreach (var asset in assets.EnumerateArray().Where(asset => asset.ValueKind == JsonValueKind.Object))
            {
                if (asset.TryGetProperty("Type", out var type) && type.GetString() == "Full"
                    && asset.TryGetProperty("Version", out var text) && text.GetString() is { } version
                    && ProgramVersions.TryParse(version, out var parsed) && (newest is null || ProgramVersions.Compare(parsed, best) > 0))
                {
                    newest = version;
                    best = parsed;
                }
            }

            return newest;
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            // A feed that cannot be read is no feed: nothing is offered.
            return null;
        }
    }
}

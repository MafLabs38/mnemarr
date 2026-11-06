using System.Security.Cryptography;
using Microsoft.Extensions.Options;

namespace Mnemarr.Core.Collectors;

/// <summary>A program a machine can install: the system it is for, its file, its size and the checksum the installer verifies.</summary>
public sealed record CollectorDownload(string Platform, string FileName, long Size, string Sha256);

/// <summary>
/// The programs of the collector that this Mnemarr serves (ADR 0027 §12): a machine installs the program of the version of its server, from the
/// server it already trusts. Only the files named here are ever served, whatever the folder holds, and their checksum is computed from the file, so
/// that the installer can verify what it got.
/// </summary>
public sealed class CollectorDownloads
{
    /// <summary>The systems, and the name of the file of each in the folder.</summary>
    private static readonly IReadOnlyDictionary<string, string> Files = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        ["windows-x86_64"] = "mnemarr-collector-windows-x86_64.exe",
        ["linux-x86_64"] = "mnemarr-collector-linux-x86_64",
        ["macos-aarch64"] = "mnemarr-collector-macos-aarch64",
    };

    private readonly IOptions<CollectorOptions> _options;
    private readonly Lock _gate = new();
    private readonly Dictionary<string, (long Length, DateTime Written, string Sha256)> _checksums = [];

    public CollectorDownloads(IOptions<CollectorOptions> options)
    {
        _options = options;
    }

    public static IEnumerable<string> Platforms => Files.Keys;

    /// <summary>The programs that are in the folder.</summary>
    public IReadOnlyList<CollectorDownload> Available() => [.. Platforms.Select(Find).OfType<CollectorDownload>()];

    /// <summary>The program for a system, or null when it is not one of the known systems or its file is not there.</summary>
    public CollectorDownload? Find(string platform) => Open(platform) is { } found ? found.Download : null;

    /// <summary>
    /// The program for a system, and what is to be written after it (the address, the machine, the code), and the name a person is offered it under;
    /// null when there is no program for that system. The program is not read here: it is streamed, with the trailer after it.
    /// </summary>
    public (string Path, byte[] Trailer, string FileName)? Personalised(string platform, EmbeddedSetting setting)
    {
        if (Open(platform) is not { } found)
        {
            return null;
        }

        var extension = Path.GetExtension(found.Download.FileName);
        return (found.Path, setting.Trailer(), $"mnemarr-collector-{setting.Machine}{extension}");
    }

    /// <summary>The program for a system, ready to be read, or null.</summary>
    public (CollectorDownload Download, string Path)? Open(string platform)
    {
        if (string.IsNullOrWhiteSpace(_options.Value.DownloadFolder) || !Files.TryGetValue(platform, out var name))
        {
            return null;
        }

        var path = Path.Combine(Path.GetFullPath(_options.Value.DownloadFolder), name);
        var info = new FileInfo(path);
        // A link to somewhere else is not followed: what is served is a file of this folder.
        if (!info.Exists || info.LinkTarget is not null)
        {
            return null;
        }

        return (new CollectorDownload(platform, name, info.Length, ChecksumOf(path, info)), path);
    }

    private string ChecksumOf(string path, FileInfo info)
    {
        lock (_gate)
        {
            if (_checksums.TryGetValue(path, out var known) && known.Length == info.Length && known.Written == info.LastWriteTimeUtc)
            {
                return known.Sha256;
            }

            using var stream = File.OpenRead(path);
            var hash = Convert.ToHexStringLower(SHA256.HashData(stream));
            _checksums[path] = (info.Length, info.LastWriteTimeUtc, hash);
            return hash;
        }
    }
}

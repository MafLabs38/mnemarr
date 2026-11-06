using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Options;
using Mnemarr.Core.Collectors;

namespace Mnemarr.Api.V1.Collectors;

/// <summary>One line a person copies to install the collector on a system.</summary>
/// <param name="System"><c>windows</c> or <c>unix</c> (Linux and macOS).</param>
/// <param name="Label">What the person is told it is for.</param>
/// <param name="Command">The line itself.</param>
/// <param name="DownloadUrl">The address of a program that already carries its setting, to open with a double click; null where that is not offered.</param>
public sealed record InstallCommand(string System, string Label, string Command, string? DownloadUrl = null);

/// <summary>The lines to install on each system this Mnemarr has a program for, and whether the address is served without TLS (the person must be told).</summary>
public sealed record InstallInfo(IReadOnlyList<InstallCommand> Commands, bool Insecure);

/// <summary>
/// What a person runs on the machine to join it (ADR 0027 §12): one line that fetches the installer from this Mnemarr, which downloads the program
/// from the same place, verifies its checksum, joins the machine with the code and starts it. Nothing has to be installed first.
/// </summary>
public sealed class InstallCommands
{
    private readonly CollectorDownloads _downloads;
    private readonly IOptions<CollectorOptions> _options;

    public InstallCommands(CollectorDownloads downloads, IOptions<CollectorOptions> options)
    {
        _downloads = downloads;
        _options = options;
    }

    /// <summary>The address machines reach this Mnemarr at: the one set for it, or the one the request came with.</summary>
    public string Server(HttpRequest request) =>
        (string.IsNullOrWhiteSpace(_options.Value.PublicUrl) ? $"{request.Scheme}://{request.Host}" : _options.Value.PublicUrl).TrimEnd('/');

    public InstallInfo For(string server, string code, string machine)
    {
        var query = $"code={Uri.EscapeDataString(code)}&machine={Uri.EscapeDataString(machine)}";
        var have = _downloads.Available().Select(download => download.Platform).ToHashSet(StringComparer.Ordinal);
        var commands = new List<InstallCommand>();
        if (have.Contains("windows-x86_64"))
        {
            commands.Add(new InstallCommand("windows", "Windows", $"irm '{server}/api/v1/ingest/install.ps1?{query}' | iex", $"{server}/api/v1/ingest/download/windows-x86_64?{query}"));
        }

        if (have.Contains("linux-x86_64") || have.Contains("macos-aarch64"))
        {
            commands.Add(new InstallCommand("unix", "Linux or macOS (terminal)", $"curl -fsSL '{server}/api/v1/ingest/install.sh?{query}' | sh"));
        }

        return new InstallInfo(commands, Insecure: server.StartsWith("http://", StringComparison.OrdinalIgnoreCase));
    }
}

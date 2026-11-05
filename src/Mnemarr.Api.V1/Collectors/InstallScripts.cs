using System.Text.RegularExpressions;
using Mnemarr.Core.Collectors;

namespace Mnemarr.Api.V1.Collectors;

/// <summary>
/// The installers a machine runs (ADR 0027 §12), written by the server for one code and one machine. They are text that a person is asked to run,
/// so everything put into them has been checked first to be of a shape that cannot end a string or add a command: a code of the alphabet of the
/// codes, a machine name, an address of letters, digits, dots, hyphens, colons and slashes, a checksum in hexadecimal.
/// </summary>
public static partial class InstallScripts
{
    public static bool IsSafeAddress(string server) => Address().IsMatch(server);

    /// <summary>The installer for Windows, in PowerShell 5 and later: nothing to install first, no administrator rights, in the profile of the user.</summary>
    public static string Windows(string server, string code, string machine, string sha256)
    {
        var insecure = server.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ? " --allow-insecure-http" : string.Empty;
        return Fill(WindowsTemplate, server, code, machine, sha256).Replace("{{INSECURE}}", insecure, StringComparison.Ordinal);
    }

    /// <summary>The installer for Linux and macOS: it picks the program of the system it runs on, and stops if there is none.</summary>
    public static string Unix(string server, string code, string machine, IReadOnlyList<CollectorDownload> downloads)
    {
        var insecure = server.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ? " --allow-insecure-http" : string.Empty;
        var choices = string.Join('\n', downloads.Select(download => download.Platform switch
        {
            "linux-x86_64" => $"  Linux-x86_64) platform=linux-x86_64; expected={download.Sha256} ;;",
            "macos-aarch64" => $"  Darwin-arm64) platform=macos-aarch64; expected={download.Sha256} ;;",
            _ => string.Empty,
        }).Where(line => line.Length > 0));
        return Fill(UnixTemplate, server, code, machine, string.Empty)
            .Replace("{{CHOICES}}", choices, StringComparison.Ordinal)
            .Replace("{{INSECURE}}", insecure, StringComparison.Ordinal);
    }

    private static string Fill(string template, string server, string code, string machine, string sha256) => template
        .Replace("{{SERVER}}", server, StringComparison.Ordinal)
        .Replace("{{CODE}}", code, StringComparison.Ordinal)
        .Replace("{{MACHINE}}", machine, StringComparison.Ordinal)
        .Replace("{{SHA256}}", sha256, StringComparison.Ordinal)
        .Replace("\r\n", "\n", StringComparison.Ordinal);

    [GeneratedRegex(@"\Ahttps?://[A-Za-z0-9.\-]+(:[0-9]{1,5})?(/[A-Za-z0-9._\-/]*)?\z", RegexOptions.CultureInvariant, matchTimeoutMilliseconds: 1000)]
    private static partial Regex Address();

    private const string WindowsTemplate = """
        # Installs the Mnemarr collector for your user, from {{SERVER}}. It was written by that server for the machine '{{MACHINE}}': read it before you run it.
        # No administrator rights are needed. The program goes in your profile; it is checked against the checksum below before it runs.
        $ErrorActionPreference = 'Stop'
        [Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12
        $server = '{{SERVER}}'
        $dir = Join-Path $env:LOCALAPPDATA 'mnemarr-collector'
        $exe = Join-Path $dir 'mnemarr-collector.exe'
        New-Item -ItemType Directory -Force $dir | Out-Null
        # An older copy that runs would keep its file: it is stopped first (nothing happens when there is none).
        if (Test-Path $exe) { & $exe stop 2>$null | Out-Null }
        Write-Host 'Downloading the collector...'
        Invoke-WebRequest -UseBasicParsing "$server/api/v1/ingest/download/windows-x86_64" -OutFile $exe
        if ((Get-FileHash $exe -Algorithm SHA256).Hash.ToLower() -ne '{{SHA256}}') {
            Remove-Item $exe -Force
            throw 'The program does not match its checksum: it was removed and nothing was installed.'
        }
        Write-Host 'Joining this machine as {{MACHINE}}...'
        & $exe enroll --server $server --code '{{CODE}}' --machine '{{MACHINE}}'{{INSECURE}}
        if ($LASTEXITCODE -ne 0) { throw 'This machine could not join. Ask for a new code in Mnemarr.' }
        & $exe install
        if ($LASTEXITCODE -ne 0) { throw 'The collector joined but could not be installed.' }
        Write-Host ''
        Write-Host 'Done. The collector runs in the background and starts with your session; you can close this window.'
        Write-Host "To see it: & '$exe' status    To stop it: & '$exe' stop    To remove it: & '$exe' uninstall"
        """;

    private const string UnixTemplate = """
        #!/bin/sh
        # Installs the Mnemarr collector for your user, from {{SERVER}}. It was written by that server for the machine '{{MACHINE}}': read it before you run it.
        # No administrator rights are needed. The program goes in your home folder; it is checked against the checksum below before it runs.
        set -eu
        server='{{SERVER}}'
        case "$(uname -s)-$(uname -m)" in
        {{CHOICES}}
          *) echo "This Mnemarr has no collector for $(uname -s) $(uname -m)." >&2; exit 1 ;;
        esac
        dir="${XDG_DATA_HOME:-$HOME/.local/share}/mnemarr-collector"
        exe="$dir/mnemarr-collector"
        mkdir -p "$dir"
        # An older copy that runs would keep its file: it is stopped first (nothing happens when there is none).
        [ -x "$exe" ] && "$exe" stop >/dev/null 2>&1 || true
        echo 'Downloading the collector...'
        curl -fsSL "$server/api/v1/ingest/download/$platform" -o "$exe"
        if command -v sha256sum >/dev/null 2>&1; then actual=$(sha256sum "$exe" | cut -d' ' -f1); else actual=$(shasum -a 256 "$exe" | cut -d' ' -f1); fi
        if [ "$actual" != "$expected" ]; then
          rm -f "$exe"
          echo 'The program does not match its checksum: it was removed and nothing was installed.' >&2
          exit 1
        fi
        chmod 700 "$exe"
        echo 'Joining this machine as {{MACHINE}}...'
        "$exe" enroll --server "$server" --code '{{CODE}}' --machine '{{MACHINE}}'{{INSECURE}}
        "$exe" install
        echo
        echo "Done. The collector runs in the background and starts with your session."
        echo "To see it: $exe status    To stop it: $exe stop    To remove it: $exe uninstall"
        """;
}

# The installer of the collector, tried for real on Windows (ADR 0032): installed without a window, then updated from a feed, then removed. This is what a person
# does, and what the unit tests cannot: Velopack puts the program in place, the hooks make it start with the session and put it on the PATH, a newer version is
# taken from the Mnemarr it sends to and replaces the running one, and the uninstaller leaves nothing that starts it again.
#
#   smoke.ps1 -Program <mnemarr-collector.exe>
param([Parameter(Mandatory = $true)][string]$Program)
$ErrorActionPreference = 'Stop'
$server = $null
# Whatever fails, the little web server of the feed does not stay behind.
trap { if ($server) { Stop-Process -Id $server.Id -Force -ErrorAction SilentlyContinue }; throw $_ }

function Wait-Until([scriptblock]$Condition, [string]$What, [int]$Seconds = 90) {
    for ($i = 0; $i -lt $Seconds; $i++) {
        if (& $Condition) { return }
        Start-Sleep -Seconds 1
    }
    throw "Timed out waiting for: $What"
}

$work = Join-Path $env:RUNNER_TEMP 'installer-smoke'
Remove-Item $work -Recurse -Force -ErrorAction SilentlyContinue
New-Item $work -ItemType Directory | Out-Null
$pack = Join-Path $PSScriptRoot 'pack.sh'
$first = Join-Path $work 'v1'
$second = Join-Path $work 'v2'
# bash reads these as paths: forward slashes are right for it on Windows too.
function Slashes([string]$Path) { return $Path -replace '\\', '/' }
bash (Slashes $pack) (Slashes $Program) 0.0.1 (Slashes $first)
bash (Slashes $pack) (Slashes $Program) 0.0.2 (Slashes $second)

$app = Join-Path $env:LOCALAPPDATA 'MnemarrCollector'
# The program at the root of the folder, which an update does not change, is named after the application (Velopack's title).
$stub = Join-Path $app 'Mnemarr collector.exe'
# The program at the root is a launcher with no console of its own: what the program says is read from the one in `current`, which an update replaces.
$real = Join-Path $app 'current\mnemarr-collector.exe'
$run = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Run'

# The test can run again on the same computer: what an earlier run left (an installed collector, its entry in the session, the PATH) is removed first,
# by the uninstaller itself, and waited for.
function Wait-Program([string]$Path, [string[]]$Arguments) {
    # The installer is not a console program: called with `&`, PowerShell would go on while it still works. Its last step stops every program in the folder,
    # so a check made in that moment is stopped with it (a `--version` that answered nothing). Only that process is waited for, not the collector it starts.
    $process = Start-Process -FilePath $Path -ArgumentList $Arguments -PassThru -WindowStyle Hidden
    $process.WaitForExit()
    if ($process.ExitCode -ne 0) { throw "$Path $Arguments ended with exit code $($process.ExitCode)" }
}
$leftover = Join-Path $app 'Update.exe'
if (Test-Path $leftover) {
    Write-Host '--- an earlier run left a collector: uninstall it first'
    Wait-Program $leftover @('uninstall', '--silent')
    Wait-Until { -not (Test-Path (Join-Path $app 'current')) } 'the earlier collector taken away' -Seconds 180
}
# What a run stopped midway can leave without its uninstaller: a running collector, the folder, the entry in the session, the folder on the PATH.
Get-Process | Where-Object { $_.Path -like "$app\*" } | Stop-Process -Force -ErrorAction SilentlyContinue
Remove-Item $app -Recurse -Force -ErrorAction SilentlyContinue
Remove-ItemProperty $run -Name MnemarrCollector -ErrorAction SilentlyContinue
$userPath = [Environment]::GetEnvironmentVariable('Path', 'User')
if ($userPath) {
    $kept = ($userPath -split ';' | Where-Object { $_ -and $_ -ne $app }) -join ';'
    if ($kept -ne $userPath) { [Environment]::SetEnvironmentVariable('Path', $kept, 'User') }
}

Write-Host '--- install 0.0.1 without a window'
Wait-Program (Join-Path $first 'MnemarrCollector-win-Setup.exe') @('--silent')
Wait-Until { Test-Path (Join-Path $app 'current\mnemarr-collector.exe') } 'the program in place'
Wait-Until { Test-Path $stub } 'the program at the root, which an update does not change'
Wait-Until { (Get-ItemProperty $run -ErrorAction SilentlyContinue).MnemarrCollector -like "*$stub*" } 'the entry that starts it with the session'
Wait-Until { ([Environment]::GetEnvironmentVariable('Path', 'User') -split ';') -contains $app } 'the folder on the PATH of the user'
# A new terminal reads the PATH of the user: this one is given it, as a person would find it.
$env:Path = $env:Path + ';' + [Environment]::GetEnvironmentVariable('Path', 'User')
$typed = (cmd /c 'mnemarr-collector --version 2>&1') -join ' '
if ($typed -notmatch 'mnemarr-collector') { throw "mnemarr-collector is not a command in a terminal: '$typed'" }
# The installer has ended (above); the program is still asked again for a while, the way the other checks wait, and what it said last is shown if it never answers.
$said = ''
$code = $null
try {
    # The condition runs in a scope of its own: what it saw is kept in the script's, to be shown if it never answers.
    Wait-Until { $script:said = (& $real --version) -join ' '; $script:code = $LASTEXITCODE; $script:said -match '^mnemarr-collector \d' } 'the program to say its version' -Seconds 30
} catch {
    throw "The program does not answer: '$said' (exit code $code): $($_.Exception.Message)"
}
Write-Host "installed: $said"
# The application the installer starts is a program of windows, not of a console (no window to close that would close the collector); the one for the
# terminal is a console program. The subsystem is a field of the header of the file: 2 = windows, 3 = console.
function Subsystem([string]$Path) {
    $bytes = [IO.File]::ReadAllBytes($Path)
    return [BitConverter]::ToUInt16($bytes, [BitConverter]::ToInt32($bytes, 0x3C) + 24 + 68)
}
$application = Join-Path $app 'current\mnemarr-collector-app.exe'
if ((Subsystem $application) -ne 2) { throw 'The application the installer starts opens a console' }
if ((Subsystem $real) -ne 3) { throw 'The program for the terminal is not a console program' }

Write-Host '--- a Mnemarr that carries 0.0.2 (this computer)'
$config = Join-Path $work 'config'
New-Item $config -ItemType Directory | Out-Null
Set-Content -Path (Join-Path $config 'key-feed') -Value 'a-key' -NoNewline
$destinations = @{ destinations = @(@{ name = 'feed'; server = 'http://127.0.0.1:8099'; machine = 'ci-pc'; insecure = $true; keyFile = 'key-feed'; stateFile = 'state-feed.json' }) } | ConvertTo-Json -Depth 4
Set-Content -Path (Join-Path $config 'destinations.json') -Value $destinations
New-Item (Join-Path $work 'feed\api\v1\ingest\updates') -ItemType Directory -Force | Out-Null
Copy-Item (Join-Path $second '*') (Join-Path $work 'feed\api\v1\ingest\updates') -Recurse
$server = Start-Process python -ArgumentList '-m', 'http.server', '8099', '--bind', '127.0.0.1', '--directory', (Join-Path $work 'feed') -PassThru -WindowStyle Hidden
# The feed is served the way a Mnemarr serves it: anonymously (the integration tests prove the real controller; the key here is only what a destination holds).
$feedUrl = 'http://127.0.0.1:8099/api/v1/ingest/updates/releases.win.json'
$lastError = ''
try {
    Wait-Until { try { (New-Object Net.WebClient).DownloadString($feedUrl) | Out-Null; $true } catch { $script:lastError = $_.Exception.Message; $false } } 'the feed served' -Seconds 30
} catch {
    Write-Host "feed not served: $lastError; the server has exited: $($server.HasExited)"
    Get-ChildItem (Join-Path $work 'feed') -Recurse | Select-Object -First 20 -ExpandProperty FullName | Write-Host
    throw
}

Write-Host '--- update from it'
$before = (& $real status --config-dir $config) -join "`n"
Write-Host $before
if ($before -notmatch 'version: 0\.0\.1') { throw "It does not say it is 0.0.1: '$before'" }
& $stub update --config-dir $config
Wait-Until { (Get-Content (Join-Path $app 'current\sq.version') -Raw) -match '0\.0\.2' } 'version 0.0.2 in place'
$after = (& $real status --config-dir $config) -join "`n"
Write-Host $after
if ($after -notmatch 'version: 0\.0\.2') { throw "It did not become 0.0.2: '$after'" }
Wait-Until { (Get-ItemProperty $run).MnemarrCollector -like "*$stub*" } 'the entry that starts it with the session, still right after the update'

# After an update the program restarts, and starts the collector in the background through a chain of programs: it is let to finish before the program is taken
# away, as a person who uninstalls a minute later would find it (an uninstall in the middle of that chain starts a collector after the folder is gone).
Wait-Until { ((& $real status) -join ' ') -match 'running: yes' } 'the collector running again after the update'

Write-Host '--- uninstall'
Wait-Program (Join-Path $app 'Update.exe') @('uninstall', '--silent')
try {
    Wait-Until { -not (Test-Path (Join-Path $app 'current')) } 'the program taken away' -Seconds 180
} catch {
    Write-Host 'What is left, and who holds it:'
    Get-ChildItem $app -Recurse -ErrorAction SilentlyContinue | Select-Object -First 40 -ExpandProperty FullName | Write-Host
    Get-Process | Where-Object { $_.Path -like "$app*" } | ForEach-Object {
        $details = Get-CimInstance Win32_Process -Filter "ProcessId=$($_.Id)"
        Write-Host "process $($_.Id) started $($_.StartTime) parent $($details.ParentProcessId): $($details.CommandLine)"
    }
    Write-Host "now: $(Get-Date)"
    Get-Content (Join-Path $env:LOCALAPPDATA 'velopack\velopack_MnemarrCollector.log') -Tail 25 -ErrorAction SilentlyContinue | Write-Host
    throw
}
Wait-Until { -not (Get-ItemProperty $run -ErrorAction SilentlyContinue).MnemarrCollector } 'the entry that starts it with the session taken away'
Wait-Until { -not (([Environment]::GetEnvironmentVariable('Path', 'User') -split ';') -contains $app) } 'the folder taken off the PATH'
# The machine stays joined: its keys and positions are in the folder of the collector, which is not the one the uninstaller removes.
if (-not (Test-Path (Join-Path $config 'destinations.json'))) { throw 'The uninstall removed the folder of the collector: the machine would not stay joined' }
Stop-Process -Id $server.Id -Force -ErrorAction SilentlyContinue
Write-Host 'installer, update and uninstall: all done'

# The installer of the collector, tried for real on Windows (ADR 0032): installed without a window, then updated from a feed, then removed. This is what a person
# does, and what the unit tests cannot: Velopack puts the program in place, the hooks make it start with the session and put it on the PATH, a newer version is
# taken from the Mnemarr it sends to and replaces the running one, and the uninstaller leaves nothing that starts it again.
#
#   smoke.ps1 -Program <mnemarr-collector.exe>
param([Parameter(Mandatory = $true)][string]$Program)
$ErrorActionPreference = 'Stop'

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

Write-Host '--- install 0.0.1 without a window'
& (Join-Path $first 'MnemarrCollector-win-Setup.exe') --silent
Wait-Until { Test-Path (Join-Path $app 'current\mnemarr-collector.exe') } 'the program in place'
Wait-Until { Test-Path $stub } 'the program at the root, which an update does not change'
Wait-Until { (Get-ItemProperty $run -ErrorAction SilentlyContinue).MnemarrCollector -like "*$stub*" } 'the entry that starts it with the session'
Wait-Until { ([Environment]::GetEnvironmentVariable('Path', 'User') -split ';') -contains $app } 'the folder on the PATH of the user'
$said = (& $real --version) -join ' '
if ($said -notmatch 'mnemarr-collector') { throw "The program does not answer: '$said'" }
Write-Host "installed: $said"

Write-Host '--- a Mnemarr that carries 0.0.2 (this computer)'
$config = Join-Path $work 'config'
New-Item $config -ItemType Directory | Out-Null
Set-Content -Path (Join-Path $config 'key-feed') -Value 'a-key' -NoNewline
$destinations = @{ destinations = @(@{ name = 'feed'; server = 'http://127.0.0.1:8099'; machine = 'ci-pc'; insecure = $true; keyFile = 'key-feed'; stateFile = 'state-feed.json' }) } | ConvertTo-Json -Depth 4
Set-Content -Path (Join-Path $config 'destinations.json') -Value $destinations
New-Item (Join-Path $work 'feed\api\v1\ingest\updates') -ItemType Directory -Force | Out-Null
Copy-Item (Join-Path $second '*') (Join-Path $work 'feed\api\v1\ingest\updates') -Recurse
$server = Start-Process python -ArgumentList '-m', 'http.server', '8099', '--bind', '127.0.0.1', '--directory', (Join-Path $work 'feed') -PassThru -WindowStyle Hidden
Start-Sleep -Seconds 2

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

Write-Host '--- uninstall'
& (Join-Path $app 'Update.exe') uninstall --silent
Wait-Until { -not (Test-Path (Join-Path $app 'current')) } 'the program taken away'
Wait-Until { -not (Get-ItemProperty $run -ErrorAction SilentlyContinue).MnemarrCollector } 'the entry that starts it with the session taken away'
Wait-Until { -not (([Environment]::GetEnvironmentVariable('Path', 'User') -split ';') -contains $app) } 'the folder taken off the PATH'
Stop-Process -Id $server.Id -Force -ErrorAction SilentlyContinue
Write-Host 'installer, update and uninstall: all done'

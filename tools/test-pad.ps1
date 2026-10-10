#requires -Version 5.1
<#
.SYNOPSIS
    Starts (or stops) everything a phone needs to use the secure controller page: the relay
    with the shared certificate, then the offline hotspot. Built for #20 testing.

.DESCRIPTION
    Start does, in order:
      1. checks Node is installed;
      2. installs the relay's packages;
      3. stops any hotspot already running, because Windows' own DNS for the hotspot would
         take port 53 before the relay can;
      4. opens the relay in its own PowerShell window (npm run start:pad), so you can watch it;
      5. waits until the relay is listening on 8443 and answering DNS on 53;
      6. starts the offline hotspot;
      7. prints the Wi-Fi name, password and the address to open on the phone.
    Stop turns the hotspot off and stops the relay.

    Run it with Windows PowerShell 5.1 (powershell.exe); hotspot-control.ps1 needs 5.1.

.EXAMPLE
    powershell -ExecutionPolicy Bypass -File tools\test-pad.ps1
.EXAMPLE
    powershell -ExecutionPolicy Bypass -File tools\test-pad.ps1 -Action Stop
#>
[CmdletBinding()]
param(
    [ValidateSet('Start', 'Stop')] [string] $Action = 'Start',
    [string] $Ssid = 'AJ Motion',
    [string] $Passphrase = 'motion1234'
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$RepoRoot = Split-Path -Parent $PSScriptRoot
$RelayDir = Join-Path $RepoRoot 'relay'
$HotspotScript = Join-Path $RepoRoot 'hotspot\hotspot-control.ps1'
$CertBundle = Join-Path $env:LOCALAPPDATA 'AJsMotionControls\cert\pad-cert.json'
$RelayPort = 8443
$ReadyTimeoutSeconds = 90

function Write-Step { param([string] $Message) Write-Host "==> $Message" -ForegroundColor Cyan }
function Write-Good { param([string] $Message) Write-Host "    $Message" -ForegroundColor Green }

# hotspot-control.ps1 prints exactly one JSON line as its last line of output.
function Invoke-Hotspot {
    param([string[]] $Arguments)
    # Its progress text goes to stderr, which 5.1 turns into a terminating error under Stop.
    $ErrorActionPreference = 'Continue'
    $output = & powershell.exe -NoProfile -ExecutionPolicy Bypass -File $HotspotScript @Arguments 2>&1
    $last = ($output | Where-Object { "$_".Trim().StartsWith('{') } | Select-Object -Last 1)
    if (-not $last) { return [pscustomobject]@{ ok = $false; error = ($output -join ' ') } }
    return ("$last" | ConvertFrom-Json)
}

# The relay is a node process; find it by the port it listens on rather than by window, since
# npm starts node as a child of the window's PowerShell.
function Get-RelayProcessId {
    $listener = Get-NetTCPConnection -LocalPort $RelayPort -State Listen -ErrorAction SilentlyContinue |
        Select-Object -First 1
    if ($listener) { return [int] $listener.OwningProcess }
    return $null
}

function Stop-Relay {
    $relayPid = Get-RelayProcessId
    if (-not $relayPid) { return $false }
    $process = Get-Process -Id $relayPid -ErrorAction SilentlyContinue
    if ($process -and $process.ProcessName -eq 'node') {
        Stop-Process -Id $relayPid
        return $true
    }
    throw "Port $RelayPort is held by '$($process.ProcessName)' (process $relayPid), not the relay. Close that program and try again."
}

if ($Action -eq 'Stop') {
    Write-Step 'Turning the hotspot off'
    $result = Invoke-Hotspot @('-Action', 'Stop')
    if ($result.ok) { Write-Good 'Hotspot off.' } else { Write-Host "    Hotspot: $($result | ConvertTo-Json -Compress)" }
    Write-Step 'Stopping the relay'
    if (Stop-Relay) { Write-Good 'Relay stopped.' } else { Write-Good 'The relay was not running.' }
    return
}

Write-Step 'Checking Node'
$nodeVersion = & node --version 2>$null
if (-not $nodeVersion) { throw 'Node is not installed. Install the LTS version from https://nodejs.org and run this again.' }
Write-Good "Node $nodeVersion"

Write-Step 'Installing the relay packages'
# npm.cmd, not npm: npm.ps1 breaks under Set-StrictMode Latest.
& npm.cmd --prefix $RelayDir ci --omit=dev --no-audit --no-fund
if ($LASTEXITCODE -ne 0) { throw 'npm could not install the relay packages. The lines above say why.' }

Write-Step 'Making sure nothing old is in the way'
[void] (Invoke-Hotspot @('-Action', 'Stop'))
if (Stop-Relay) { Write-Good 'Stopped a relay left over from an earlier run.' }

Write-Step 'Starting the relay in its own window'
$relayCommand = "npm --prefix '$RelayDir' run start:pad"
$window = Start-Process -FilePath 'powershell.exe' -ArgumentList @('-NoExit', '-NoProfile', '-Command', $relayCommand) -PassThru
Write-Good 'A new window opened. Leave it open; it shows what the relay is doing.'

Write-Step "Waiting for the relay (up to $ReadyTimeoutSeconds seconds)"
$deadline = (Get-Date).AddSeconds($ReadyTimeoutSeconds)
$relayPid = $null
$dnsReady = $false
while ((Get-Date) -lt $deadline) {
    if ($window.HasExited) { throw 'The relay window closed. Run this again and read the relay window before it closes.' }
    $relayPid = Get-RelayProcessId
    if ($relayPid) {
        $dns = Get-NetUDPEndpoint -LocalPort 53 -ErrorAction SilentlyContinue | Where-Object { $_.OwningProcess -eq $relayPid }
        if ($dns) { $dnsReady = $true; break }
    }
    Start-Sleep -Seconds 1
}
if (-not $relayPid) { throw "The relay did not start listening on port $RelayPort. Its window says why; the usual cause is no certificate (connect this PC to the internet once)." }
if (-not $dnsReady) { throw 'The relay started but is not answering DNS on port 53. Look for "DNS responder FAILED" in its window.' }
Write-Good "Relay listening on $RelayPort and answering DNS."

Write-Step 'Starting the offline hotspot'
$result = Invoke-Hotspot @('-Action', 'Start', '-Ssid', $Ssid, '-Passphrase', $Passphrase)
if (-not $result.ok) { throw "The hotspot did not start: $($result | ConvertTo-Json -Compress)" }
Write-Good 'Hotspot on.'

$name = 'pad.skiprocking.ca'
if (Test-Path $CertBundle) { $name = (Get-Content -Raw $CertBundle | ConvertFrom-Json).name }

Write-Host ''
Write-Host 'Ready. On the phone:' -ForegroundColor Green
Write-Host "  1. Join Wi-Fi  $Ssid   password  $Passphrase"
Write-Host "  2. Open        https://${name}:$RelayPort/pad.html"
Write-Host 'Dolphin reads the controller at 127.0.0.1:26760 (DSU), as before.'
Write-Host 'When you are done:  powershell -ExecutionPolicy Bypass -File tools\test-pad.ps1 -Action Stop'

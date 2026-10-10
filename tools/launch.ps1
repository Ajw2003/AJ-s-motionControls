#requires -Version 5.1
<#
.SYNOPSIS
    Starts AJ Motion: hotspot credentials, emulator setup, relay, offline hotspot, join page
    (#12). Stops everything when the relay exits (the join page's Stop button, or Ctrl+C).

.DESCRIPTION
    Works from a git checkout (uses Node from PATH and installs the relay's packages) and
    from an install (uses the bundled node\node.exe next to tools\). The hotspot name and
    password are made once and remembered in %LOCALAPPDATA%\AJsMotionControls\hotspot.json.

    Ceiling: closing this window ends the relay but can leave the hotspot on. The next
    launch turns it off first, and the join page's Stop button is the clean way out.

.EXAMPLE
    powershell -ExecutionPolicy Bypass -File tools\launch.ps1
.EXAMPLE
    powershell -ExecutionPolicy Bypass -File tools\launch.ps1 -Stop
#>
[CmdletBinding()]
param([switch] $Stop)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$Root = Split-Path -Parent $PSScriptRoot
$RelayDir = Join-Path $Root 'relay'
$HotspotScript = Join-Path $Root 'hotspot\hotspot-control.ps1'
$StateDir = Join-Path $env:LOCALAPPDATA 'AJsMotionControls'
$CredentialsPath = Join-Path $StateDir 'hotspot.json'
$RelayPort = 8443
$JoinPort = 8780
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

# The relay is a node process; find it by the port it listens on.
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
    throw "Port $RelayPort is held by '$($process.ProcessName)' (process $relayPid), not AJ Motion. Close that program and try again."
}

function Stop-Everything {
    Write-Step 'Turning the hotspot off'
    $result = Invoke-Hotspot @('-Action', 'Stop')
    if ($result.ok) { Write-Good 'Hotspot off.' } else { Write-Host "    Hotspot: $($result | ConvertTo-Json -Compress)" }
    Write-Step 'Stopping the relay'
    if (Stop-Relay) { Write-Good 'Relay stopped.' } else { Write-Good 'The relay was not running.' }
}

# Random text from an alphabet without look-alike characters (no 0/O, 1/l/I).
function New-RandomText {
    param([string] $Alphabet, [int] $Length)
    $bytes = New-Object byte[] $Length
    [Security.Cryptography.RandomNumberGenerator]::Create().GetBytes($bytes)
    return -join ($bytes | ForEach-Object { $Alphabet[$_ % $Alphabet.Length] })
}

function Get-HotspotCredentials {
    if (Test-Path -LiteralPath $CredentialsPath) {
        $saved = Get-Content -Raw -LiteralPath $CredentialsPath | ConvertFrom-Json
        if ($saved.ssid -and $saved.passphrase) { return $saved }
    }
    $credentials = [pscustomobject]@{
        ssid       = 'AJ Motion ' + (New-RandomText 'ABCDEFGHJKLMNPQRSTUVWXYZ23456789' 4)
        passphrase = New-RandomText 'abcdefghjkmnpqrstuvwxyz23456789' 12
    }
    New-Item -ItemType Directory -Force -Path $StateDir | Out-Null
    $credentials | ConvertTo-Json | Set-Content -LiteralPath $CredentialsPath -Encoding UTF8
    return $credentials
}

function Get-NodePath {
    $bundled = Join-Path $Root 'node\node.exe'
    if (Test-Path -LiteralPath $bundled) { return $bundled }
    $onPath = Get-Command node -ErrorAction SilentlyContinue
    if ($onPath) { return $onPath.Source }
    throw 'Node is not installed. Install the LTS version from https://nodejs.org and run this again.'
}

if ($Stop) {
    Stop-Everything
    return
}

Write-Step 'Hotspot name and password'
$credentials = Get-HotspotCredentials
Write-Good "$($credentials.ssid) (saved in $CredentialsPath)"

Write-Step 'Setting up emulators'
. (Join-Path $PSScriptRoot 'emulators.ps1')
$emulators = @(Invoke-EmulatorSetup)
foreach ($e in $emulators) { Write-Good "$($e.emulator): $($e.detail)" }

Write-Step 'Making sure nothing old is in the way'
[void] (Invoke-Hotspot @('-Action', 'Stop'))
if (Stop-Relay) { Write-Good 'Stopped a relay left over from an earlier run.' }

Write-Step 'Checking Node'
$node = Get-NodePath
Write-Good $node

if (-not (Test-Path -LiteralPath (Join-Path $RelayDir 'node_modules'))) {
    Write-Step 'Installing the relay packages'
    # npm.cmd, not npm: npm.ps1 breaks under Set-StrictMode Latest.
    & npm.cmd --prefix $RelayDir ci --omit=dev --no-audit --no-fund
    if ($LASTEXITCODE -ne 0) { throw 'npm could not install the relay packages. The lines above say why.' }
}

$env:JOIN_PORT = "$JoinPort"
$env:JOIN_SSID = $credentials.ssid
$env:JOIN_PASS = $credentials.passphrase
$env:JOIN_EMULATORS = ConvertTo-Json -InputObject $emulators -Compress

$relay = $null
try {
    Write-Step 'Starting the relay'
    $relay = Start-Process -FilePath $node -ArgumentList 'start-pad.js' -WorkingDirectory $RelayDir -NoNewWindow -PassThru

    Write-Step "Waiting for the relay (up to $ReadyTimeoutSeconds seconds)"
    $deadline = (Get-Date).AddSeconds($ReadyTimeoutSeconds)
    $ready = $false
    while ((Get-Date) -lt $deadline) {
        if ($relay.HasExited) {
            throw 'The relay stopped. Its last lines above say why; the usual cause is no certificate yet, so connect this PC to the internet once and start again.'
        }
        try {
            $health = Invoke-RestMethod -Uri "http://127.0.0.1:$JoinPort/health" -TimeoutSec 2
            if ($health.dns.bound) { $ready = $true; break }
        } catch {
            Write-Verbose "Relay not answering yet: $($_.Exception.Message)"
        }
        Start-Sleep -Milliseconds 500
    }
    if (-not $ready) { throw 'The relay started but is not answering DNS on port 53. Look for "DNS responder FAILED" above.' }
    Write-Good "Relay listening on $RelayPort and answering DNS."

    Write-Step 'Starting the offline hotspot'
    # 5 GHz, not the script's 2.4 GHz default: on aj's PC 2.4 GHz stalled the motion stream
    # for 0.6-0.8 s about every 10 s, 5 GHz none (docs/6-decisions/Decisions.md, 2026-10-10).
    # An adapter that refuses the band is reported by hotspot-control and left to Windows.
    $result = Invoke-Hotspot @('-Action', 'Start', '-Ssid', $credentials.ssid, '-Passphrase', $credentials.passphrase, '-Band', 'FiveGigahertz')
    if (-not $result.ok) { throw "The hotspot did not start: $($result | ConvertTo-Json -Compress)" }
    Write-Good 'Hotspot on.'

    $joinUrl = "http://127.0.0.1:$JoinPort/join.html"
    Start-Process $joinUrl
    Write-Host ''
    Write-Host 'Ready. The join page is open in your browser. On the phone:' -ForegroundColor Green
    Write-Host "  1. Join Wi-Fi  $($credentials.ssid)   password  $($credentials.passphrase)"
    Write-Host "  2. Open        https://pad.skiprocking.ca:$RelayPort/pad.html"
    Write-Host "Join page: $joinUrl"
    Write-Host 'To stop: click Stop AJ Motion on the join page, or press Ctrl+C here.'

    while (-not $relay.HasExited) { Start-Sleep -Seconds 1 }
} finally {
    Stop-Everything
}

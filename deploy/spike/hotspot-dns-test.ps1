#requires -Version 5.1
<#
.SYNOPSIS
    Answers the one question the offline-hotspot spike cannot answer without a network:
    does ICS take UDP :53 when tethering starts, and can our DNS responder hold it?

.DESCRIPTION
    Order matters and is the whole experiment. The responder binds :53 BEFORE tethering
    starts, because ICS claiming a port that is already held is the case we care about.
    If the responder survives and phones can still resolve, offline play is reachable.

    Everything is restored on exit: tethering returns to its previous state and the
    original SSID/passphrase are put back.

    DISRUPTIVE. On a machine with one Wi-Fi adapter, starting tethering can interrupt the
    existing Wi-Fi connection. Do not run this in the middle of anything that matters.

.PARAMETER Ssid
    SSID for the test hotspot. Default 'RockSkip Spike'.

.PARAMETER Passphrase
    Passphrase for the test hotspot. Minimum 8 characters. Default 'skipspike'.

.PARAMETER KeepRunning
    Leave the hotspot and responder up until Enter is pressed, so a phone can be tested.
    Without it the script reports the port result and tears down immediately.

.EXAMPLE
    ./hotspot-dns-test.ps1
.EXAMPLE
    ./hotspot-dns-test.ps1 -KeepRunning
#>
[CmdletBinding()]
param(
    [string] $Ssid = 'RockSkip Spike',
    [string] $Passphrase = 'skipspike',
    [string] $Upstream = '1.1.1.1',
    [switch] $KeepRunning
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

# Windows PowerShell 5.1 does not load this by default, and without it the WinRT await
# helpers below fail with "Unable to find type [System.WindowsRuntimeSystemExtensions]"
# at the first ConfigureAccessPointAsync call - after the responder is already running.
Add-Type -AssemblyName System.Runtime.WindowsRuntime

$SpikeDir = Split-Path -Parent $MyInvocation.MyCommand.Path

function Write-Step { param([string] $m) Write-Host "`n== $m" -ForegroundColor Cyan }
function Write-Ok   { param([string] $m) Write-Host "   [ok]   $m" -ForegroundColor Green }
function Write-Bad  { param([string] $m) Write-Host "   [FAIL] $m" -ForegroundColor Red }
function Write-Info { param([string] $m) Write-Host "   $m" -ForegroundColor Gray }

# WinRT async helpers. PowerShell cannot await these directly; this is the standard
# reflection route to AsTask for the two shapes the tethering API returns.
function Await-Operation {
    param($Operation, [Type] $ResultType)
    $asTask = ([System.WindowsRuntimeSystemExtensions].GetMethods() |
        Where-Object { $_.Name -eq 'AsTask' -and $_.GetParameters().Count -eq 1 -and
                       $_.GetParameters()[0].ParameterType.Name -eq 'IAsyncOperation`1' })[0]
    $task = $asTask.MakeGenericMethod($ResultType).Invoke($null, @($Operation))
    $task.Wait(-1) | Out-Null
    $task.Result
}

function Await-Action {
    param($Action)
    $asTask = ([System.WindowsRuntimeSystemExtensions].GetMethods() |
        Where-Object { $_.Name -eq 'AsTask' -and $_.GetParameters().Count -eq 1 -and
                       -not $_.IsGenericMethod -and
                       $_.GetParameters()[0].ParameterType.Name -eq 'IAsyncAction' })[0]
    $task = $asTask.Invoke($null, @($Action))
    $task.Wait(-1) | Out-Null
}

function Get-PortHolder {
    $endpoints = @(Get-NetUDPEndpoint -LocalPort 53 -ErrorAction SilentlyContinue)
    if ($endpoints.Count -eq 0) { return $null }
    $endpoints | ForEach-Object {
        $proc = Get-Process -Id $_.OwningProcess -ErrorAction SilentlyContinue
        [pscustomobject]@{
            Address = $_.LocalAddress
            Pid     = $_.OwningProcess
            Process = if ($proc) { $proc.ProcessName } else { '<unknown>' }
        }
    }
}

$responder    = $null
$manager      = $null
$originalCfg  = $null
$startedByUs  = $false

try {
    # ---------------------------------------------------------------- preflight
    Write-Step 'Preflight'

    if (-not (Test-Path (Join-Path $SpikeDir 'dns-responder.js'))) {
        throw "dns-responder.js not found beside this script."
    }
    if ($Passphrase.Length -lt 8) { throw "Passphrase must be at least 8 characters." }

    $node = (Get-Command node -ErrorAction SilentlyContinue)
    if (-not $node) { throw "node not found on PATH." }
    Write-Ok "node at $($node.Source)"

    $profile = [Windows.Networking.Connectivity.NetworkInformation,Windows.Networking,ContentType=WindowsRuntime]::GetInternetConnectionProfile()
    if ($null -eq $profile) {
        Write-Bad 'No internet connection profile.'
        Write-Info 'This is itself a finding: see whether tethering can start at all without one.'
        throw 'Cannot create a tethering manager without a connection profile.'
    }
    Write-Ok "uplink profile: $($profile.ProfileName)"

    $mgrType = [Windows.Networking.NetworkOperators.NetworkOperatorTetheringManager,Windows.Networking,ContentType=WindowsRuntime]
    $manager = $mgrType::CreateFromConnectionProfile($profile)
    $wasOn   = ($manager.TetheringOperationalState -eq 'On')
    Write-Ok "tethering currently: $($manager.TetheringOperationalState)"

    # ------------------------------------------------------- responder up first
    Write-Step 'Starting the DNS responder BEFORE tethering'
    Write-Info 'Order is the experiment: ICS must find :53 already taken.'

    $before = Get-PortHolder
    if ($before) {
        $before | ForEach-Object { Write-Bad "  :53 already held by $($_.Process) (pid $($_.Pid)) on $($_.Address)" }
        throw 'Something already holds :53. Clear it and retry.'
    }
    Write-Ok ':53 is free'

    # Forward upstream so the phone stays connected; bound to 0.0.0.0 out of necessity,
    # which makes the --allow source guard the real protection.
    # doc-ref 1715 docs/4-systems/HostingAndCertificates.md
    $responderArgs = @('dns-responder.js', '--ip', '192.168.137.1', '--suffix', '.local-ip.sh',
                       '--port', '53', '--bind', '0.0.0.0', '--allow', '192.168.137.0/24')
    if ($Upstream) {
        $responderArgs += @('--upstream', $Upstream)
        Write-Info "forwarding other names to $Upstream (keeps the phone from bailing to mobile data)"
    } else {
        Write-Info 'no upstream: every other name will NXDOMAIN, simulating a fully offline host'
    }

    $responder = Start-Process -FilePath $node.Source `
        -ArgumentList $responderArgs `
        -WorkingDirectory $SpikeDir -PassThru -WindowStyle Normal
    Start-Sleep -Milliseconds 800

    if ($responder.HasExited) { throw "Responder exited immediately (code $($responder.ExitCode))." }

    $held = Get-PortHolder
    if (-not $held) { throw 'Responder is running but nothing holds :53.' }
    Write-Ok "responder holds :53 (pid $($responder.Id))"

    # ----------------------------------------------------------- configure + go
    Write-Step 'Configuring and starting the hotspot'

    $originalCfg = $manager.GetCurrentAccessPointConfiguration()
    Write-Info "saving current SSID '$($originalCfg.Ssid)' for restore"

    $cfg = New-Object Windows.Networking.NetworkOperators.NetworkOperatorTetheringAccessPointConfiguration
    $cfg.Ssid = $Ssid
    $cfg.Passphrase = $Passphrase
    try {
        $cfg.Band = [Windows.Networking.NetworkOperators.TetheringWiFiBand]::TwoPointFourGigahertz
        Write-Ok 'band forced to 2.4 GHz (widest phone compatibility)'
    } catch {
        Write-Info "could not set band, leaving default: $($_.Exception.Message)"
    }

    Await-Action $manager.ConfigureAccessPointAsync($cfg)
    Write-Ok "configured SSID '$Ssid'"

    if (-not $wasOn) {
        $result = Await-Operation $manager.StartTetheringAsync() ([Windows.Networking.NetworkOperators.NetworkOperatorTetheringOperationResult])
        Write-Info "StartTetheringAsync -> $($result.Status)"
        if ($result.Status -ne 'Success') {
            throw "Tethering failed to start: $($result.Status) $($result.AdditionalErrorMessage)"
        }
        $startedByUs = $true
    }
    Write-Ok "tethering: $($manager.TetheringOperationalState)"

    # ------------------------------------------------------------ THE RESULT
    Write-Step 'THE RESULT - who holds :53 now?'
    Start-Sleep -Seconds 3

    $after = Get-PortHolder
    $stillOurs = $after | Where-Object { $_.Pid -eq $responder.Id }
    $ics       = $after | Where-Object { $_.Pid -ne $responder.Id }

    $after | ForEach-Object { Write-Info "  :53 on $($_.Address) held by $($_.Process) (pid $($_.Pid))" }

    if ($responder.HasExited) {
        Write-Bad 'RESPONDER WAS KILLED when tethering started.'
        Write-Info 'Offline play via a local responder needs ICS displaced or replaced.'
    } elseif ($stillOurs -and -not $ics) {
        Write-Ok 'RESPONDER SURVIVED and is the only holder of :53.'
        Write-Info 'This is the good outcome: offline play looks reachable.'
    } elseif ($stillOurs -and $ics) {
        Write-Host '   [MIXED] both are bound - ICS took the hotspot address, we hold the rest.' -ForegroundColor Yellow
        Write-Info 'Whether phones reach us depends on which address ICS hands out as DNS.'
        Write-Info 'The phone test below is what settles it.'
    } else {
        Write-Bad 'Responder no longer holds :53.'
    }

    $hotspotAddr = Get-NetIPAddress -AddressFamily IPv4 -ErrorAction SilentlyContinue |
        Where-Object { $_.IPAddress -like '192.168.137.*' } | Select-Object -First 1
    if ($hotspotAddr) {
        Write-Ok "hotspot address is $($hotspotAddr.IPAddress) (assumption confirmed)"
    } else {
        Write-Info 'No 192.168.137.x address found - the hotspot may use a different subnet.'
        Get-NetIPAddress -AddressFamily IPv4 | Where-Object { $_.IPAddress -notlike '127.*' } |
            ForEach-Object { Write-Info "  candidate: $($_.IPAddress) on $($_.InterfaceAlias)" }
    }

    # ------------------------------------------------------------- phone test
    if ($KeepRunning) {
        Write-Step 'Phone test'

        # Serves a plain-language success page. Do NOT tell anyone to browse to
        # 192.168.137.1 directly: an IP URL performs no DNS lookup, so it would prove
        # nothing about the very thing being tested.
        $confirm = Start-Process -FilePath $node.Source `
            -ArgumentList @('confirm-server.js', '--port', '80') `
            -WorkingDirectory $SpikeDir -PassThru -WindowStyle Normal
        Start-Sleep -Milliseconds 600
        if ($confirm.HasExited) {
            Write-Bad "Confirmation server exited (code $($confirm.ExitCode)). Port 80 may be taken."
        } else {
            Write-Ok "confirmation server on :80 (pid $($confirm.Id))"
        }

        & $node.Source (Join-Path $SpikeDir 'wifi-qr.js') `
            --ssid $Ssid --pass $Passphrase --url 'http://play.local-ip.sh/'

        Write-Host '   ON THE PHONE:' -ForegroundColor Yellow
        Write-Info '     1. Scan the FIRST QR to join the hotspot.'
        Write-Info '     2. Ignore any "no internet" warning and stay connected.'
        Write-Info '     3. Scan the SECOND QR, or type this into a browser:'
        Write-Host  '            http://play.local-ip.sh/' -ForegroundColor White
        Write-Host ''
        Write-Info '   A green "It worked" page is a PASS.'
        Write-Info '   "Server not found" is a FAIL - the phone used upstream DNS, not us.'
        Write-Info '   That name exists in no public DNS, so it can only resolve via this PC.'
        Write-Host ''
        Read-Host '   Press Enter to tear everything down'

        if ($confirm -and -not $confirm.HasExited) {
            try { Stop-Process -Id $confirm.Id -Force } catch { }
        }
    }
}
catch {
    Write-Host ''
    Write-Bad $_.Exception.Message
}
finally {
    Write-Step 'Restoring'

    if ($startedByUs -and $manager) {
        try {
            Await-Operation $manager.StopTetheringAsync() ([Windows.Networking.NetworkOperators.NetworkOperatorTetheringOperationResult]) | Out-Null
            Write-Ok 'tethering stopped'
        } catch { Write-Info "could not stop tethering: $($_.Exception.Message)" }
    }

    if ($originalCfg -and $manager) {
        try {
            Await-Action $manager.ConfigureAccessPointAsync($originalCfg)
            Write-Ok "SSID restored to '$($originalCfg.Ssid)'"
        } catch { Write-Info "could not restore SSID: $($_.Exception.Message)" }
    }

    if ($responder -and -not $responder.HasExited) {
        try { Stop-Process -Id $responder.Id -Force; Write-Ok 'responder stopped' } catch { }
    }

    Write-Host ''
}

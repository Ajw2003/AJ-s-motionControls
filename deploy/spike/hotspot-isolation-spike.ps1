#requires -Version 5.1
<#
.SYNOPSIS
    Can the hotspot carry the game but NOT the internet?

.DESCRIPTION
    If guests' traffic never traverses the host, the host cannot observe it - a far stronger
    guarantee than promising not to look. Android keeps mobile data as its default route for a
    Wi-Fi network it judges unvalidated, so a hotspot with no internet means a guest's real
    browsing never arrives here at all.

    This script finds out whether Windows lets us do that. It runs in two parts:

      -Measure  (default, no changes)  Starts tethering, records how ICS configures routing
                                       and NAT, then stops. Read-only apart from the hotspot
                                       itself. Answers "what lever exists?"

      -Apply    (needs Administrator)  Additionally tries each candidate mitigation and reports
                                       whether it took effect and whether ICS reverted it.
                                       Everything is restored on exit.

    Nothing here is a security control until a phone has confirmed it end to end. See
    ../../docs/SecurityReview.md section 2.

.PARAMETER Apply
    Attempt the mitigations rather than only measuring. Requires an elevated shell.

.PARAMETER KeepRunning
    Hold the hotspot up until Enter is pressed, so a phone can verify: game reachable,
    internet not.

.EXAMPLE
    ./hotspot-isolation-spike.ps1
.EXAMPLE
    ./hotspot-isolation-spike.ps1 -Apply -KeepRunning
#>
[CmdletBinding()]
param(
    [string] $Ssid = 'RockSkip Spike',
    [string] $Passphrase = 'skipspike',
    [switch] $Apply,
    [switch] $KeepRunning
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Runtime.WindowsRuntime

function Write-Step { param([string] $m) Write-Host "`n== $m" -ForegroundColor Cyan }
function Write-Ok   { param([string] $m) Write-Host "   [ok]   $m" -ForegroundColor Green }
function Write-Bad  { param([string] $m) Write-Host "   [FAIL] $m" -ForegroundColor Red }
function Write-Info { param([string] $m) Write-Host "   $m" -ForegroundColor Gray }
function Write-Warn2{ param([string] $m) Write-Host "   [warn] $m" -ForegroundColor Yellow }

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

function Get-HotspotInterface {
    $addr = Get-NetIPAddress -AddressFamily IPv4 -ErrorAction SilentlyContinue |
        Where-Object { $_.IPAddress -like '192.168.137.*' } | Select-Object -First 1
    if ($addr) { return $addr.InterfaceAlias }
    return $null
}

function Show-RoutingState {
    param([string] $Label)

    Write-Info "--- $Label ---"

    Get-NetIPInterface -AddressFamily IPv4 -ErrorAction SilentlyContinue |
        Where-Object { $_.ConnectionState -eq 'Connected' } |
        ForEach-Object { Write-Info ("     forwarding {0,-9} {1}" -f $_.Forwarding, $_.InterfaceAlias) }

    $ics = Get-Service SharedAccess -ErrorAction SilentlyContinue
    if ($ics) { Write-Info "     SharedAccess (ICS) service: $($ics.Status)" }

    # ipnathlp is the NAT engine. If it is loaded, packets are being translated somewhere
    # regardless of what the per-interface Forwarding flags claim. Module enumeration throws
    # on protected processes, so this is best-effort rather than authoritative.
    $natLoaded = $false
    foreach ($p in (Get-Process -Name svchost -ErrorAction SilentlyContinue)) {
        try {
            if ($p.Modules.ModuleName -contains 'ipnathlp.dll') { $natLoaded = $true; break }
        } catch { }
    }
    Write-Info "     ipnathlp.dll (NAT engine) loaded: $natLoaded"

    # The NAT's own view, which is the authoritative one when it is running.
    try {
        $natSessions = (Get-NetNat -ErrorAction SilentlyContinue | Measure-Object).Count
        Write-Info "     Get-NetNat entries: $natSessions"
    } catch { }
}

$SpikeDir    = Split-Path -Parent $MyInvocation.MyCommand.Path
$manager     = $null
$originalCfg = $null
$startedByUs = $false
$changed     = @()   # {Alias, Previous} for restore
$rulesAdded  = @()
$responder   = $null
$confirm     = $null

$isAdmin = ([Security.Principal.WindowsPrincipal] [Security.Principal.WindowsIdentity]::GetCurrent()
           ).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)

try {
    Write-Step 'Preflight'
    Write-Info "mode: $(if ($Apply) {'APPLY (will attempt mitigations)'} else {'MEASURE ONLY (no changes)'})"
    Write-Info "elevated: $isAdmin"

    if ($Apply -and -not $isAdmin) {
        throw 'Apply mode needs an elevated shell. Re-run PowerShell as Administrator.'
    }

    $profile = [Windows.Networking.Connectivity.NetworkInformation,Windows.Networking,ContentType=WindowsRuntime]::GetInternetConnectionProfile()
    if ($null -eq $profile) { throw 'No internet connection profile; cannot tether.' }
    Write-Ok "uplink: $($profile.ProfileName)"

    $mgrType = [Windows.Networking.NetworkOperators.NetworkOperatorTetheringManager,Windows.Networking,ContentType=WindowsRuntime]
    $manager = $mgrType::CreateFromConnectionProfile($profile)
    $wasOn = ($manager.TetheringOperationalState -eq 'On')

    $node = (Get-Command node -ErrorAction SilentlyContinue)
    if (-not $node) { throw 'node not found on PATH.' }

    Show-RoutingState 'BEFORE tethering'

    # ------------------------------------------------------- game side, before ICS
    # Without these the phone check fails for reasons that have nothing to do with
    # isolation - no resolver means play.local-ip.sh does not exist, and no web server
    # means nothing answers even if it did. Testing isolation against a game that was
    # never running would produce a confident, wrong conclusion.
    #
    # The responder must bind :53 BEFORE tethering starts, to win the race with ICS.
    Write-Step 'Starting the DNS responder (before tethering, to hold :53)'
    $responder = Start-Process -FilePath $node.Source -PassThru -WindowStyle Normal `
        -WorkingDirectory $SpikeDir `
        -ArgumentList @('dns-responder.js', '--ip', '192.168.137.1', '--suffix', '.local-ip.sh',
                        '--port', '53', '--bind', '0.0.0.0', '--allow', '192.168.137.0/24')
    Start-Sleep -Milliseconds 800
    if ($responder.HasExited) { throw "DNS responder exited immediately (code $($responder.ExitCode))." }
    Write-Ok "responder running (pid $($responder.Id))"

    # ------------------------------------------------------------------ hotspot up
    Write-Step 'Starting the hotspot'
    $originalCfg = $manager.GetCurrentAccessPointConfiguration()

    $cfg = New-Object Windows.Networking.NetworkOperators.NetworkOperatorTetheringAccessPointConfiguration
    $cfg.Ssid = $Ssid
    $cfg.Passphrase = $Passphrase
    try { $cfg.Band = [Windows.Networking.NetworkOperators.TetheringWiFiBand]::TwoPointFourGigahertz } catch { }
    Await-Action $manager.ConfigureAccessPointAsync($cfg)

    if (-not $wasOn) {
        $result = Await-Operation $manager.StartTetheringAsync() ([Windows.Networking.NetworkOperators.NetworkOperatorTetheringOperationResult])
        if ($result.Status -ne 'Success') { throw "Tethering failed: $($result.Status)" }
        $startedByUs = $true
    }
    Start-Sleep -Seconds 3
    Write-Ok "tethering: $($manager.TetheringOperationalState)"

    $hotspotAlias = Get-HotspotInterface
    if (-not $hotspotAlias) { throw 'No 192.168.137.x interface appeared.' }
    Write-Ok "hotspot interface: $hotspotAlias"

    Show-RoutingState 'DURING tethering (this is what ICS sets up)'

    # -------------------------------------------------- web server + firewall holes
    Write-Step 'Starting the confirmation web server'
    $confirm = Start-Process -FilePath $node.Source -PassThru -WindowStyle Normal `
        -WorkingDirectory $SpikeDir -ArgumentList @('confirm-server.js', '--port', '80')
    Start-Sleep -Milliseconds 600
    if ($confirm.HasExited) { Write-Bad 'Confirmation server exited - port 80 may be taken.' }
    else { Write-Ok "confirmation server on :80 (pid $($confirm.Id))" }

    # The hotspot adapter carries no network category, so Windows treats it as Public and
    # a Private-scoped rule is silently inert on it. This is the finding from the DNS
    # spike; adding the rules here means the test does not depend on leftovers from it.
    Write-Step 'Opening the firewall for the hotspot subnet only'
    if (-not $isAdmin) {
        Write-Warn2 'Not elevated - cannot add firewall rules.'
        Write-Warn2 'The phone will not reach the game unless rules already exist from a'
        Write-Warn2 'previous run. Re-run elevated for a valid test.'
    }
    foreach ($r in @(
        @{ Name = 'RockSkip isolation spike - DNS';  Proto = 'UDP'; Port = 53 },
        @{ Name = 'RockSkip isolation spike - HTTP'; Proto = 'TCP'; Port = 80 }
    )) {
        if (-not $isAdmin) { break }
        try {
            $rule = New-NetFirewallRule -DisplayName $r.Name -Direction Inbound `
                -Protocol $r.Proto -LocalPort $r.Port `
                -RemoteAddress 192.168.137.0/24 -Action Allow -Profile Any -ErrorAction Stop
            $rulesAdded += $rule.Name
            Write-Ok "$($r.Proto)/$($r.Port) allowed from 192.168.137.0/24"
        } catch {
            Write-Bad "could not add rule for $($r.Proto)/$($r.Port): $($_.Exception.Message)"
        }
    }

    # ----------------------------------------------------------------- mitigations
    if ($Apply) {
        Write-Step 'Attempt 1 - disable IP forwarding on the hotspot interface'
        try {
            $before = (Get-NetIPInterface -InterfaceAlias $hotspotAlias -AddressFamily IPv4).Forwarding
            Write-Info "before: $before"
            Set-NetIPInterface -InterfaceAlias $hotspotAlias -AddressFamily IPv4 -Forwarding Disabled
            $changed += @{ Alias = $hotspotAlias; Previous = $before }
            Start-Sleep -Seconds 2
            $after = (Get-NetIPInterface -InterfaceAlias $hotspotAlias -AddressFamily IPv4).Forwarding
            if ($after -eq 'Disabled') { Write-Ok "took effect (now $after)" }
            else { Write-Bad "reverted by ICS (now $after)" }
        } catch {
            Write-Bad "failed: $($_.Exception.Message)"
        }

        Write-Step 'Attempt 2 - disable forwarding on the uplink as well'
        Write-Info 'Only needed if attempt 1 alone does not stop traffic leaving the subnet.'
        Write-Info 'Routing needs forwarding on BOTH interfaces, so either should be sufficient;'
        Write-Info 'this is here because "should be" is not "is".'
        try {
            $uplinkAlias = (Get-NetIPInterface -AddressFamily IPv4 |
                Where-Object { $_.ConnectionState -eq 'Connected' -and
                               $_.InterfaceAlias -ne $hotspotAlias -and
                               $_.InterfaceAlias -notlike 'Loopback*' } |
                Select-Object -First 1).InterfaceAlias

            if (-not $uplinkAlias) {
                Write-Warn2 'no uplink interface identified; skipping'
            } else {
                $before = (Get-NetIPInterface -InterfaceAlias $uplinkAlias -AddressFamily IPv4).Forwarding
                Write-Info "uplink $uplinkAlias before: $before"
                Set-NetIPInterface -InterfaceAlias $uplinkAlias -AddressFamily IPv4 -Forwarding Disabled
                $changed += @{ Alias = $uplinkAlias; Previous = $before }
                Start-Sleep -Seconds 2
                $after = (Get-NetIPInterface -InterfaceAlias $uplinkAlias -AddressFamily IPv4).Forwarding
                if ($after -eq 'Disabled') { Write-Ok "took effect (now $after)" }
                else { Write-Bad "reverted by ICS (now $after)" }
            }
        } catch {
            Write-Bad "failed: $($_.Exception.Message)"
        }

        Show-RoutingState 'AFTER mitigations'
    } else {
        Write-Step 'Measure-only mode'
        Write-Info 'Re-run with -Apply from an elevated shell to attempt the mitigations.'
    }

    # ---------------------------------------------------------------- phone check
    if ($KeepRunning) {
        Write-Step 'Phone verification'

        & $node.Source (Join-Path $SpikeDir 'wifi-qr.js') `
            --ssid $Ssid --pass $Passphrase --url 'http://play.local-ip.sh/'

        Write-Host '   TURN OFF MOBILE DATA ON THE PHONE FIRST.' -ForegroundColor Yellow
        Write-Info '   Otherwise Android falls back to cellular when this Wi-Fi has no'
        Write-Info '   internet, a website loads anyway, and the test reads as a failure'
        Write-Info '   when it actually succeeded.'
        Write-Host ''
        Write-Info '   Then scan the first QR to join, and check BOTH:'
        Write-Host  '     1. http://play.local-ip.sh/  (second QR) -> should WORK  (game reachable)' -ForegroundColor White
        Write-Host  '     2. any normal website                    -> should FAIL  (no internet)' -ForegroundColor White
        Write-Host ''
        Write-Info '   Both must hold. Game working AND internet working means the isolation'
        Write-Info '   did not take, and the host can still see guest traffic.'
        Write-Host ''
        Read-Host '   Press Enter to restore everything'
    }
}
catch {
    Write-Host ''
    Write-Bad $_.Exception.Message
}
finally {
    Write-Step 'Restoring'

    foreach ($p in @($confirm, $responder)) {
        if ($p -and -not $p.HasExited) {
            try { Stop-Process -Id $p.Id -Force; Write-Ok "stopped pid $($p.Id)" } catch { }
        }
    }

    foreach ($name in $rulesAdded) {
        try { Remove-NetFirewallRule -Name $name -ErrorAction Stop; Write-Ok 'firewall rule removed' }
        catch { Write-Warn2 "could not remove firewall rule ${name}: $($_.Exception.Message)" }
    }

    foreach ($c in $changed) {
        try {
            Set-NetIPInterface -InterfaceAlias $c.Alias -AddressFamily IPv4 -Forwarding $c.Previous -ErrorAction Stop
            Write-Ok "forwarding restored on $($c.Alias) to $($c.Previous)"
        } catch { Write-Warn2 "could not restore forwarding on $($c.Alias): $($_.Exception.Message)" }
    }

    if ($startedByUs -and $manager) {
        try {
            Await-Operation $manager.StopTetheringAsync() ([Windows.Networking.NetworkOperators.NetworkOperatorTetheringOperationResult]) | Out-Null
            Write-Ok 'tethering stopped'
        } catch { Write-Warn2 "could not stop tethering: $($_.Exception.Message)" }
    }

    if ($originalCfg -and $manager) {
        try { Await-Action $manager.ConfigureAccessPointAsync($originalCfg); Write-Ok "SSID restored to '$($originalCfg.Ssid)'" }
        catch { Write-Warn2 "could not restore SSID: $($_.Exception.Message)" }
    }

    Write-Host ''
}

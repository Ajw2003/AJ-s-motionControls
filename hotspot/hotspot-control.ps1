#requires -Version 5.1
<#
.SYNOPSIS
    Start or stop this PC's Mobile Hotspot for Hotspot mode. Called by HotspotService.cs.

.DESCRIPTION
    Adapted from deploy/spike/hotspot-isolation-spike.ps1 — the WinRT calls below are proven,
    not new. Unity/Mono cannot call WinRT directly (see docs/ZeroSetupPlan.md Phase 4), so this
    runs as a short-lived child process spawned via ProcessUtil.Spawn, the same way the relay
    itself is spawned.

    Exactly one JSON line goes to stdout, always the last line of output — everything else
    (progress) goes to stderr, so a caller reading stdout gets one clean, parseable result.
    Exit code mirrors the JSON's "ok" field.

    #requires 5.1: the `[Type,Assembly,ContentType=WindowsRuntime]` type-literal syntax used to
    project WinRT types is a Windows PowerShell 5.1 language feature. pwsh.exe (PowerShell 7+)
    is not guaranteed to support it the same way — this must run under powershell.exe.

.PARAMETER Action
    Start or Stop.

.PARAMETER Ssid
.PARAMETER Passphrase
    Only used by -Action Start.

.EXAMPLE
    hotspot-control.ps1 -Action Start -Ssid "Rock Skipping" -Passphrase "skip1234"
.EXAMPLE
    hotspot-control.ps1 -Action Stop
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)] [ValidateSet('Start', 'Stop')] [string] $Action,
    [string] $Ssid,
    [string] $Passphrase
)

Set-StrictMode -Version Latest
Add-Type -AssemblyName System.Runtime.WindowsRuntime

# The name the elevated one-time setup registers. Fixed here and in that setup — never passed
# as a parameter, or it becomes a local privilege-escalation vector (docs/ZeroSetupPlan.md
# Phase 5's "no parameters" rule for the scheduled task's own action; the same reasoning
# applies to whatever triggers it).
$ForwardingTaskName = 'RockSkippingDisableForwarding'

function Write-Progress2 { param([string] $m) [Console]::Error.WriteLine($m) }

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

function Write-Result {
    param([hashtable] $Result)
    # -Compress: the contract is one line; a pretty-printed object would break that.
    Write-Output ($Result | ConvertTo-Json -Compress)
    exit ([int](-not $Result.ok))
}

function Get-TetheringManager {
    $profile = [Windows.Networking.Connectivity.NetworkInformation,Windows.Networking,ContentType=WindowsRuntime]::GetInternetConnectionProfile()
    if ($null -eq $profile) { return $null }

    $mgrType = [Windows.Networking.NetworkOperators.NetworkOperatorTetheringManager,Windows.Networking,ContentType=WindowsRuntime]
    return $mgrType::CreateFromConnectionProfile($profile)
}

if ($Action -eq 'Start') {
    try {
        Write-Progress2 'Resolving the tethering manager…'
        $manager = Get-TetheringManager
        if ($null -eq $manager) {
            Write-Result @{ ok = $false; error = 'no-connection-profile'
                message = 'No internet connection profile — this machine has no uplink to tether from.' }
        }

        $wasOn = ($manager.TetheringOperationalState -eq 'On')

        Write-Progress2 "Configuring the access point ($Ssid)…"
        $cfg = New-Object Windows.Networking.NetworkOperators.NetworkOperatorTetheringAccessPointConfiguration
        $cfg.Ssid = $Ssid
        $cfg.Passphrase = $Passphrase
        try {
            # Compatibility, not speed: some phones cannot see 5/6 GHz APs, and 2.4 GHz
            # carries further across a room. Best-effort — not every adapter exposes Band.
            $cfg.Band = [Windows.Networking.NetworkOperators.TetheringWiFiBand]::TwoPointFourGigahertz
        } catch { }

        try {
            Await-Action $manager.ConfigureAccessPointAsync($cfg)
        } catch {
            Write-Result @{ ok = $false; error = 'configure-failed'; message = $_.Exception.Message }
        }

        if (-not $wasOn) {
            Write-Progress2 'Starting tethering…'
            $result = Await-Operation $manager.StartTetheringAsync() ([Windows.Networking.NetworkOperators.NetworkOperatorTetheringOperationResult])
            if ($result.Status -ne 'Success') {
                Write-Result @{ ok = $false; error = "tethering-failed:$($result.Status)"
                    message = "StartTetheringAsync returned $($result.Status)." }
            }
        }

        Write-Progress2 'Tethering is on. Disabling IP forwarding on the hotspot interface…'
        # ICS re-enables forwarding on every tethering start, so this must run fresh each
        # session — the scheduled task exists precisely to do that without a UAC prompt per
        # party (docs/ZeroSetupPlan.md Phase 4.2 / 5). A missing/failed task means the hotspot
        # is up but NOT isolated: HotspotService treats a non-null warning here as a failure,
        # not a soft caveat, and stops tethering rather than leave that state running.
        $warning = $null
        try {
            Start-ScheduledTask -TaskName $ForwardingTaskName -ErrorAction Stop
        } catch {
            $warning = "forwarding-not-disabled: $($_.Exception.Message)"
            Write-Progress2 "  [warn] $warning"
        }

        Write-Result @{ ok = $true; ssid = $Ssid; passphrase = $Passphrase; warning = $warning }
    } catch {
        Write-Result @{ ok = $false; error = 'exception'; message = $_.Exception.Message }
    }
}
else {
    try {
        Write-Progress2 'Stopping tethering…'
        $manager = Get-TetheringManager
        if ($null -eq $manager) {
            # No uplink profile to resolve a manager from — nothing left to stop. Not an
            # error: this runs on the way out, and the goal is a clean exit, not a fight.
            Write-Result @{ ok = $true; stopped = $false }
        }

        if ($manager.TetheringOperationalState -eq 'On') {
            Await-Operation $manager.StopTetheringAsync() ([Windows.Networking.NetworkOperators.NetworkOperatorTetheringOperationResult]) | Out-Null
        }

        Write-Result @{ ok = $true; stopped = $true }
    } catch {
        Write-Result @{ ok = $false; error = 'exception'; message = $_.Exception.Message }
    }
}

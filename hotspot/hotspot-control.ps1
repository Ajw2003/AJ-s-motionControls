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

# Tethering needs *a* connection profile to hang off. It does not need that profile to carry
# internet, or even to be connected - measured 2026-08-21, see docs/Decision-OfflineHosting.md.
#
# This used to ask only GetInternetConnectionProfile(), which returns null whenever there is no
# internet, and reported that as no-connection-profile. That single call is the entire reason
# Hotspot mode appeared not to work offline, and why ZeroSetupPlan.md recorded Open Question 2
# as "answered - no". Confirmed with the Ethernet cable out and Wi-Fi joined to nothing:
# tethering starts, takes 192.168.137.1, and serves DHCP and DNS.
$script:TetheringProfileName = $null

function Get-TetheringManager {
    $ni = [Windows.Networking.Connectivity.NetworkInformation,Windows.Networking,ContentType=WindowsRuntime]
    $mgrType = [Windows.Networking.NetworkOperators.NetworkOperatorTetheringManager,Windows.Networking,ContentType=WindowsRuntime]

    # Preferred: the internet-bearing profile. Online behaviour is then byte-for-byte what it
    # was before this change, which matters because this path is shared with every LAN-mode
    # session that has been tested to date.
    $profile = $ni::GetInternetConnectionProfile()
    if ($null -ne $profile) {
        try {
            $manager = $mgrType::CreateFromConnectionProfile($profile)
            $script:TetheringProfileName = $profile.ProfileName
            return $manager
        } catch {
            Write-Progress2 "  [warn] internet profile '$($profile.ProfileName)' rejected tethering: $($_.Exception.Message)"
        }
    }

    # Offline: any profile that will host tethering. Remembered Wi-Fi networks and an idle
    # Bluetooth PAN adapter all qualify, and all keep enumerating while disconnected.
    foreach ($p in $ni::GetConnectionProfiles()) {
        try {
            $manager = $mgrType::CreateFromConnectionProfile($p)
            $script:TetheringProfileName = $p.ProfileName
            Write-Progress2 "  no internet profile; tethering from '$($p.ProfileName)' instead"
            return $manager
        } catch { }
    }

    return $null
}

if ($Action -eq 'Start') {
    try {
        Write-Progress2 'Resolving the tethering manager…'
        $manager = Get-TetheringManager
        if ($null -eq $manager) {
            # Still the right result, but it now means something much rarer than it used to:
            # not "no internet" (which is fine and supported), but "no network profile of any
            # kind will host tethering" — a machine with no Wi-Fi adapter, or a radio switched
            # off. The message says so, because the old one sent people looking for an uplink
            # that is not required.
            Write-Result @{ ok = $false; error = 'no-connection-profile'
                message = 'No connection profile can host tethering. Internet is not required, but a Wi-Fi adapter with its radio on is — check that Wi-Fi is not switched off.' }
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

        Write-Progress2 'Tethering is on. Disabling IP forwarding on the hotspot interface...'
        # ICS re-enables forwarding on every tethering start, so this must run fresh each
        # session - the scheduled task exists precisely to do that without a UAC prompt per
        # party (docs/ZeroSetupPlan.md Phase 4.2 / 5). A missing/failed task means the hotspot
        # is up but NOT isolated: HotspotService treats a non-null warning here as a failure,
        # not a soft caveat, and stops tethering rather than leave that state running.
        #
        # VERIFY THE FACT, NOT THE LAUNCH. This used to report success when Start-ScheduledTask
        # returned, which proves only that a task was started - not that forwarding changed, and
        # not that it stayed changed. It reported isolation on a machine that measurably had
        # none, for as long as the task existed, which is the worst way for this to fail. The
        # state is read back below instead, and it needs no elevation to read.
        #
        # Polled rather than read once, because this is a race: the task runs asynchronously and
        # ICS may re-assert forwarding after it. If the flag will not stay down within the
        # window, that is the honest answer and the caller should act on it.
        $warning = $null
        try {
            Start-ScheduledTask -TaskName $ForwardingTaskName -ErrorAction Stop
        } catch {
            $warning = "forwarding-task-failed: $($_.Exception.Message)"
            Write-Progress2 "  [warn] $warning"
        }

        if (-not $warning) {
            $deadline = (Get-Date).AddSeconds(10)
            $stillOn = @()

            do {
                Start-Sleep -Milliseconds 500
                $stillOn = @(
                    Get-NetIPAddress -AddressFamily IPv4 -IPAddress 192.168.137.* -ErrorAction SilentlyContinue |
                        ForEach-Object {
                            Get-NetIPInterface -InterfaceIndex $_.InterfaceIndex -AddressFamily IPv4 -ErrorAction SilentlyContinue
                        } |
                        Where-Object { $_.Forwarding -eq 'Enabled' }
                )
            } while ($stillOn.Count -gt 0 -and (Get-Date) -lt $deadline)

            if ($stillOn.Count -gt 0) {
                $names = ($stillOn | ForEach-Object { $_.InterfaceAlias }) -join ', '
                $warning = "forwarding-still-enabled: the disable task ran but forwarding is " +
                           "still Enabled on $names after 10s. Guests are NOT isolated."
                Write-Progress2 "  [warn] $warning"
            } else {
                Write-Progress2 '  forwarding confirmed Disabled.'
            }
        }

        # profile is additive and purely diagnostic — it answers "did we tether from the
        # internet connection or fall back?" in the one line HotspotService already logs,
        # which is the difference between an online and an offline session.
        Write-Result @{ ok = $true; ssid = $Ssid; passphrase = $Passphrase; warning = $warning
            profile = $script:TetheringProfileName }
    } catch {
        Write-Result @{ ok = $false; error = 'exception'; message = $_.Exception.Message }
    }
}
else {
    try {
        Write-Progress2 'Stopping tethering…'
        $manager = Get-TetheringManager
        if ($null -eq $manager) {
            # No profile at all to resolve a manager from — nothing left to stop. Not an
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

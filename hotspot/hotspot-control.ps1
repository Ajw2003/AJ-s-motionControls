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

.PARAMETER ShareInternet
    -Action Start only. Leave ICS forwarding on so guests get this PC's internet (#72), and
    report whether it is on (`shared`) instead of isolating guests. Not sharing is never fatal.

.EXAMPLE
    hotspot-control.ps1 -Action Start -Ssid "Rock Skipping" -Passphrase "skip1234"
.EXAMPLE
    hotspot-control.ps1 -Action Start -Ssid "Rock Skipping" -Passphrase "skip1234" -ShareInternet
.EXAMPLE
    hotspot-control.ps1 -Action Stop
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)] [ValidateSet('Start', 'Stop')] [string] $Action,
    [string] $Ssid,
    [string] $Passphrase,
    [switch] $ShareInternet
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

function Get-StationBandName {
    # Which TetheringWiFiBand the access point should ask for, given what this radio is
    # already doing. Returns a band name, plus the human-readable station band for logging.
    #
    # WHY THIS EXISTS. Adapters in this class report "Soft AP : Not supported", so Windows
    # runs Mobile Hotspot as a Wi-Fi Direct GO on the SAME PHYSICAL RADIO as the station
    # link. Ask for a band the station is not on and the radio has to time-slice between
    # two channels: packets are queued rather than dropped, and the 20Hz AIM stream reaches
    # the game in bursts. That is felt directly as a jerky, delayed aim preview.
    #
    # Measured 2026-09-09, host on 5 GHz ch44, AP pinned to 2.4 GHz, 60 pings to a joined
    # phone: mean 156.9ms, stdev 194.3ms, max 1161ms, 2% loss. Disconnecting the station
    # link and changing nothing else: mean 26.6ms, stdev 52.9ms, max 289ms, 0% loss.
    # See docs/plans/aim-preview-lag-on-shared-radio.md.
    #
    # Matching the band is what lets the GO share the station's channel instead of
    # alternating with it. It is a request, not a guarantee - the driver places the GO.
    $text = (netsh wlan show interfaces | Out-String)

    if ($text -notmatch '(?m)^\s*State\s*:\s*connected') {
        # No station link: the radio is ours alone, so nothing to match and 2.4 GHz is the
        # better choice on its own merits. This is the shipping living-room case.
        return @{ Band = 'TwoPointFourGigahertz'; Station = $null; Matched = $true }
    }

    $stationBand = $null
    if ($text -match '(?m)^\s*Band\s*:\s*(.+?)\s*$') { $stationBand = $Matches[1] }

    if ($stationBand -like '5*') {
        return @{ Band = 'FiveGigahertz'; Station = $stationBand; Matched = $true }
    }
    if ($stationBand -like '2.4*') {
        return @{ Band = 'TwoPointFourGigahertz'; Station = $stationBand; Matched = $true }
    }

    # 6 GHz, or a band string this does not recognise. A GO cannot follow the host onto
    # 6 GHz in a way most phones can join, so take the compatible band and report that the
    # split is unavoidable - HotspotService turns Matched=false into an on-screen warning
    # rather than pretending the link is fine.
    return @{ Band = 'TwoPointFourGigahertz'; Station = $stationBand; Matched = $false }
}

# wlanapi channel lookup. netsh cannot see the hotspot's virtual adapter, so this is the only
# unelevated way to learn which channel the GO landed on. C# 5 syntax: Windows PowerShell 5.1's
# Add-Type compiler. Why the channel matters: docs/plans/aim-preview-lag-on-shared-radio.md.
$script:WlanSource = @'
using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;

public static class RockSkippingWlan
{
    [DllImport("wlanapi.dll")] static extern uint WlanOpenHandle(uint clientVersion, IntPtr reserved, out uint negotiated, out IntPtr handle);
    [DllImport("wlanapi.dll")] static extern uint WlanCloseHandle(IntPtr handle, IntPtr reserved);
    [DllImport("wlanapi.dll")] static extern uint WlanEnumInterfaces(IntPtr handle, IntPtr reserved, out IntPtr list);
    [DllImport("wlanapi.dll")] static extern uint WlanQueryInterface(IntPtr handle, ref Guid iface, int opcode, IntPtr reserved, out uint size, out IntPtr data, IntPtr valueType);
    [DllImport("wlanapi.dll")] static extern void WlanFreeMemory(IntPtr p);

    const int ChannelNumberOpcode = 8;   // wlan_intf_opcode_channel_number
    const int InterfaceInfoSize = 532;   // GUID(16) + WCHAR[256](512) + state(4)

    public static Guid[] Interfaces()
    {
        uint negotiated; IntPtr handle;
        if (WlanOpenHandle(2, IntPtr.Zero, out negotiated, out handle) != 0) return new Guid[0];
        var found = new List<Guid>();
        IntPtr list;
        if (WlanEnumInterfaces(handle, IntPtr.Zero, out list) == 0)
        {
            int count = Marshal.ReadInt32(list, 0);
            for (int i = 0; i < count; i++)
                found.Add((Guid)Marshal.PtrToStructure(IntPtr.Add(list, 8 + i * InterfaceInfoSize), typeof(Guid)));
            WlanFreeMemory(list);
        }
        WlanCloseHandle(handle, IntPtr.Zero);
        return found.ToArray();
    }

    // The channel, or 0 when the interface is not connected or not readable.
    public static int Channel(Guid iface)
    {
        uint negotiated; IntPtr handle;
        if (WlanOpenHandle(2, IntPtr.Zero, out negotiated, out handle) != 0) return 0;
        int channel = 0;
        uint size; IntPtr data;
        if (WlanQueryInterface(handle, ref iface, ChannelNumberOpcode, IntPtr.Zero, out size, out data, IntPtr.Zero) == 0)
        {
            channel = Marshal.ReadInt32(data);
            WlanFreeMemory(data);
        }
        WlanCloseHandle(handle, IntPtr.Zero);
        return channel;
    }
}
'@

function Write-ChannelReport {
    # Diagnostic only: logs which channel the hotspot and the station link ended up on, so a
    # split radio shows in the log instead of being inferred from a laggy aim preview. Never
    # throws and never affects the result - a failure here is one more log line.
    param([hashtable] $BandChoice)
    try {
        if (-not ('RockSkippingWlan' -as [type])) { Add-Type -TypeDefinition $script:WlanSource }

        $direct = @(Get-NetAdapter -IncludeHidden -ErrorAction Stop |
            Where-Object { $_.InterfaceDescription -like 'Microsoft Wi-Fi Direct Virtual Adapter*' } |
            ForEach-Object { [Guid]$_.InterfaceGuid })

        # The GO can take a moment after StartTetheringAsync to report a channel.
        $hotspotChannel = 0
        for ($attempt = 0; $attempt -lt 5 -and $hotspotChannel -le 0 -and $direct.Count -gt 0; $attempt++) {
            if ($attempt -gt 0) { Start-Sleep -Seconds 1 }
            foreach ($guid in $direct) {
                $channel = [RockSkippingWlan]::Channel($guid)
                if ($channel -gt 0) { $hotspotChannel = $channel }
            }
        }

        $stationChannel = 0
        foreach ($guid in [RockSkippingWlan]::Interfaces()) {
            if ($direct -contains $guid) { continue }
            $channel = [RockSkippingWlan]::Channel($guid)
            if ($channel -gt 0) { $stationChannel = $channel }
        }

        if ($hotspotChannel -le 0) {
            Write-Progress2 '  [warn] could not read the hotspot channel, so a split radio cannot be ruled out'
        } elseif ($stationChannel -le 0) {
            Write-Progress2 ("  hotspot is on channel {0}; no station link, so the radio is not shared" -f $hotspotChannel)
        } elseif ($hotspotChannel -eq $stationChannel) {
            Write-Progress2 ("  hotspot is on channel {0}; station link is on channel {1} (same channel)" -f $hotspotChannel, $stationChannel)
        } else {
            # 52-144 are the 5 GHz DFS channels.
            $dfs = ''
            if ($BandChoice.Station -like '5*' -and $stationChannel -ge 52 -and $stationChannel -le 144) { $dfs = ' (DFS)' }
            Write-Progress2 ("  [warn] hotspot is on channel {0} but station link is on channel {1}{2}: the radio will time-slice between them and the aim preview will lag" -f $hotspotChannel, $stationChannel, $dfs)
        }
    } catch {
        Write-Progress2 ("  [warn] could not read channels: {0}" -f $_.Exception.Message)
    }
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
        # 2.4 GHz is the default on compatibility grounds - some phones cannot see 5/6 GHz
        # APs, and 2.4 GHz carries further across a room - but it is NOT the default when
        # this radio is already holding a 5 GHz station link, because then it costs a
        # channel switch on every slot and the aim preview is what pays. See
        # Get-StationBandName.
        $bandChoice = Get-StationBandName
        if ($bandChoice.Station) {
            Write-Progress2 ("  station link is on {0}; asking for {1}" -f $bandChoice.Station, $bandChoice.Band)
        } else {
            Write-Progress2 ("  no station link; asking for {0}" -f $bandChoice.Band)
        }

        try {
            # Best-effort - not every adapter exposes Band.
            #
            # The full [Type,Assembly,ContentType=WindowsRuntime] literal, as in
            # Get-TetheringManager. The bare short form happens to resolve here only because
            # the literals in that function project the whole Windows.Networking assembly
            # first, and depending on that is a trap for whoever edits either piece next.
            # [enum]::Parse rather than a cast: casting a string to a projected WinRT enum
            # is not reliable across hosts.
            $bandType = [Windows.Networking.NetworkOperators.TetheringWiFiBand,Windows.Networking,ContentType=WindowsRuntime]
            $cfg.Band = [enum]::Parse($bandType, $bandChoice.Band)
        } catch {
            # Reported, not swallowed. A bare `catch { }` here is why nobody could tell
            # whether the band was ever being applied at all.
            Write-Progress2 ("  [warn] this adapter would not accept Band={0}; leaving it to Windows ({1})" -f $bandChoice.Band, $_.Exception.Message)
        }

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

        Write-ChannelReport -BandChoice $bandChoice

        $warning = $null
        $shared = $false
        if ($ShareInternet) {
            # ICS turns forwarding on by itself; sharing only means not turning it off. Measured
            # 2026-10-02: with the task skipped it read Enabled for the whole 12 s sampled. Read
            # back anyway, polled because the hotspot address can lag the tethering start.
            Write-Progress2 'Tethering is on. Sharing internet: leaving IP forwarding on...'
            $deadline = (Get-Date).AddSeconds(6)
            do {
                $nics = @(
                    Get-NetIPAddress -AddressFamily IPv4 -IPAddress 192.168.137.* -ErrorAction SilentlyContinue |
                        ForEach-Object {
                            Get-NetIPInterface -InterfaceIndex $_.InterfaceIndex -AddressFamily IPv4 -ErrorAction SilentlyContinue
                        }
                )
                $shared = $nics.Count -gt 0 -and @($nics | Where-Object { $_.Forwarding -ne 'Enabled' }).Count -eq 0
                if (-not $shared) { Start-Sleep -Milliseconds 250 }
            } while (-not $shared -and (Get-Date) -lt $deadline)
            Write-Progress2 $(if ($shared) { '  forwarding Enabled: guests share this PC''s internet.' }
                             else { '  [warn] forwarding is not on: guests will have no internet.' })
        }
        else {
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
            try {
                Start-ScheduledTask -TaskName $ForwardingTaskName -ErrorAction Stop
            } catch {
                $warning = "forwarding-task-failed: $($_.Exception.Message)"
                Write-Progress2 "  [warn] $warning"
            }

            # STABLE, NOT MOMENTARY (#70). Traced with tools/hotspot/trace-forwarding.ps1: ICS turns
            # forwarding back on ~2 s after the task disables it, so the first Disabled reading
            # proved nothing and a "confirmed" start left guests unisolated. The task is re-run
            # whenever forwarding reads Enabled, and isolation is only reported once it has stayed
            # Disabled for $StableSeconds in a row. The ceiling sits inside HotspotService's 30 s
            # startTimeout; running out of it is the same fail-closed warning as before.
            if (-not $warning) {
                $StableSeconds = 6
                $deadline = (Get-Date).AddSeconds(20)
                $stableSince = $null
                $nextRetrigger = (Get-Date).AddSeconds(1.5)
                $stillOn = @()

                do {
                    Start-Sleep -Milliseconds 250
                    $hotspotNics = @(
                        Get-NetIPAddress -AddressFamily IPv4 -IPAddress 192.168.137.* -ErrorAction SilentlyContinue |
                            ForEach-Object {
                                Get-NetIPInterface -InterfaceIndex $_.InterfaceIndex -AddressFamily IPv4 -ErrorAction SilentlyContinue
                            }
                    )
                    $stillOn = @($hotspotNics | Where-Object { $_.Forwarding -eq 'Enabled' })

                    # No address yet counts as unconfirmed, not as Disabled.
                    if ($hotspotNics.Count -eq 0 -or $stillOn.Count -gt 0) {
                        $stableSince = $null
                        if ($stillOn.Count -gt 0 -and (Get-Date) -ge $nextRetrigger) {
                            Start-ScheduledTask -TaskName $ForwardingTaskName -ErrorAction SilentlyContinue
                            $nextRetrigger = (Get-Date).AddSeconds(1.5)
                        }
                    } elseif ($null -eq $stableSince) {
                        $stableSince = Get-Date
                    }

                    $stable = $null -ne $stableSince -and ((Get-Date) - $stableSince).TotalSeconds -ge $StableSeconds
                } while (-not $stable -and (Get-Date) -lt $deadline)

                if (-not $stable) {
                    $names = if ($stillOn.Count -gt 0) { ($stillOn | ForEach-Object { $_.InterfaceAlias }) -join ', ' } else { 'the hotspot interface' }
                    $warning = "forwarding-still-enabled: forwarding did not stay Disabled on $names " +
                               "for ${StableSeconds}s within 20s. Guests are NOT isolated."
                    Write-Progress2 "  [warn] $warning"
                } else {
                    Write-Progress2 "  forwarding confirmed Disabled (held for ${StableSeconds}s)."
                }
            }
        }

        # profile is additive and purely diagnostic — it answers "did we tether from the
        # internet connection or fall back?" in the one line HotspotService already logs,
        # which is the difference between an online and an offline session.
        Write-Result @{ ok = $true; ssid = $Ssid; passphrase = $Passphrase; warning = $warning
            shared = $shared
            profile = $script:TetheringProfileName
            band = $bandChoice.Band; stationBand = $bandChoice.Station
            bandMatched = $bandChoice.Matched }
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

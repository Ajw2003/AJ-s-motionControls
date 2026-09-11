<#
.SYNOPSIS
    Measures the phone-to-PC wireless link while the game is hosting, and says whether the
    Wi-Fi radio is being split between the game's hotspot and an infrastructure link.

.DESCRIPTION
    The aim preview is fed by a 20Hz AIM stream over one wireless hop. When this PC is
    associated to a Wi-Fi network AND serving the hotspot, a single radio has to time-slice
    between two channels, the stream arrives in bursts, and the preview reads as jerky and
    delayed. That is an RF problem, not a Unity one, and this script is how you tell them
    apart without touching the game.

    Run it twice with the game in Play and a phone joined: once associated to Wi-Fi, once
    with Wi-Fi disconnected. Compare the stdev. See
    docs/plans/aim-preview-lag-on-shared-radio.md.

    Read-only. Pings and reads adapter state; changes nothing.

.PARAMETER PhoneIp
    Skip auto-discovery and measure this address.

.PARAMETER Count
    Pings to send. Default 100 - about 100 seconds, long enough for the switching pattern
    to show up in the stdev rather than hiding in a short sample.

.EXAMPLE
    powershell -File tools/measure-hotspot-jitter.ps1
#>
[CmdletBinding()]
param(
    [string] $PhoneIp,
    [int]    $Count = 100
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Continue'

$HotspotSubnet = '192.168.137.'
$HotspotHost   = '192.168.137.1'
$HotspotBroadcast = '192.168.137.255'

function Write-Heading([string] $Text) {
    Write-Host ''
    Write-Host $Text -ForegroundColor Cyan
    Write-Host ('-' * $Text.Length) -ForegroundColor Cyan
}

# --- 1. Station link -------------------------------------------------------------------
# Parsed out of netsh rather than Get-NetAdapter because only netsh reports band and
# channel, and the band is the whole point of the measurement.
Write-Heading 'Station link (this PC to an access point)'

$netshText = (netsh wlan show interfaces | Out-String)
$staBand    = $null
$staChannel = $null
$staSsid    = $null

if ($netshText -match '(?m)^\s*SSID\s*:\s*(.+?)\s*$')    { $staSsid    = $Matches[1] }
if ($netshText -match '(?m)^\s*Band\s*:\s*(.+?)\s*$')    { $staBand    = $Matches[1] }
if ($netshText -match '(?m)^\s*Channel\s*:\s*(.+?)\s*$') { $staChannel = $Matches[1] }

if ($staSsid) {
    Write-Host ("  Associated to '{0}' on {1}, channel {2}" -f $staSsid, $staBand, $staChannel)
} else {
    Write-Host '  Not associated to any network - the radio is free for the hotspot.' -ForegroundColor Green
}

# --- 2. The hotspot side ---------------------------------------------------------------
# Windows Mobile Hotspot on an adapter without SoftAP support runs as a Wi-Fi Direct GO on
# 'Local Area Connection* N'. Its presence with an address on the hotspot subnet is the
# proof the hotspot is actually up, independent of what the game thinks.
Write-Heading 'Hotspot interface'

$hotspotIf = Get-NetIPAddress -AddressFamily IPv4 -ErrorAction SilentlyContinue |
             Where-Object { $_.IPAddress -eq $HotspotHost }

if (-not $hotspotIf) {
    Write-Host "  No interface holds $HotspotHost - the hotspot is not up." -ForegroundColor Yellow
    Write-Host '  Press Play in Unity and let the hotspot start, then run this again.'
    exit 1
}
Write-Host ("  {0} holds {1}" -f $hotspotIf.InterfaceAlias, $HotspotHost)

$softAp = (netsh wlan show wirelesscapabilities | Out-String)
if ($softAp -match '(?m)^\s*Soft AP\s*:\s*Not supported') {
    Write-Host '  Adapter reports "Soft AP : Not supported" - the hotspot is a Wi-Fi Direct GO' -ForegroundColor Yellow
    Write-Host '  on the same physical radio as the station link above.'
}

# --- 3. Find the phone -----------------------------------------------------------------
Write-Heading 'Phone'

if (-not $PhoneIp) {
    # The neighbour table on this subnet is mostly not hosts. It carries the subnet
    # broadcast (192.168.137.255, FF-FF-FF-FF-FF-FF) and a fistful of IPv4 multicast
    # groups (224.x / 239.x, 01-00-5E-*), all of them Permanent. An earlier version of
    # this script pinged the broadcast address and sat there for 100 seconds getting no
    # replies, so filter by the MAC rather than trusting the address to be a host.
    $candidates = Get-NetNeighbor -AddressFamily IPv4 -ErrorAction SilentlyContinue |
        Where-Object {
            $_.IPAddress -like "$HotspotSubnet*" -and
            $_.IPAddress -ne $HotspotHost -and
            $_.IPAddress -ne $HotspotBroadcast -and
            $_.LinkLayerAddress -and
            $_.LinkLayerAddress -ne 'FF-FF-FF-FF-FF-FF' -and
            $_.LinkLayerAddress -notlike '01-00-5E-*'
        }

    # Reachable/Stale first: a Permanent entry that survives here is usually a leftover
    # from a previous session, and pinging a phone that has gone home measures nothing.
    $live = @($candidates | Where-Object { $_.State -in @('Reachable', 'Stale', 'Delay', 'Probe') })
    $any  = @($candidates)

    if ($live.Count -gt 0) {
        $PhoneIp = $live[0].IPAddress
    } elseif ($any.Count -gt 0) {
        $PhoneIp = $any[0].IPAddress
        Write-Host ("  Only a cached entry for {0} - it may be from an earlier session." -f $PhoneIp) -ForegroundColor Yellow
    } else {
        Write-Host "  No phone found on $HotspotSubnet*." -ForegroundColor Yellow
        Write-Host '  Join a phone to the hotspot AND open the controller page - the page is what'
        Write-Host '  puts the phone in this PC neighbour table. Then run this again.'
        Write-Host '  Or pass one explicitly:  -PhoneIp 192.168.137.x'
        exit 1
    }
}
Write-Host ("  Measuring against {0}" -f $PhoneIp)

# --- 4. Measure ------------------------------------------------------------------------
Write-Heading ("Pinging {0} x {1} - roughly {1} seconds" -f $PhoneIp, $Count)

# A phone whose screen is off parks its Wi-Fi radio and only wakes on beacons, which
# produces min ~1ms / max ~500ms / 0% loss - almost exactly what a time-sliced radio
# produces. The two are not distinguishable from one ping run, so remove the ambiguity
# by keeping the phone busy instead of trying to detect it afterwards.
Write-Host '  KEEP AIMING with the phone for the whole run - screen on, controller page' -ForegroundColor Yellow
Write-Host '  open, moving. An idle phone parks its radio and fakes this exact result.' -ForegroundColor Yellow
Write-Host ''

$raw     = ping -n $Count $PhoneIp
$samples = @($raw |
    Select-String -Pattern 'time[=<](\d+)\s*ms' |
    ForEach-Object { [int]$_.Matches[0].Groups[1].Value })

if ($samples.Count -eq 0) {
    Write-Host '  No replies at all. The phone is not reachable at that address.' -ForegroundColor Red
    exit 1
}

$stats = $samples | Measure-Object -Minimum -Maximum -Average
$mean  = $stats.Average
$stdev = [math]::Sqrt( (($samples | ForEach-Object { [math]::Pow($_ - $mean, 2) }) |
                        Measure-Object -Average).Average )
$lost  = $Count - $samples.Count

Write-Host ''
Write-Host ("  replies : {0} of {1}   ({2} lost, {3:N0}%)" -f $samples.Count, $Count, $lost, (100.0 * $lost / $Count))
Write-Host ("  min     : {0} ms" -f $stats.Minimum)
Write-Host ("  max     : {0} ms" -f $stats.Maximum)
Write-Host ("  mean    : {0:N1} ms" -f $mean)
Write-Host ("  stdev   : {0:N1} ms" -f $stdev) -ForegroundColor White

# --- 5. Verdict ------------------------------------------------------------------------
# The threshold is judgement, not physics: an uncontended link on this hardware measured
# stdev 1.0 ms, and channel time-slicing puts it into the tens. 8 ms sits clear of both.
Write-Heading 'Verdict'

if ($stdev -ge 8) {
    Write-Host '  JITTERY. This link will make the aim preview jerk.' -ForegroundColor Red
    if ($staSsid) {
        Write-Host ("  The radio is also holding '{0}' ({1}), which would explain it -" -f $staSsid, $staBand)
        Write-Host '  but so would an idle phone parking its radio. This run alone cannot tell'
        Write-Host '  them apart. Disconnect from Wi-Fi, rejoin the SAME phone, and run this'
        Write-Host '  again: if it goes clean, the split radio is the cause; if it stays'
        Write-Host '  jittery, the phone was asleep and this measured nothing.'
    } else {
        Write-Host '  No station link, so the split radio is NOT the cause here. Look at'
        Write-Host '  interference, distance, or the number of clients on the hotspot.'
    }
} else {
    Write-Host '  CLEAN. Jitter is low enough that the link is not what the preview is feeling.' -ForegroundColor Green
    if ($staSsid) {
        Write-Host ("  Note this was measured while still associated to '{0}'." -f $staSsid)
    }
}
Write-Host ''

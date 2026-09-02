#requires -Version 5.1
<#
.SYNOPSIS
    Can Wi-Fi Direct host the game's access point with the PC attached to NO network at all?

.DESCRIPTION
    docs/ZeroSetupPlan.md Open Question 2 is answered and the answer is no: Mobile Hotspot
    (NetworkOperatorTetheringManager) is built to *share* a connection, so with Wi-Fi and
    Ethernet both down GetInternetConnectionProfile() returns null and there is nothing to
    tether from. The host must stay attached to something.

    Wi-Fi Direct is a different mechanism with no such input. WiFiDirectAdvertisementPublisher
    with IsAutonomousGroupOwnerEnabled makes the radio a group owner outright, and
    LegacySettings puts an ordinary WPA2 SSID on top of it so a phone - which knows nothing
    about Wi-Fi Direct - joins it like any other network. Nothing in that API names a
    connection to share. This script finds out whether that theory survives contact with the
    driver.

    Four things have to be true for this to replace Hotspot mode. The script walks them as
    numbered steps and reports each separately rather than as one pass/fail, because the
    interesting failures are partial:

      STEP 3  The group owner STARTS with no uplink.       (Status -> Started, not Aborted)
      STEP 4  The interface gets 192.168.137.1, not APIPA. (the address the cert is pinned to)
      STEP 5  Something serves DHCP with nothing to share. (ICS may need an uplink to bother)
      STEP 6  A real phone joins, leases, and loads a page. (-KeepRunning)

    STEP 4 is the one that decides how much of the existing design survives. LocalServices
    forces the certificate and relay to 192.168.137.1 in Hotspot mode; if Wi-Fi Direct lands
    somewhere else, or nowhere, that pinning and the cached 192-168-137-1.local-ip.sh
    certificate have to change with it.

    Unelevated by design - WiFiDirectAdvertisementPublisher needs no rights. If a step here
    ever needs Administrator, that is a finding, not something to paper over with a prompt.
    The one exception is the inbound firewall rule STEP 6 needs, which the script detects and
    prints for you to run rather than running itself.

    Everything printed is also written to a transcript (see -LogPath) so a run made while the
    machine is offline can be read back afterwards.

.PARAMETER WaitForOffline
    Pause at STEP 2 until the machine has no connected adapter, showing a live status line, then
    continue. This is how the real test is staged: start the script, unplug the Ethernet cable,
    watch it proceed. Unplugging needs no admin, which is why the test is shaped this way rather
    than around Disable-NetAdapter.

.PARAMETER KeepRunning
    Hold the group owner up at STEP 6, polling for clients, so a phone can join for real. The
    advertisement lives and dies with this process - there is no persistent state to clean up,
    and no orphaned hotspot if it is killed.

.PARAMETER Serve
    With -KeepRunning, also start deploy/spike/confirm-server.js on port 80 so the phone has
    something unambiguous to load.

.EXAMPLE
    ./wifidirect-spike.ps1
    Stage A - does it work at all, with the network still plugged in.

.EXAMPLE
    ./wifidirect-spike.ps1 -WaitForOffline
    Stage B - the real question. Unplug the cable when it asks.

.EXAMPLE
    ./wifidirect-spike.ps1 -WaitForOffline -KeepRunning -Serve
    Stage C - stage B, then hold it up for a phone to join.
#>
[CmdletBinding()]
param(
    # The Wi-Fi Direct spec requires a group owner's SSID to begin with "DIRECT-". Windows
    # accepts any string here without complaint and echoes it straight back from
    # LegacySettings.Ssid, so the readback is NOT evidence of what is in the beacon - and the
    # host cannot scan its own AP to check. Defaulting to a compliant name removes the whole
    # question. The first phone test used 'RockSkip Direct' and the network was not findable.
    [string] $Ssid = 'DIRECT-RockSkip',
    [string] $Passphrase = 'skipdirect',
    [switch] $WaitForOffline,
    [switch] $KeepRunning,
    [switch] $Serve,
    [int]    $StartTimeoutSeconds = 20,
    [int]    $WaitForOfflineTimeoutSeconds = 300,
    [int]    $Attempts = 3,
    [int]    $SettleSeconds = 8,
    [switch] $EnableRadio,
    [switch] $QrHidden,
    [switch] $Dns,
    [switch] $DnsAfter,
    # Forward names we are not authoritative for, instead of answering NXDOMAIN. This is not a
    # convenience: Android probes a known host to decide whether a network is usable, and a
    # network that NXDOMAINs everything fails that probe. An unvalidated network keeps mobile
    # data as the default route, and the phone then resolves through cellular - never asking
    # us, which is exactly what the 2026-08-21 run showed (zero queries logged).
    [string] $DnsUpstream,
    [string] $LogPath
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Runtime.WindowsRuntime

# The QR renderer draws with half-block characters, and node emits them as UTF-8. A console
# left on codepage 437 turns the symbol into mojibake that no phone can read. Setting
# OutputEncoding calls SetConsoleOutputCP, which applies to child processes sharing this
# console too - which is what actually matters here, since node does the drawing.
try { [Console]::OutputEncoding = [System.Text.Encoding]::UTF8 } catch { }

$SpikeDir = Split-Path -Parent $MyInvocation.MyCommand.Path
if (-not $LogPath) { $LogPath = Join-Path $SpikeDir 'wifidirect-run.log' }

function Write-Step { param([string] $m) Write-Host "`n=== $m" -ForegroundColor Cyan }
function Write-Ok   { param([string] $m) Write-Host "   [ok]   $m" -ForegroundColor Green }
function Write-Bad  { param([string] $m) Write-Host "   [FAIL] $m" -ForegroundColor Red }
function Write-Warn2{ param([string] $m) Write-Host "   [warn] $m" -ForegroundColor Yellow }
function Write-Info { param([string] $m) Write-Host "   $m" -ForegroundColor Gray }
function Write-Say  { param([string] $m) Write-Host "   $m" -ForegroundColor White }

function Await-Operation {
    param($Operation, [Type] $ResultType)
    $asTask = ([System.WindowsRuntimeSystemExtensions].GetMethods() |
        Where-Object { $_.Name -eq 'AsTask' -and $_.GetParameters().Count -eq 1 -and
                       $_.GetParameters()[0].ParameterType.Name -eq 'IAsyncOperation`1' })[0]
    $task = $asTask.MakeGenericMethod($ResultType).Invoke($null, @($Operation))
    $task.Wait(-1) | Out-Null
    $task.Result
}

# The radio's software state is the difference between "Wi-Fi Direct cannot work offline" and
# "Wi-Fi was switched off" - two findings that look identical from the outside, because a
# software-off radio aborts the advertisement with RadioNotAvailable no matter what the network
# looks like. The first offline run of this spike was confounded exactly this way, so the check
# is now a precondition rather than a diagnostic.
function Get-WiFiRadio {
    $radioType  = [Windows.Devices.Radios.Radio,Windows.Devices,ContentType=WindowsRuntime]
    $accessType = [Windows.Devices.Radios.RadioAccessStatus,Windows.Devices,ContentType=WindowsRuntime]
    $access = Await-Operation $radioType::RequestAccessAsync() $accessType
    if ("$access" -ne 'Allowed') { return [pscustomobject]@{ Radio = $null; Access = "$access" } }
    $listType = [System.Collections.Generic.IReadOnlyList[Windows.Devices.Radios.Radio]]
    $radios = Await-Operation $radioType::GetRadiosAsync() $listType
    $wifi = $radios | Where-Object { "$($_.Kind)" -eq 'WiFi' } | Select-Object -First 1
    [pscustomobject]@{ Radio = $wifi; Access = "$access" }
}

# Shared by -Dns (before the group owner) and -DnsAfter (once it is up). The ordering is the
# whole question, so the two paths must differ ONLY in when this is called - anything else
# would confound the comparison.
function Start-DnsResponder {
    param([string] $SpikeDirectory, [switch] $BindHotspotAddressOnly)

    $dnsJs = Join-Path $SpikeDirectory 'dns-responder.js'
    if (-not (Test-Path $dnsJs)) { Write-Bad "dns-responder.js not found at $dnsJs"; return $null }

    $held = @(Get-NetUDPEndpoint -LocalPort 53 -ErrorAction SilentlyContinue)
    if ($held.Count) {
        foreach ($h in $held) {
            $ho = Get-Process -Id $h.OwningProcess -ErrorAction SilentlyContinue
            Write-Warn2 (":53 already held on {0} by {1} (pid {2})" -f $h.LocalAddress, $(if ($ho) { $ho.ProcessName } else { '?' }), $h.OwningProcess)
        }
    } else {
        Write-Ok ':53 is free'
    }

    # Binding the hotspot address specifically is only possible once it exists, which is the
    # point of -DnsAfter: it leaves :53 on every other address to ICS instead of taking the
    # wildcard out from under it.
    $bind = if ($BindHotspotAddressOnly) { '192.168.137.1' } else { '0.0.0.0' }
    $dnsArgs = @($dnsJs, '--ip', '192.168.137.1', '--suffix', '.local-ip.sh',
                 '--bind', $bind, '--allow', '192.168.137.0/24')
    if ($DnsUpstream) {
        $dnsArgs += @('--upstream', $DnsUpstream)
        Write-Info "forwarding other names to $DnsUpstream (so the phone's probe can validate)"
    } else {
        Write-Info 'other names -> NXDOMAIN (no -DnsUpstream). If the phone resolves via cellular'
        Write-Info 'instead of asking us, this is the first thing to change.'
    }
    Write-Info "binding $bind`:53"

    $proc = Start-Process -FilePath 'node' -ArgumentList $dnsArgs -PassThru -NoNewWindow
    Start-Sleep -Seconds 2

    if ($proc.HasExited) {
        Write-Bad "the responder exited immediately (code $($proc.ExitCode)) - it could not have the port"
        return $null
    }
    $mine = @(Get-NetUDPEndpoint -LocalPort 53 -ErrorAction SilentlyContinue |
        Where-Object { $_.OwningProcess -eq $proc.Id })
    if ($mine.Count) { Write-Ok "responder holds :53 (pid $($proc.Id))" }
    else { Write-Warn2 "responder is running (pid $($proc.Id)) but does not appear to hold :53" }

    $rule = Get-NetFirewallRule -DisplayName 'RockSkipping spike DNS' -ErrorAction SilentlyContinue
    if (-not $rule) {
        Write-Warn2 'no inbound rule for UDP 53 - the phone will get ERR_NAME_NOT_RESOLVED.'
        Write-Info  'Run this ONCE in an ADMIN PowerShell (this script will not do it for you):'
        Write-Host  ''
        Write-Host  '     New-NetFirewallRule -DisplayName "RockSkipping spike DNS" -Direction Inbound -Protocol UDP -LocalPort 53 -RemoteAddress 192.168.137.0/24 -Action Allow -Profile Any' -ForegroundColor White
        Write-Host  ''
    } else {
        Write-Ok 'inbound firewall rule "RockSkipping spike DNS" is present'
    }
    return $proc
}

# "Did the phone ask US for DNS?" is the single most important fact in this whole test, and on
# 2026-08-21 it was only discoverable by reading dns-responder.log by hand afterwards. The run
# looked like a success right up to the phone. Counting queries here puts the answer in the
# script's own output, where the person running it can see it.
$script:DnsLogBaseline = 0
function Get-DnsLogPath { param([string] $SpikeDirectory) Join-Path $SpikeDirectory 'dns-responder.log' }

function Set-DnsQueryBaseline {
    param([string] $SpikeDirectory)
    $p = Get-DnsLogPath $SpikeDirectory
    $script:DnsLogBaseline = if (Test-Path $p) { @(Get-Content $p -ErrorAction SilentlyContinue).Count } else { 0 }
}

function Show-DnsQuerySummary {
    param([string] $SpikeDirectory)
    $p = Get-DnsLogPath $SpikeDirectory
    if (-not (Test-Path $p)) { Write-Warn2 'no dns-responder.log to read'; return 0 }

    $all = @(Get-Content $p -ErrorAction SilentlyContinue)
    $new = if ($all.Count -gt $script:DnsLogBaseline) { $all[$script:DnsLogBaseline..($all.Count - 1)] } else { @() }
    # Query lines carry an arrow; the banner lines the responder prints on startup do not.
    $queries = @($new | Where-Object { $_ -match '->' -and $_ -notmatch 'Answering' })

    if ($queries.Count -eq 0) {
        Write-Bad 'ZERO DNS queries reached this machine.'
        Write-Info 'The phone did not ask us at all - so nothing about the responder is at fault.'
        Write-Info 'It resolved somewhere else. Causes, in the order worth testing:'
        Write-Info '  1. THE BROWSER, not the OS. Chrome has its own DNS-over-HTTPS setting'
        Write-Info '     (chrome://settings/security > "Use secure DNS") which is SEPARATE from'
        Write-Info '     Android Private DNS and survives turning that off. With it on, Chrome'
        Write-Info '     never uses the system resolver and we see nothing. Turn it off, or test'
        Write-Info '     with a browser that has no DoH of its own.'
        Write-Info '  2. Mobile data. A network that fails Android connectivity validation keeps'
        Write-Info '     cellular as the default route for DNS. Turn mobile data OFF and retry.'
        Write-Info '  3. Private DNS (Settings > Network > Private DNS). Set it to Off - when it'
        Write-Info '     is set to a hostname, every lookup goes over TLS to that server and the'
        Write-Info '     local resolver is never consulted.'
        Write-Info '  4. DHCP is not advertising us as the resolver at all. See the interface'
        Write-Info '     report above - if ICS hands the phone an upstream resolver instead of'
        Write-Info '     192.168.137.1, the phone is asking the internet and getting NXDOMAIN.'
        Write-Info '  5. -DnsUpstream <ip> so we stop answering NXDOMAIN for everything else,'
        Write-Info '     which is what makes the network fail validation in (2).'
        Write-Info ''
        Write-Info 'To see where the queries ARE going, in an ADMIN shell while a phone is joined:'
        Write-Info '  pktmon filter remove; pktmon filter add -p 53; pktmon start --capture --pkt-size 128'
        Write-Info '  (reproduce on the phone, then) pktmon stop; pktmon format PktMon.etl -o dns.txt'
    } else {
        Write-Ok "$($queries.Count) DNS quer(ies) reached this machine - the phone DID ask us:"
        foreach ($q in ($queries | Select-Object -Last 8)) { Write-Info "  $($q.Trim())" }
    }
    return $queries.Count
}

function Set-WiFiRadioOn {
    param($Radio)
    $stateType  = [Windows.Devices.Radios.RadioState,Windows.Devices,ContentType=WindowsRuntime]
    $accessType = [Windows.Devices.Radios.RadioAccessStatus,Windows.Devices,ContentType=WindowsRuntime]
    Await-Operation $Radio.SetStateAsync($stateType::On) $accessType
}

function Get-WiFiDirectAdapters {
    # PowerShell unrolls an array returned from a function: zero elements arrive as $null and
    # one element as a bare object, and under Set-StrictMode .Count on either throws. Every
    # CALL SITE therefore wraps this in @(). Do not also return `,@(...)` from here - the two
    # idioms together produce a nested array, and $adapter.Name then fails instead.
    Get-NetAdapter -ErrorAction SilentlyContinue |
        Where-Object { $_.InterfaceDescription -match 'Wi-Fi Direct' -and $_.Status -eq 'Up' }
}

# This driver reports "P2P GO ports count: 1". A group owner left behind by a process that died
# without running its cleanup holds that single port, and every later start returns Aborted
# (ResourceInUse) with no hint as to why - which cost a full debugging cycle on 2026-08-20,
# chasing a DNS-ordering theory for a failure that had nothing to do with DNS.
#
# Cycling the radio tears the orphan down, and does so unelevated - no Disable-NetAdapter, no
# admin shell.
function Clear-StaleGroupOwner {
    param($Radio)
    $stateType  = [Windows.Devices.Radios.RadioState,Windows.Devices,ContentType=WindowsRuntime]
    $accessType = [Windows.Devices.Radios.RadioAccessStatus,Windows.Devices,ContentType=WindowsRuntime]

    Write-Info 'cycling the Wi-Fi radio to tear it down...'
    Await-Operation $Radio.SetStateAsync($stateType::Off) $accessType | Out-Null
    Start-Sleep -Seconds 4
    $mid = @(Get-WiFiDirectAdapters).Count
    Await-Operation $Radio.SetStateAsync($stateType::On) $accessType | Out-Null
    Start-Sleep -Seconds 5

    $left = @(Get-WiFiDirectAdapters).Count
    if ($left -eq 0) { Write-Ok "stale group owner cleared (gone after radio off: $($mid -eq 0))" }
    else { Write-Bad "$left Wi-Fi Direct adapter(s) still up after a radio cycle" }
    return ($left -eq 0)
}

# Write-Host bypasses the pipeline, so Tee-Object cannot capture this script. A transcript can.
try { Start-Transcript -Path $LogPath -Force | Out-Null; $transcribing = $true }
catch { $transcribing = $false; Write-Warn2 "could not start transcript at ${LogPath}: $($_.Exception.Message)" }

$publisher = $null
$sub       = $null
$confirm   = $null
$dnsProc   = $null
$wfd       = $null
$result    = [ordered]@{
    startedWithNoUplink = $null
    address             = $null
    is137               = $null
    dhcpListening       = $null
    clientsSeen         = @()
    status              = $null
    error               = $null
    uplinkAtStart       = $null
    radioAfter          = $null
    broadcastSsid       = $null
    broadcastPassphrase = $null
    attemptsUsed        = $null
    dnsQueries          = $null
}

Write-Host ''
Write-Host '  Wi-Fi Direct spike - true offline hosting' -ForegroundColor White
Write-Host '  ----------------------------------------' -ForegroundColor DarkGray
Write-Host "  transcript: $LogPath" -ForegroundColor DarkGray
if ($WaitForOffline) { Write-Host '  mode: STAGE B/C - will pause and ask you to unplug the network' -ForegroundColor Yellow }
else                 { Write-Host '  mode: STAGE A - runs straight through, network stays plugged in' -ForegroundColor DarkGray }

# ---------------------------------------------------------------- STEP 1

Write-Step 'STEP 1  What this machine can do, and what it is attached to'

$drv  = (netsh wlan show drivers) 2>&1 | Out-String
$caps = (netsh wlan show wirelesscapabilities) 2>&1 | Out-String
$both = $drv + $caps
foreach ($pair in @(
    @{ label = 'Hosted network (netsh, deprecated path)'; pattern = 'Hosted network supported\s*:\s*(\S+)' },
    @{ label = 'Soft AP';                                 pattern = 'Soft AP\s*:\s*(.+)' },
    @{ label = 'P2P GO on 5 GHz';                         pattern = 'P2P GO on 5 GHz\s*:\s*(.+)' },
    @{ label = 'P2P max clients';                         pattern = 'P2P Max Mobile AP Clients\s*:\s*(.+)' })) {
    $m = [regex]::Match($both, $pair.pattern)
    $val = if ($m.Success) { $m.Groups[1].Value.Trim() } else { '(not reported)' }
    Write-Info ("{0,-40} {1}" -f $pair.label, $val)
}

function Get-UplinkDescription {
    # Two independent views, because they disagree in exactly the case this spike cares about:
    # an adapter can be Up with a cable in it while WinRT still reports no *internet* profile.
    # The tethering API keys off the WinRT view; a "no network at all" test needs both empty.
    $connected = @(Get-NetAdapter -ErrorAction SilentlyContinue |
        Where-Object { $_.Status -eq 'Up' -and $_.InterfaceDescription -notmatch 'Wi-Fi Direct|Loopback|Bluetooth' })
    $prof = [Windows.Networking.Connectivity.NetworkInformation,Windows.Networking,ContentType=WindowsRuntime]::GetInternetConnectionProfile()
    [pscustomobject]@{
        Adapters        = $connected
        AdapterNames    = ($connected | ForEach-Object { $_.Name }) -join ', '
        InternetProfile = $(if ($prof) { $prof.ProfileName } else { $null })
    }
}

$uplink = Get-UplinkDescription
Write-Info ''
Write-Info ("connected adapters : {0}" -f $(if ($uplink.AdapterNames) { $uplink.AdapterNames } else { '(none)' }))
Write-Info ("internet profile   : {0}" -f $(if ($uplink.InternetProfile) { $uplink.InternetProfile } else { '(null - Mobile Hotspot would fail here)' }))

# ---- radio precondition. Checked here, before STEP 2 asks anyone to unplug a cable, because a
# ---- run that cannot possibly succeed should cost nothing.
$radioInfo = Get-WiFiRadio
$radioState = if ($radioInfo.Radio) { "$($radioInfo.Radio.State)" } else { "(no WiFi radio; access=$($radioInfo.Access))" }
Write-Info ("wi-fi radio        : {0}" -f $radioState)
$ifaceRadio = (netsh wlan show interfaces) 2>&1 | Out-String
$swOff = $ifaceRadio -match 'Software Off'
if ($swOff) { Write-Info '                     (netsh agrees: Software Off)' }

if ($radioState -ne 'On') {
    Write-Host ''
    if ($EnableRadio -and $radioInfo.Radio) {
        Write-Warn2 'Wi-Fi radio is off - turning it on because -EnableRadio was passed'
        $set = Set-WiFiRadioOn $radioInfo.Radio
        Start-Sleep -Seconds 3
        $radioInfo = Get-WiFiRadio
        $radioState = if ($radioInfo.Radio) { "$($radioInfo.Radio.State)" } else { '(gone)' }
        Write-Info "SetStateAsync -> $set, radio is now: $radioState"
        if ($radioState -ne 'On') {
            Write-Bad 'could not turn the radio on - stopping'
            $result.status = 'not-tested-radio-off'
            if ($transcribing) { Stop-Transcript | Out-Null }
            exit 3
        }
        Write-Ok 'radio is On'
        $radioInfo = Get-WiFiRadio
    } else {
        Write-Bad 'THE WI-FI RADIO IS OFF. Wi-Fi Direct cannot start, online or offline.'
        Write-Host ''
        Write-Say 'This aborts the advertisement with RadioNotAvailable and would look exactly'
        Write-Say 'like "Wi-Fi Direct does not work offline" - a false negative. Stopping instead.'
        Write-Host ''
        Write-Say 'Fix it either way:'
        Write-Say '  - Click the Wi-Fi tile in Quick Settings (Win+A) to turn Wi-Fi back on, or'
        Write-Say '  - re-run this script with  -EnableRadio  and it will turn it on for you.'
        Write-Host ''
        $result.status = 'not-tested-radio-off'
        Write-Host ($result | ConvertTo-Json) -ForegroundColor White
        if ($transcribing) { Stop-Transcript | Out-Null }
        exit 3
    }
}

# ---- stale group-owner precondition. Same reasoning as the radio: fail before STEP 2 asks
# ---- anyone to unplug a cable, and fail with the actual reason rather than a bare Aborted.
$stale = @(Get-WiFiDirectAdapters)
if ($stale.Count) {
    Write-Host ''
    Write-Warn2 "A Wi-Fi Direct adapter is ALREADY up before this run started:"
    foreach ($s in $stale) { Write-Info "  $($s.Name) - $($s.InterfaceDescription)" }
    Write-Info 'This driver has one P2P GO port. An orphan holds it and every start below will'
    Write-Info 'return Aborted (ResourceInUse). Orphans come from a run that was killed instead'
    Write-Info 'of exited - Ctrl+C is fine, Stop-Process is not.'
    Write-Host ''
    if ($radioInfo.Radio) {
        if (Clear-StaleGroupOwner $radioInfo.Radio) {
            $radioInfo = Get-WiFiRadio
        } else {
            Write-Bad 'could not clear it - stopping rather than reporting a meaningless Aborted'
            $result.status = 'not-tested-stale-group-owner'
            Write-Host ($result | ConvertTo-Json) -ForegroundColor White
            if ($transcribing) { Stop-Transcript | Out-Null }
            exit 4
        }
    }
}

# ---------------------------------------------------------------- STEP 2

if ($WaitForOffline) {
    Write-Step 'STEP 2  Waiting for this machine to go completely offline'
    Write-Host ''
    Write-Say '>>> UNPLUG THE ETHERNET CABLE NOW.  (leave Wi-Fi disconnected)'
    Write-Say '>>> No admin needed. Nothing is changed on disk. Ctrl+C abandons the test.'
    Write-Host ''
    Write-Info 'The line below updates every second. It continues by itself the moment both'
    Write-Info 'the adapter list and the WinRT internet profile are empty.'
    Write-Host ''

    $spun = 0
    $spinner = '|/-\'
    while ($true) {
        $uplink = Get-UplinkDescription
        if ($uplink.Adapters.Count -eq 0 -and -not $uplink.InternetProfile) { break }

        # Bounded, so an abandoned run exits on its own rather than holding the radio forever.
        if ($spun -ge $WaitForOfflineTimeoutSeconds) {
            Write-Host ''
            Write-Bad "still attached after ${WaitForOfflineTimeoutSeconds}s - giving up without testing"
            $result.status = 'not-tested-still-online'
            Write-Output ($result | ConvertTo-Json -Compress)
            if ($transcribing) { Stop-Transcript | Out-Null }
            exit 2
        }

        $adapters = if ($uplink.AdapterNames) { $uplink.AdapterNames } else { '(none)' }
        $prof     = if ($uplink.InternetProfile) { $uplink.InternetProfile } else { '(none)' }
        Write-Host ("`r   {0} waiting {1,3}s   adapters: {2,-24} profile: {3,-24}" -f `
            $spinner[$spun % 4], $spun, $adapters, $prof) -NoNewline -ForegroundColor Yellow

        Start-Sleep -Seconds 1
        $spun += 1
    }
    Write-Host ''
    Write-Host ''
    Write-Ok 'no connected adapter, no internet profile - THIS IS THE REAL TEST CONDITION'
} else {
    Write-Step 'STEP 2  (skipped - no -WaitForOffline, so the network stays plugged in)'
    Write-Info 'A pass here proves the mechanism works, not that it works offline.'
}

$result.uplinkAtStart = if ($uplink.InternetProfile) { $uplink.InternetProfile }
                        elseif ($uplink.Adapters.Count) { "adapter-up-no-profile: $($uplink.AdapterNames)" }
                        else { 'none' }

# Is the control case actually the control case? Ask the tethering API the same question the
# game asks it, so this run's finding sits next to the mechanism it is meant to replace.
Write-Info ''
Write-Info 'Control - what Mobile Hotspot would say under these exact conditions:'
$profileNow = [Windows.Networking.Connectivity.NetworkInformation,Windows.Networking,ContentType=WindowsRuntime]::GetInternetConnectionProfile()
if ($null -eq $profileNow) {
    Write-Info '  GetInternetConnectionProfile() -> null. NetworkOperatorTetheringManager has'
    Write-Info '  nothing to tether from; hotspot-control.ps1 would return no-connection-profile.'
} else {
    Write-Info "  GetInternetConnectionProfile() -> '$($profileNow.ProfileName)'. Mobile Hotspot starts here too."
}

# ---------------------------------------------------------------- STEP 3

try {
    # ------------------------------------------------------------ STEP 2b

    if ($Dns) {
        Write-Step 'STEP 2b  DNS responder - started BEFORE the group owner'
        Write-Info 'ICS takes UDP :53 when it brings up the network. hotspot-dns-test.ps1 found'
        Write-Info 'that binding first wins and ICS does not contend afterwards; that holds here'
        Write-Info 'too - both orderings work. -DnsAfter is the other one, and binds'
        Write-Info '192.168.137.1:53 specifically rather than the wildcard.'
        Set-DnsQueryBaseline -SpikeDirectory $SpikeDir
        $dnsProc = Start-DnsResponder -SpikeDirectory $SpikeDir
    }

    Write-Step 'STEP 3  Starting a Wi-Fi Direct autonomous group owner (legacy WPA2 SSID)'

    # WinRT types only project into PowerShell through the [Type,WinMD,ContentType=WindowsRuntime]
    # literal - the WinMD is the union metadata name (Windows.Devices), not the namespace. Plain
    # New-Object on the full namespace fails with "cannot find type", which is what the first run
    # of this spike did. Same form hotspot-control.ps1 uses for Windows.Networking.
    $pubType    = [Windows.Devices.WiFiDirect.WiFiDirectAdvertisementPublisher,Windows.Devices,ContentType=WindowsRuntime]
    $discovType = [Windows.Devices.WiFiDirect.WiFiDirectAdvertisementListenStateDiscoverability,Windows.Devices,ContentType=WindowsRuntime]
    $credType   = [Windows.Security.Credentials.PasswordCredential,Windows.Security,ContentType=WindowsRuntime]

    # Retried, because pulling the cable reconfigures the network stack and the first attempt can
    # land in the middle of that. A single Aborted right after an unplug is not evidence of a
    # hard limitation; the same Aborted three times over 30 seconds is.
    $status = 'Created'
    for ($attempt = 1; $attempt -le $Attempts; $attempt++) {
        if ($attempt -gt 1) {
            Write-Info ''
            Write-Info "attempt $attempt of $Attempts - waiting ${SettleSeconds}s for the stack to settle first"
            for ($s = $SettleSeconds; $s -gt 0; $s--) {
                Write-Host ("`r   settling... {0,3}s   " -f $s) -NoNewline -ForegroundColor DarkGray
                Start-Sleep -Seconds 1
            }
            Write-Host "`r                           `r" -NoNewline
        }

        # A fresh publisher per attempt - an aborted one is not documented as restartable, and
        # reusing it would make a retry prove nothing.
        $publisher = $pubType::new()
        $adv = $publisher.Advertisement
        $adv.IsAutonomousGroupOwnerEnabled = $true
        $adv.ListenStateDiscoverability = $discovType::Normal

        $legacy = $adv.LegacySettings
        $legacy.IsEnabled = $true
        $legacy.Ssid = $Ssid
        $cred = $credType::new()
        $cred.Password = $Passphrase
        $legacy.Passphrase = $cred
        if ($attempt -eq 1) {
            Write-Info "SSID '$Ssid' / passphrase '$Passphrase', autonomous GO, legacy clients enabled"
        }

        # StatusChanged carries the WiFiDirectError; the publisher's own Status property does not.
        # PowerShell 5.1 refuses to subscribe to WinRT events at all ("cannot subscribe to Windows
        # RT events"), so polling Status is the load-bearing signal and the error detail is a
        # bonus we do not get here. The radio precondition in STEP 1 exists because that missing
        # detail is what made the first offline run ambiguous.
        $global:WfdError = $null
        try {
            $sub = Register-ObjectEvent -InputObject $publisher -EventName StatusChanged -Action {
                $global:WfdError = $Event.SourceEventArgs.Error
            }
            if ($attempt -eq 1) { Write-Info 'subscribed to StatusChanged (error detail available)' }
        } catch {
            $sub = $null
            if ($attempt -eq 1) {
                Write-Info "no StatusChanged subscription ($($_.Exception.Message.Trim()))"
                Write-Info 'polling Status instead - an abort will be visible but unexplained'
            }
        }

        $publisher.Start()

        $deadline = (Get-Date).AddSeconds($StartTimeoutSeconds)
        while ((Get-Date) -lt $deadline -and "$($publisher.Status)" -eq 'Created') {
            Write-Host ("`r   attempt {0}: status Created (up to {1}s)   " -f $attempt, $StartTimeoutSeconds) -NoNewline -ForegroundColor DarkGray
            Start-Sleep -Milliseconds 250
        }
        Write-Host "`r                                                       `r" -NoNewline

        $status = "$($publisher.Status)"
        if ($status -eq 'Started') { break }

        Write-Warn2 "attempt $attempt of ${Attempts}: $status"
        # Stop the failed publisher explicitly. An abort can still leave the virtual adapter
        # up, and that adapter holds the driver's only GO port - so an unstopped failure
        # guarantees the next attempt fails too, making the retry loop prove nothing.
        try { $publisher.Stop() } catch { }
        $orphan = @(Get-WiFiDirectAdapters)
        if ($orphan.Count) {
            Write-Info "a Wi-Fi Direct adapter is still up after the failed attempt - clearing it"
            $r = Get-WiFiRadio
            if ($r.Radio) { Clear-StaleGroupOwner $r.Radio | Out-Null }
        }
        # Re-check the radio - a failed start can leave it software-off, which would poison
        # every later attempt and, worse, look like a fresh independent failure.
        $recheck = Get-WiFiRadio
        $recheckState = if ($recheck.Radio) { "$($recheck.Radio.State)" } else { '(none)' }
        if ($recheckState -ne 'On') {
            Write-Bad "the radio is now '$recheckState' - the failed start turned it off"
            Write-Info 'that is itself a finding: production code would have to restore it'
            if ($EnableRadio -and $recheck.Radio) {
                Set-WiFiRadioOn $recheck.Radio | Out-Null
                Start-Sleep -Seconds 3
                Write-Info 'turned it back on for the next attempt'
            } else {
                break
            }
        }
        if ($sub) { try { Unregister-Event -SubscriptionId $sub.Id -ErrorAction SilentlyContinue } catch { }; $sub = $null }
    }

    $result.status = $status
    # $attempt is Attempts+1 when the loop runs out rather than breaking on success - reporting
    # that as "4 of 3" is just noise in a result someone has to read.
    $result.attemptsUsed = [Math]::Min($attempt, $Attempts)
    $result.error  = if ($global:WfdError) { "$($global:WfdError)" } else { $null }

    if ($status -eq 'Started') {
        $result.startedWithNoUplink = ($result.uplinkAtStart -eq 'none')
        Write-Ok 'publisher status: Started'
        if ($result.uplinkAtStart -eq 'none') {
            Write-Ok 'STARTED WITH NO NETWORK ATTACHED - the thing Mobile Hotspot cannot do'
        } else {
            Write-Warn2 "started, but an uplink was present ($($result.uplinkAtStart))"
            Write-Info  'rerun with -WaitForOffline for the answer that actually matters'
        }

        # Windows is free to substitute its own SSID and passphrase for the legacy AP - the
        # values set before Start() are a request, not a guarantee. Sending someone to their
        # phone to look for a network name that is not being broadcast wastes a whole test
        # cycle, so read back what the radio is actually advertising and use that everywhere.
        try {
            $realSsid = "$($adv.LegacySettings.Ssid)"
            $realPass = "$($adv.LegacySettings.Passphrase.Password)"
            $result.broadcastSsid = $realSsid
            $result.broadcastPassphrase = $realPass

            if ($realSsid -ne $Ssid) {
                Write-Warn2 "Windows substituted the SSID."
                Write-Say   "  requested:   $Ssid"
                Write-Say   "  BROADCASTING: $realSsid"
            } else {
                # Worth stating plainly: this property is the value we set, echoed back. It is
                # not read from the beacon, and the host has no way to scan its own AP (netsh
                # shows one WLAN interface even with a GO running). Only a phone can confirm.
                Write-Info "LegacySettings.Ssid reads back as '$realSsid' (echo of what was set,"
                Write-Info 'not a reading of the beacon - only a phone can confirm the real name)'
            }
            if ($realSsid -notlike 'DIRECT-*') {
                Write-Warn2 "'$realSsid' does not start with DIRECT-, which the Wi-Fi Direct spec"
                Write-Warn2 'requires of a group owner. Windows may be beaconing a different name.'
            }
            if ($realPass -ne $Passphrase) {
                Write-Warn2 "Windows substituted the passphrase - the real one is: $realPass"
            }
        } catch {
            Write-Warn2 "could not read back the legacy settings: $($_.Exception.Message)"
            $realSsid = $Ssid
            $realPass = $Passphrase
        }
    } else {
        Write-Bad "publisher status: $status$(if ($result.error) { " (error: $($result.error))" })"
        Write-Info 'RadioNotAvailable = radio off or absent.'
        Write-Info 'ResourceInUse     = something else owns the radio (a live Mobile Hotspot, a'
        Write-Info '                    Miracast session, or an active Wi-Fi connection on a'
        Write-Info '                    driver with only one GO port).'
        throw "Wi-Fi Direct advertisement did not start (status $status)"
    }

    # ------------------------------------------------------------ STEP 4

    Write-Step 'STEP 4  Addressing - did it land on 192.168.137.1?'

    # The virtual adapter is created by the start above, so it is worth a few retries; the
    # driver surfaces it a beat after Status flips.
    for ($i = 0; $i -lt 20; $i++) {
        $wfd = Get-NetAdapter -ErrorAction SilentlyContinue |
            Where-Object { $_.InterfaceDescription -match 'Wi-Fi Direct' -and $_.Status -eq 'Up' } |
            Select-Object -First 1
        if ($wfd) { break }
        Start-Sleep -Milliseconds 500
    }

    if (-not $wfd) {
        Write-Bad 'no Wi-Fi Direct virtual adapter came up - the advertisement is running but'
        Write-Bad 'there is no interface for the relay to bind to'
    } else {
        Write-Ok "virtual adapter: $($wfd.Name) - $($wfd.InterfaceDescription) (ifIndex $($wfd.ifIndex))"

        $addr = $null
        for ($i = 0; $i -lt 30; $i++) {
            $addr = Get-NetIPAddress -AddressFamily IPv4 -InterfaceIndex $wfd.ifIndex -ErrorAction SilentlyContinue |
                Select-Object -First 1
            # An APIPA address means nothing configured it yet; keep waiting in case ICS is late.
            if ($addr -and $addr.IPAddress -notlike '169.254.*') { break }
            Write-Host ("`r   waiting for a non-APIPA address... {0}s   " -f ($i * 0.5)) -NoNewline -ForegroundColor DarkGray
            Start-Sleep -Milliseconds 500
        }
        Write-Host "`r                                                      `r" -NoNewline

        if (-not $addr) {
            Write-Bad 'the interface has no IPv4 address at all'
        } else {
            $result.address = $addr.IPAddress
            $result.is137 = ($addr.IPAddress -eq '192.168.137.1')
            if ($result.is137) {
                Write-Ok 'address is 192.168.137.1 - the address LocalServices already pins the certificate to'
            } elseif ($addr.IPAddress -like '169.254.*') {
                Write-Bad "address is APIPA ($($addr.IPAddress)) - nothing configured this interface,"
                Write-Bad 'so there is no DHCP for guests either'
            } else {
                Write-Warn2 "address is $($addr.IPAddress), NOT 192.168.137.1"
                Write-Info  'the pinned certificate and the relay bind address would have to follow it'
            }
        }
    }

    # ------------------------------------------------------------ STEP 5

    Write-Step 'STEP 5  DHCP - is anything prepared to hand a phone a lease?'
    $ics = Get-Service SharedAccess -ErrorAction SilentlyContinue
    if ($ics) { Write-Info "SharedAccess (ICS, which is what serves DHCP here): $($ics.Status)" }
    $dhcp = @(Get-NetUDPEndpoint -LocalPort 67 -ErrorAction SilentlyContinue)
    $result.dhcpListening = ($dhcp.Count -gt 0)
    if ($dhcp.Count) {
        foreach ($d in $dhcp) {
            $owner = Get-Process -Id $d.OwningProcess -ErrorAction SilentlyContinue
            Write-Ok ("UDP :67 bound on {0} by {1} (pid {2})" -f $d.LocalAddress, $(if ($owner) { $owner.ProcessName } else { '?' }), $d.OwningProcess)
        }
    } else {
        Write-Bad 'nothing is listening on UDP :67 - a joining phone will not get an address.'
        Write-Info 'That would mean shipping our own DHCP responder next to relay/dns.js.'
    }

    # ------------------------------------------------------------ STEP 5b

    Write-Step 'STEP 5b  What the radio is actually transmitting'
    Write-Info 'If a phone cannot see the network, the answer is almost always in here: the'
    Write-Info 'SSID being broadcast, or the band/channel it landed on. A 6 GHz channel is'
    Write-Info 'invisible to most phones; 5 GHz is invisible to a few older ones.'
    Write-Host ''
    $ifaceDump = (netsh wlan show interfaces) 2>&1 | Out-String
    foreach ($line in ($ifaceDump -split "`r?`n")) {
        if ($line.Trim()) { Write-Host "     $($line.TrimEnd())" -ForegroundColor DarkGray }
    }
    Write-Host ''
    # A GO shows up as a second, "Secondary"-type interface while it is running. Its SSID and
    # channel are the ground truth for what a phone would see.
    if ($ifaceDump -match 'There (is|are) (\d+) interfaces?') {
        Write-Info "netsh reports $($Matches[2]) WLAN interface(s) - a running GO usually adds one"
    }

    # ------------------------------------------------------------ STEP 5c

    if ($DnsAfter) {
        Write-Step 'STEP 5c  DNS responder - started AFTER the group owner'
        Write-Info 'The other ordering. The group owner is already up, so ICS has had its chance'
        Write-Info 'at :53 and 192.168.137.1 now exists to bind to specifically. Whether anything'
        Write-Info 'is left for us is the question.'
        Set-DnsQueryBaseline -SpikeDirectory $SpikeDir
        $dnsProc = Start-DnsResponder -SpikeDirectory $SpikeDir -BindHotspotAddressOnly
        if (-not $dnsProc) {
            Write-Bad 'could not start the responder after the group owner either.'
            Write-Info 'If ICS holds 0.0.0.0:53, a bind to 192.168.137.1:53 cannot succeed on'
            Write-Info 'Windows without exclusive-address options. That would mean local DNS and'
            Write-Info 'a Wi-Fi Direct AP cannot coexist as currently built, and the responder'
            Write-Info 'would have to move into ICS-free territory (static addressing, own DHCP).'
        }
    }

    # What the hotspot interface itself is configured with. ICS normally advertises its own
    # address (192.168.137.1) to DHCP clients as the resolver; if it is instead handing out the
    # host's upstream servers, the phone asks the internet and gets NXDOMAIN for a name only we
    # know - and no query ever reaches us, which is indistinguishable from the browser bypassing
    # us unless we look here.
    if (($Dns -or $DnsAfter) -and $wfd) {
        Write-Step 'STEP 5d  What resolver is this interface set up with?'
        try {
            $srv = Get-DnsClientServerAddress -InterfaceIndex $wfd.ifIndex -AddressFamily IPv4 -ErrorAction SilentlyContinue
            $list = if ($srv) { ($srv.ServerAddresses -join ', ') } else { '' }
            Write-Info ("hotspot interface resolvers : {0}" -f $(if ($list) { $list } else { '(none set)' }))
        } catch { Write-Warn2 "could not read interface DNS: $($_.Exception.Message)" }
        Write-Info 'Windows does not expose the DHCP options ICS hands out, so confirm on the'
        Write-Info 'phone: Wi-Fi > the network > Advanced/Details should show DNS 192.168.137.1.'
        Write-Info 'If it shows anything else, that is the answer and no browser setting matters.'
    }

    if ($Dns -and $dnsProc) {
        Write-Step 'STEP 5c  Did the responder survive the group owner starting?'
        if ($dnsProc.HasExited) {
            Write-Bad "the responder died when the network came up (exit $($dnsProc.ExitCode))"
            Write-Info 'ICS would have to be displaced or replaced for local DNS to work here.'
        } else {
            $after = @(Get-NetUDPEndpoint -LocalPort 53 -ErrorAction SilentlyContinue)
            $ours  = @($after | Where-Object { $_.OwningProcess -eq $dnsProc.Id })
            $theirs = @($after | Where-Object { $_.OwningProcess -ne $dnsProc.Id })
            if ($ours.Count -and -not $theirs.Count) {
                Write-Ok "responder is the SOLE holder of :53 (pid $($dnsProc.Id)) - ICS did not contend"
            } elseif ($ours.Count) {
                Write-Warn2 "responder holds :53, but so does something else:"
                foreach ($t in $theirs) {
                    $to = Get-Process -Id $t.OwningProcess -ErrorAction SilentlyContinue
                    Write-Info ("  {0} by {1} (pid {2})" -f $t.LocalAddress, $(if ($to) { $to.ProcessName } else { '?' }), $t.OwningProcess)
                }
            } else {
                Write-Bad 'the responder no longer holds :53'
            }
            Write-Info ''
            Write-Info 'The real proof is the responder logging a query from the phone address.'
            Write-Info "Watch deploy/spike/dns-responder.log while the phone loads the page."
        }
    }

    # ------------------------------------------------------------ STEP 6

    if ($KeepRunning) {
        Write-Step 'STEP 6  Join from a real phone'

        if ($Serve) {
            $confirmJs = Join-Path $SpikeDir 'confirm-server.js'
            if (Test-Path $confirmJs) {
                # The prior hotspot spike found the firewall was the one thing that failed, so
                # check before sending someone to a phone to debug a silent timeout.
                $rule = Get-NetFirewallRule -DisplayName 'RockSkipping spike HTTP' -ErrorAction SilentlyContinue
                if (-not $rule) {
                    Write-Warn2 'no inbound rule for port 80 - the phone will probably time out.'
                    Write-Info  'Run this ONCE in an ADMIN PowerShell (this script will not do it for you):'
                    Write-Host  ''
                    Write-Host  '     New-NetFirewallRule -DisplayName "RockSkipping spike HTTP" -Direction Inbound -Protocol TCP -LocalPort 80 -RemoteAddress 192.168.137.0/24 -Action Allow -Profile Any' -ForegroundColor White
                    Write-Host  ''
                } else {
                    Write-Ok 'inbound firewall rule "RockSkipping spike HTTP" is present'
                }
                $confirm = Start-Process -FilePath 'node' -ArgumentList @($confirmJs) -PassThru -NoNewWindow
                Write-Ok "confirm-server.js running (pid $($confirm.Id)) on port 80"
            } else {
                Write-Warn2 "confirm-server.js not found at $confirmJs"
            }
        }

        # These are the read-back values, not what was requested - see the substitution check
        # in STEP 3. Printing the requested name when Windows is broadcasting a different one
        # sends someone hunting for a network that does not exist.
        $showSsid = if ($result.broadcastSsid) { $result.broadcastSsid } else { $Ssid }
        $showPass = if ($result.broadcastPassphrase) { $result.broadcastPassphrase } else { $Passphrase }

        # Scan, do not hunt. Browsing the Wi-Fi list is the worst way to join this network:
        # phones routinely filter P2P/DIRECT- SSIDs out of the picker UI even when the radio
        # can see them perfectly well, which is very likely what happened on the first two
        # phone attempts. A WIFI: payload joins by SSID directly and never consults that list.
        # deploy/spike/wifi-qr.js already renders this - reuse it rather than writing another.
        $qrJs = Join-Path $SpikeDir 'wifi-qr.js'
        if (Test-Path $qrJs) {
            $qrArgs = @('--ssid', $showSsid, '--pass', $showPass, '--hidden', $(if ($QrHidden) { 'true' } else { 'false' }))
            # With the responder up, SCAN 2 carries the NAME rather than the address, so the QR
            # itself exercises DNS. An IP URL performs no lookup and would pass whether or not
            # the responder works - which is exactly how the last run looked like a success
            # until the final step.
            $joinUrl = if (($Dns -or $DnsAfter) -and $dnsProc -and -not $dnsProc.HasExited) { 'http://play.local-ip.sh/' }
                       elseif ($result.address) { "http://$($result.address)/" }
                       else { $null }
            if ($joinUrl) { $qrArgs += @('--url', $joinUrl) }
            # Straight to the console, not captured - capturing round-trips the block
            # characters through PowerShell's encoding and can wreck the symbol.
            & node $qrJs @qrArgs
        } else {
            Write-Warn2 "wifi-qr.js not found at $qrJs - falling back to typing it in by hand"
        }

        Write-Host ''
        Write-Say '>>> ON YOUR PHONE, IN ORDER:'
        Write-Say '      1. Point the camera at SCAN 1 above and tap the Wi-Fi prompt.'
        Write-Say ''
        Write-Say '      2. If the page fails to resolve, force the resolver by hand. Android'
        Write-Say '         hides the DHCP-provided DNS unless you switch to static, and ICS'
        Write-Say '         appears not to advertise a working one:'
        Write-Say "           Wi-Fi > tap '$showSsid' > IP settings > Static"
        Write-Say '           IP address      192.168.137.157'
        Write-Say '           Gateway         192.168.137.1'
        Write-Say '           Prefix length   24'
        Write-Say '           DNS 1           192.168.137.1     <- the whole point'
        Write-Say '         Then reload. If it works with this and not with DHCP, the defect is'
        Write-Say '         what ICS hands out - not the responder, not Wi-Fi Direct.'
        Write-Say '         Set IP settings back to DHCP afterwards.'
        Write-Say ''
        Write-Say '      3. Then point the camera at SCAN 2 to open the page.'
        Write-Host ''
        Write-Info "If you would rather type it: network '$showSsid', password '$showPass'"
        if ($joinUrl) { Write-Info "then $joinUrl" }
        Write-Host ''
        Write-Info 'If SCAN 1 fails to connect:'
        Write-Info '  - Re-run with -QrHidden. That flags the network as hidden in the payload,'
        Write-Info '    which makes the phone actively probe for the SSID instead of relying on'
        Write-Info '    seeing it in a passive scan.'
        Write-Info '  - Tell me the exact error the phone gives. "Could not join" and "network'
        Write-Info '    not found" mean different things and point at different causes.'
        Write-Info '  - On Android, Settings -> Wi-Fi -> Wi-Fi Direct lists P2P peers. If this PC'
        Write-Info '    shows up there, the radio IS beaconing and the problem is only the'
        Write-Info '    legacy/AP layer. That is the single most useful thing you can check.'
        Write-Host ''
        Write-Info 'Any phone that appears below has completed DHCP - association alone does not'
        Write-Info 'create a neighbour entry with an IPv4 address, so this line is the real proof.'
        Write-Host ''
        Write-Say '>>> Press any key here when you are done.'
        Write-Host ''

        $seen = @{}
        $ticks = 0
        while ($true) {
            try { if ([Console]::KeyAvailable) { [Console]::ReadKey($true) | Out-Null; break } } catch { }

            if ($wfd) {
                Get-NetNeighbor -InterfaceIndex $wfd.ifIndex -AddressFamily IPv4 -ErrorAction SilentlyContinue |
                    Where-Object { $_.State -in 'Reachable', 'Stale', 'Permanent' -and
                                   $_.IPAddress -notlike '224.*' -and $_.IPAddress -notlike '239.*' -and
                                   $_.IPAddress -ne '255.255.255.255' } |
                    ForEach-Object {
                        if (-not $seen.ContainsKey($_.IPAddress)) {
                            $seen[$_.IPAddress] = $true
                            Write-Host ''
                            Write-Ok "CLIENT JOINED: $($_.IPAddress)  (mac $($_.LinkLayerAddress))"
                        }
                    }
            }
            $ticks += 1
            Write-Host ("`r   listening... {0,4}s   clients so far: {1}    " -f $ticks, $(if ($seen.Count) { ($seen.Keys -join ', ') } else { 'none yet' })) -NoNewline -ForegroundColor DarkGray
            Start-Sleep -Seconds 1
        }
        Write-Host ''
        $result.clientsSeen = @($seen.Keys)
        Write-Info ("clients seen: {0}" -f $(if ($seen.Count) { ($seen.Keys -join ', ') } else { 'none' }))
    }
}
catch {
    Write-Bad $_.Exception.Message
}
finally {
    Write-Step 'Cleanup'
    if ($confirm) {
        try { Stop-Process -Id $confirm.Id -Force -ErrorAction SilentlyContinue; Write-Ok 'confirm-server stopped' } catch { }
    }
    if ($dnsProc) {
        Write-Step 'DNS - did the phone actually ask us?'
        try { $result.dnsQueries = Show-DnsQuerySummary -SpikeDirectory $SpikeDir } catch { Write-Warn2 $_.Exception.Message }
        try { Stop-Process -Id $dnsProc.Id -Force -ErrorAction SilentlyContinue; Write-Ok 'DNS responder stopped (:53 released)' } catch { }
    }
    if ($sub) {
        try { Unregister-Event -SubscriptionId $sub.Id -ErrorAction SilentlyContinue } catch { }
    }
    if ($publisher) {
        try {
            if ("$($publisher.Status)" -eq 'Started') { $publisher.Stop() }
            Start-Sleep -Seconds 2
            # NOT "nothing persists" - that claim was in here and it was wrong. A killed process
            # leaves the virtual adapter up, holding the driver's only GO port, and the next run
            # fails with a bare Aborted that names no cause. Say what is actually true.
            $left = @(Get-WiFiDirectAdapters)
            if ($left.Count) {
                Write-Warn2 "advertisement stopped, but $($left.Count) Wi-Fi Direct adapter(s) still up"
                Write-Info  'the next run will clear it automatically at STEP 1'
            } else {
                Write-Ok 'advertisement stopped and the virtual adapter is gone'
            }
        } catch { Write-Warn2 "stop failed: $($_.Exception.Message)" }
    }

    # The radio state AFTER the session matters as much as before it. The timeline of the first
    # offline run suggests stopping an advertisement can leave the radio software-off, which
    # would silently poison the next run - and in production would leave the host's Wi-Fi off
    # after a match. Measured here rather than assumed.
    try {
        $radioAfter = Get-WiFiRadio
        $radioAfterState = if ($radioAfter.Radio) { "$($radioAfter.Radio.State)" } else { '(none)' }
        $result.radioAfter = $radioAfterState
        if ($radioAfterState -eq 'On') {
            Write-Ok "wi-fi radio after this run: On (the session left it usable)"
        } else {
            Write-Bad "wi-fi radio after this run: $radioAfterState"
            Write-Info 'the session turned the radio off. This poisons the NEXT run, and in'
            Write-Info 'production would leave the host with Wi-Fi off after a match.'
        }
    } catch { Write-Warn2 "could not read radio state after the run: $($_.Exception.Message)" }

    Write-Step 'RESULT'
    Write-Host ''
    Write-Host ($result | ConvertTo-Json) -ForegroundColor White
    Write-Host ''
    if ($WaitForOffline) {
        Write-Say '>>> TEST DONE - safe to plug the network back in now.'
    }
    Write-Info "transcript saved to $LogPath"
    if ($transcribing) { try { Stop-Transcript | Out-Null } catch { } }
}

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
    [string] $Ssid = 'RockSkip Direct',
    [string] $Passphrase = 'skipdirect',
    [switch] $WaitForOffline,
    [switch] $KeepRunning,
    [switch] $Serve,
    [int]    $StartTimeoutSeconds = 20,
    [int]    $WaitForOfflineTimeoutSeconds = 300,
    [string] $LogPath
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Runtime.WindowsRuntime

$SpikeDir = Split-Path -Parent $MyInvocation.MyCommand.Path
if (-not $LogPath) { $LogPath = Join-Path $SpikeDir 'wifidirect-run.log' }

function Write-Step { param([string] $m) Write-Host "`n=== $m" -ForegroundColor Cyan }
function Write-Ok   { param([string] $m) Write-Host "   [ok]   $m" -ForegroundColor Green }
function Write-Bad  { param([string] $m) Write-Host "   [FAIL] $m" -ForegroundColor Red }
function Write-Warn2{ param([string] $m) Write-Host "   [warn] $m" -ForegroundColor Yellow }
function Write-Info { param([string] $m) Write-Host "   $m" -ForegroundColor Gray }
function Write-Say  { param([string] $m) Write-Host "   $m" -ForegroundColor White }

# Write-Host bypasses the pipeline, so Tee-Object cannot capture this script. A transcript can.
try { Start-Transcript -Path $LogPath -Force | Out-Null; $transcribing = $true }
catch { $transcribing = $false; Write-Warn2 "could not start transcript at ${LogPath}: $($_.Exception.Message)" }

$publisher = $null
$sub       = $null
$confirm   = $null
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
    Write-Step 'STEP 3  Starting a Wi-Fi Direct autonomous group owner (legacy WPA2 SSID)'

    # WinRT types only project into PowerShell through the [Type,WinMD,ContentType=WindowsRuntime]
    # literal - the WinMD is the union metadata name (Windows.Devices), not the namespace. Plain
    # New-Object on the full namespace fails with "cannot find type", which is what the first run
    # of this spike did. Same form hotspot-control.ps1 uses for Windows.Networking.
    $pubType    = [Windows.Devices.WiFiDirect.WiFiDirectAdvertisementPublisher,Windows.Devices,ContentType=WindowsRuntime]
    $discovType = [Windows.Devices.WiFiDirect.WiFiDirectAdvertisementListenStateDiscoverability,Windows.Devices,ContentType=WindowsRuntime]
    $credType   = [Windows.Security.Credentials.PasswordCredential,Windows.Security,ContentType=WindowsRuntime]

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
    Write-Info "SSID '$Ssid' / passphrase '$Passphrase', autonomous GO, legacy clients enabled"

    # StatusChanged carries the WiFiDirectError; the publisher's own Status property does not.
    # PowerShell 5.1 refuses to subscribe to WinRT events at all ("cannot subscribe to Windows RT
    # events"), so polling Status is the load-bearing signal and the error detail is a bonus we
    # do not get here. A production C# implementation would get it.
    $global:WfdError = $null
    try {
        $sub = Register-ObjectEvent -InputObject $publisher -EventName StatusChanged -Action {
            $global:WfdError = $Event.SourceEventArgs.Error
        }
        Write-Info 'subscribed to StatusChanged (error detail available)'
    } catch {
        $sub = $null
        Write-Info "no StatusChanged subscription ($($_.Exception.Message.Trim()))"
        Write-Info 'polling Status instead - an abort will be visible but unexplained'
    }

    $publisher.Start()

    $deadline = (Get-Date).AddSeconds($StartTimeoutSeconds)
    while ((Get-Date) -lt $deadline -and "$($publisher.Status)" -eq 'Created') {
        Write-Host ("`r   status: Created (waiting up to {0}s)   " -f $StartTimeoutSeconds) -NoNewline -ForegroundColor DarkGray
        Start-Sleep -Milliseconds 250
    }
    Write-Host "`r                                                 `r" -NoNewline

    $status = "$($publisher.Status)"
    $result.status = $status
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

        Write-Host ''
        Write-Say '>>> ON YOUR PHONE:'
        Write-Say "      1. Settings -> Wi-Fi, and look for the network:   $Ssid"
        Write-Say "      2. Password:                                      $Passphrase"
        if ($result.address) {
            Write-Say "      3. Open a browser and go to:                      http://$($result.address)/"
        }
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
    if ($sub) {
        try { Unregister-Event -SubscriptionId $sub.Id -ErrorAction SilentlyContinue } catch { }
    }
    if ($publisher) {
        try {
            if ("$($publisher.Status)" -eq 'Started') { $publisher.Stop() }
            Write-Ok 'advertisement stopped (it would have died with this process anyway - nothing persists)'
        } catch { Write-Warn2 "stop failed: $($_.Exception.Message)" }
    }

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

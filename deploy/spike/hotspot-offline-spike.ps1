#requires -Version 5.1
<#
.SYNOPSIS
    Does Mobile Hotspot actually need an INTERNET connection, or just A connection profile?

.DESCRIPTION
    ZeroSetupPlan.md Open Question 2 is recorded as "Answered - no": with Wi-Fi and Ethernet
    both down, hotspot-control.ps1 returns no-connection-profile, and the conclusion drawn was
    that NetworkOperatorTetheringManager cannot conjure a network from nothing, "with no
    parameter or workaround within that API".

    That conclusion does not follow from the evidence. hotspot-control.ps1 asks
    GetInternetConnectionProfile(), which by definition returns null when there is no internet.
    It never asks GetConnectionProfiles(), which returns every profile including ones with
    NetworkConnectivityLevel.None.

    Measured 2026-08-21 on this machine: CreateFromConnectionProfile() succeeds on all four
    profiles present, including a DISCONNECTED Bluetooth PAN adapter and two Wi-Fi profiles for
    networks not currently joined, each returning a manager with MaxClientCount 8.

        name=Ethernet 4                     connectivity=InternetAccess   -> OK
        name=TP-Link_C978_6G                connectivity=None             -> OK
        name=Wilkins                        connectivity=None             -> OK
        name=Bluetooth Network Connection 3 connectivity=None             -> OK

    So the question this script answers is whether tethering STARTS from a profile carrying no
    connectivity - and, just as important, whether ICS then brings up the DNS proxy that the
    Wi-Fi Direct path never did.

    That second point is the whole reason this matters. Wi-Fi Direct gets an access point up
    with no network attached (proven - see Findings-WiFiDirect.md), but the network it creates
    has no working resolver: :53 stays free, phones are handed no DNS, and Android loops
    forever failing to validate. Mobile Hotspot's network is fully configured by comparison,
    and a phone resolving play.local-ip.sh through it is already a proven result
    (deploy/spike/README.md #8). If tethering starts without internet, every unsolved problem
    in the Wi-Fi Direct path disappears rather than needing to be rebuilt.

.PARAMETER PreferNoConnectivity
    Deliberately pick a profile with NetworkConnectivityLevel.None even when an internet-bearing
    one exists. This is what makes an online run meaningful: it proves the mechanism does not
    depend on the profile having internet, without needing the machine to be offline first.

.PARAMETER WaitForOffline
    Wait until no adapter is connected, then run. The full-strength version of the same test.

.PARAMETER KeepRunning
    Hold the hotspot up and render join QRs so a phone can be tested. Never asks anyone to hunt
    for an SSID - see ../../CLAUDE.md.

.EXAMPLE
    ./hotspot-offline-spike.ps1 -PreferNoConnectivity
.EXAMPLE
    ./hotspot-offline-spike.ps1 -WaitForOffline -KeepRunning
#>
[CmdletBinding()]
param(
    [string] $Ssid = 'RockSkip Offline',
    [string] $Passphrase = 'skipoffline',
    [switch] $PreferNoConnectivity,
    [switch] $WaitForOffline,
    [switch] $KeepRunning,
    [switch] $EnableRadio,
    [switch] $Dns,
    [switch] $Serve,
    [string] $DnsUpstream,
    [int]    $WaitForOfflineTimeoutSeconds = 300,
    [string] $LogPath
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Runtime.WindowsRuntime
try { [Console]::OutputEncoding = [System.Text.Encoding]::UTF8 } catch { }

$SpikeDir = Split-Path -Parent $MyInvocation.MyCommand.Path
if (-not $LogPath) { $LogPath = Join-Path $SpikeDir 'hotspot-offline-run.log' }

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

function Await-Action {
    param($Action)
    $asTask = ([System.WindowsRuntimeSystemExtensions].GetMethods() |
        Where-Object { $_.Name -eq 'AsTask' -and $_.GetParameters().Count -eq 1 -and
                       -not $_.IsGenericMethod -and
                       $_.GetParameters()[0].ParameterType.Name -eq 'IAsyncAction' })[0]
    $task = $asTask.Invoke($null, @($Action))
    $task.Wait(-1) | Out-Null
}

# A software-off Wi-Fi radio makes tethering fail, and the failure status is the useless
# 'Unknown' - indistinguishable from "Windows cannot do this offline". The Wi-Fi Direct spike
# was derailed by exactly this confound once already; turning Wi-Fi off is also the most
# natural way for someone to make the machine "offline", so it is likely, not exotic.
function Get-WiFiRadio {
    $radioType  = [Windows.Devices.Radios.Radio,Windows.Devices,ContentType=WindowsRuntime]
    $accessType = [Windows.Devices.Radios.RadioAccessStatus,Windows.Devices,ContentType=WindowsRuntime]
    $access = Await-Operation $radioType::RequestAccessAsync() $accessType
    if ("$access" -ne 'Allowed') { return $null }
    $listType = [System.Collections.Generic.IReadOnlyList[Windows.Devices.Radios.Radio]]
    (Await-Operation $radioType::GetRadiosAsync() $listType) |
        Where-Object { "$($_.Kind)" -eq 'WiFi' } | Select-Object -First 1
}

function Set-WiFiRadioOn {
    param($Radio)
    $stateType  = [Windows.Devices.Radios.RadioState,Windows.Devices,ContentType=WindowsRuntime]
    $accessType = [Windows.Devices.Radios.RadioAccessStatus,Windows.Devices,ContentType=WindowsRuntime]
    Await-Operation $Radio.SetStateAsync($stateType::On) $accessType
}

function Get-DnsLogPath { Join-Path $SpikeDir 'dns-responder.log' }

function Set-DnsQueryBaseline {
    $f = Get-DnsLogPath
    $script:DnsLogBaseline = if (Test-Path $f) { @(Get-Content $f -ErrorAction SilentlyContinue).Count } else { 0 }
}

# The one fact that matters most, and the one that lived only in a log file last time.
function Show-DnsQuerySummary {
    $f = Get-DnsLogPath
    if (-not (Test-Path $f)) { Write-Warn2 'no dns-responder.log'; return 0 }
    $all = @(Get-Content $f -ErrorAction SilentlyContinue)
    $new = if ($all.Count -gt $script:DnsLogBaseline) { $all[$script:DnsLogBaseline..($all.Count - 1)] } else { @() }
    $q = @($new | Where-Object { $_ -match '->' -and $_ -notmatch 'Answering' })
    if ($q.Count -eq 0) {
        Write-Bad 'ZERO DNS queries reached this machine - the phone never asked us.'
    } else {
        Write-Ok "$($q.Count) DNS quer(ies) reached us - the phone DID ask:"
        foreach ($line in ($q | Select-Object -Last 8)) { Write-Info "  $($line.Trim())" }
    }
    return $q.Count
}

try { Start-Transcript -Path $LogPath -Force | Out-Null; $transcribing = $true }
catch { $transcribing = $false; Write-Warn2 "no transcript: $($_.Exception.Message)" }

$ni      = [Windows.Networking.Connectivity.NetworkInformation,Windows.Networking,ContentType=WindowsRuntime]
$mgrType = [Windows.Networking.NetworkOperators.NetworkOperatorTetheringManager,Windows.Networking,ContentType=WindowsRuntime]
$manager = $null
$started = $false
$dnsProc = $null
$confirm = $null
$script:DnsLogBaseline = 0
$result  = [ordered]@{
    profileUsed          = $null
    profileConnectivity  = $null
    startStatus          = $null
    address              = $null
    dhcpListening        = $null
    icsDnsProxyOnPort53  = $null
    uplinkAtStart        = $null
    attempts             = $null
    radioState           = $null
    dnsHeldByUs          = $null
    dnsQueries           = $null
}

Write-Host ''
Write-Host '  Mobile Hotspot offline spike - does it need internet, or just a profile?' -ForegroundColor White
Write-Host "  transcript: $LogPath" -ForegroundColor DarkGray

try {
    # ------------------------------------------------------------ STEP 1

    Write-Step 'STEP 1  Every connection profile, and whether tethering can be built from it'

    $internet = $ni::GetInternetConnectionProfile()
    Write-Info ("GetInternetConnectionProfile() : {0}" -f $(if ($internet) { $internet.ProfileName } else { 'null  <- what hotspot-control.ps1 gives up on' }))
    Write-Host ''

    $candidates = @()
    foreach ($p in $ni::GetConnectionProfiles()) {
        $lvl = "$($p.GetNetworkConnectivityLevel())"
        $ok = $false
        try { $mgrType::CreateFromConnectionProfile($p) | Out-Null; $ok = $true } catch { }
        Write-Info ("  {0,-34} connectivity={1,-16} tetherable={2}" -f $p.ProfileName, $lvl, $ok)
        if ($ok) { $candidates += [pscustomobject]@{ Profile = $p; Name = $p.ProfileName; Level = $lvl } }
    }

    if (-not $candidates.Count) { throw 'No profile can host tethering. This is the real ceiling, if it holds offline too.' }

    # ------------------------------------------------------------ STEP 2

    if ($WaitForOffline) {
        Write-Step 'STEP 2  Waiting for the machine to go completely offline'
        Write-Host ''
        Write-Say '>>> UNPLUG THE ETHERNET CABLE NOW.'
        Write-Host ''
        Write-Say '>>> DO NOT TURN WI-FI OFF. This is the important part.'
        Write-Info '    A hotspot IS the Wi-Fi radio, so switching Wi-Fi off guarantees failure,'
        Write-Info '    and the failure it produces is the uninformative status "Unknown" - which'
        Write-Info '    reads exactly like "Windows cannot do this offline". That is what happened'
        Write-Info '    on the first attempt at this test, and it was a false negative.'
        Write-Info '    If Wi-Fi is joined to a network, click that network and choose Disconnect.'
        Write-Info '    Disconnected-with-the-radio-on is the state this test needs.'
        Write-Host ''
        $spun = 0
        while ($true) {
            $up = @(Get-NetAdapter -ErrorAction SilentlyContinue |
                Where-Object { $_.Status -eq 'Up' -and $_.InterfaceDescription -notmatch 'Wi-Fi Direct|Loopback|Bluetooth' })
            $prof = $ni::GetInternetConnectionProfile()
            if ($up.Count -eq 0 -and -not $prof) { break }
            if ($spun -ge $WaitForOfflineTimeoutSeconds) { throw "still attached after ${WaitForOfflineTimeoutSeconds}s" }
            Write-Host ("`r   waiting {0,3}s   adapters: {1,-24}" -f $spun, $(if ($up.Count) { ($up.Name -join ',') } else { '(none)' })) -NoNewline -ForegroundColor Yellow
            Start-Sleep -Seconds 1; $spun += 1
        }
        Write-Host ''
        Write-Ok 'no connected adapter, no internet profile - the real test condition'

        # Re-enumerate: the profile list changes when adapters go down, and stale objects from
        # STEP 1 may refer to profiles that no longer exist.
        $candidates = @()
        foreach ($p in $ni::GetConnectionProfiles()) {
            $lvl = "$($p.GetNetworkConnectivityLevel())"
            try { $mgrType::CreateFromConnectionProfile($p) | Out-Null
                  $candidates += [pscustomobject]@{ Profile = $p; Name = $p.ProfileName; Level = $lvl } } catch { }
        }
        Write-Info ("tetherable profiles while offline: {0}" -f $candidates.Count)
        foreach ($c in $candidates) { Write-Info "  $($c.Name)  ($($c.Level))" }
        if (-not $candidates.Count) {
            Write-Bad 'NO tetherable profile exists offline. ZeroSetupPlan Open Question 2 stands after all.'
            throw 'no tetherable profile while offline'
        }
    }

    $up = @(Get-NetAdapter -ErrorAction SilentlyContinue |
        Where-Object { $_.Status -eq 'Up' -and $_.InterfaceDescription -notmatch 'Wi-Fi Direct|Loopback|Bluetooth' })
    $result.uplinkAtStart = if ($up.Count) { ($up.Name -join ',') } else { 'none' }

    # ---- radio gate. Checked AFTER the offline wait, because that is when it gets turned off:
    # ---- switching Wi-Fi off is the most natural way to make a machine offline.
    Write-Step 'STEP 2b  Wi-Fi radio'
    $radio = Get-WiFiRadio
    $radioState = if ($radio) { "$($radio.State)" } else { '(no WiFi radio / access denied)' }
    Write-Info "wi-fi radio: $radioState"
    if ($radioState -ne 'On') {
        if ($EnableRadio -and $radio) {
            Write-Warn2 'radio is off - turning it on (-EnableRadio)'
            Set-WiFiRadioOn $radio | Out-Null
            Start-Sleep -Seconds 4
            $radio = Get-WiFiRadio
            $radioState = if ($radio) { "$($radio.State)" } else { '(gone)' }
            Write-Info "radio is now: $radioState"
        }
        if ($radioState -ne 'On') {
            Write-Bad 'THE WI-FI RADIO IS OFF. Tethering cannot start, and the failure it returns'
            Write-Bad 'is the useless status "Unknown" - which reads exactly like "Windows cannot'
            Write-Bad 'do this offline". Stopping rather than recording a false negative.'
            Write-Info 'Turn Wi-Fi on (Win+A) or re-run with -EnableRadio. Note the radio can be'
            Write-Info 'ON while connected to nothing - that is the state this test needs.'
            $result.startStatus = 'not-tested-radio-off'
            throw 'radio off'
        }
    }
    $result.radioState = $radioState
    Write-Ok 'radio is On (being connected to nothing is fine and expected)'

    # Tethering left running by a previous session keeps ICS holding :53, so our responder
    # cannot bind and the DNS half of the test silently degrades to "ICS answers, we do not".
    # Stop it first. This also matters in production: a crashed host must not leave a hotspot up.
    Write-Step 'STEP 2b2  Is tethering already running from an earlier session?'
    $preExisting = $mgrType::CreateFromConnectionProfile($candidates[0].Profile)
    if ("$($preExisting.TetheringOperationalState)" -eq 'On') {
        Write-Warn2 'tethering is ALREADY ON - stopping it so :53 is free for the responder'
        $stopType = [Windows.Networking.NetworkOperators.NetworkOperatorTetheringOperationResult,Windows.Networking,ContentType=WindowsRuntime]
        $st = Await-Operation $preExisting.StopTetheringAsync() $stopType
        Write-Info "stop -> $($st.Status)"
        for ($i = 0; $i -lt 30; $i++) {
            if (-not @(Get-NetUDPEndpoint -LocalPort 53 -ErrorAction SilentlyContinue).Count) { break }
            Start-Sleep -Milliseconds 500
        }
        $stillHeld = @(Get-NetUDPEndpoint -LocalPort 53 -ErrorAction SilentlyContinue).Count
        if ($stillHeld) { Write-Warn2 ":53 still held by $stillHeld socket(s) after stopping" }
        else { Write-Ok ':53 released' }
    } else {
        Write-Ok 'not already tethering'
    }

    # The responder must hold :53 BEFORE tethering starts. Once ICS is up it owns 0.0.0.0:53 -
    # observed on this machine - and binding afterwards is impossible. hotspot-dns-test.ps1
    # established that binding first wins and ICS does not then contend for it.
    if ($Dns) {
        Write-Step 'STEP 2c  DNS responder - bound BEFORE tethering, the only order that works'
        $dnsJs = Join-Path $SpikeDir 'dns-responder.js'
        if (-not (Test-Path $dnsJs)) {
            Write-Bad "dns-responder.js not found at $dnsJs"
        } else {
            $held = @(Get-NetUDPEndpoint -LocalPort 53 -ErrorAction SilentlyContinue)
            if ($held.Count) { Write-Warn2 ":53 already held by $($held.Count) socket(s) - ICS may already own it" }
            else { Write-Ok ':53 is free' }

            Set-DnsQueryBaseline
            $dnsArgs = @($dnsJs, '--ip', '192.168.137.1', '--suffix', '.local-ip.sh',
                         '--bind', '0.0.0.0', '--allow', '192.168.137.0/24')
            if ($DnsUpstream) { $dnsArgs += @('--upstream', $DnsUpstream) }
            $dnsProc = Start-Process -FilePath 'node' -ArgumentList $dnsArgs -PassThru -NoNewWindow
            Start-Sleep -Seconds 2
            if ($dnsProc.HasExited) { Write-Bad "responder exited immediately ($($dnsProc.ExitCode))"; $dnsProc = $null }
            else { Write-Ok "responder holds :53 (pid $($dnsProc.Id))" }

            if (-not (Get-NetFirewallRule -DisplayName 'RockSkipping spike DNS' -ErrorAction SilentlyContinue)) {
                Write-Warn2 'no inbound UDP 53 rule - phones will get ERR_NAME_NOT_RESOLVED'
            }
        }
    }

    # Try EVERY tetherable profile, not just the first. The previous run picked 'Ethernet 4' -
    # whose cable had just been pulled - got Unknown, and stopped, which proved nothing about
    # the other three. One run should test them all.
    $order = if ($PreferNoConnectivity) {
        @($candidates | Where-Object { $_.Level -eq 'None' }) + @($candidates | Where-Object { $_.Level -ne 'None' })
    } else { $candidates }

    Write-Step "STEP 3  Trying to start tethering from each of $($order.Count) profile(s)"
    $opResultType = [Windows.Networking.NetworkOperators.NetworkOperatorTetheringOperationResult,Windows.Networking,ContentType=WindowsRuntime]
    $attemptLog = @()

    foreach ($cand in $order) {
        Write-Host ''
        Write-Info "--- '$($cand.Name)'  (connectivity=$($cand.Level))"
        try {
            $manager = $mgrType::CreateFromConnectionProfile($cand.Profile)
            Write-Info "    tethering state: $($manager.TetheringOperationalState)"

            $cfg = $manager.GetCurrentAccessPointConfiguration()
            $cfg.Ssid = $Ssid
            $cfg.Passphrase = $Passphrase
            Await-Action $manager.ConfigureAccessPointAsync($cfg)

            $op = Await-Operation $manager.StartTetheringAsync() $opResultType
            $status = "$($op.Status)"
            $extra = "$($op.AdditionalErrorMessage)"
            $attemptLog += "$($cand.Name)=$status"

            if ($status -eq 'Success') {
                $started = $true
                $result.profileUsed = $cand.Name
                $result.profileConnectivity = $cand.Level
                $result.startStatus = $status
                Write-Ok "StartTetheringAsync -> Success  from '$($cand.Name)'"
                if ($result.uplinkAtStart -eq 'none') { Write-Ok 'STARTED WITH NO NETWORK ATTACHED' }
                break
            }
            Write-Bad "    -> $status$(if ($extra) { "  ($extra)" })"
        } catch {
            $attemptLog += "$($cand.Name)=exception"
            Write-Bad "    -> threw: $($_.Exception.Message.Split([Environment]::NewLine)[0])"
        }
    }

    $result.attempts = ($attemptLog -join ', ')
    if (-not $started) {
        $result.startStatus = 'all-profiles-failed'
        Write-Host ''
        Write-Bad "NO profile could start tethering while offline. Attempts: $($result.attempts)"
        Write-Info 'With the radio confirmed On, this is a real answer: Mobile Hotspot needs more'
        Write-Info 'than a profile object - it needs a live connection to share. ZeroSetupPlan'
        Write-Info 'Open Question 2 then stands, and Wi-Fi Direct plus our own DHCP is the only'
        Write-Info 'remaining path to true offline play.'
        throw 'tethering did not start from any profile'
    }

    # ------------------------------------------------------------ STEP 4

    Write-Step 'STEP 4  Is the network it created actually usable by a phone?'
    Write-Info 'This is where Wi-Fi Direct fell down: AP up, address fine, DHCP fine, but no'
    Write-Info 'resolver - :53 free, no DNS handed out, Android looping on validation.'
    Write-Host ''

    $addr = $null
    for ($i = 0; $i -lt 30; $i++) {
        $addr = Get-NetIPAddress -AddressFamily IPv4 -ErrorAction SilentlyContinue |
            Where-Object { $_.IPAddress -like '192.168.137.*' } | Select-Object -First 1
        if ($addr) { break }
        Start-Sleep -Milliseconds 500
    }
    if ($addr) { $result.address = $addr.IPAddress; Write-Ok "hotspot address: $($addr.IPAddress)" }
    else { Write-Bad 'no 192.168.137.x address appeared' }

    # ICS brings its services up a few seconds after tethering reports Success. Checking once,
    # immediately, produces a false negative - and a false "DHCP is broken" would be a much
    # more damaging thing to record than the delay costs.
    Write-Info 'waiting up to 20s for ICS to bring up DHCP and DNS...'
    $dhcp = @(); $dns53 = @()
    for ($i = 0; $i -lt 40; $i++) {
        $dhcp  = @(Get-NetUDPEndpoint -LocalPort 67 -ErrorAction SilentlyContinue)
        $dns53 = @(Get-NetUDPEndpoint -LocalPort 53 -ErrorAction SilentlyContinue)
        if ($dhcp.Count -and $dns53.Count) { break }
        Start-Sleep -Milliseconds 500
    }

    $result.dhcpListening = ($dhcp.Count -gt 0)
    if ($dhcp.Count) { Write-Ok "DHCP listening on UDP :67" } else { Write-Bad 'nothing on UDP :67 after 20s' }

    # THE decisive comparison against the Wi-Fi Direct path.
    $result.icsDnsProxyOnPort53 = ($dns53.Count -gt 0)
    if ($dnsProc -and -not $dnsProc.HasExited) {
        $ours = @($dns53 | Where-Object { $_.OwningProcess -eq $dnsProc.Id })
        $theirs = @($dns53 | Where-Object { $_.OwningProcess -ne $dnsProc.Id })
        $result.dnsHeldByUs = ($ours.Count -gt 0)
        if ($ours.Count -and -not $theirs.Count) { Write-Ok 'OUR responder is the sole holder of :53 - ICS did not take it' }
        elseif ($ours.Count) { Write-Warn2 'our responder holds :53, but so does something else' }
        else { Write-Bad 'ICS took :53 - names would resolve via ICS, not via our responder' }
    }
    if ($dns53.Count) {
        foreach ($d in $dns53) {
            $o = Get-Process -Id $d.OwningProcess -ErrorAction SilentlyContinue
            Write-Ok ("UDP :53 held on {0} by {1} (pid {2})" -f $d.LocalAddress, $(if ($o) { $o.ProcessName } else { '?' }), $d.OwningProcess)
        }
        # Who holds it decides what this line means. With -Dns the holder is our own responder,
        # which is the goal; without it, the holder is ICS, which still gives clients a resolver.
        # Saying "ICS" either way would be wrong half the time.
        if ($result.dnsHeldByUs) {
            Write-Info 'Our responder answers for clients. ICS still hands DHCP clients this'
            Write-Info 'address as their resolver, so names reach us - which is the whole design.'
        } else {
            Write-Info 'ICS is running its own DNS proxy, so clients get a resolver - which is'
            Write-Info 'exactly what the Wi-Fi Direct network never provided. It will not answer'
            Write-Info 'for *.local-ip.sh offline though; that needs our responder (-Dns).'
        }
    } else {
        Write-Bad 'UDP :53 is free - ICS is NOT running a DNS proxy here either.'
        Write-Info 'Then the resolver problem is not specific to Wi-Fi Direct, and either path'
        Write-Info 'needs us to own DHCP and DNS ourselves.'
    }

    # ------------------------------------------------------------ STEP 5

    if ($KeepRunning) {
        Write-Step 'STEP 5  Join from a phone'

        if ($Serve) {
            $confirmJs = Join-Path $SpikeDir 'confirm-server.js'
            if (Test-Path $confirmJs) {
                $confirm = Start-Process -FilePath 'node' -ArgumentList @($confirmJs) -PassThru -NoNewWindow
                Write-Ok "confirm-server.js running (pid $($confirm.Id)) on port 80"
            } else { Write-Warn2 "confirm-server.js not found at $confirmJs" }
        } else {
            Write-Warn2 'no -Serve, so NOTHING is listening on port 80. The phone will hang and'
            Write-Warn2 'time out on scan 2, which says nothing about whether the network works.'
        }

        # An IP URL performs no DNS lookup, so it would pass whether or not resolution works.
        # With the responder up, scan 2 carries the NAME - the thing actually under test.
        $joinUrl = if ($dnsProc -and -not $dnsProc.HasExited) { 'http://play.local-ip.sh/' }
                   else { "http://$($result.address)/" }

        $qrJs = Join-Path $SpikeDir 'wifi-qr.js'
        if (Test-Path $qrJs) {
            & node $qrJs --ssid $Ssid --pass $Passphrase --url $joinUrl
        } else {
            Write-Warn2 "wifi-qr.js missing - network '$Ssid', password '$Passphrase', then $joinUrl"
        }

        Write-Host ''
        Write-Say ">>> Scan 1 to join, scan 2 to open $joinUrl"
        if ($joinUrl -match 'local-ip') {
            Write-Info 'Watch for confirm-server printing [BY NAME - this is the result].'
            Write-Info 'Reaching it by IP would mean DNS was skipped entirely.'
        }
        Write-Say '>>> Press any key here when done.'
        while (-not [Console]::KeyAvailable) { Start-Sleep -Milliseconds 500 }
        [Console]::ReadKey($true) | Out-Null
    }
}
catch { Write-Bad $_.Exception.Message }
finally {
    Write-Step 'Cleanup'
    if ($dnsProc) {
        Write-Step 'DNS - did the phone actually ask us?'
        try { $result.dnsQueries = Show-DnsQuerySummary } catch { Write-Warn2 $_.Exception.Message }
        try { Stop-Process -Id $dnsProc.Id -Force -ErrorAction SilentlyContinue; Write-Ok 'DNS responder stopped' } catch { }
    }
    if ($confirm) {
        try { Stop-Process -Id $confirm.Id -Force -ErrorAction SilentlyContinue; Write-Ok 'confirm-server stopped' } catch { }
    }
    if ($manager -and $started) {
        try {
            $opResultType = [Windows.Networking.NetworkOperators.NetworkOperatorTetheringOperationResult,Windows.Networking,ContentType=WindowsRuntime]
            $stop = Await-Operation $manager.StopTetheringAsync() $opResultType
            Write-Ok "tethering stopped ($($stop.Status))"
        } catch { Write-Warn2 "stop failed: $($_.Exception.Message)" }
    }
    Write-Step 'RESULT'
    Write-Host ''
    Write-Host ($result | ConvertTo-Json) -ForegroundColor White
    if ($WaitForOffline) { Write-Host "`n   TEST DONE - safe to plug the network back in." -ForegroundColor Yellow }
    if ($transcribing) { try { Stop-Transcript | Out-Null } catch { } }
}

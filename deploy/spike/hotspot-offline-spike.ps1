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

try { Start-Transcript -Path $LogPath -Force | Out-Null; $transcribing = $true }
catch { $transcribing = $false; Write-Warn2 "no transcript: $($_.Exception.Message)" }

$ni      = [Windows.Networking.Connectivity.NetworkInformation,Windows.Networking,ContentType=WindowsRuntime]
$mgrType = [Windows.Networking.NetworkOperators.NetworkOperatorTetheringManager,Windows.Networking,ContentType=WindowsRuntime]
$manager = $null
$started = $false
$result  = [ordered]@{
    profileUsed          = $null
    profileConnectivity  = $null
    startStatus          = $null
    address              = $null
    dhcpListening        = $null
    icsDnsProxyOnPort53  = $null
    uplinkAtStart        = $null
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
        Write-Say '>>> UNPLUG THE ETHERNET CABLE NOW. (leave Wi-Fi disconnected)'
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

    # Choosing a no-connectivity profile on purpose is what makes an ONLINE run informative:
    # if tethering starts from a profile that has no internet, "it needs internet" is disproved
    # without anyone touching a cable.
    $chosen = if ($PreferNoConnectivity) {
        $none = @($candidates | Where-Object { $_.Level -eq 'None' })
        if ($none.Count) { $none[0] } else { Write-Warn2 'no zero-connectivity profile available; falling back'; $candidates[0] }
    } else { $candidates[0] }

    $result.profileUsed = $chosen.Name
    $result.profileConnectivity = $chosen.Level

    Write-Step "STEP 3  Starting tethering from '$($chosen.Name)' (connectivity=$($chosen.Level))"
    if ($chosen.Level -eq 'None') {
        Write-Info 'This profile carries NO connectivity. If tethering starts anyway, the premise'
        Write-Info 'behind ZeroSetupPlan Open Question 2 is wrong.'
    }

    $manager = $mgrType::CreateFromConnectionProfile($chosen.Profile)
    Write-Info "current tethering state: $($manager.TetheringOperationalState)"

    $cfg = $manager.GetCurrentAccessPointConfiguration()
    $cfg.Ssid = $Ssid
    $cfg.Passphrase = $Passphrase
    Await-Action $manager.ConfigureAccessPointAsync($cfg)
    Write-Ok "configured SSID '$Ssid'"

    $opResultType = [Windows.Networking.NetworkOperators.NetworkOperatorTetheringOperationResult,Windows.Networking,ContentType=WindowsRuntime]
    $op = Await-Operation $manager.StartTetheringAsync() $opResultType
    $result.startStatus = "$($op.Status)"

    if ("$($op.Status)" -eq 'Success') {
        $started = $true
        Write-Ok "StartTetheringAsync -> Success"
        if ($result.uplinkAtStart -eq 'none') { Write-Ok 'STARTED WITH NO NETWORK ATTACHED' }
        elseif ($chosen.Level -eq 'None') { Write-Ok 'started from a profile with NO connectivity' }
    } else {
        Write-Bad "StartTetheringAsync -> $($op.Status)  $($op.AdditionalErrorMessage)"
        throw "tethering did not start ($($op.Status))"
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
    if ($dns53.Count) {
        foreach ($d in $dns53) {
            $o = Get-Process -Id $d.OwningProcess -ErrorAction SilentlyContinue
            Write-Ok ("UDP :53 held on {0} by {1} (pid {2})" -f $d.LocalAddress, $(if ($o) { $o.ProcessName } else { '?' }), $d.OwningProcess)
        }
        Write-Info 'ICS IS running its DNS proxy - so clients get a resolver, which is exactly'
        Write-Info 'what the Wi-Fi Direct network never provided.'
    } else {
        Write-Bad 'UDP :53 is free - ICS is NOT running a DNS proxy here either.'
        Write-Info 'Then the resolver problem is not specific to Wi-Fi Direct, and either path'
        Write-Info 'needs us to own DHCP and DNS ourselves.'
    }

    # ------------------------------------------------------------ STEP 5

    if ($KeepRunning) {
        Write-Step 'STEP 5  Join from a phone'
        $qrJs = Join-Path $SpikeDir 'wifi-qr.js'
        if (Test-Path $qrJs) {
            & node $qrJs --ssid $Ssid --pass $Passphrase --url "http://$($result.address)/"
        } else {
            Write-Warn2 "wifi-qr.js missing - network '$Ssid', password '$Passphrase'"
        }
        Write-Say '>>> Scan 1 to join, scan 2 to open. Press any key here when done.'
        while (-not [Console]::KeyAvailable) { Start-Sleep -Milliseconds 500 }
        [Console]::ReadKey($true) | Out-Null
    }
}
catch { Write-Bad $_.Exception.Message }
finally {
    Write-Step 'Cleanup'
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

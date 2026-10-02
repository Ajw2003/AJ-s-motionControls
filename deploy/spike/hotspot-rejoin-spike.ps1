#requires -Version 5.1
<#
.SYNOPSIS
    Spike (#71): with the host's internet shared, does a phone rejoin the hotspot by itself?

.DESCRIPTION
    Starts the game's hotspot, turns ICS forwarding back on so guests get real internet, then
    walks you through two rejoin tests on the phone:

      Test A  home Wi-Fi auto-reconnect OFF (a venue: no other Wi-Fi the phone knows)
      Test B  home Wi-Fi auto-reconnect ON  (your house: the phone already has Wi-Fi)

    While it waits for you it logs every change in the hotspot's client count, with a
    timestamp, so whether the phone came back is measured rather than remembered. Forwarding
    is re-enabled and logged if anything turns it off.

    Results go to deploy/spike/hotspot-rejoin-results.txt. The hotspot is stopped on the way
    out, whatever happens. Needs administrator rights (forwarding) and raises its own UAC
    prompt. Run with Unity NOT in Play mode. -DryRun prints the plan and changes nothing.

.EXAMPLE
    powershell -NoProfile -ExecutionPolicy Bypass -File deploy\spike\hotspot-rejoin-spike.ps1
#>
[CmdletBinding()]
param([switch] $DryRun)

Set-StrictMode -Version Latest
$root = Resolve-Path (Join-Path $PSScriptRoot '..\..')
$control = Join-Path $root 'hotspot\hotspot-control.ps1'
$results = Join-Path $PSScriptRoot 'hotspot-rejoin-results.txt'
$creds = Get-Content (Join-Path $root 'hotspot-credentials.json') -Raw | ConvertFrom-Json

if ($DryRun) {
    "DRY RUN - nothing will change."
    "  hotspot script : $control"
    "  network        : $($creds.ssid)"
    "  results file   : $results"
    return
}

$isAdmin = ([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole(
    [Security.Principal.WindowsBuiltInRole]::Administrator)
if (-not $isAdmin) {
    'Asking for administrator rights - approve the UAC prompt. The spike continues in that new window.'
    Start-Process powershell.exe -Verb RunAs -ArgumentList "-NoProfile -NoExit -ExecutionPolicy Bypass -File `"$PSCommandPath`""
    return
}

function Log([string] $line) {
    $stamped = "$(Get-Date -Format 'HH:mm:ss')  $line"
    Write-Host $stamped
    Add-Content $results $stamped
}

$ni = [Windows.Networking.Connectivity.NetworkInformation,Windows.Networking,ContentType=WindowsRuntime]
$mgrType = [Windows.Networking.NetworkOperators.NetworkOperatorTetheringManager,Windows.Networking,ContentType=WindowsRuntime]
$script:lastCount = -1

# One poll: log a client-count change, and put forwarding back if something turned it off.
function Watch-Once($manager, [int] $ifIndex) {
    $count = $manager.ClientCount
    if ($count -ne $script:lastCount) {
        Log "hotspot clients: $count"
        $script:lastCount = $count
    }
    if ((Get-NetIPInterface -InterfaceIndex $ifIndex -AddressFamily IPv4).Forwarding -ne 'Enabled') {
        Set-NetIPInterface -InterfaceIndex $ifIndex -AddressFamily IPv4 -Forwarding Enabled
        Log 'forwarding was off - turned it back on'
    }
}

# Keeps watching once a second until you answer, so joins are logged while you work the phone.
function Ask($manager, [int] $ifIndex, [string] $question) {
    Write-Host "`n$question (y/n)" -ForegroundColor Cyan
    while ($true) {
        Watch-Once $manager $ifIndex
        if ([Console]::KeyAvailable) {
            $key = [Console]::ReadKey($true).KeyChar.ToString().ToLower()
            if ($key -eq 'y' -or $key -eq 'n') {
                Log "$question -> $key"
                return
            }
        }
        Start-Sleep -Milliseconds 1000
    }
}

function Say([string] $text) { Write-Host "`n$text" -ForegroundColor Yellow }

Set-Content $results "Hotspot rejoin spike, $(Get-Date -Format 'yyyy-MM-dd HH:mm'), network '$($creds.ssid)'"

try {
    'Starting the hotspot the way the game does...'
    & powershell.exe -NoProfile -ExecutionPolicy Bypass -File $control -Action Start -Ssid $creds.ssid -Passphrase $creds.passphrase 2>$null | Out-Null

    $nic = Get-NetIPAddress -AddressFamily IPv4 -IPAddress 192.168.137.* -ErrorAction Stop | Select-Object -First 1
    Set-NetIPInterface -InterfaceIndex $nic.InterfaceIndex -AddressFamily IPv4 -Forwarding Enabled
    $manager = $mgrType::CreateFromConnectionProfile($ni::GetInternetConnectionProfile())
    Log "hotspot $($manager.TetheringOperationalState), forwarding Enabled (internet shared)"

    Say "SETUP. On the phone: Settings > Wi-Fi. If '$($creds.ssid)' is listed, tap its gear > Forget,
because it may be saved as 'no internet'. Then join '$($creds.ssid)' (password $($creds.passphrase)).
Leave mobile data ON."
    Ask $manager $nic.InterfaceIndex "Joined, and the phone shows it Connected with no 'no internet' warning"

    Say "TEST A - a venue. On the phone: tap your HOME Wi-Fi's gear > turn 'Auto reconnect' OFF.
Turn Wi-Fi OFF, wait 10 seconds, turn Wi-Fi ON, then put the phone down and touch nothing for 60 seconds.
Watch the 'hotspot clients' lines here."
    Ask $manager $nic.InterfaceIndex "Test A: did the phone rejoin '$($creds.ssid)' by itself"

    Say "TEST B - your house. On the phone: turn your HOME Wi-Fi's 'Auto reconnect' back ON.
Turn Wi-Fi OFF, wait 10 seconds, turn Wi-Fi ON, touch nothing for 60 seconds, then look at which
network it joined."
    Ask $manager $nic.InterfaceIndex "Test B: did it pick '$($creds.ssid)' over your home Wi-Fi"
}
catch {
    Log "FAILED: $($_.Exception.Message)"
}
finally {
    & powershell.exe -NoProfile -ExecutionPolicy Bypass -File $control -Action Stop 2>$null | Out-Null
    Log 'cleanup: hotspot stopped'
    "`nDone. Hotspot stopped. Results saved to $results - you can close this window."
}

<#
.SYNOPSIS
  One command that runs the whole captive-portal test: restarts the relay onto current code,
  waits until it is provably the new build, captures packets, and prints the verdict.

.DESCRIPTION
  This exists because the test had grown to three terminals, two shells and a log grep, and the
  most likely outcome of that is a step done out of order producing a result that means nothing.
  Every check that a human was doing by eye is done here instead:

    1. Kill the running relay. LocalServices reuses any relay already listening
       (LocalServices.cs, StartRelay), so without this, pressing Play serves the OLD code and
       the test silently measures the wrong build - which has already happened twice.
    2. Wait until the relay is back AND is demonstrably the configured one. The marker is
       www.google.com resolving to a public address, which the relay can only do by forwarding
       upstream. The old configuration answers NXDOMAIN, so this cannot pass against a stale
       relay however the restart went.
    3. Capture packets and report the verdict, by handing off to capture-probe.ps1.

  Only step 3 needs Administrator, so the prompts and the waiting happen in this window where
  they can be read, and the UAC prompt arrives once, at the end, for the capture alone.

.PARAMETER Seconds
  Capture duration, passed through to capture-probe.ps1.

.PARAMETER HotspotIp
  The host's address on the hotspot: the DNS server to ask, and the capture filter.

.PARAMETER RestartTimeout
  How long to wait for the relay to come back after Play is pressed.

.EXAMPLE
  .\test-portal-fix.ps1
#>

[CmdletBinding()]
param(
    [int]$Seconds = 90,
    [string]$HotspotIp = '192.168.137.1',
    [int]$RestartTimeout = 180
)

$ErrorActionPreference = 'Continue'

# The name that distinguishes a relay running current code from a stale one. www.google.com is
# in CAPTIVE_PROBE_DOMAINS as of the isolation fix, and only for that build, so the answer says
# which code is running:
#
#   192.168.137.1     current build. Android's HTTPS probe now reaches our :443 and is refused,
#                     which is a clean failure it can act on - unlike NXDOMAIN, which it cannot.
#   NXDOMAIN          stale relay: the name is not being served at all.
#   any other address a DNS_UPSTREAM override is still in place. relay-env.txt should be gone
#                     for an isolation test, because forwarding upstream is the opposite of it.
$markerName = 'www.google.com'

# The address prefix that identifies a hotspot guest, derived from the hotspot address so the
# two cannot drift apart. Guarded, because an empty value here silently widens every lookup
# below from "the hotspot interface" to "every interface on this machine" - which reported the
# uplink's own forwarding as a hotspot isolation failure until it was caught.
$guestPrefix = $HotspotIp -replace '\.\d+$', '.'

if ([string]::IsNullOrWhiteSpace($guestPrefix) -or $guestPrefix -eq $HotspotIp) {
    Write-Host "Could not derive a guest prefix from HotspotIp '$HotspotIp'." -ForegroundColor Red
    Write-Host 'Expected something like 192.168.137.1. Nothing was changed.'
    exit 1
}

<#
  Asks the relay's own resolver for a name and returns the address it answered, or $null.

  Deliberately -DnsOnly -NoHostsFile so nothing local can satisfy the query and make a dead
  relay look alive, and -QuickTimeout so polling stays responsive.
#>
function Resolve-ViaRelay {
    param(
        [Parameter(Mandatory)][string]$Name,
        [Parameter(Mandatory)][string]$Server
    )

    try {
        $answer = Resolve-DnsName -Server $Server -Name $Name -Type A -DnsOnly -NoHostsFile `
            -QuickTimeout -ErrorAction Stop
        return ($answer | Where-Object { $_.IPAddress } | Select-Object -First 1).IPAddress
    } catch {
        return $null
    }
}

Write-Host ''
Write-Host '=== Captive-portal fix test ===' -ForegroundColor Cyan
Write-Host ''

# --- 1. Kill the relay ---------------------------------------------------------------------

$relays = @(Get-CimInstance Win32_Process -Filter "Name='node.exe'" |
    Where-Object { $_.CommandLine -like '*server.js*' })

if ($relays.Count -gt 0) {
    foreach ($relay in $relays) {
        Stop-Process -Id $relay.ProcessId -Force -ErrorAction SilentlyContinue
    }
    Write-Host "Killed $($relays.Count) running relay process(es)."
} else {
    Write-Host 'No relay was running.'
}

# --- 2. Wait for the new build -------------------------------------------------------------

Write-Host ''
Write-Host 'Now press Stop and then Play in Unity.' -ForegroundColor Yellow
Write-Host "Waiting up to $RestartTimeout s for the relay to come back on current code..."

$deadline = (Get-Date).AddSeconds($RestartTimeout)
$live = $false

$answer = $null

while ((Get-Date) -lt $deadline) {
    $answer = Resolve-ViaRelay -Name $markerName -Server $HotspotIp
    if ($answer -eq $HotspotIp) {
        $live = $true
        break
    }
    Start-Sleep -Seconds 2
}

if (-not $live) {
    Write-Host ''
    Write-Host "TIMED OUT. $markerName does not resolve to $HotspotIp." -ForegroundColor Red
    if ($answer) {
        Write-Host "It answered $answer, so a DNS_UPSTREAM override is still forwarding."
        Write-Host 'Delete relay-env.txt at the repo root if it is still there - upstream'
        Write-Host 'forwarding defeats isolation. If it is already gone, the running relay'
        Write-Host 'simply predates its removal and a restart is all that is needed.'
    } else {
        Write-Host 'Either Play was not pressed, or the relay that started is stale code.'
    }
    Write-Host 'Nothing was captured, because a capture against the old build proves nothing.'
    return
}

Write-Host ''
Write-Host "Relay is up on current code ($markerName -> $answer)." -ForegroundColor Green

# --- 2b. Isolation ---------------------------------------------------------------------

# The whole point of this configuration is that guests have NO internet. ICS re-enables
# forwarding on every tethering start and the disable task is asynchronous, so this is a race
# that has silently been lost before - see the decision section in
# docs/plans/join-failure-reduction.md. Read the fact rather than assume it, and rather than
# print an instruction: the task is idempotent and needs no elevation to start, so losing the
# race is something to fix here, not to hand back to whoever ran this.
function Get-ForwardingInterfaces {
    param([Parameter(Mandatory)][string]$Prefix)

    return @(
        Get-NetIPAddress -AddressFamily IPv4 -IPAddress "$Prefix*" -ErrorAction SilentlyContinue |
            ForEach-Object {
                Get-NetIPInterface -InterfaceIndex $_.InterfaceIndex -AddressFamily IPv4 `
                    -ErrorAction SilentlyContinue
            } |
            Where-Object { $_.Forwarding -eq 'Enabled' }
    )
}

$forwarding = Get-ForwardingInterfaces -Prefix $guestPrefix

if ($forwarding.Count -gt 0) {
    $names = ($forwarding | ForEach-Object { $_.InterfaceAlias }) -join ', '
    Write-Host "Forwarding is Enabled on $names - disabling it." -ForegroundColor Yellow

    try {
        Start-ScheduledTask -TaskName 'RockSkippingDisableForwarding' -ErrorAction Stop
    } catch {
        Write-Host ''
        Write-Host "Could not start the disable task: $($_.Exception.Message)" -ForegroundColor Red
        Write-Host 'Run deploy/setup-offline.ps1 to register it, then try again.'
        return
    }

    $deadline = (Get-Date).AddSeconds(15)
    do {
        Start-Sleep -Milliseconds 500
        $forwarding = Get-ForwardingInterfaces -Prefix $guestPrefix
    } while ($forwarding.Count -gt 0 -and (Get-Date) -lt $deadline)
}

if ($forwarding.Count -gt 0) {
    $names = ($forwarding | ForEach-Object { $_.InterfaceAlias }) -join ', '
    Write-Host ''
    Write-Host "NOT ISOLATED: forwarding is still Enabled on $names." -ForegroundColor Red
    Write-Host 'The disable task ran and did not take. Guests can still reach the internet, so'
    Write-Host 'this would not be testing the offline case. Nothing was captured.'
    return
}

Write-Host 'Isolation confirmed: forwarding is Disabled on the hotspot interface.' -ForegroundColor Green
# --- 3. Capture ------------------------------------------------------------------------

Write-Host ''
Write-Host 'Make sure the phone is joined to the hotspot, then leave it alone.'
Write-Host "Starting the $Seconds s packet capture - accept the UAC prompt."
Write-Host ''

& (Join-Path $PSScriptRoot 'capture-probe.ps1') -Seconds $Seconds -HotspotIp $HotspotIp

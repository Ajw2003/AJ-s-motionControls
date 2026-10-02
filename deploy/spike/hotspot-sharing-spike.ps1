#requires -Version 5.1
<#
.SYNOPSIS
    Spike (#71): can guests use the host's internet while being blocked from the home network?

.DESCRIPTION
    Starts the game's hotspot, then turns ICS forwarding back on so phones get real internet.
    It then asks you to test from a phone in two stages:

      Stage 1  forwarding on, no block   - the baseline. Expect: websites AND the router load.
      Stage 2  forwarding on, block rule - expect: websites load, the router does NOT.

    The block is a Windows Firewall inbound rule on the hotspot interface. It covers traffic
    from guests to private address ranges, excluding the hotspot's own 192.168.137.0/24. The
    open question is whether Windows Firewall filters routed (ICS) traffic at all.

    Answers are written to deploy/spike/hotspot-sharing-results.txt. Whatever happens, the rule
    is removed and the hotspot is stopped on the way out.

    Needs administrator rights (forwarding and firewall changes) and raises its own UAC
    prompt. Run with Unity NOT in Play mode. -DryRun prints the plan and changes nothing.

.EXAMPLE
    powershell -NoProfile -ExecutionPolicy Bypass -File deploy\spike\hotspot-sharing-spike.ps1
#>
[CmdletBinding()]
param([switch] $DryRun)

Set-StrictMode -Version Latest
$root = Resolve-Path (Join-Path $PSScriptRoot '..\..')
$control = Join-Path $root 'hotspot\hotspot-control.ps1'
$results = Join-Path $PSScriptRoot 'hotspot-sharing-results.txt'
$ruleName = 'RockSkipping SPIKE block guests from home network'
$privateRanges = @('10.0.0.0/8', '172.16.0.0/12', '192.168.0.0-192.168.136.255', '192.168.138.0-192.168.255.255')

$router = (Get-NetIPConfiguration | Where-Object { $_.IPv4DefaultGateway } | Select-Object -First 1).IPv4DefaultGateway.NextHop

if ($DryRun) {
    "DRY RUN - nothing will change."
    "  hotspot script : $control"
    "  router to test : http://$router"
    "  block rule     : '$ruleName', inbound on the hotspot interface, local addresses $($privateRanges -join ', ')"
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

function Ask([string] $question) {
    do { $a = (Read-Host "$question (y/n)").Trim().ToLower() } while ($a -ne 'y' -and $a -ne 'n')
    Add-Content $results "$question -> $a"
    return $a
}

Set-Content $results "Hotspot sharing spike, $(Get-Date -Format 'yyyy-MM-dd HH:mm'), router http://$router"
$creds = Get-Content (Join-Path $root 'hotspot-credentials.json') -Raw | ConvertFrom-Json

try {
    'Starting the hotspot the way the game does...'
    & powershell.exe -NoProfile -ExecutionPolicy Bypass -File $control -Action Start -Ssid $creds.ssid -Passphrase $creds.passphrase 2>$null | Out-Null

    $nic = Get-NetIPAddress -AddressFamily IPv4 -IPAddress 192.168.137.* -ErrorAction Stop | Select-Object -First 1
    $alias = (Get-NetIPInterface -InterfaceIndex $nic.InterfaceIndex -AddressFamily IPv4).InterfaceAlias
    Set-NetIPInterface -InterfaceIndex $nic.InterfaceIndex -AddressFamily IPv4 -Forwarding Enabled
    Add-Content $results "hotspot interface: $alias, forwarding Enabled"

    ''
    "=== STAGE 1 - sharing on, no block. Join '$($creds.ssid)' from the phone (mobile data OFF)."
    "    On the phone, open https://example.com and then http://$router"
    Ask 'Stage 1: did https://example.com load' | Out-Null
    Ask "Stage 1: did the router page http://$router load" | Out-Null

    New-NetFirewallRule -DisplayName $ruleName -Direction Inbound -Action Block -InterfaceAlias $alias `
        -RemoteAddress '192.168.137.0/24' -LocalAddress $privateRanges | Out-Null
    Add-Content $results "block rule added"

    ''
    '=== STAGE 2 - block rule on. On the phone, reload both pages (close the old tabs first).'
    Ask 'Stage 2: did https://example.com load' | Out-Null
    Ask "Stage 2: did the router page http://$router load" | Out-Null
}
catch {
    "FAILED: $($_.Exception.Message)"
    Add-Content $results "FAILED: $($_.Exception.Message)"
}
finally {
    Remove-NetFirewallRule -DisplayName $ruleName -ErrorAction SilentlyContinue
    & powershell.exe -NoProfile -ExecutionPolicy Bypass -File $control -Action Stop 2>$null | Out-Null
    Add-Content $results 'cleanup: rule removed, hotspot stopped'
    ''
    "Done. Rule removed, hotspot stopped. Results saved to $results"
}

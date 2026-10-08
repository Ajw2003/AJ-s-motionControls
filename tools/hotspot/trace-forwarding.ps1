#requires -Version 5.1
<#
.SYNOPSIS
    Start the hotspot the way the game does and print a timeline of the forwarding flag.

.DESCRIPTION
    Diagnoses the "forwarding-still-enabled" start failure
    (docs/Findings-HotspotForwardingAndInternet-2026-10-01.md). It does four things:

    1. Runs hotspot/hotspot-control.ps1 -Action Start in a background job, with the saved
       hotspot-credentials.json.
    2. Samples the 192.168.137.* interface's Forwarding flag every 250 ms for -Seconds.
    3. Records when the RockSkippingDisableForwarding task last ran.
    4. Stops the hotspot again, whatever happened.

    Run it with the game NOT in Play mode, or the two will fight over the hotspot. It needs no
    elevation; the task does the elevated part.

.EXAMPLE
    powershell -NoProfile -ExecutionPolicy Bypass -File tools\hotspot\trace-forwarding.ps1
#>
[CmdletBinding()]
param([int] $Seconds = 20)

Set-StrictMode -Version Latest
$root = Resolve-Path (Join-Path $PSScriptRoot '..\..')
$control = Join-Path $root 'hotspot\hotspot-control.ps1'
# Where the game saves it since #81 (Application.persistentDataPath, from ProjectSettings'
# companyName and productName); beside the project before that.
$credsPath = Join-Path $env:USERPROFILE 'AppData\LocalLow\DefaultCompany\RockSkipping\hotspot-credentials.json'
if (-not (Test-Path $credsPath)) { $credsPath = Join-Path $root 'hotspot-credentials.json' }
$creds = Get-Content $credsPath -Raw | ConvertFrom-Json

$started = Get-Date
$job = Start-Job -ScriptBlock {
    param($script, $ssid, $pass)
    & powershell.exe -NoProfile -ExecutionPolicy Bypass -File $script -Action Start -Ssid $ssid -Passphrase $pass 2>&1
} -ArgumentList $control, $creds.ssid, $creds.passphrase

$last = $null
try {
    while ((Get-Date) -lt $started.AddSeconds($Seconds)) {
        $state = 'no 192.168.137.x address'
        $ip = @(Get-NetIPAddress -AddressFamily IPv4 -IPAddress 192.168.137.* -ErrorAction SilentlyContinue)
        if ($ip.Count -gt 0) {
            $nic = Get-NetIPInterface -InterfaceIndex $ip[0].InterfaceIndex -AddressFamily IPv4 -ErrorAction SilentlyContinue
            $state = "$($nic.InterfaceAlias): Forwarding $($nic.Forwarding)"
        }
        # Only print changes, so the timeline reads as events rather than 80 identical lines.
        if ($state -ne $last) {
            '{0,6:N2}s  {1}' -f ((Get-Date) - $started).TotalSeconds, $state
            $last = $state
        }
        Start-Sleep -Milliseconds 250
    }

    $info = Get-ScheduledTask -TaskName RockSkippingDisableForwarding | Get-ScheduledTaskInfo
    ''
    'disable task last ran at {0,6:N2}s (result 0x{1:X})' -f ($info.LastRunTime - $started).TotalSeconds, $info.LastTaskResult
    ''
    '--- hotspot-control.ps1 output'
    Wait-Job $job -Timeout 30 | Out-Null
    Receive-Job $job | ForEach-Object { "  $_" }
}
finally {
    Remove-Job $job -Force -ErrorAction SilentlyContinue
    & powershell.exe -NoProfile -ExecutionPolicy Bypass -File $control -Action Stop 2>$null | Out-Null
    '--- hotspot stopped'
}

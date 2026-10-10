#requires -Version 5.1
<#
.SYNOPSIS
    Lets phones on the hotspot reach AJ Motion through Windows Firewall (#25).

.DESCRIPTION
    Run elevated by the installer. Adds three inbound rules: TCP 8443 (the controller page),
    TCP 80 (phone connectivity checks) and UDP 53 (the relay's DNS answers). Idempotent: an
    existing rule with the same name is replaced, never duplicated. -Remove deletes every
    rule whose name starts with "AJ Motion ", which the uninstaller runs.
#>
[CmdletBinding()]
param([switch] $Remove)
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$Rules = @(
    @{ Name = 'AJ Motion TCP 8443'; Protocol = 'TCP'; Port = 8443 },
    @{ Name = 'AJ Motion TCP 80'; Protocol = 'TCP'; Port = 80 },
    @{ Name = 'AJ Motion UDP 53'; Protocol = 'UDP'; Port = 53 }
)

Get-NetFirewallRule -DisplayName 'AJ Motion *' -ErrorAction SilentlyContinue | Remove-NetFirewallRule
if ($Remove) {
    Write-Output 'Removed the AJ Motion firewall rules.'
    return
}
foreach ($rule in $Rules) {
    New-NetFirewallRule -DisplayName $rule.Name -Direction Inbound -Action Allow -Profile Any `
        -Protocol $rule.Protocol -LocalPort $rule.Port | Out-Null
    Write-Output "Added $($rule.Name)."
}

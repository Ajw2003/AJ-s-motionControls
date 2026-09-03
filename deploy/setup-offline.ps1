#requires -Version 5.1
<#
.SYNOPSIS
    One-time machine setup for offline Hotspot mode. Idempotent; safe to re-run.

.DESCRIPTION
    Offline play needs three things this game cannot do for itself, because all three are
    machine-wide and require Administrator:

      1. A scheduled task that disables IP forwarding on the hotspot interface, so guests are
         isolated from each other and from anything else this machine can reach. ICS re-enables
         forwarding on every tethering start, so this runs per session - the task exists to do
         that without a UAC prompt at party time.
      2. Firewall rules for the relay (TCP 8443) and the DNS responder (UDP 53).
      3. A hosts entry so the HOST can resolve its own relay. Unity connects to
         wss://192-168-137-1.local-ip.sh:8443; with no network there is no resolver to answer,
         and the game fails while the hotspot itself is working perfectly.

    Everything else - Node, npm install, the certificate, the network mode - needs no elevation
    and is only checked here, not changed.

    See docs/OfflineSetup.md for the reasoning behind each item, and
    docs/Decision-OfflineHosting.md for how offline hosting was proven.

    IDEMPOTENT BY DESIGN, because this gets re-run on machines in every half-configured state.
    Register-ScheduledTask -Force is naturally idempotent; New-NetFirewallRule and Add-Content
    are emphatically not - both cheerfully create duplicates, and both already have on the
    development machine. Every step here checks first, repairs what is wrong, and says which of
    the two it did.

.PARAMETER Uninstall
    Remove everything this script adds, so a test machine can be returned to clean. A hosts
    entry nobody remembers adding is exactly what derails a later debugging session.

.PARAMETER DryRun
    Report what would change without changing anything. Needs no elevation, because it writes
    nothing - which makes it the right way to check a machine's state before committing to it.

.EXAMPLE
    ./setup-offline.ps1 -DryRun
.EXAMPLE
    ./setup-offline.ps1
.EXAMPLE
    ./setup-offline.ps1 -Uninstall
#>
[CmdletBinding()]
param(
    [switch] $Uninstall,
    [switch] $DryRun
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

# ---------------------------------------------------------------- constants

# Fixed, not discovered. Windows ICS always puts the hotspot on 192.168.137.1, LocalServices
# forces the certificate to that address in Hotspot mode, and the firewall rule and DNS
# responder are both scoped to the matching /24. One address, asserted in one place.
$HotspotAddress   = '192.168.137.1'
$HotspotSubnet    = '192.168.137.0/24'
$RelayPort        = 8443
$ForwardingTask   = 'RockSkippingDisableForwarding'
$HostsPath        = Join-Path $env:SystemRoot 'System32\drivers\etc\hosts'

# The certificate is single-name now (play.skiprocking.ca, renewed by
# .github/workflows/renew-cert.yml), not a wildcard keyed to this machine's address — see
# docs/plans/magic-domain-replacement.md. Reads the same optional override file
# Assets/Scripts/Net/MagicDomain.cs does, sited the same way as relay-url.txt/
# network-mode.txt, so hand-editing one file repoints both the game and this script
# instead of the literal drifting out of sync with C# the way $RelayHostName used to.
function Get-MagicDomainHostName {
    $default = 'play.skiprocking.ca'
    $root = Split-Path -Parent $PSScriptRoot
    $path = Join-Path $root 'magic-domain.txt'

    if (-not (Test-Path $path)) { return $default }

    try {
        # Line 1 is the hostname; any further lines are cert/key URL pairs that only
        # MagicDomain.cs needs. Never throws — a malformed file falls back to the
        # compiled default exactly like MagicDomain.cs's own Load() does.
        $lines = @(Get-Content $path -ErrorAction Stop |
            ForEach-Object { $_.Trim() } |
            Where-Object { $_.Length -gt 0 -and $_[0] -ne '#' })
        if ($lines.Count -eq 0) { return $default }
        return $lines[0]
    } catch {
        Write-Warning "Could not read magic-domain.txt, using the default: $($_.Exception.Message)"
        return $default
    }
}

$RelayHostName    = Get-MagicDomainHostName

$FirewallRules = @(
    @{ Name = 'RockSkipping relay';       Protocol = 'TCP'; Port = "$RelayPort"; Remote = 'LocalSubnet'
       Why = 'phones reach the relay and the controller page' }
    @{ Name = 'RockSkipping hotspot DNS'; Protocol = 'UDP'; Port = '53';         Remote = $HotspotSubnet
       Why = 'phones resolve the relay address; hotspot subnet only, never a normal LAN' }
    @{ Name = 'RockSkipping captive portal'; Protocol = 'TCP'; Port = '80';      Remote = $HotspotSubnet
       Why = 'answers Android/iOS captive-portal probes so phones do not flag "No internet"; hotspot subnet only, see docs/plans/magic-domain-replacement.md Phase 4' }
)

# Added by the spikes, never used by the game. Removed so a machine set up by this script does
# not carry a wider firewall surface than it needs.
$ObsoleteRules = @('RockSkipping spike HTTP', 'RockSkipping spike DNS')

# ---------------------------------------------------------------- output

$script:Changed = 0
$script:Already = 0
$script:Failed  = 0

function Write-Step   { param([string] $m) Write-Host "`n=== $m" -ForegroundColor Cyan }
function Write-Change { param([string] $m) $script:Changed++; Write-Host "   [changed] $m" -ForegroundColor Yellow }
function Write-Same   { param([string] $m) $script:Already++; Write-Host "   [ok]      $m" -ForegroundColor Green }
function Write-Bad    { param([string] $m) $script:Failed++;  Write-Host "   [FAIL]    $m" -ForegroundColor Red }
function Write-Info   { param([string] $m) Write-Host "            $m" -ForegroundColor Gray }
function Write-Would  { param([string] $m) $script:Changed++; Write-Host "   [would]   $m" -ForegroundColor Magenta }

function Set-Or-Would {
    <# Every mutation goes through here, so -DryRun cannot be forgotten at one call site. #>
    param([string] $Description, [scriptblock] $Action)
    if ($DryRun) { Write-Would $Description; return }
    & $Action
    Write-Change $Description
}

# ---------------------------------------------------------------- elevation

function Test-Elevated {
    $id = [Security.Principal.WindowsIdentity]::GetCurrent()
    (New-Object Security.Principal.WindowsPrincipal $id).IsInRole(
        [Security.Principal.WindowsBuiltInRole]::Administrator)
}

# ---------------------------------------------------------------- steps

function Sync-ForwardingTask {
    Write-Step "Scheduled task '$ForwardingTask'"
    Write-Info 'Disables IP forwarding on the hotspot interface each session, unelevated at run'
    Write-Info 'time. Without it the hotspot is up but NOT isolated.'

    $existing = Get-ScheduledTask -TaskName $ForwardingTask -ErrorAction SilentlyContinue
    if ($existing) {
        Write-Same "present (state $($existing.State))"
        return
    }

    Set-Or-Would "registered scheduled task '$ForwardingTask'" {
        # The action's command is fixed with no parameters. A task that runs elevated and takes
        # an argument from its caller is a local privilege-escalation vector, so the interface
        # match is hardcoded here rather than passed in.
        $argument = @'
-NoProfile -WindowStyle Hidden -Command "Get-NetIPAddress -AddressFamily IPv4 -IPAddress 192.168.137.* -ErrorAction SilentlyContinue | ForEach-Object { Set-NetIPInterface -InterfaceIndex $_.InterfaceIndex -AddressFamily IPv4 -Forwarding Disabled }"
'@
        $action = New-ScheduledTaskAction -Execute 'powershell.exe' -Argument $argument
        # S4U: runs elevated as this user with no stored password and no prompt at trigger time.
        $principal = New-ScheduledTaskPrincipal -UserId "$env:USERDOMAIN\$env:USERNAME" `
            -RunLevel Highest -LogonType S4U
        Register-ScheduledTask -TaskName $ForwardingTask -Action $action -Principal $principal -Force | Out-Null
    }
}

function Sync-FirewallRule {
    param([hashtable] $Spec)

    $all = @(Get-NetFirewallRule -DisplayName $Spec.Name -ErrorAction SilentlyContinue)

    # Duplicates first. New-NetFirewallRule does not merge, so a script run twice leaves two
    # identical rules - already true on this machine for two of them.
    if ($all.Count -gt 1) {
        Set-Or-Would "removed $($all.Count - 1) duplicate '$($Spec.Name)' rule(s)" {
            $all | Select-Object -Skip 1 | Remove-NetFirewallRule
        }
        $all = @($all | Select-Object -First 1)
    }

    if ($all.Count -eq 1) {
        $rule = $all[0]
        $port = $rule | Get-NetFirewallPortFilter -ErrorAction SilentlyContinue
        $addr = $rule | Get-NetFirewallAddressFilter -ErrorAction SilentlyContinue

        $portOk  = ("$($port.Protocol)" -eq $Spec.Protocol) -and ("$($port.LocalPort)" -eq $Spec.Port)
        $allowOk = ("$($rule.Action)" -eq 'Allow') -and ("$($rule.Enabled)" -eq 'True') -and
                   ("$($rule.Direction)" -eq 'Inbound')
        # Windows normalises RemoteAddress ('192.168.137.0/24' comes back as a range), so match
        # loosely on the network rather than on an exact string that varies by OS build.
        $remote  = "$($addr.RemoteAddress)"
        $remoteOk = if ($Spec.Remote -eq 'LocalSubnet') { $remote -match 'LocalSubnet' }
                    else { $remote -match '192\.168\.137\.' }

        if ($portOk -and $allowOk -and $remoteOk) {
            Write-Same "'$($Spec.Name)' correct ($($Spec.Protocol)/$($Spec.Port), remote $remote)"
            return
        }

        Set-Or-Would "replaced '$($Spec.Name)' (was $($port.Protocol)/$($port.LocalPort), remote $remote, $($rule.Action)/$($rule.Enabled))" {
            $rule | Remove-NetFirewallRule
            New-NetFirewallRule -DisplayName $Spec.Name -Direction Inbound -Protocol $Spec.Protocol `
                -LocalPort $Spec.Port -RemoteAddress $Spec.Remote -Action Allow -Profile Any | Out-Null
        }
        return
    }

    Set-Or-Would "created '$($Spec.Name)' ($($Spec.Protocol)/$($Spec.Port), remote $($Spec.Remote))" {
        New-NetFirewallRule -DisplayName $Spec.Name -Direction Inbound -Protocol $Spec.Protocol `
            -LocalPort $Spec.Port -RemoteAddress $Spec.Remote -Action Allow -Profile Any | Out-Null
    }
}

function Remove-ObsoleteRules {
    foreach ($name in $ObsoleteRules) {
        $found = @(Get-NetFirewallRule -DisplayName $name -ErrorAction SilentlyContinue)
        if (-not $found.Count) { continue }
        Set-Or-Would "removed $($found.Count) obsolete spike rule(s) '$name'" {
            $found | Remove-NetFirewallRule
        }
    }
}

function Get-HostsLines {
    if (-not (Test-Path $HostsPath)) { return @() }
    @(Get-Content -Path $HostsPath -ErrorAction Stop)
}

function Sync-HostsEntry {
    Write-Step "Hosts entry  $HotspotAddress  $RelayHostName"
    Write-Info 'The host does not take DHCP from itself, so it never consults the DNS responder'
    Write-Info 'running on its own :53. Offline, without this line, Unity cannot resolve the'
    Write-Info 'relay it just started - the symptom that looked like "hotspot mode is broken".'
    Write-Info ''
    Write-Info 'Unlike the old local-ip.sh name, this hostname does not encode 192.168.137.1 in'
    Write-Info 'itself - it is an ordinary domain (play.skiprocking.ca by default), so this'
    Write-Info 'entry is what makes it resolve to the hotspot address at all when this machine'
    Write-Info 'has no internet to ask a real resolver. It only ever matters in Hotspot mode,'
    Write-Info 'where the address is the fixed constant above - this would be wrong to add for'
    Write-Info 'LAN mode, whose address varies per network.'

    $lines = Get-HostsLines
    # Match the hostname, not the whole line: whitespace and comments vary, and a stale entry
    # pointing somewhere else must be found and corrected, not duplicated alongside.
    $matching = @($lines | Where-Object { $_ -match [regex]::Escape($RelayHostName) -and $_ -notmatch '^\s*#' })
    $desired = "$HotspotAddress  $RelayHostName"

    if ($matching.Count -eq 1 -and $matching[0].Trim() -eq $desired) {
        Write-Same 'present and correct'
        return
    }

    $what = if ($matching.Count -eq 0) { 'added hosts entry' }
            elseif ($matching.Count -gt 1) { "collapsed $($matching.Count) hosts entries into one" }
            else { "corrected hosts entry (was '$($matching[0].Trim())')" }

    Set-Or-Would $what {
        # Back up before touching a system file that breaks name resolution machine-wide if
        # mangled. Timestamped by content, not by clock, so re-runs do not litter.
        $backup = "$HostsPath.rockskipping.bak"
        if (-not (Test-Path $backup)) { Copy-Item -Path $HostsPath -Destination $backup -Force }

        $kept = $lines | Where-Object { -not ($_ -match [regex]::Escape($RelayHostName) -and $_ -notmatch '^\s*#') }
        $out = @($kept) + @($desired)
        Set-Content -Path $HostsPath -Value $out -Encoding ASCII
    }
}

function Remove-HostsEntry {
    Write-Step "Hosts entry for $RelayHostName"
    $lines = Get-HostsLines
    $matching = @($lines | Where-Object { $_ -match [regex]::Escape($RelayHostName) -and $_ -notmatch '^\s*#' })
    if (-not $matching.Count) { Write-Same 'not present'; return }

    Set-Or-Would "removed $($matching.Count) hosts entr(y/ies)" {
        $kept = $lines | Where-Object { -not ($_ -match [regex]::Escape($RelayHostName) -and $_ -notmatch '^\s*#') }
        Set-Content -Path $HostsPath -Value $kept -Encoding ASCII
    }
}

# ---------------------------------------------------------------- checks (no elevation)

function Test-Prerequisites {
    Write-Step 'Prerequisites this script does NOT change'

    # $PSScriptRoot, not $MyInvocation - inside a function the latter describes the function,
    # not the script, and silently yields an empty path.
    $root = Split-Path -Parent $PSScriptRoot

    $node = Get-Command node -ErrorAction SilentlyContinue
    if ($node) {
        $v = (& node --version) 2>&1
        Write-Same "node $v ($($node.Source))"
    } else {
        Write-Bad 'node not found on PATH - the relay cannot start. Install Node 20 or newer.'
    }

    $modules = Join-Path $root 'relay\node_modules'
    if (Test-Path $modules) { Write-Same 'relay/node_modules present' }
    else { Write-Bad "relay/node_modules missing - run: npm install --prefix `"$(Join-Path $root 'relay')`"" }

    $modeFile = Join-Path $root 'network-mode.txt'
    if (Test-Path $modeFile) {
        $mode = (Get-Content $modeFile -Raw).Trim()
        if ($mode -eq 'Hotspot') { Write-Same "network-mode.txt = Hotspot" }
        else { Write-Info "network-mode.txt = '$mode' - offline play needs Hotspot (pick it on the main menu)" }
    } else {
        Write-Info 'network-mode.txt absent - the main menu writes it on first launch'
    }

    # The certificate is cached and works offline, but only until it expires, and nothing warns
    # about that at a party. Surfacing it here is the cheapest place to notice.
    $certPath = Join-Path $env:LOCALAPPDATA '..\LocalLow\DefaultCompany\RockSkipping\local-https\server.pem'
    if (Test-Path $certPath) {
        try {
            $cert = New-Object System.Security.Cryptography.X509Certificates.X509Certificate2 (Resolve-Path $certPath)
            $days = [int]($cert.NotAfter - (Get-Date)).TotalDays
            if ($days -gt 14) { Write-Same "certificate valid $days more days (to $($cert.NotAfter.ToString('yyyy-MM-dd')))" }
            else { Write-Bad "certificate expires in $days days - connect this machine to the internet once to renew" }
        } catch { Write-Info "could not read the cached certificate: $($_.Exception.Message)" }
    } else {
        Write-Info 'no cached certificate yet - the first online launch fetches one'
    }
}

function Test-Result {
    Write-Step 'Verifying'

    $task = Get-ScheduledTask -TaskName $ForwardingTask -ErrorAction SilentlyContinue
    if ($task) { Write-Same "task present (state $($task.State))" } else { Write-Bad 'task missing' }

    foreach ($spec in $FirewallRules) {
        $r = @(Get-NetFirewallRule -DisplayName $spec.Name -ErrorAction SilentlyContinue)
        if ($r.Count -eq 1) { Write-Same "'$($spec.Name)' present (exactly one)" }
        elseif ($r.Count -eq 0) { Write-Bad "'$($spec.Name)' missing" }
        else { Write-Bad "'$($spec.Name)' present $($r.Count) times" }
    }

    # The real test of the hosts entry is not that the line exists but that the resolver honours
    # it. Resolve-DnsName consults the hosts file first, so this passes offline too.
    try {
        $answer = Resolve-DnsName -Name $RelayHostName -Type A -ErrorAction Stop |
            Where-Object { $_.IPAddress } | Select-Object -First 1
        if ($answer -and $answer.IPAddress -eq $HotspotAddress) {
            Write-Same "$RelayHostName resolves to $($answer.IPAddress)"
        } elseif ($answer) {
            Write-Bad "$RelayHostName resolves to $($answer.IPAddress), expected $HotspotAddress"
        } else {
            Write-Bad "$RelayHostName returned no address"
        }
    } catch {
        Write-Bad "$RelayHostName does not resolve: $($_.Exception.Message)"
    }
}

# ---------------------------------------------------------------- main

Write-Host ''
Write-Host '  Rock Skipping - offline mode machine setup' -ForegroundColor White
Write-Host '  ------------------------------------------' -ForegroundColor DarkGray
if ($DryRun)   { Write-Host '  DRY RUN - nothing will be changed' -ForegroundColor Magenta }
if ($Uninstall){ Write-Host '  UNINSTALL - removing everything this script adds' -ForegroundColor Yellow }

if (-not $DryRun -and -not (Test-Elevated)) {
    Write-Host ''
    Write-Bad 'This needs Administrator.'
    Write-Info 'Open Terminal (Admin) and run it again, or use -DryRun to inspect without changing'
    Write-Info 'anything (that needs no elevation).'
    Write-Host ''
    exit 2
}

try {
    if ($Uninstall) {
        Write-Step "Scheduled task '$ForwardingTask'"
        if (Get-ScheduledTask -TaskName $ForwardingTask -ErrorAction SilentlyContinue) {
            Set-Or-Would 'unregistered scheduled task' {
                Unregister-ScheduledTask -TaskName $ForwardingTask -Confirm:$false
            }
        } else { Write-Same 'not present' }

        Write-Step 'Firewall rules'
        foreach ($spec in ($FirewallRules.Name + $ObsoleteRules)) {
            $found = @(Get-NetFirewallRule -DisplayName $spec -ErrorAction SilentlyContinue)
            if ($found.Count) {
                Set-Or-Would "removed $($found.Count) rule(s) '$spec'" { $found | Remove-NetFirewallRule }
            } else { Write-Same "'$spec' not present" }
        }

        Remove-HostsEntry
    }
    else {
        Sync-ForwardingTask

        Write-Step 'Firewall rules'
        foreach ($spec in $FirewallRules) {
            Write-Info "$($spec.Name): $($spec.Why)"
            Sync-FirewallRule $spec
        }
        Remove-ObsoleteRules

        Sync-HostsEntry
        Test-Prerequisites
        if (-not $DryRun) { Test-Result }
    }
}
catch {
    Write-Bad $_.Exception.Message
}

Write-Step 'Summary'
$verb = if ($DryRun) { 'would change' } else { 'changed' }
Write-Host "   $verb : $script:Changed" -ForegroundColor $(if ($script:Changed) { 'Yellow' } else { 'Gray' })
Write-Host "   already correct: $script:Already" -ForegroundColor Green
Write-Host "   problems       : $script:Failed" -ForegroundColor $(if ($script:Failed) { 'Red' } else { 'Gray' })
Write-Host ''

if ($script:Failed) {
    Write-Host '   Not ready. Fix the items marked [FAIL] above and re-run.' -ForegroundColor Red
    Write-Host ''
    exit 1
}

if ($DryRun) {
    Write-Host '   Re-run without -DryRun, as Administrator, to apply.' -ForegroundColor Magenta
} elseif (-not $Uninstall) {
    Write-Host '   This machine is set up for offline play.' -ForegroundColor Green
    Write-Host '   Verify the mechanism without unplugging anything:' -ForegroundColor Gray
    Write-Host '     ./deploy/spike/hotspot-offline-spike.ps1 -PreferNoConnectivity -EnableRadio -Dns' -ForegroundColor Gray
}
Write-Host ''
exit 0

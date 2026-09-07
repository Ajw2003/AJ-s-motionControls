<#
.SYNOPSIS
  Captures hotspot traffic at the NIC to settle one question: does the phone's captive-portal
  probe reach this machine at all?

.DESCRIPTION
  Every other tool in this diagnosis reads a layer above the wire. netstat shows sockets, so a
  SYN the host drops leaves no trace in it; the relay's own log only sees requests that reached
  a handler. Both go silent in exactly the case we need to tell apart, and they go silent the
  same way whether the phone sent nothing or the host discarded it.

  pktmon reads the NIC, below the firewall, so a packet that arrives and is then dropped still
  appears.

  THE POSITIVE CONTROL IS THE POINT. Filtering the capture to TCP 80 would make "no packets"
  ambiguous: it means the same thing whether the probe never arrived or the hotspot adapter was
  never in scope. So the filter is this host's hotspot ADDRESS, every protocol, and the DNS
  traffic - which the relay log independently proves is flowing - becomes the control. Only a
  capture containing that DNS is allowed to conclude anything from the absence of TCP 80.

    DNS present, TCP 80 present  ->  the probe IS sent; check whether it was dropped.
    DNS present, no TCP 80       ->  the phone resolves us and never connects. Conclusive.
    No DNS either                ->  the capture missed the adapter. Proves nothing; re-run.

  Requires elevation, which it obtains itself - one UAC prompt, matching how HotspotService
  already runs deploy/setup-offline.ps1. The capture filter is always removed on the way out,
  including on failure, and any error is written into the report rather than vanishing with the
  elevated window.

.PARAMETER Seconds
  How long to capture. The default spans a full Android validation cycle, measured from the
  relay log at about 62 seconds, with margin.

.PARAMETER HotspotIp
  The host's own address on the hotspot. Every packet between a phone and this machine has
  it as one endpoint, so filtering on it captures the DNS control and the probe alike.

  NOTE: pktmon's -i takes one or two literal IPs, NOT a CIDR - `pktmon filter add --help`
  spells it `-i <ip> [ip2]`. A subnet passed there is accepted silently and matches nothing,
  which produced a capture of zero packets that looked exactly like a quiet network.

.EXAMPLE
  .\capture-probe.ps1
  .\capture-probe.ps1 -Seconds 180
#>

[CmdletBinding()]
param(
    [int]$Seconds = 90,
    [string]$HotspotIp = '192.168.137.1',
    [switch]$Elevated
)

$captureEtl = Join-Path $PSScriptRoot 'probe-capture.etl'
$captureTxt = Join-Path $PSScriptRoot 'probe-capture.txt'
$reportPath = Join-Path $PSScriptRoot 'probe-capture-report.txt'

# The address prefix that identifies a guest in the dump, derived from the hotspot address so
# the two cannot drift apart.
$guestPrefix = $HotspotIp -replace '\.\d+$', '.'

function Test-Elevated {
    $identity = [Security.Principal.WindowsIdentity]::GetCurrent()
    return ([Security.Principal.WindowsPrincipal]$identity).IsInRole(
        [Security.Principal.WindowsBuiltInRole]::Administrator)
}

<#
  Turns a pktmon etl2txt dump into a verdict.

  Split out from the capture so it can be exercised against a saved dump with no driver and no
  elevation - the capture half cannot be tested without hardware, this half can, and it is the
  half that decides what we believe.

  etl2txt renders a packet as "IP.port > IP.port: Flags [S]", putting the port after a dot
  rather than a colon, and writes the file as UTF-16.
#>
function Get-ProbeVerdict {
    param(
        [Parameter(Mandatory)][string]$DumpPath,
        [Parameter(Mandatory)][string]$GuestPrefix
    )

    if (-not (Test-Path $DumpPath)) {
        return [PSCustomObject]@{
            Verdict = 'NO DUMP'
            Detail  = "pktmon produced no text dump at $DumpPath, so the capture did not run."
            Lines   = @()
        }
    }

    $all = @(Get-Content -LiteralPath $DumpPath)
    $escaped = [regex]::Escape($GuestPrefix)

    # Only lines that actually render a packet between two endpoints. pktmon also emits
    # component metadata carrying the host's own hotspot address, and counting that as traffic
    # is exactly what would let an empty capture masquerade as a populated one.
    $packets = @($all | Where-Object {
        $_ -match "$escaped\d+\.\d+\s*>" -or $_ -match ">\s*$escaped\d+\.\d+"
    })

    $dnsLines = @($packets | Where-Object { $_ -match '\.53:' })
    $httpLines = @($packets | Where-Object { $_ -match '\.80:' })

    if ($packets.Count -eq 0) {
        return [PSCustomObject]@{
            Verdict = 'INCONCLUSIVE - ADAPTER NOT CAPTURED'
            Detail  = "The dump has $($all.Count) line(s) but not one packet to or from " +
                      "$GuestPrefix*. The hotspot adapter was not in scope, so this says " +
                      'nothing about the phone. Re-run with the hotspot up and a phone joined.'
            Lines   = @()
        }
    }

    if ($dnsLines.Count -eq 0) {
        return [PSCustomObject]@{
            Verdict = 'INCONCLUSIVE - NO CONTROL'
            Detail  = "$($packets.Count) guest packet(s) were captured but none is DNS. The " +
                      'relay log shows the phone querying constantly, so a capture missing ' +
                      'that traffic is not sampling the path the probe would take. Absence of ' +
                      'TCP 80 here cannot be trusted.'
            Lines   = @($packets | Select-Object -First 20)
        }
    }

    if ($httpLines.Count -eq 0) {
        return [PSCustomObject]@{
            Verdict = 'NOTHING ARRIVED'
            Detail  = "Control satisfied: $($dnsLines.Count) DNS packet(s) from the guest were " +
                      "captured, so the adapter was in scope. None of the $($packets.Count) " +
                      'guest packets is TCP 80. The phone resolves our address and never opens ' +
                      'the connection. Nothing on this host can fix that.'
            Lines   = @($dnsLines | Select-Object -First 5)
        }
    }

    $dropped = @($httpLines | Where-Object { $_ -match 'Drop' })

    if ($dropped.Count -gt 0) {
        return [PSCustomObject]@{
            Verdict = 'ARRIVED AND DROPPED'
            Detail  = "$($httpLines.Count) TCP 80 packet(s) from the guest reached the NIC and " +
                      "$($dropped.Count) were dropped. The probe IS being sent and this host " +
                      'is discarding it. Investigate firewall scope and interface binding.'
            Lines   = $httpLines
        }
    }

    return [PSCustomObject]@{
        Verdict = 'ARRIVED'
        Detail  = "$($httpLines.Count) TCP 80 packet(s) from the guest reached the NIC with no " +
                  'drop recorded, so the probe is delivered and answered. The failure is above ' +
                  'the network layer.'
        Lines   = $httpLines
    }
}

# Re-launch elevated rather than sending the user off to find an admin prompt. -Wait so the
# unelevated parent can print the report the elevated child leaves behind.
if (-not $Elevated -and -not (Test-Elevated)) {
    Write-Host 'Capturing packets needs Administrator. Accept the UAC prompt...'
    $arguments = @(
        '-NoProfile', '-ExecutionPolicy', 'Bypass',
        '-File', $PSCommandPath,
        '-Seconds', $Seconds,
        '-HotspotIp', $HotspotIp,
        '-Elevated'
    )
    Start-Process -FilePath 'powershell.exe' -ArgumentList $arguments -Verb RunAs -Wait | Out-Null

    if (Test-Path $reportPath) {
        Get-Content -LiteralPath $reportPath | Write-Host
    } else {
        Write-Host 'The elevated run wrote no report - it was probably cancelled at the UAC prompt.'
    }
    return
}

# Deliberately NOT 'Stop'. Windows PowerShell turns a native command's stderr into an
# ErrorRecord, so pktmon writing a harmless notice - removing a filter that is not there,
# stopping a session already stopped - would throw and take the report with it. That is exactly
# what happened on the first run: an elevated window that closed on an error nobody could read.
$ErrorActionPreference = 'Continue'

Write-Host "Capturing all traffic to and from $HotspotIp for $Seconds seconds."
Write-Host 'Leave the phone joined to the hotspot and do not touch it.'
Write-Host ''

Remove-Item -LiteralPath $captureEtl, $captureTxt -ErrorAction SilentlyContinue

$captureError = $null

try {
    & pktmon filter remove 2>&1 | Out-Null
    & pktmon filter add 'RockSkippingGuest' -i $HotspotIp 2>&1 | Out-Null
    & pktmon start --capture --pkt-size 128 --file $captureEtl 2>&1 | Out-Null

    Start-Sleep -Seconds $Seconds

    & pktmon stop 2>&1 | Out-Null
    & pktmon etl2txt $captureEtl --out $captureTxt 2>&1 | Out-Null
} catch {
    $captureError = $_.Exception.Message
} finally {
    # Always hand the machine back its packet-filter state. A filter left behind is invisible
    # and would silently shape whatever anyone captures next.
    & pktmon stop 2>&1 | Out-Null
    & pktmon filter remove 2>&1 | Out-Null
}

$result = Get-ProbeVerdict -DumpPath $captureTxt -GuestPrefix $guestPrefix

$report = @()
$report += "Probe capture - $(Get-Date -Format 'yyyy-MM-dd HH:mm:ss')"
$report += "Captured ${Seconds}s of all traffic to and from $HotspotIp"
if ($captureError) { $report += "Capture reported: $captureError" }
$report += ''
$report += "VERDICT: $($result.Verdict)"
$report += ''
$report += $result.Detail
$report += ''

if ($result.Lines.Count -gt 0) {
    $report += 'Sample packets:'
    $report += ($result.Lines | ForEach-Object { '  ' + $_.Trim() })
    $report += ''
}

$report += "Full dump: $captureTxt"

$report | Set-Content -LiteralPath $reportPath -Encoding utf8
$report | Write-Host

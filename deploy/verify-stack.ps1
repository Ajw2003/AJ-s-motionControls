#requires -Version 5.1
<#
.SYNOPSIS
    Proves the local hosting stack works, without Unity and without a phone. Read-only.

.DESCRIPTION
    Answers, in order, the questions a failed play session actually raises - and answers them
    by doing the thing rather than by inspecting configuration:

      1. Node and the relay's dependencies are present.
      2. A certificate covering the magic domain is available (cached, or downloadable).
      3. THIS PC resolves the magic domain to the hotspot address.
      4. The one-time machine setup has been applied.
      5. The relay starts with the hotspot environment Unity gives it, over TLS.
      6. /health reports the DNS responder actually bound :53 - the field name Unity reads.
      7. The responder answers the magic domain with the hotspot address.
      8. It answers the connectivity probe domains too.
      9. It refuses everything else, so this is not an open resolver.

    Step 3 is the one that matters most and the one nothing used to check. The host takes no
    DHCP from itself, so it never consults the responder running on its own :53 - it asks a
    public resolver, and the magic domain is a real registered name whose public record is the
    registrar's parking page. Without the hosts entry setup-offline.ps1 writes, Unity connects
    to a stranger's web server on the internet and retries there forever, which does not look
    like a DNS problem from any angle.

    NEEDS NO ELEVATION. Binding :53 needs no privilege on Windows, which is the whole reason
    this design works (deploy/spike/README.md, question 1).

.PARAMETER KeepRunning
    Leave the relay running at the end instead of stopping it, so a phone or a browser can be
    pointed at it by hand. Stop it with Ctrl+C or by killing the reported PID.

.PARAMETER ProjectRoot
    Where the game's own runtime files live - relay-url.txt, magic-domain.txt.
    Defaults to the folder above this script, which is right when the checkout being verified is
    the one Unity has open.

    It is NOT right when running from a git worktree: the worktree holds the source, but Unity
    is open on the main checkout and reads its files, not the worktree's. Point this at the
    Unity project root in that case, or the mode check silently verifies the wrong machine
    state - which is exactly how a clean 13/13 run coexisted with a game that could not connect.

.EXAMPLE
    ./verify-stack.ps1
.EXAMPLE
    ./verify-stack.ps1 -KeepRunning
.EXAMPLE
    ./verify-stack.ps1 -ProjectRoot C:\Users\aj\Desktop\GameDev\RockSkipping
#>
[CmdletBinding()]
param(
    [switch] $KeepRunning,
    [string] $ProjectRoot
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

. (Join-Path $PSScriptRoot 'MagicDomain.ps1')

# ---------------------------------------------------------------- constants

$Root           = Split-Path -Parent $PSScriptRoot
if (-not $ProjectRoot) { $ProjectRoot = $Root }
$RelayDir       = Join-Path $Root 'relay'
$DnsQuery       = Join-Path $PSScriptRoot 'spike\dns-query.js'
$HotspotAddress = '192.168.137.1'
$HotspotSubnet  = '192.168.137.0/24'
$SecurePort     = 8443
$HostName       = Get-MagicDomainHostName

# Same LocalLow path CertificateProvider.CacheDir resolves to - Application.persistentDataPath
# is <LocalLow>/<companyName>/<productName>, and both are set in ProjectSettings.
$CertCacheDir   = Join-Path $env:USERPROFILE 'AppData\LocalLow\DefaultCompany\RockSkipping\local-https'

# Where a certificate is fetched to when the cache holds nothing usable. Deliberately NOT the
# game's cache: this script is a diagnostic and must not change what the next launch sees.
$ScratchDir     = Join-Path $env:TEMP 'rockskipping-verify'

$CertUrl = "https://github.com/Ajw2003/rockskipping-cert/releases/download/current/server.pem"
$KeyUrl  = "https://github.com/Ajw2003/rockskipping-cert/releases/download/current/server.key"

# ---------------------------------------------------------------- output

$script:Passed = 0
$script:Failed = 0
$script:Warned = 0

function Write-Step { param([string] $m) Write-Host "`n=== $m" -ForegroundColor Cyan }
function Write-Pass { param([string] $m) $script:Passed++; Write-Host "   [pass]    $m" -ForegroundColor Green }
function Write-Bad  { param([string] $m) $script:Failed++; Write-Host "   [FAIL]    $m" -ForegroundColor Red }
function Write-Warn { param([string] $m) $script:Warned++; Write-Host "   [warn]    $m" -ForegroundColor Yellow }
function Write-Info { param([string] $m) Write-Host "             $m" -ForegroundColor Gray }

# ---------------------------------------------------------------- helpers

function Get-CertificateSan {
    param([string] $Path)
    $cert = New-Object System.Security.Cryptography.X509Certificates.X509Certificate2 (Resolve-Path $Path)
    $san = ($cert.Extensions | Where-Object { $_.Oid.Value -eq '2.5.29.17' } |
        ForEach-Object { $_.Format($false) }) -join ' '
    [pscustomobject]@{ Cert = $cert; San = $san; Days = [int]($cert.NotAfter - (Get-Date)).TotalDays }
}

function Test-PortFree {
    param([int] $Port, [ValidateSet('TCP', 'UDP')] [string] $Protocol)
    if ($Protocol -eq 'TCP') {
        -not @(Get-NetTCPConnection -LocalPort $Port -State Listen -ErrorAction SilentlyContinue).Count
    } else {
        -not @(Get-NetUDPEndpoint -LocalPort $Port -ErrorAction SilentlyContinue).Count
    }
}

# ---------------------------------------------------------------- steps

function Test-Toolchain {
    Write-Step 'Node and relay dependencies'

    $node = Get-Command node -ErrorAction SilentlyContinue
    if ($node) { Write-Pass "node $(& node --version) ($($node.Source))" }
    else { Write-Bad 'node not found on PATH - the relay cannot start at all.'; return $false }

    if (Test-Path (Join-Path $RelayDir 'node_modules')) {
        Write-Pass 'relay/node_modules present'
    } else {
        Write-Bad "relay/node_modules missing - run: npm install --prefix `"$RelayDir`""
        return $false
    }

    if (Test-Path $DnsQuery) { Write-Pass 'deploy/spike/dns-query.js present' }
    else { Write-Bad "dns-query.js missing at $DnsQuery - cannot probe the responder."; return $false }

    return $true
}

function Resolve-Certificate {
    <# Returns @{ Cert; Key } for a usable pair, or $null. Never writes to the game's cache. #>
    Write-Step "Certificate for $HostName"

    $cachedCert = Join-Path $CertCacheDir 'server.pem'
    $cachedKey  = Join-Path $CertCacheDir 'server.key'

    if ((Test-Path $cachedCert) -and (Test-Path $cachedKey)) {
        try {
            $info = Get-CertificateSan $cachedCert
            if ($info.San -match [regex]::Escape($HostName) -and $info.Days -gt 0) {
                Write-Pass "cached certificate covers $HostName, $($info.Days) days left"
                return @{ Cert = $cachedCert; Key = $cachedKey }
            }
            Write-Info "cached certificate is '$($info.Cert.Subject)' ($($info.Days) days) - not usable for $HostName"
        } catch {
            Write-Info "cached certificate unreadable: $($_.Exception.Message)"
        }
    } else {
        Write-Info 'no cached certificate yet - the first launch fetches one'
    }

    Write-Info "fetching from $CertUrl"
    New-Item -ItemType Directory -Force -Path $ScratchDir | Out-Null
    $cert = Join-Path $ScratchDir 'server.pem'
    $key  = Join-Path $ScratchDir 'server.key'

    try {
        Invoke-WebRequest -Uri $CertUrl -OutFile $cert -UseBasicParsing -TimeoutSec 30
        Invoke-WebRequest -Uri $KeyUrl  -OutFile $key  -UseBasicParsing -TimeoutSec 30
    } catch {
        Write-Bad "could not download a certificate: $($_.Exception.Message)"
        Write-Info 'Offline with no usable cache. The game would fall back to the seed in'
        Write-Info 'StreamingAssets, if one was baked in at packaging time.'
        return $null
    }

    $info = Get-CertificateSan $cert
    if ($info.San -notmatch [regex]::Escape($HostName)) {
        Write-Bad "downloaded certificate does not cover $HostName (SAN: $($info.San))"
        return $null
    }
    if ($info.Days -le 0) {
        Write-Bad "downloaded certificate expired on $($info.Cert.NotAfter.ToString('yyyy-MM-dd'))"
        return $null
    }
    if ($info.Cert.Issuer -match 'STAGING') {
        Write-Bad "downloaded certificate is a Let's Encrypt STAGING cert - no phone will trust it."
        Write-Info 'Re-run renew-cert.yml via workflow_dispatch with environment=production.'
        return $null
    }

    Write-Pass "downloaded certificate covers $HostName, $($info.Days) days left (issuer $($info.Cert.Issuer))"
    return @{ Cert = $cert; Key = $key }
}




function Test-HostResolution {
    Write-Step "This PC resolving $HostName"
    Write-Info 'The host never asks the responder on its own :53 - it asks a public resolver,'
    Write-Info 'and this name is registered, so a missing hosts entry does not fail loudly. It'
    Write-Info "resolves to the registrar's parked address and the game talks to that instead."

    try {
        # An A lookup returns the whole chain, and a CNAME record in it has no IPAddress
        # property at all - which Set-StrictMode turns into a terminating error that reads as
        # "does not resolve" rather than "resolved via a CNAME". This name IS a CNAME publicly,
        # so that is the common case, not the edge one.
        $answers = @(Resolve-DnsName -Name $HostName -Type A -ErrorAction Stop |
            Where-Object { $_.PSObject.Properties.Name -contains 'IPAddress' -and $_.IPAddress } |
            ForEach-Object { $_.IPAddress })
    } catch {
        Write-Bad "$HostName does not resolve at all: $($_.Exception.Message)"
        Write-Info 'Run deploy/setup-offline.ps1 as Administrator to add the hosts entry.'
        return $false
    }

    if ($answers -contains $HotspotAddress) {
        Write-Pass "$HostName -> $HotspotAddress (hosts entry in place)"
        return $true
    }

    Write-Bad "$HostName -> $($answers -join ', '), expected $HotspotAddress"
    Write-Info 'That is the public record, not this PC. Unity would connect there and retry'
    Write-Info 'forever. Run deploy/setup-offline.ps1 as Administrator, then relaunch.'
    return $false
}

function Test-MachineSetup {
    Write-Step 'One-time machine setup'

    $script = Join-Path $PSScriptRoot 'setup-offline.ps1'
    & powershell.exe -NoProfile -NonInteractive -ExecutionPolicy Bypass -File $script -DryRun |
        Out-Null
    $code = $LASTEXITCODE

    switch ($code) {
        0 { Write-Pass 'setup-offline.ps1 -DryRun: already correct'; return $true }
        3 {
            Write-Warn 'setup-offline.ps1 -DryRun: setup needed (exit 3)'
            Write-Info 'The game runs this itself, elevated, on the first hotspot start. Run it'
            Write-Info 'by hand to see the detail:  ./deploy/setup-offline.ps1 -DryRun'
            return $false
        }
        default {
            Write-Bad "setup-offline.ps1 -DryRun exited $code - something is genuinely wrong"
            return $false
        }
    }
}

function Test-Relay {
    param([hashtable] $Certificate)

    Write-Step 'Relay with the hotspot environment Unity supplies'

    foreach ($p in @(@{ N = $SecurePort; P = 'TCP' }, @{ N = 80; P = 'TCP' })) {
        if (-not (Test-PortFree -Port $p.N -Protocol $p.P)) {
            Write-Warn "$($p.P)/$($p.N) is already in use - the relay may reuse or fail to bind it"
        }
    }
    if (-not (Test-PortFree -Port 53 -Protocol 'UDP')) {
        Write-Warn 'UDP/53 is already in use - the responder will lose the race for it'
    }

    # Exactly the variables LocalServices.Spawn sets in Hotspot mode. Any drift between this
    # list and that method makes this script prove something the game does not do.
    $env:TLS_CERT        = $Certificate.Cert
    $env:TLS_KEY         = $Certificate.Key
    $env:PORT            = "$SecurePort"
    $env:DNS_RESPOND_IP  = $HotspotAddress
    $env:DNS_ALLOW       = $HotspotSubnet
    $env:DNS_NAME        = $HostName

    $log = Join-Path $ScratchDir 'relay.log'
    New-Item -ItemType Directory -Force -Path $ScratchDir | Out-Null

    $relay = Start-Process -FilePath 'node' -ArgumentList 'server.js' -WorkingDirectory $RelayDir `
        -RedirectStandardOutput $log -RedirectStandardError "$log.err" -PassThru -WindowStyle Hidden

    Write-Info "relay pid $($relay.Id), log $log"
    return $relay
}

# The certificate's SAN never covers 127.0.0.1, and probing loopback is a liveness check on a
# process this script just started rather than a trust decision - the same reasoning, and the
# same narrow scope, as LocalServices.AcceptAnyCertificate.
#
# A compiled delegate rather than the usual `= { $true }` scriptblock. .NET invokes the
# validation callback on a TLS worker thread, which under -NonInteractive has no runspace to
# run PowerShell in, so the scriptblock form fails with "There is no Runspace available to run
# scripts in this thread" - surfaced as a bare "the underlying connection was closed", which
# reads exactly like the relay refusing the connection.
if (-not ('RockSkipping.LoopbackTrust' -as [type])) {
    Add-Type -TypeDefinition @'
namespace RockSkipping {
    public static class LoopbackTrust {
        public static void Enable() {
            System.Net.ServicePointManager.ServerCertificateValidationCallback =
                delegate { return true; };
        }
        public static void Disable() {
            System.Net.ServicePointManager.ServerCertificateValidationCallback = null;
        }
    }
}
'@
}

function Wait-Health {
    <# Polls /health over loopback, exactly as LocalServices.ProbeHealthUrl does. #>
    param([int] $TimeoutSec = 20)

    [RockSkipping.LoopbackTrust]::Enable()
    try {
        $deadline = (Get-Date).AddSeconds($TimeoutSec)
        while ((Get-Date) -lt $deadline) {
            try {
                $r = Invoke-WebRequest -Uri "https://127.0.0.1:$SecurePort/health" -UseBasicParsing -TimeoutSec 3
                return ($r.Content | ConvertFrom-Json)
            } catch {
                Start-Sleep -Milliseconds 300
            }
        }
        return $null
    } finally {
        [RockSkipping.LoopbackTrust]::Disable()
    }
}

function Test-Dns {
    Write-Step 'DNS responder, queried for real'

    $cases = @(
        @{ Name = $HostName;                  Expect = $HotspotAddress; Why = 'the game address phones open' }
        @{ Name = 'captive.apple.com';        Expect = $HotspotAddress; Why = "iOS's connectivity probe" }
        @{ Name = 'connectivitycheck.gstatic.com'; Expect = $HotspotAddress; Why = "Android's connectivity probe" }
        @{ Name = 'example.com';              Expect = 'NXDOMAIN';      Why = 'anything else - must not be answered' }
    )

    foreach ($case in $cases) {
        $out = (& node $DnsQuery $case.Name A 127.0.0.1 53 2>&1) -join "`n"

        if ($case.Expect -eq 'NXDOMAIN') {
            if ($out -match 'NXDOMAIN') { Write-Pass "$($case.Name) -> NXDOMAIN  ($($case.Why))" }
            else { Write-Bad "$($case.Name) should be NXDOMAIN, got: $out" }
            continue
        }

        if ($out -match [regex]::Escape($case.Expect)) {
            Write-Pass "$($case.Name) -> $($case.Expect)  ($($case.Why))"
        } else {
            Write-Bad "$($case.Name) should answer $($case.Expect), got: $out"
        }
    }
}

# ---------------------------------------------------------------- main

Write-Host ''
Write-Host '  Rock Skipping - local hosting stack verification' -ForegroundColor White
Write-Host '  ------------------------------------------------' -ForegroundColor DarkGray
Write-Host "  magic domain: $HostName" -ForegroundColor DarkGray
Write-Host "  project root: $ProjectRoot" -ForegroundColor DarkGray
if ($ProjectRoot -ne $Root) {
    Write-Host "  source tree : $Root" -ForegroundColor DarkGray
}

$relay = $null
try {
    if (-not (Test-Toolchain)) { throw 'toolchain' }

    $certificate = Resolve-Certificate
    if (-not $certificate) { throw 'certificate' }

    Test-HostResolution | Out-Null
    Test-MachineSetup   | Out-Null

    $relay = Test-Relay -Certificate $certificate

    $health = Wait-Health
    Write-Step 'Relay health'
    if (-not $health) {
        Write-Bad "the relay never answered https://127.0.0.1:$SecurePort/health"
        Write-Info "See $(Join-Path $ScratchDir 'relay.log') and .err for why."
    } else {
        Write-Pass "/health answered ok=$($health.ok)"

        # dns.bound, not dns.isBound. JsonUtility silently leaves an unmatched field at its
        # default, so a wire-name mismatch here reads as a permanent DNS failure in Unity - it
        # has been wrong once already (commit 0f6e67f).
        if ($health.dns.enabled) { Write-Pass 'health reports dns.enabled = true' }
        else { Write-Bad 'health reports dns.enabled = false - DNS_RESPOND_IP did not reach the relay' }

        if ($health.dns.bound) { Write-Pass 'health reports dns.bound = true - :53 is really held' }
        else { Write-Bad 'health reports dns.bound = false - the :53 bind failed (something else has it?)' }

        Test-Dns
    }
}
catch {
    if ("$_" -notin @('toolchain', 'certificate')) { Write-Bad $_.Exception.Message }
}
finally {
    if ($relay -and -not $KeepRunning) {
        try { Stop-Process -Id $relay.Id -Force -ErrorAction Stop } catch { }
    }
}

Write-Step 'Summary'
Write-Host "   passed  : $script:Passed" -ForegroundColor Green
Write-Host "   warnings: $script:Warned" -ForegroundColor $(if ($script:Warned) { 'Yellow' } else { 'Gray' })
Write-Host "   failed  : $script:Failed" -ForegroundColor $(if ($script:Failed) { 'Red' } else { 'Gray' })
Write-Host ''

if ($relay -and $KeepRunning) {
    Write-Host "   Relay left running as pid $($relay.Id). Stop it with: Stop-Process -Id $($relay.Id)" -ForegroundColor Gray
    Write-Host ''
}

if ($script:Failed) {
    Write-Host '   NOT ready to test a build. Fix the [FAIL] items above.' -ForegroundColor Red
    Write-Host ''
    exit 1
}

Write-Host '   The local hosting stack works end to end.' -ForegroundColor Green
Write-Host ''
exit 0

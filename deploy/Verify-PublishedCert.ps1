#requires -Version 5.1
<#
.SYNOPSIS
    Verifies the certificate published by .github/workflows/renew-cert.yml, by downloading it
    from the same public URL a player's machine uses. Read-only, needs no credentials.

.DESCRIPTION
    This is the end-to-end check on the certificate half of offline hosting. It deliberately
    does not look at any local build output, any cached copy, or anything inside GitHub
    Actions - it fetches the published artifact anonymously, exactly as
    CertificateProvider.Download() does, and validates it the way CertificateProvider.Inspect()
    and fetch-seed-cert.ps1 do. If this passes, the pipeline worked; if it fails, it names
    which link broke.

    Distinct from verify-stack.ps1, which checks whether THIS MACHINE can host: that script
    accepts a cached or seeded certificate, because for hosting it does not matter where a
    usable certificate came from. This one only ever looks at what is published, because the
    question here is whether the renewal pipeline is delivering.

    Two checks here are ones nothing else in the project makes:

      - The private key actually matches the certificate. Inspect() opens the certificate and
        only stats the key file, so a mismatched pair passes every check on the Unity side and
        then kills the relay inside Node at TLS startup - which does not look like a
        certificate problem to anyone reading the logs.

      - server.pem is a fullchain rather than a bare leaf. A bare leaf still validates on
        desktops that have cached the intermediate and fails on phones that have not, which is
        the worst possible way to discover it.

    The workflow runs this too, before and after publishing, so a publish that "succeeds" but
    produces something unusable fails in CI instead of at a party.

.PARAMETER Tag
    Release tag to check. Defaults to 'current', the tag MagicDomain.cs points at. Use
    'staging' to check a rehearsal run's output.

.PARAMETER Domain
    Hostname the SAN must cover. Kept in sync by hand with MagicDomain.cs's DefaultHostName
    and renew-cert.yml's DOMAIN.

.PARAMETER Repo
    The public certificate repository, owner/name.

.PARAMETER AllowStaging
    Accept a Let's Encrypt STAGING issuer. Only for verifying a staging rehearsal - a staging
    certificate is trusted by no device.

.PARAMETER MinDays
    Fail if fewer than this many days remain. Defaults to 1. Packaging a seed wants a much
    higher bar; fetch-seed-cert.ps1 uses 30.

.PARAMETER Keep
    Keep the downloaded files and print where they are, instead of deleting them.

.EXAMPLE
    pwsh ./deploy/Verify-PublishedCert.ps1

.EXAMPLE
    ./deploy/Verify-PublishedCert.ps1 -Tag staging -AllowStaging

.EXAMPLE
    ./deploy/Verify-PublishedCert.ps1 -MinDays 30
    Checks against the bar fetch-seed-cert.ps1 applies before baking a seed into a build.
#>
[CmdletBinding()]
param(
    [string] $Tag = 'current',
    [string] $Domain = 'play.skiprocking.ca',
    [string] $Repo = 'Ajw2003/rockskipping-cert',
    [switch] $AllowStaging,
    [int]    $MinDays = 1,
    [switch] $Keep
)

Set-StrictMode -Version Latest
# Not 'Stop': this script's job is to report every problem it can see in one run, so a failed
# check records itself and continues rather than aborting at the first one. The exceptions are
# the two download failures and an unparseable certificate, which are handled explicitly
# because nothing after them can mean anything.
$ErrorActionPreference = 'Continue'

$script:Passed = 0
$script:Failed = 0
$script:Warned = 0

function Write-Step { param([string] $m) Write-Host "`n=== $m" -ForegroundColor Cyan }
function Write-Pass { param([string] $m) $script:Passed++; Write-Host "   [pass]  $m" -ForegroundColor Green }
function Write-Bad  { param([string] $m) $script:Failed++; Write-Host "   [FAIL]  $m" -ForegroundColor Red }
function Write-Warn { param([string] $m) $script:Warned++; Write-Host "   [warn]  $m" -ForegroundColor Yellow }
function Write-Info { param([string] $m) Write-Host "           $m" -ForegroundColor Gray }

$base = "https://github.com/$Repo/releases/download/$Tag"
$work = Join-Path ([System.IO.Path]::GetTempPath()) ("certcheck-" + [guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Force -Path $work | Out-Null
$certPath = Join-Path $work 'server.pem'
$keyPath  = Join-Path $work 'server.key'

try {
    # ------------------------------------------------------------------ download
    Write-Step "Downloading from the public URL ($Tag)"
    Write-Info "$base/server.pem"

    # A missing asset must be a hard stop, not a 404 error page written into server.pem and
    # then failing later as a baffling parse error.
    try {
        Invoke-WebRequest -Uri "$base/server.pem" -OutFile $certPath -UseBasicParsing
    } catch {
        Write-Bad 'server.pem could not be downloaded. The release asset does not exist, or the tag is wrong.'
        Write-Info "Nothing published under tag '$Tag' in $Repo, or the repo is private."
        Write-Info $_.Exception.Message
        exit 1
    }
    try {
        Invoke-WebRequest -Uri "$base/server.key" -OutFile $keyPath -UseBasicParsing
    } catch {
        Write-Bad 'server.key could not be downloaded, though server.pem was.'
        Write-Info 'A half-published release is worse than none: the game downloads both or neither.'
        Write-Info $_.Exception.Message
        exit 1
    }
    $certBytes = (Get-Item $certPath).Length
    $keyBytes  = (Get-Item $keyPath).Length
    Write-Pass "both assets downloaded ($certBytes and $keyBytes bytes)"

    # ------------------------------------------------------------------ parse
    Write-Step 'Certificate parses'
    # Not X509Certificate2.CreateFromPemFile - that is .NET 5+, and this script supports
    # Windows PowerShell 5.1 like the rest of deploy/. The plain constructor reads PEM there
    # via CryptQueryObject.
    try {
        $x509 = 'System.Security.Cryptography.X509Certificates.X509Certificate2'
        $cert = New-Object $x509 ((Resolve-Path $certPath).Path)
    } catch {
        Write-Bad 'server.pem is not a parseable certificate.'
        Write-Info ("First line: " + (Get-Content $certPath -TotalCount 1))
        Write-Info $_.Exception.Message
        exit 1
    }
    Write-Pass 'parses as PEM'
    Write-Info "subject=$($cert.Subject)"
    Write-Info "issuer=$($cert.Issuer)"

    # ------------------------------------------------------------------ SAN
    Write-Step "Subject Alternative Name covers $Domain"
    # Same approach as Get-CertificateSan in verify-stack.ps1: CertificateProvider.CoversWildcard
    # reads SAN (OID 2.5.29.17) specifically and ignores the subject CN entirely, so a
    # certificate whose CN is right and whose SAN is wrong passes a casual eyeball and fails in
    # the game.
    $sanExtension = $cert.Extensions | Where-Object { $_.Oid.Value -eq '2.5.29.17' }
    if (-not $sanExtension) {
        Write-Bad 'no Subject Alternative Name extension at all. CertificateProvider requires one.'
    } else {
        $sanText = ($sanExtension | ForEach-Object { $_.Format($false) }) -join ' '
        Write-Info "SAN: $sanText"
        # Match the name as a whole token. A substring test would accept
        # 'notplay.skiprocking.ca.evil.example' as covering the domain.
        if ($sanText -match ('(^|[^A-Za-z0-9.-])DNS Name=' + [regex]::Escape($Domain) + '($|[^A-Za-z0-9.-])') -or
            $sanText -match ('(^|[^A-Za-z0-9.-])DNS:' + [regex]::Escape($Domain) + '($|[^A-Za-z0-9.-])')) {
            Write-Pass "SAN contains $Domain"
        } else {
            Write-Bad "SAN does not cover $Domain. Every phone would show a certificate warning."
        }
    }

    # ------------------------------------------------------------------ issuer
    Write-Step 'Issuer is a real CA'
    if ($cert.Issuer -match 'staging|pretend|bogus') {
        if ($AllowStaging) {
            Write-Pass 'STAGING issuer, accepted because -AllowStaging was passed'
            Write-Info 'This certificate is trusted by no device. Rehearsal only.'
        } else {
            Write-Bad "issued by Let's Encrypt STAGING. No device trusts it."
            Write-Info 'Re-run renew-cert.yml with environment=production.'
        }
    } else {
        Write-Pass 'not a staging issuer'
    }

    # ------------------------------------------------------------------ validity
    Write-Step 'Validity window'
    $days = [int]([datetime]$cert.NotAfter - (Get-Date)).TotalDays
    Write-Info ("expires " + $cert.NotAfter.ToString('yyyy-MM-dd'))
    if ($days -lt 0) {
        Write-Bad ("already expired ({0} days ago)" -f (-$days))
    } elseif ($days -lt $MinDays) {
        Write-Bad "$days days remaining, under the -MinDays $MinDays threshold"
    } else {
        Write-Pass "$days days remaining"
    }
    # CertificateProvider renews within 14 days of expiry (RenewWithinDays), so anything under
    # that triggers a download on the next launch anyway - worth saying rather than only
    # failing hard.
    if ($days -ge 0 -and $days -le 14) {
        Write-Info 'note: under CertificateProvider.RenewWithinDays (14), so clients will try to re-download.'
    }

    # ------------------------------------------------------------------ key pairing
    Write-Step 'Private key matches the certificate'
    # Tiered, because the one-call answer needs .NET 5+ and this script supports 5.1. Where
    # neither tier is available the check is SKIPPED and says so - reporting a pass for a check
    # that never ran would be worse than not having the check.
    $pairChecked = $false
    $hasPemApi = @([System.Security.Cryptography.X509Certificates.X509Certificate2].GetMethods() |
        Where-Object { $_.Name -eq 'CreateFromPemFile' }).Count -gt 0
    if ($hasPemApi) {
        try {
            # Throws if the key does not correspond to the certificate. Definitive.
            [void][System.Security.Cryptography.X509Certificates.X509Certificate2]::CreateFromPemFile(
                (Resolve-Path $certPath).Path, (Resolve-Path $keyPath).Path)
            Write-Pass 'key and certificate match (verified by CreateFromPemFile)'
        } catch {
            Write-Bad 'server.key does NOT match server.pem. The relay would fail to start TLS.'
            Write-Info $_.Exception.Message
        }
        $pairChecked = $true
    } elseif (Get-Command openssl -ErrorAction SilentlyContinue) {
        $certPub = & openssl x509 -in $certPath -noout -pubkey 2>$null
        $keyPub  = & openssl pkey -in $keyPath -pubout 2>$null
        if (-not $keyPub) {
            Write-Bad 'server.key is not a readable private key.'
        } elseif (($certPub -join "`n") -eq ($keyPub -join "`n")) {
            Write-Pass 'key and certificate share a public key (verified by openssl)'
        } else {
            Write-Bad 'server.key does NOT match server.pem. The relay would fail to start TLS.'
        }
        $pairChecked = $true
    }
    if (-not $pairChecked) {
        Write-Warn 'SKIPPED - cannot check the key against the certificate on this host.'
        Write-Info 'Needs PowerShell 7 (.NET 5+) or openssl on PATH. Re-run with pwsh for the full check.'
        Write-Info 'Everything else above was checked normally.'
    }

    # ------------------------------------------------------------------ chain
    Write-Step 'Chain shape'
    # --fullchain-file should give leaf + intermediate(s). A bare leaf still validates on
    # desktops that cache intermediates and fails on a phone that does not.
    $count = ([regex]::Matches((Get-Content $certPath -Raw), 'BEGIN CERTIFICATE')).Count
    Write-Info "$count certificate(s) in server.pem"
    if ($count -ge 2) {
        Write-Pass 'fullchain (leaf + intermediate)'
    } else {
        Write-Bad "only $count certificate - this is a bare leaf, not a fullchain."
        Write-Info 'Phones do not cache intermediates the way desktops do; expect intermittent failures.'
    }

    # ------------------------------------------------------------------ verdict
    Write-Host ''
    if ($script:Failed -eq 0) {
        Write-Host "PASS - the published certificate at $base is usable." -ForegroundColor Green
        if ($script:Warned -gt 0) {
            Write-Host "       ($script:Warned check(s) skipped - see [warn] above)" -ForegroundColor Yellow
        }
        exit 0
    }
    Write-Host 'FAIL - the published certificate is not usable. See the [FAIL] lines above.' -ForegroundColor Red
    exit 1
} finally {
    if ($Keep) {
        Write-Host "`nkept: $work" -ForegroundColor Gray
    } else {
        Remove-Item -Recurse -Force $work -ErrorAction SilentlyContinue
    }
}

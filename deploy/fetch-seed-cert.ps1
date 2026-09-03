<#
.SYNOPSIS
    Downloads the published certificate so the installer can bake it into a build as the
    seed CertificateProvider.TrySeedCache falls back to.

.DESCRIPTION
    Run this at packaging time, immediately before compiling installer.iss.

    Why a fetch step rather than committing server.pem/server.key to the repo: the seed has
    to reach the build somehow, and the obvious route — checking it into
    Assets/StreamingAssets/ — puts a private key in git history permanently, where it cannot
    be removed without a history rewrite and where GitHub's push protection is liable to
    block the push outright. .gitignore already ignores *.pem and *.key as a deliberate
    backstop against exactly that, so committing the seed would mean overriding a decision
    this repo already made on purpose. Fetching at packaging time gets the key into the
    installer without it ever becoming a tracked object.

    The key being downloadable by anyone is not the problem being solved here — it is
    published deliberately (see docs/plans/magic-domain-replacement.md's security note, and
    SecurityReview.md section 1). Git history permanence is.

    The seed exists so a brand-new install still reaches Ready on a day the download URL is
    unreachable. That is not hypothetical: it is precisely the failure that took this project
    down when local-ip.sh went offline, where an empty cache plus an unreachable service was
    fatal.

.PARAMETER OutputDir
    Where to write server.pem/server.key. Defaults to deploy/seed-cert, which is gitignored
    and is where installer.iss expects to find them.

.EXAMPLE
    pwsh ./deploy/fetch-seed-cert.ps1
#>
[CmdletBinding()]
param(
    [string] $OutputDir = (Join-Path $PSScriptRoot 'seed-cert')
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

# Kept in sync by hand with MagicDomain.cs's DefaultHostName/DefaultCertUrls and
# .github/workflows/renew-cert.yml's DOMAIN. Three copies is two too many, but the domain
# changes about once a decade and the alternative is a shared config file that all three
# stacks — C#, YAML and PowerShell — would have to learn to parse.
$ExpectedHostName = 'play.skiprocking.ca'
$CertUrl = 'https://github.com/Ajw2003/rockskipping-cert/releases/download/current/server.pem'
$KeyUrl  = 'https://github.com/Ajw2003/rockskipping-cert/releases/download/current/server.key'

# Refuse a seed this close to expiry. A seed is only ever a fallback for a machine that
# cannot reach the network, so it may sit unused for a long time before it matters; shipping
# one already most of the way through its 90 days makes it useless exactly when it is needed.
$MinimumDaysRemaining = 30

function Write-Step { param([string] $Message) Write-Host "==> $Message" -ForegroundColor Cyan }
function Write-Good { param([string] $Message) Write-Host "    $Message" -ForegroundColor Green }

New-Item -ItemType Directory -Force -Path $OutputDir | Out-Null
$certPath = Join-Path $OutputDir 'server.pem'
$keyPath  = Join-Path $OutputDir 'server.key'

Write-Step "Downloading the published certificate"
# -UseBasicParsing keeps this working on hosts with no IE engine registered; the release
# asset URL redirects to objects.githubusercontent.com, which Invoke-WebRequest follows.
Invoke-WebRequest -Uri $CertUrl -OutFile $certPath -UseBasicParsing
Invoke-WebRequest -Uri $KeyUrl  -OutFile $keyPath  -UseBasicParsing
Write-Good "server.pem  $((Get-Item $certPath).Length) bytes"
Write-Good "server.key  $((Get-Item $keyPath).Length) bytes"

Write-Step "Validating before letting it near a build"
# Validate here rather than trusting the download, because a bad seed fails in the worst
# possible place: on a player's machine, offline, with no way to diagnose it. Better to fail
# on the packaging machine where someone is watching.
$cert = [System.Security.Cryptography.X509Certificates.X509Certificate2]::new($certPath)

$sanExtension = $cert.Extensions | Where-Object { $_.Oid.Value -eq '2.5.29.17' }
if (-not $sanExtension) {
    throw "The downloaded certificate has no Subject Alternative Name extension. CertificateProvider.CoversWildcard checks SAN specifically, so a certificate without one is unusable regardless of its subject."
}

$sanText = $sanExtension.Format($false)
if ($sanText -notmatch [regex]::Escape($ExpectedHostName)) {
    throw "The downloaded certificate's SAN ($sanText) does not cover $ExpectedHostName. Shipping it would produce a certificate error on every phone that scans the QR."
}

$daysLeft = [int]([datetime]$cert.NotAfter - (Get-Date)).TotalDays
if ($daysLeft -lt $MinimumDaysRemaining) {
    throw "The downloaded certificate expires in $daysLeft days, under the $MinimumDaysRemaining-day minimum for a seed. Re-run .github/workflows/renew-cert.yml to publish a fresh one, then run this again."
}

if ($cert.Issuer -match 'STAGING') {
    throw "The downloaded certificate was issued by Let's Encrypt STAGING ($($cert.Issuer)), which no phone trusts. Re-run renew-cert.yml with environment=production."
}

Write-Good "Subject     $($cert.Subject)"
Write-Good "Issuer      $($cert.Issuer)"
Write-Good "SAN         $sanText"
Write-Good "Expires     $($cert.NotAfter.ToString('yyyy-MM-dd')) ($daysLeft days left)"

Write-Host ""
Write-Host "Seed certificate staged at $OutputDir" -ForegroundColor Green
Write-Host "installer.iss copies it into the installed build's StreamingAssets folder." -ForegroundColor Gray

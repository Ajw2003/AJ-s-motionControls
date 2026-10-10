#requires -Version 5.1
<#
.SYNOPSIS
    Builds installer\stage\, the folder ajmotion.iss packs (#25).

.DESCRIPTION
    Downloads the pinned portable Node and checks it against nodejs.org's SHASUMS256.txt,
    copies the relay (production packages only), the controller page, the hotspot script and
    the launcher, laid out as the install folder will be:
        node\node.exe  relay\  controller\  hotspot\  tools\  installer\firewall.ps1

.EXAMPLE
    powershell -ExecutionPolicy Bypass -File installer\stage.ps1
#>
[CmdletBinding()]
param(
    [string] $NodeVersion = '22.19.0',
    [string] $OutDir = (Join-Path $PSScriptRoot 'stage')
)
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue' # Invoke-WebRequest's progress bar slows downloads badly in 5.1
[Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12

$Repo = Split-Path -Parent $PSScriptRoot
if (Test-Path -LiteralPath $OutDir) { throw "$OutDir already exists. Remove it first so the stage holds only this build." }
New-Item -ItemType Directory -Path $OutDir | Out-Null

Write-Host "==> Node $NodeVersion"
$zipName = "node-v$NodeVersion-win-x64.zip"
$download = Join-Path $OutDir '..\node-download'
New-Item -ItemType Directory -Force -Path $download | Out-Null
$zip = Join-Path $download $zipName
Invoke-WebRequest -UseBasicParsing -Uri "https://nodejs.org/dist/v$NodeVersion/$zipName" -OutFile $zip
$sums = (Invoke-WebRequest -UseBasicParsing -Uri "https://nodejs.org/dist/v$NodeVersion/SHASUMS256.txt").Content
$expected = ($sums -split "`n" | Where-Object { $_ -match "\s$([regex]::Escape($zipName))$" } | Select-Object -First 1) -replace '\s.*$', ''
$actual = (Get-FileHash -Algorithm SHA256 -LiteralPath $zip).Hash.ToLower()
if (-not $expected -or $actual -ne $expected.ToLower()) { throw "Node checksum mismatch: expected '$expected', got '$actual'." }
Write-Host "    checksum ok ($actual)"
Add-Type -AssemblyName System.IO.Compression.FileSystem
$archive = [IO.Compression.ZipFile]::OpenRead($zip)
try {
    $entry = $archive.Entries | Where-Object { $_.FullName -eq "node-v$NodeVersion-win-x64/node.exe" }
    New-Item -ItemType Directory -Path (Join-Path $OutDir 'node') | Out-Null
    [IO.Compression.ZipFileExtensions]::ExtractToFile($entry, (Join-Path $OutDir 'node\node.exe'))
} finally {
    $archive.Dispose()
}

Write-Host '==> Files'
$relayOut = Join-Path $OutDir 'relay'
New-Item -ItemType Directory -Path $relayOut | Out-Null
Get-ChildItem -LiteralPath (Join-Path $Repo 'relay') -File |
    Where-Object { $_.Name -notlike 'test-*' -and $_.Extension -ne '.txt' } |
    Copy-Item -Destination $relayOut
$controllerOut = Join-Path $OutDir 'controller'
New-Item -ItemType Directory -Path (Join-Path $controllerOut 'js') | Out-Null
Get-ChildItem -LiteralPath (Join-Path $Repo 'controller') -File -Filter '*.html' | Copy-Item -Destination $controllerOut
Copy-Item -Path (Join-Path $Repo 'controller\js\*.js') -Destination (Join-Path $controllerOut 'js')
New-Item -ItemType Directory -Path (Join-Path $OutDir 'hotspot'), (Join-Path $OutDir 'tools'), (Join-Path $OutDir 'installer') | Out-Null
Copy-Item (Join-Path $Repo 'hotspot\hotspot-control.ps1') (Join-Path $OutDir 'hotspot')
Copy-Item (Join-Path $Repo 'tools\launch.ps1'), (Join-Path $Repo 'tools\emulators.ps1') (Join-Path $OutDir 'tools')
Copy-Item (Join-Path $PSScriptRoot 'firewall.ps1') (Join-Path $OutDir 'installer')

Write-Host '==> Relay packages (production only)'
& npm.cmd --prefix $relayOut ci --omit=dev --no-audit --no-fund
if ($LASTEXITCODE -ne 0) { throw 'npm ci failed in the stage. The lines above say why.' }

$size = (Get-ChildItem -LiteralPath $OutDir -Recurse -File | Measure-Object -Property Length -Sum).Sum
Write-Host ("==> Stage ready: {0} ({1:N1} MB)" -f $OutDir, ($size / 1MB))

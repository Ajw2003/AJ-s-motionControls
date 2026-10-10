#requires -Version 5.1
# Runs tools/emulators.ps1 against throwaway copies; exits 1 on the first failure.
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'emulators.ps1')

$root = Join-Path ([IO.Path]::GetTempPath()) ("emu-test-" + [guid]::NewGuid())
New-Item -ItemType Directory -Path (Join-Path $root 'dolphin') | Out-Null
$failures = 0
function Check([string] $name, [bool] $ok) {
    if ($ok) { Write-Host "ok   $name" } else { Write-Host "FAIL $name" -ForegroundColor Red; $script:failures++ }
}
function Write-Crlf([string] $path, [string[]] $lines) {
    [IO.File]::WriteAllText($path, ($lines -join "`r`n") + "`r`n", (New-Object Text.UTF8Encoding $false))
}
$notRunning = { param($n) $false }

$sudachi = Join-Path $root 'qt-config.ini'
Write-Crlf $sudachi @('[Controls]', 'udp_input_servers\default=true', 'udp_input_servers=127.0.0.1:26760',
    'enable_udp_controller\default=true', 'enable_udp_controller=false',
    'player_0_button_a="engine:sdl,port:0,guid:abc,button:1"')
$before = [IO.File]::ReadAllText($sudachi)

$r = Invoke-EmulatorSetup -SudachiConfig $sudachi -DolphinConfigDir (Join-Path $root 'dolphin') -IsRunning $notRunning
$after = [IO.File]::ReadAllText($sudachi)
Check 'sudachi changed' (($r | Where-Object emulator -eq 'Sudachi').status -eq 'changed')
Check 'sudachi udp on' ($after -match "(?m)^enable_udp_controller=true`r$")
Check 'sudachi crlf kept' (-not ($after -match "[^`r]`n"))
Check 'sudachi mapping untouched' ($after -match [regex]::Escape('player_0_button_a="engine:sdl,port:0,guid:abc,button:1"'))
Check 'sudachi only two lines differ' ((Compare-Object ($before -split "`r`n") ($after -split "`r`n")).Count -eq 4)
Check 'sudachi backup made' (@(Get-ChildItem $root -Filter 'qt-config.ini.ajmotion-*.bak').Count -eq 1)
Check 'dolphin created' ((Get-Content (Join-Path $root 'dolphin\DSUClient.ini') -Raw) -eq "[Server]`r`nEnabled = True`r`nEntries = AJ Motion:127.0.0.1:26760;`r`n")

$r2 = Invoke-EmulatorSetup -SudachiConfig $sudachi -DolphinConfigDir (Join-Path $root 'dolphin') -IsRunning $notRunning
Check 'second run is a no-op' (@($r2 | Where-Object status -ne 'already').Count -eq 0)

Write-Crlf (Join-Path $root 'dolphin\DSUClient.ini') @('[Server]', 'Enabled = False', 'Entries = ajsMotion:127.0.0.1:26760;')
$null = Invoke-EmulatorSetup -SudachiConfig $sudachi -DolphinConfigDir (Join-Path $root 'dolphin') -IsRunning $notRunning
$d = Get-Content (Join-Path $root 'dolphin\DSUClient.ini') -Raw
Check 'dolphin keeps existing entry' ($d -match 'Entries = ajsMotion:127\.0\.0\.1:26760;\r\n' -and $d -notmatch 'AJ Motion')
Check 'dolphin enabled' ($d -match 'Enabled = True')

Write-Crlf (Join-Path $root 'dolphin\DSUClient.ini') @('[Server]', 'Enabled = True', 'Entries = other:10.0.0.5:26760;')
$null = Invoke-EmulatorSetup -SudachiConfig $sudachi -DolphinConfigDir (Join-Path $root 'dolphin') -IsRunning $notRunning
$d = Get-Content (Join-Path $root 'dolphin\DSUClient.ini') -Raw
Check 'dolphin appends to other entries' ($d -match 'Entries = other:10\.0\.0\.5:26760;AJ Motion:127\.0\.0\.1:26760;\r\n')

$running = { param($n) $n -eq 'sudachi' }
Write-Crlf $sudachi @('[Controls]', 'enable_udp_controller=false')
$r4 = Invoke-EmulatorSetup -SudachiConfig $sudachi -DolphinConfigDir (Join-Path $root 'dolphin') -IsRunning $running
Check 'running sudachi skipped' (($r4 | Where-Object emulator -eq 'Sudachi').status -eq 'running')
Check 'running sudachi untouched' ([IO.File]::ReadAllText($sudachi) -match 'enable_udp_controller=false')

Write-Crlf $sudachi @('[UI]', 'theme=dark')
$r5 = Invoke-EmulatorSetup -SudachiConfig $sudachi -DolphinConfigDir (Join-Path $root 'nothing') -IsRunning $notRunning
Check 'unknown format left alone' ((($r5 | Where-Object emulator -eq 'Sudachi').status -eq 'unknown') -and ([IO.File]::ReadAllText($sudachi) -eq "[UI]`r`ntheme=dark`r`n"))
Check 'dolphin missing reported' (($r5 | Where-Object emulator -eq 'Dolphin').status -eq 'missing')

Remove-Item -Recurse -Force $root
if ($failures) { Write-Host "$failures failed" -ForegroundColor Red; exit 1 }
Write-Host 'all passed' -ForegroundColor Green

# Plays the game headlessly and writes a screenshot of the TV screen.
#
# Why this exists: art and UI changes cannot be checked by reading code. Four rounds of
# presentation changes were shipped blind and each was wrong in a different way. This turns
# "change a value and ask someone to press Play" into a loop that produces an image.
#
# Requires the Unity Editor to be CLOSED  -  Unity holds an exclusive lock on the project, and
# batchmode cannot open a project the Editor already has. That is the same limitation that
# makes an out-of-band compile check impossible while the Editor is running.
#
#   pwsh/powershell tools\capture-scene.ps1
#   powershell tools\capture-scene.ps1 -DelaySeconds 8 -Width 1920
#
# Written for Windows PowerShell 5.1: no && or || chaining, no ternary.

[CmdletBinding()]
param(
    # Seconds of play before the shot. Bootstrap loads Lobby immediately, so anything
    # above ~3 captures the lobby with the join screen up.
    [double] $DelaySeconds = 4,

    # Capture width; height is derived 16:9.
    [int] $Width = 1280,

    # How long to wait for the run before giving up and killing the editor.
    [int] $TimeoutSeconds = 300
)

$ErrorActionPreference = 'Stop'

$projectPath = Split-Path -Parent $PSScriptRoot
$lockFile    = Join-Path $projectPath 'Temp\UnityLockfile'
$imagePath   = Join-Path $projectPath 'scene-capture.png'
$markerPath  = Join-Path $projectPath 'scene-capture.done'
$logPath     = Join-Path $projectPath 'scene-capture.log'

# The editor version is read from the project rather than hardcoded, so this keeps working
# after an editor upgrade instead of silently launching the wrong one.
$versionLine = Get-Content (Join-Path $projectPath 'ProjectSettings\ProjectVersion.txt') |
    Where-Object { $_ -like 'm_EditorVersion:*' } |
    Select-Object -First 1
$version = $versionLine.Split(':')[1].Trim()

$unity = "C:\Program Files\Unity\Hub\Editor\$version\Editor\Unity.exe"
if (-not (Test-Path $unity)) {
    throw "Unity $version not found at $unity. Installed versions: " +
          ((Get-ChildItem 'C:\Program Files\Unity\Hub\Editor' -Directory).Name -join ', ')
}

# Checked rather than assumed: launching against a locked project fails with a message about
# the project being open, which is easy to misread as a problem with the harness.
if (Test-Path $lockFile) {
    throw "The Unity Editor has this project open (Temp\UnityLockfile exists). Close it first  -  batchmode cannot share the project lock."
}

foreach ($stale in @($imagePath, $markerPath, $logPath)) {
    if (Test-Path $stale) { Remove-Item $stale -Force }
}

Write-Host "Unity $version"
Write-Host "Capturing after $DelaySeconds s at ${Width}px wide..."

# No -nographics: there would be no graphics device and the capture would be blank.
# No -quit: the method enters play mode, so the editor must stay alive past the call. The
# same reasoning is documented for RunVerification in README.md.
$unityArgs = @(
    '-batchmode'
    '-projectPath', $projectPath
    '-executeMethod', 'RockSkippingEditor.SceneCaptureRunner.Capture'
    '-logFile', $logPath
    '-captureDelay', $DelaySeconds
    '-captureSize', $Width
)

$process = Start-Process -FilePath $unity -ArgumentList $unityArgs -PassThru

$deadline = (Get-Date).AddSeconds($TimeoutSeconds)
$status = $null

while ((Get-Date) -lt $deadline) {
    # The marker, never the PNG: a PNG exists on disk the moment writing begins, so polling
    # for the image races the write and yields a truncated file.
    if (Test-Path $markerPath) {
        $status = (Get-Content $markerPath -Raw).Trim()
        break
    }

    if ($process.HasExited) {
        Write-Warning "Unity exited before writing the marker (exit code $($process.ExitCode))."
        break
    }

    Start-Sleep -Milliseconds 500
}

if (-not $process.HasExited) {
    # Killed rather than asked to quit: leaving play mode in batchmode goes through a domain
    # reload that does not reliably terminate, which is why RunVerification tells callers to
    # poll for the file instead of waiting on the process.
    Stop-Process -Id $process.Id -Force

    # Unity writes Temp\UnityLockfile on open and removes it on a CLEAN exit. Killing it
    # skips that, so the lock survives and every later run refuses to start against a
    # project nothing actually has open. Removing it here is safe precisely because we
    # killed the process that owned it - do not remove a lock this script did not orphan.
    Start-Sleep -Milliseconds 500
    if (Test-Path $lockFile) { Remove-Item $lockFile -Force -ErrorAction SilentlyContinue }
}

if ($status -eq 'ok' -and (Test-Path $imagePath)) {
    $size = (Get-Item $imagePath).Length
    $kb = [math]::Round($size / 1024)
    Write-Host "OK  $imagePath ($kb KB)"
    exit 0
}

if ($null -ne $status) {
    Write-Warning "The capture ran but reported: $status"
} else {
    Write-Warning "No capture marker appeared within $TimeoutSeconds s."
}

Write-Warning "Editor log: $logPath"
if (Test-Path $logPath) {
    Write-Host '--- last 30 log lines ---'
    Get-Content $logPath -Tail 30
}
exit 1

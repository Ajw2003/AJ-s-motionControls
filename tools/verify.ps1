<#
.SYNOPSIS
    One command that checks every layer of the project before a human reviews a change:
    the coding-standards audit, then relay and controller tests and lint.

.DESCRIPTION
    Written so an agent can catch its own mistakes instead of handing every check to a person.
    Each stage reports PASS or FAIL with the reason, and the script exits 0 only when every
    stage that ran passed. Requires node/npm on PATH.

.PARAMETER Skip
    Stages to leave out: standards, relay, controller. A skipped stage is reported as
    SKIP and does not fail the run. A stage that could not run because something is missing is
    a FAIL.

.EXAMPLE
    powershell -NoProfile -ExecutionPolicy Bypass -File tools/verify.ps1

.EXAMPLE
    powershell -NoProfile -ExecutionPolicy Bypass -File tools/verify.ps1 -Skip standards
#>
[CmdletBinding()]
param(
    [string[]] $Skip = @()
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Continue'

# Split and validated here rather than with [ValidateSet]. Under `powershell -File`, the form
# every doc uses (`-Skip relay,controller`) arrives as ONE comma-joined string, not an array,
# so ValidateSet rejected a perfectly correct command before the script ran.
$KnownStages = @('standards', 'relay', 'controller')
$Skip = @($Skip | ForEach-Object { $_ -split ',' } | ForEach-Object { $_.Trim().ToLowerInvariant() } | Where-Object { $_ })
$unknownStages = @($Skip | Where-Object { $KnownStages -notcontains $_ })
if ($unknownStages.Count -gt 0) {
    Write-Host ("Unknown -Skip stage(s): {0}. Known: {1}" -f ($unknownStages -join ', '), ($KnownStages -join ', ')) -ForegroundColor Red
    exit 2
}

$RepoRoot = Split-Path -Parent $PSScriptRoot
$Results  = New-Object System.Collections.Generic.List[object]

# Keep the CLI from pausing on its one-time analytics prompt or printing update notices into
# output this script parses. Neither records a choice on the user's behalf.
$env:UNITY_NO_CONSENT_PROMPT = '1'
$env:UNITY_NO_UPDATE_CHECK   = '1'
$env:UNITY_NO_CRASH_REPORT   = '1'

function Write-Heading([string] $Text) {
    Write-Host ''
    Write-Host $Text -ForegroundColor Cyan
    Write-Host ('-' * $Text.Length) -ForegroundColor Cyan
}

function Add-Result([string] $Stage, [string] $Outcome, [string] $Detail) {
    $Results.Add([pscustomobject]@{ Stage = $Stage; Outcome = $Outcome; Detail = $Detail })
    $colour = switch ($Outcome) { 'PASS' { 'Green' } 'SKIP' { 'DarkGray' } default { 'Red' } }
    Write-Host ("  {0}  {1}" -f $Outcome, $Detail) -ForegroundColor $colour
}

# ---------------------------------------------------------------------------------------------
# npm stages
# ---------------------------------------------------------------------------------------------

# npm.cmd rather than npm: under a restrictive execution policy the npm.ps1 shim is refused,
# and the .cmd shim is present in every Node install on Windows.
function Invoke-Npm([string] $Folder, [string[]] $NpmArgs) {
    $output = & npm.cmd --prefix (Join-Path $RepoRoot $Folder) @NpmArgs 2>&1 | ForEach-Object { "$_" }
    return [pscustomobject]@{ ExitCode = $LASTEXITCODE; Lines = @($output) }
}

function Show-Tail([string[]] $Lines, [int] $Count) {
    $Lines | Select-Object -Last $Count | ForEach-Object { Write-Host "      $_" -ForegroundColor DarkGray }
}

function Test-Relay {
    Write-Heading 'Relay (relay/)'

    $run = Invoke-Npm 'relay' @('test')
    $text = $run.Lines -join "`n"
    $pass = if ($text -match '(?m)^# pass (\d+)') { [int]$Matches[1] } else { -1 }
    $fail = if ($text -match '(?m)^# fail (\d+)') { [int]$Matches[1] } else { -1 }
    $skipped = if ($text -match '(?m)^# skipped (\d+)') { [int]$Matches[1] } else { 0 }

    if ($run.ExitCode -eq 0 -and $fail -eq 0) {
        Add-Result 'relay tests' 'PASS' ("{0} passed, {1} skipped" -f $pass, $skipped)
    } else {
        Add-Result 'relay tests' 'FAIL' ("exit {0}, {1} failed" -f $run.ExitCode, $fail)
        $run.Lines | Where-Object { $_ -match 'not ok|FAIL' } | Select-Object -First 20 |
            ForEach-Object { Write-Host "      $_" -ForegroundColor Red }
        Show-Tail $run.Lines 15
    }

    $lint = Invoke-Npm 'relay' @('run', 'lint')
    if ($lint.ExitCode -eq 0) { Add-Result 'relay lint' 'PASS' 'eslint clean' }
    else { Add-Result 'relay lint' 'FAIL' "eslint exit $($lint.ExitCode)"; Show-Tail $lint.Lines 25 }
}

function Test-Controller {
    Write-Heading 'Controller (controller/)'

    # The test script may chain several files, each printing its own PASS/FAIL summary, so the
    # exit code is the verdict and the FAIL lines are the explanation.
    $run = Invoke-Npm 'controller' @('test')
    $failLines = @($run.Lines | Where-Object { $_ -match '^\s*FAIL' })

    if ($run.ExitCode -eq 0 -and $failLines.Count -eq 0) {
        $summaries = @($run.Lines | Where-Object { $_ -match '^PASS' })
        Add-Result 'controller tests' 'PASS' ("{0} suite summary line(s) green" -f $summaries.Count)
    } else {
        Add-Result 'controller tests' 'FAIL' ("exit {0}, {1} FAIL line(s)" -f $run.ExitCode, $failLines.Count)
        $failLines | Select-Object -First 20 | ForEach-Object { Write-Host "      $_" -ForegroundColor Red }
        if ($failLines.Count -eq 0) { Show-Tail $run.Lines 15 }
    }

    $lint = Invoke-Npm 'controller' @('run', 'lint')
    if ($lint.ExitCode -eq 0) { Add-Result 'controller lint' 'PASS' 'eslint clean' }
    else { Add-Result 'controller lint' 'FAIL' "eslint exit $($lint.ExitCode)"; Show-Tail $lint.Lines 25 }
}

# ---------------------------------------------------------------------------------------------
# Standards stage
# ---------------------------------------------------------------------------------------------

# The project's own coding-standards audit (tools/audit/check-standards.js). Reads files only.
function Test-Standards {
    Write-Heading 'Coding standards (tools/audit/check-standards.js)'

    $script = Join-Path $RepoRoot 'tools\audit\check-standards.js'
    $output = & node $script 2>&1 | ForEach-Object { "$_" }
    $exitCode = $LASTEXITCODE
    $text = @($output) -join "`n"
    $failLines = @($output | Where-Object { $_ -match '^\[FAIL\]' })

    if ($exitCode -eq 0 -and $failLines.Count -eq 0) {
        $detail = if ($text -match '(\d+) checks, (\d+) failing') { "{0} checks, 0 failing" -f $Matches[1] } else { 'all checks pass' }
        Add-Result 'standards' 'PASS' $detail
    } else {
        Add-Result 'standards' 'FAIL' ("exit {0}, {1} failing check(s)" -f $exitCode, $failLines.Count)
        $failLines | ForEach-Object { Write-Host "      $_" -ForegroundColor Red }
    }
}

Write-Host ("Motion controls verify - {0}" -f $RepoRoot)

foreach ($stage in @('standards', 'relay', 'controller')) {
    if ($Skip -contains $stage) {
        Write-Heading $stage
        Add-Result $stage 'SKIP' 'skipped by -Skip'
        continue
    }
    switch ($stage) {
        'standards'  { Test-Standards }
        'relay'      { Test-Relay }
        'controller' { Test-Controller }
    }
}

Write-Heading 'Summary'
$Results | ForEach-Object {
    $colour = switch ($_.Outcome) { 'PASS' { 'Green' } 'SKIP' { 'DarkGray' } default { 'Red' } }
    Write-Host ("  {0,-4}  {1,-17} {2}" -f $_.Outcome, $_.Stage, $_.Detail) -ForegroundColor $colour
}

$failures = @($Results | Where-Object { $_.Outcome -eq 'FAIL' }).Count
Write-Host ''
if ($failures -eq 0) {
    Write-Host 'VERIFY PASSED' -ForegroundColor Green
    exit 0
}
Write-Host ("VERIFY FAILED - {0} stage(s)" -f $failures) -ForegroundColor Red
exit 1

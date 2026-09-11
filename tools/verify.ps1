<#
.SYNOPSIS
    One command that checks every layer of the project before a human reviews a change:
    relay tests and lint, controller tests and lint, and - through the Unity Editor that is
    already open - a C# compile, new Console errors, and the EditMode test suite.

.DESCRIPTION
    Written so an agent can catch its own mistakes instead of handing every check to a person.
    Each stage reports PASS or FAIL with the reason, and the script exits 0 only when every
    stage that ran passed.

    The Unity half never closes the Editor, and that is the point of how it works. Unity holds
    an exclusive lock on the project, so a headless `unity test` cannot run while the Editor is
    open (Assets/CLAUDE.md). Instead this talks to the open Editor through the Unity CLI and the
    com.unity.pipeline package, which serve compile, console and test commands without a
    domain reload of their own.

    It never focuses the Editor, never enters or leaves Play mode, and never clears the
    Console. Console errors are counted from a cursor taken at the start of the run, so errors
    already sitting in the Console from earlier work are not blamed on this change.

    Requirements, checked and reported by name if missing:
      - node/npm on PATH (relay and controller stages)
      - Unity CLI: `unity` on PATH, or %LOCALAPPDATA%\Unity\bin\unity.exe
      - Unity Editor open on this project with com.unity.pipeline (Packages/manifest.json)

.PARAMETER Skip
    Stages to leave out: standards, relay, controller, unity. A skipped stage is reported as
    SKIP and does not fail the run. A stage that could not run because something is missing is
    a FAIL.

.PARAMETER TestFilter
    Only run EditMode tests whose full name contains this text (Unity's own partial match).
    An empty result is a FAIL, so a mistyped filter cannot pass silently.

.PARAMETER TimeoutSeconds
    Upper bound for waiting on the Editor: becoming ready, compiling, and running tests.
    Default 300.

.EXAMPLE
    powershell -NoProfile -ExecutionPolicy Bypass -File tools/verify.ps1

.EXAMPLE
    powershell -NoProfile -ExecutionPolicy Bypass -File tools/verify.ps1 -Skip relay,controller -TestFilter TurnOrder
#>
[CmdletBinding()]
param(
    [string[]] $Skip = @(),
    [string]   $TestFilter = '',
    [int]      $TimeoutSeconds = 300
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Continue'

# Split and validated here rather than with [ValidateSet]. Under `powershell -File`, the form
# every doc uses (`-Skip relay,controller`) arrives as ONE comma-joined string, not an array,
# so ValidateSet rejected a perfectly correct command before the script ran.
$KnownStages = @('standards', 'relay', 'controller', 'unity')
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
# Unity stage
# ---------------------------------------------------------------------------------------------

function Resolve-UnityCli {
    $onPath = Get-Command unity -ErrorAction SilentlyContinue
    if ($onPath) { return $onPath.Source }

    # The installer adds this folder to the user PATH, but a shell opened before the install
    # still has the old PATH. Looking here directly keeps the script working in that shell.
    $default = Join-Path $env:LOCALAPPDATA 'Unity\bin\unity.exe'
    if (Test-Path $default) { return $default }
    return $null
}

# Runs one Pipeline command against this project's Editor and returns what came back.
# `result` is sometimes an object and sometimes a JSON document encoded as a string
# (recompile_status does the latter), so both are normalised to an object here.
function Invoke-UnityCommand([string] $Cli, [string] $Name, [string[]] $CommandArgs = @(), [int] $WaitSeconds = 30) {
    $raw = & $Cli command $Name @CommandArgs --project-path $RepoRoot --timeout $WaitSeconds --format json --no-banner 2>$null
    $exitCode = $LASTEXITCODE
    $text = (@($raw) -join "`n").Trim()

    $envelope = $null
    try { $envelope = $text | ConvertFrom-Json } catch { $envelope = $null }

    if ($null -eq $envelope) {
        return [pscustomobject]@{ Ok = $false; Result = $null; Error = "no JSON from unity (exit $exitCode)" }
    }
    if (-not $envelope.success) {
        $message = if ($envelope.errors -and @($envelope.errors).Count -gt 0) { @($envelope.errors)[0].message } else { "exit $exitCode" }
        return [pscustomobject]@{ Ok = $false; Result = $null; Error = $message }
    }

    $result = $envelope.data.result
    if ($result -is [string] -and $result.TrimStart().StartsWith('{')) {
        try { $result = $result | ConvertFrom-Json } catch { }
    }
    return [pscustomobject]@{ Ok = $true; Result = $result; Error = $null }
}

function Get-Prop($Object, [string] $Name, $Default = $null) {
    if ($null -eq $Object) { return $Default }
    $property = $Object.PSObject.Properties[$Name]
    if ($null -eq $property) { return $Default }
    return $property.Value
}

# Polls until the Editor answers and is idle. Connection failures are expected for a few
# seconds after a compile - the domain reload restarts the Pipeline server - so they are
# retried rather than reported, up to the deadline.
function Wait-EditorIdle([string] $Cli, [datetime] $Deadline) {
    $lastError = 'never answered'
    do {
        $status = Invoke-UnityCommand $Cli 'editor_status'
        if ($status.Ok) {
            $busy = (Get-Prop $status.Result 'compiling' $false) -or (Get-Prop $status.Result 'domainReloadInProgress' $false)
            if (-not $busy -and (Get-Prop $status.Result 'status' '') -eq 'ready') { return $status.Result }
            $lastError = 'Editor busy (compiling or reloading)'
        } else {
            $lastError = $status.Error
        }
        Start-Sleep -Seconds 2
    } while ((Get-Date) -lt $Deadline)

    throw "Editor not ready before timeout: $lastError"
}

function Test-Unity {
    Write-Heading 'Unity (through the open Editor)'

    $cli = Resolve-UnityCli
    if (-not $cli) {
        Add-Result 'unity' 'FAIL' 'Unity CLI not found on PATH or at %LOCALAPPDATA%\Unity\bin\unity.exe'
        return
    }

    $deadline = (Get-Date).AddSeconds($TimeoutSeconds)

    try {
        $state = Wait-EditorIdle $cli $deadline
    } catch {
        Add-Result 'unity editor' 'FAIL' ("{0}. Is Unity open on this project? If it is, run 'unity pipeline list' - Safe Mode (compile errors) hides the Pipeline server." -f $_.Exception.Message)
        return
    }

    # Tests in Play mode would run against a live game, and leaving Play mode is the person's
    # call, not this script's.
    $playMode = Get-Prop $state 'playMode' 'stopped'
    if ($playMode -ne 'stopped') {
        Add-Result 'unity editor' 'FAIL' "Editor is in Play mode ($playMode) - exit Play mode, then re-run"
        return
    }
    Add-Result 'unity editor' 'PASS' ("reachable, Unity {0}" -f (Get-Prop $state 'unityVersion' '?'))

    # Cursor BEFORE compiling, so only errors raised by this run count.
    $cursorProbe = Invoke-UnityCommand $cli 'console' @('--level', 'error', '--tail', '1')
    $cursor = if ($cursorProbe.Ok) { [long](Get-Prop $cursorProbe.Result 'cursor' -1) } else { -1 }

    # --- compile ---
    $compile = Invoke-UnityCommand $cli 'recompile'
    if (-not $compile.Ok) {
        Add-Result 'unity compile' 'FAIL' "recompile request failed: $($compile.Error)"
        return
    }

    $compileState = [string](Get-Prop $compile.Result 'status' '')
    $compileResult = $compile.Result
    while ($compileState -notin @('completed', 'up_to_date', 'failed', 'idle') -and (Get-Date) -lt $deadline) {
        Start-Sleep -Seconds 2
        $poll = Invoke-UnityCommand $cli 'recompile_status'
        if ($poll.Ok) {
            $compileResult = $poll.Result
            $compileState = [string](Get-Prop $poll.Result 'status' '')
        }
    }

    try { $null = Wait-EditorIdle $cli $deadline } catch {
        Add-Result 'unity compile' 'FAIL' $_.Exception.Message
        return
    }

    $finalStatus = Invoke-UnityCommand $cli 'recompile_status'
    if ($finalStatus.Ok) { $compileResult = $finalStatus.Result }

    $compileErrors = @(Get-Prop $compileResult 'errors' @())
    $compileFailed = [bool](Get-Prop $compileResult 'failed' $false)
    if ($compileFailed -or $compileErrors.Count -gt 0) {
        Add-Result 'unity compile' 'FAIL' ("{0} compile error(s)" -f $compileErrors.Count)
        $compileErrors | Select-Object -First 20 | ForEach-Object {
            Write-Host ("      {0}" -f (($_ | ConvertTo-Json -Compress -Depth 5))) -ForegroundColor Red
        }
        return
    }
    Add-Result 'unity compile' 'PASS' ("scripts {0}" -f ($(if ($compileState) { $compileState } else { 'compiled' })))

    # --- console errors raised during this run ---
    $consoleArgs = @('--level', 'error', '--tail', '200')
    if ($cursor -ge 0) { $consoleArgs += @('--since', "$cursor") }
    $console = Invoke-UnityCommand $cli 'console' $consoleArgs
    if (-not $console.Ok) {
        Add-Result 'unity console' 'FAIL' "could not read the Console: $($console.Error)"
    } else {
        $entries = @(Get-Prop $console.Result 'entries' @())
        if ($entries.Count -eq 0) {
            Add-Result 'unity console' 'PASS' 'no new errors'
        } else {
            Add-Result 'unity console' 'FAIL' ("{0} new error(s) in the Console" -f $entries.Count)
            $entries | Select-Object -First 10 | ForEach-Object {
                $line = ($_ | ConvertTo-Json -Compress -Depth 4)
                if ($line.Length -gt 400) { $line = $line.Substring(0, 400) + ' ...' }
                Write-Host "      $line" -ForegroundColor Red
            }
        }
    }

    # --- EditMode tests ---
    # Synchronous: run_tests returns the full result. The CLI's own wait is set a little past
    # the Editor's so a slow suite reports its failures rather than a client timeout.
    $testArgs = @('--mode', 'editor')
    if ($TestFilter) { $testArgs += @('--filter', $TestFilter) }
    $remaining = [int][Math]::Max(60, ($deadline - (Get-Date)).TotalSeconds)
    $tests = Invoke-UnityCommand $cli 'run_tests' $testArgs ($remaining + 30)

    if (-not $tests.Ok) {
        Add-Result 'unity tests' 'FAIL' "run_tests failed: $($tests.Error)"
        return
    }

    $summary = Get-Prop $tests.Result 'Summary'
    $total   = [int](Get-Prop $summary 'Total' 0)
    $passed  = [int](Get-Prop $summary 'Passed' 0)
    $failed  = [int](Get-Prop $summary 'Failed' 0)
    $skipped = [int](Get-Prop $summary 'Skipped' 0)

    if ($total -eq 0) {
        Add-Result 'unity tests' 'FAIL' ("no EditMode tests ran{0}" -f ($(if ($TestFilter) { " for filter '$TestFilter'" } else { '' })))
        return
    }

    if ($failed -gt 0) {
        Add-Result 'unity tests' 'FAIL' ("{0} of {1} failed" -f $failed, $total)
        @(Get-Prop $tests.Result 'Results' @()) | Where-Object { (Get-Prop $_ 'Status' '') -eq 'Failed' } |
            Select-Object -First 20 | ForEach-Object {
                Write-Host ("      {0}" -f (Get-Prop $_ 'FullName' '?')) -ForegroundColor Red
                $message = [string](Get-Prop $_ 'Message' '')
                if ($message) { Write-Host ("        {0}" -f ($message.Trim() -replace '\s+', ' ')) -ForegroundColor Red }
            }
        return
    }

    Add-Result 'unity tests' 'PASS' ("{0} passed, {1} skipped (EditMode)" -f $passed, $skipped)
}

# ---------------------------------------------------------------------------------------------
# run
# ---------------------------------------------------------------------------------------------

# The project's own coding-standards audit (tools/audit/check-standards.js). First because it
# is the cheapest stage and reads files only, so a naming or structure slip is reported before
# anything spends minutes compiling.
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

Write-Host ("RockSkipping verify - {0}" -f $RepoRoot)

foreach ($stage in @('standards', 'relay', 'controller', 'unity')) {
    if ($Skip -contains $stage) {
        Write-Heading $stage
        Add-Result $stage 'SKIP' 'skipped by -Skip'
        continue
    }
    switch ($stage) {
        'standards'  { Test-Standards }
        'relay'      { Test-Relay }
        'controller' { Test-Controller }
        'unity'      { Test-Unity }
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

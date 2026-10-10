#requires -Version 5.1
<#
.SYNOPSIS
    Turns on DSU input in Sudachi and Dolphin so they see the phone (#24).

.DESCRIPTION
    Changes only the DSU keys below; existing player mappings are never touched. A dated
    backup is taken before the first change, the file's newline style is kept, an emulator
    that is running is skipped (it would overwrite the edit when it exits), and a second
    run changes nothing. A file whose layout is not recognised is reported and left alone.

    Dot-source it for Invoke-EmulatorSetup; run it directly to print the results as JSON.
#>
[CmdletBinding()]
param()
Set-StrictMode -Version Latest

$DsuServer = '127.0.0.1:26760'

function Read-Config([string] $Path) {
    $text = [IO.File]::ReadAllText($Path)
    $newline = if ($text.Contains("`r`n")) { "`r`n" } else { "`n" }
    return @{ Text = $text; Newline = $newline }
}

function Save-Config([string] $Path, [string] $Text) {
    $stamp = Get-Date -Format 'yyyyMMdd-HHmmss'
    Copy-Item -LiteralPath $Path -Destination "$Path.ajmotion-$stamp.bak"
    [IO.File]::WriteAllText($Path, $Text, (New-Object Text.UTF8Encoding $false))
}

# Replaces the value of one `key=value` line; returns $null when the key isn't there.
function Set-IniValue([string] $Text, [string] $Key, [string] $Value) {
    $pattern = '(?m)^(' + [regex]::Escape($Key) + ')=[^\r\n]*'
    if ($Text -notmatch $pattern) { return $null }
    $regex = New-Object Text.RegularExpressions.Regex $pattern
    return $regex.Replace($Text, { param($m) "$($m.Groups[1].Value)=$Value" }, 1)
}

function Set-SudachiDsu([string] $Path) {
    $config = Read-Config $Path
    $text = $config.Text
    foreach ($pair in @(@('enable_udp_controller\default', 'false'), @('enable_udp_controller', 'true'))) {
        $text = Set-IniValue $text $pair[0] $pair[1]
        if ($null -eq $text) { return 'unknown', "no $($pair[0]) line in $Path; left as it was" }
    }
    $servers = [regex]::Match($text, '(?m)^udp_input_servers=([^\r\n]*)')
    if ($servers.Success -and $servers.Groups[1].Value -notmatch [regex]::Escape($DsuServer)) {
        $list = if ($servers.Groups[1].Value) { "$($servers.Groups[1].Value),$DsuServer" } else { $DsuServer }
        $text = Set-IniValue $text 'udp_input_servers' $list
        $withDefault = Set-IniValue $text 'udp_input_servers\default' 'false'
        if ($null -ne $withDefault) { $text = $withDefault }
    }
    if ($text -ceq $config.Text) { return 'already', 'UDP controllers already on' }
    Save-Config $Path $text
    return 'changed', 'turned on UDP controllers'
}

function Set-DolphinDsu([string] $ConfigDir) {
    $path = Join-Path $ConfigDir 'DSUClient.ini'
    if (-not (Test-Path -LiteralPath $path)) {
        [IO.File]::WriteAllText($path, "[Server]`r`nEnabled = True`r`nEntries = AJ Motion:${DsuServer};`r`n", (New-Object Text.UTF8Encoding $false))
        return 'changed', 'added the AJ Motion DSU server'
    }
    $config = Read-Config $path
    $text = $config.Text
    $entries = [regex]::Match($text, '(?m)^Entries = ([^\r\n]*)')
    $enabled = [regex]::Match($text, '(?m)^Enabled = [^\r\n]*')
    if (-not $entries.Success -or -not $enabled.Success) { return 'unknown', "unexpected layout in $path; left as it was" }
    if ($entries.Groups[1].Value -notmatch (':' + [regex]::Escape($DsuServer) + ';')) {
        $text = $text.Remove($entries.Index, $entries.Length).Insert($entries.Index, "Entries = $($entries.Groups[1].Value)AJ Motion:${DsuServer};")
    }
    $text = ([regex] '(?m)^Enabled = [^\r\n]*').Replace($text, 'Enabled = True', 1)
    if ($text -ceq $config.Text) { return 'already', 'DSU server already set up' }
    Save-Config $path $text
    return 'changed', 'turned on the DSU server'
}

function Invoke-EmulatorSetup {
    param(
        [string] $SudachiConfig = (Join-Path $env:APPDATA 'sudachi\config\qt-config.ini'),
        [string] $DolphinConfigDir = (Join-Path $env:APPDATA 'Dolphin Emulator\Config'),
        [scriptblock] $IsRunning = { param($name) [bool](Get-Process -Name $name -ErrorAction SilentlyContinue) }
    )
    $jobs = @(
        @{ Name = 'Sudachi'; Process = 'sudachi'; Target = $SudachiConfig; Run = { Set-SudachiDsu $SudachiConfig } },
        @{ Name = 'Dolphin'; Process = 'Dolphin'; Target = $DolphinConfigDir; Run = { Set-DolphinDsu $DolphinConfigDir } }
    )
    foreach ($job in $jobs) {
        if (-not (Test-Path -LiteralPath $job.Target)) {
            $status, $detail = 'missing', 'not installed'
        } elseif (& $IsRunning $job.Process) {
            $status, $detail = 'running', "close $($job.Name), then start AJ Motion again"
        } else {
            try {
                $status, $detail = & $job.Run
            } catch {
                $status, $detail = 'unknown', "could not update its settings: $($_.Exception.Message)"
            }
        }
        [pscustomobject]@{ emulator = $job.Name; status = $status; detail = $detail }
    }
}

if ($MyInvocation.InvocationName -ne '.') { Invoke-EmulatorSetup | ConvertTo-Json -Compress }

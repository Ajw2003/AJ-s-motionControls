#requires -Version 5.1
<#
.SYNOPSIS
    Reads magic-domain.txt the same way Assets/Scripts/Net/MagicDomain.cs does.

.DESCRIPTION
    Dot-source this; it defines functions and does nothing else.

    There are two readers of this file by necessity - the game's (C#, MagicDomain.cs) and
    PowerShell's - because the scripts run outside Unity and cannot call into it. There is no
    reason for there to be three, which is what was starting to happen: setup-offline.ps1 grew
    its own copy, and verify-stack.ps1 would have grown another. One copy per language.

    Format, one value per line, blank lines and '#' comments ignored: line 1 is the hostname,
    any further lines are cert/key URL pairs that only MagicDomain.cs consumes. Never throws -
    a missing or malformed file falls back to the compiled default, exactly as the C# does,
    because neither a launch nor a diagnostic should be blocked by a hand-edited file.
#>

function Get-MagicDomainDefault { 'play.skiprocking.ca' }

function Get-MagicDomainPath {
    # deploy/ sits beside Assets/, and magic-domain.txt lives at the repo root next to
    # relay-url.txt - the same siting MagicDomain.cs resolves to from Application.dataPath.
    Join-Path (Split-Path -Parent $PSScriptRoot) 'magic-domain.txt'
}

function Get-MagicDomainHostName {
    $default = Get-MagicDomainDefault
    $path = Get-MagicDomainPath

    if (-not (Test-Path $path)) { return $default }

    try {
        $lines = @(Get-Content $path -ErrorAction Stop |
            ForEach-Object { $_.Trim() } |
            Where-Object { $_.Length -gt 0 -and $_[0] -ne '#' })
        if ($lines.Count -eq 0) { return $default }
        return $lines[0]
    } catch {
        Write-Warning "Could not read magic-domain.txt, using the default: $($_.Exception.Message)"
        return $default
    }
}

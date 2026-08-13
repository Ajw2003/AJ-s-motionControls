#!/usr/bin/env pwsh
#requires -Version 5.1
<#
.SYNOPSIS
    Serves the controller page from this machine over HTTPS, so a phone gets a secure
    context and its motion sensors unlock. Automates deploy/LOCAL-HTTPS-SPIKE.md.

.DESCRIPTION
    Detects this machine's LAN IP, borrows the published *.local-ip.sh certificate for
    the matching IP-encoded name, points Unity at it, starts the relay with TLS, and
    proves the whole chain works before you pick up the phone.

    Everything it writes is either gitignored or lives outside the repo, and it is all
    undone on Ctrl+C.

    Two things it deliberately does NOT do:

      * It never touches the Windows firewall. The prompt is a one-shot measurement per
        machine (spike doc Step 5) and creating a rule would spend it silently.
      * It never puts the private key inside the repo. That key is published on purpose,
        .gitignore has no *.pem rule by default, and .dockerignore is an allowlist that
        would bake a key left in relay/ straight into the Fly image.

.PARAMETER Ip
    Use this LAN IP instead of detecting one. Needed when the machine has several
    plausible interfaces, since guessing wrong fails exactly like a broken certificate.

.PARAMETER Port
    Port for the TLS relay. Default 8443.

.PARAMETER SetupOnly
    Verify the chain, then stop the relay and exit leaving the config in place, so
    Unity's LocalServices starts the relay itself when you press Play. Leaves residue
    that a normal run would have cleaned up - it says so on the way out.

.EXAMPLE
    ./deploy/local-https.ps1
.EXAMPLE
    ./deploy/local-https.ps1 -Ip 10.0.0.213 -Port 9443
.EXAMPLE
    ./deploy/local-https.ps1 -SetupOnly
#>
[CmdletBinding()]
param(
    [string] $Ip,
    [ValidateRange(1, 65535)]
    [int]    $Port = 8443,
    [switch] $SetupOnly
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

# Runs on both PowerShell editions, because which one a double-click or an IDE terminal
# hands you is not something the person running this chose. $IsWindowsPowerShell is the
# test the few version-dependent spots below share.
$IsWindowsPowerShell = $PSVersionTable.PSVersion.Major -lt 6

# Windows PowerShell inherits a protocol list that can predate the TLS 1.2 local-ip.sh
# requires, and the failure reads as "could not reach local-ip.sh". Additive on purpose -
# never turn off something this machine has already decided it wants. PowerShell 7
# negotiates on its own and is left alone.
if ($IsWindowsPowerShell) {
    [Net.ServicePointManager]::SecurityProtocol =
        [Net.ServicePointManager]::SecurityProtocol -bor [Net.SecurityProtocolType]::Tls12
}

# ---------------------------------------------------------------------------- paths

$Root      = Split-Path $PSScriptRoot -Parent
$RelayDir  = Join-Path $Root 'relay'
$UrlFile   = Join-Path $Root 'relay-url.txt'
$EnvFile   = Join-Path $Root 'relay-env.txt'

# Outside the repo, on purpose. See the note in .DESCRIPTION.
$Scratch   = Join-Path $env:TEMP 'rockskip-local-https'
$CertPath  = Join-Path $Scratch 'server.pem'
$KeyPath   = Join-Path $Scratch 'server.key'
$UrlBackup = Join-Path $Scratch 'relay-url.txt.bak'
$EnvBackup = Join-Path $Scratch 'relay-env.txt.bak'

$script:RelayProcess  = $null
$script:OwnsRelay     = $false
$script:ConfigWritten = $false

# Lets a later run tell its own leftovers from your configuration. relay-url.txt cannot
# carry this - RelayClient trims the whole file and treats it as one URL - so the env
# file speaks for both.
$ConfigMarker = 'Written by deploy/local-https.ps1'

# ------------------------------------------------------------------------- output

function Write-Step { param([string] $Text) Write-Host "`n== $Text" -ForegroundColor Cyan }
function Write-Ok   { param([string] $Text) Write-Host "   PASS  $Text" -ForegroundColor Green }
function Write-Info { param([string] $Text) Write-Host "   $Text" -ForegroundColor DarkGray }
function Write-Warn { param([string] $Text) Write-Host "   WARN  $Text" -ForegroundColor Yellow }

function Fail {
    param([string] $Text, [string] $Hint)
    Write-Host "   FAIL  $Text" -ForegroundColor Red
    if ($Hint) { Write-Host "         $Hint" -ForegroundColor Red }
    throw $Text
}

# -------------------------------------------------------------------------- shells

# Two of the places Windows PowerShell and PowerShell 7 disagree - the third is the TLS
# line above. All three are silent until they are fatal and none of them reads as a version
# problem when it goes, so the two worth naming are named here rather than left inline for
# the phases to explain.

function Write-Utf8NoBom {
    param([string] $Path, [string[]] $Lines)

    # Set-Content -Encoding utf8NoBOM is PowerShell 6+. Windows PowerShell has no spelling
    # for it at all - its 'UTF8' means with a BOM - so the only way to say this to both is
    # to go around the cmdlet. What the BOM would and would not cost is at the call site.
    #
    # [System.IO.File] resolves relative paths against the process working directory rather
    # than PowerShell's location, which are not the same thing; this makes the path absolute
    # before that can bite.
    $full = $ExecutionContext.SessionState.Path.GetUnresolvedProviderPathFromPSPath($Path)
    [System.IO.File]::WriteAllLines($full, $Lines, [System.Text.UTF8Encoding]::new($false))
}

function Stop-ProcessTree {
    param([System.Diagnostics.Process] $Process)

    if (-not $Process -or $Process.HasExited) { return }

    # Kill($true) - the whole tree - arrived with .NET Core 3.0, so Windows PowerShell,
    # which is .NET Framework, does not have the overload at all. taskkill /T is the same
    # idea and ships with Windows. Killing only node would orphan whatever it spawned.
    if ($IsWindowsPowerShell) {
        # A native command's stderr becomes an ErrorRecord, and $ErrorActionPreference is
        # Stop; this scope-local override keeps taskkill's own complaints from throwing
        # past the exit check below, which is the honest test of whether this worked.
        $ErrorActionPreference = 'Continue'
        & taskkill.exe /PID "$($Process.Id)" /T /F 2>&1 | Out-Null
    } else {
        $Process.Kill($true)
    }

    if (-not $Process.WaitForExit(5000)) {
        throw "Process $($Process.Id) is still running five seconds after being killed."
    }
}

# ------------------------------------------------------------------------ cleanup

function Invoke-Teardown {
    Write-Step 'Putting it back'

    if ($script:RelayProcess -and $script:OwnsRelay) {
        try {
            Stop-ProcessTree $script:RelayProcess
            Write-Info 'Relay stopped.'
        } catch {
            Write-Warn "Could not stop the relay: $($_.Exception.Message)"
        }
    } elseif ($script:RelayProcess) {
        Write-Info 'Left the relay running - it belongs to whoever started it.'
    }

    if ($script:ConfigWritten) {
        foreach ($pair in @(@($UrlFile, $UrlBackup), @($EnvFile, $EnvBackup))) {
            $live, $backup = $pair
            if (Test-Path $backup) {
                Move-Item $backup $live -Force
                Write-Info "Restored $(Split-Path $live -Leaf) to its previous contents."
            } elseif (Test-Path $live) {
                Remove-Item $live -Force
                Write-Info "Removed $(Split-Path $live -Leaf)."
            }
        }
    }

    # A published private key must never outlive the run that borrowed it.
    foreach ($f in @($CertPath, $KeyPath)) {
        if (Test-Path $f) { Remove-Item $f -Force }
    }
    Write-Info 'Borrowed certificate and key deleted.'
}

# ------------------------------------------------------------------------- phase A

function Test-Prerequisites {
    Write-Step 'Prerequisites'

    $node = Get-Command node -ErrorAction SilentlyContinue
    if (-not $node) {
        Fail 'Node.js is not on PATH.' 'Install Node 20 or newer from nodejs.org, then open a new terminal.'
    }

    $version = (& node --version).TrimStart('v')
    $major   = [int]($version -split '\.')[0]
    if ($major -lt 20) {
        Fail "Node $version is too old." 'relay/package.json requires Node 20 or newer.'
    }
    Write-Ok "Node $version"

    if (-not (Test-Path (Join-Path $RelayDir 'server.js'))) {
        Fail "No relay found at $RelayDir." 'Run this script from a full checkout.'
    }

    if (-not (Test-Path (Join-Path $RelayDir 'node_modules'))) {
        Write-Info 'First run - installing relay dependencies...'
        Push-Location $RelayDir
        try {
            & npm install --no-audit --no-fund
            if ($LASTEXITCODE -ne 0) { Fail "npm install failed (exit $LASTEXITCODE)." "Run it yourself in $RelayDir to see why." }
        } finally {
            Pop-Location
        }
    }
    Write-Ok 'Relay dependencies present'
}

# ------------------------------------------------------------------------- phase B

function Resolve-LanAddress {
    Write-Step 'LAN address'

    if ($Ip) {
        $parsed = [ipaddress]::Any
        if (-not [ipaddress]::TryParse($Ip, [ref] $parsed) -or $parsed.AddressFamily -ne 'InterNetwork') {
            Fail "-Ip '$Ip' is not an IPv4 address."
        }
        Write-Ok "$Ip (from -Ip)"
        return $Ip
    }

    # Interfaces with a default gateway, minus the virtual ones. The gateway test is
    # what discards the 169.254.x APIPA addresses that unplugged NICs, Bluetooth and
    # Hyper-V switches leave lying around - a naive sweep of Get-NetIPAddress returns
    # those too, and they look entirely plausible.
    $virtual = 'vEthernet|VMware|VirtualBox|Hyper-V|Loopback|TAP-|TeamViewer|ZeroTier|Tailscale|Npcap'

    $candidates = @(
        Get-NetIPConfiguration |
            Where-Object { $_.IPv4DefaultGateway -and $_.IPv4Address } |
            Where-Object { $_.InterfaceAlias -notmatch $virtual } |
            ForEach-Object {
                [pscustomobject]@{
                    Alias   = $_.InterfaceAlias
                    Address = @($_.IPv4Address)[0].IPAddress
                    Gateway = @($_.IPv4DefaultGateway)[0].NextHop
                }
            }
    )

    if ($candidates.Count -eq 0) {
        Write-Host ''
        Get-NetIPAddress -AddressFamily IPv4 |
            Select-Object InterfaceAlias, IPAddress, PrefixOrigin | Format-Table -AutoSize | Out-Host
        Fail 'No network interface has an IPv4 default gateway.' 'Join a Wi-Fi network or a phone hotspot first, then re-run.'
    }

    # Deliberately not picking one. A wrong IP here fails identically to a certificate
    # problem once you are standing at the phone, which is the confusion this script
    # exists to remove - so ambiguity is an error, never a guess.
    if ($candidates.Count -gt 1) {
        Write-Host ''
        $candidates | Format-Table -AutoSize | Out-Host
        Fail 'More than one interface could be the LAN one.' 'Re-run with -Ip <address> naming the one the phone can reach.'
    }

    Write-Ok "$($candidates[0].Address) on '$($candidates[0].Alias)' (gateway $($candidates[0].Gateway))"
    return $candidates[0].Address
}

# ------------------------------------------------------------------------- phase C

function Get-BorrowedCertificate {
    Write-Step 'Borrowed certificate'

    $null = New-Item -ItemType Directory -Force -Path $Scratch

    try {
        # -UseBasicParsing: Windows PowerShell otherwise hands the response to the Internet
        # Explorer engine, which throws outright on a machine where IE's first-run wizard
        # never happened. PowerShell 7 accepts the switch and ignores it.
        Invoke-WebRequest -Uri 'https://local-ip.sh/server.pem' -OutFile $CertPath -TimeoutSec 20 -UseBasicParsing | Out-Null
        Invoke-WebRequest -Uri 'https://local-ip.sh/server.key' -OutFile $KeyPath  -TimeoutSec 20 -UseBasicParsing | Out-Null
        Write-Ok 'Downloaded from local-ip.sh'
    } catch {
        # A phone hotspot with mobile data off is a real case, and the cached copy is
        # good for the rest of its 90 days.
        if (-not (Test-Path $CertPath) -or -not (Test-Path $KeyPath)) {
            Fail "Could not reach local-ip.sh and there is no cached copy: $($_.Exception.Message)"
        }
        Write-Warn "Download failed, using the cached copy: $($_.Exception.Message)"
    }

    $pem   = Get-Content $CertPath -Raw
    $match = [regex]::Match($pem, '-----BEGIN CERTIFICATE-----(?<b64>[\s\S]*?)-----END CERTIFICATE-----')
    if (-not $match.Success) { Fail 'The downloaded server.pem holds no certificate.' }

    $bytes = [Convert]::FromBase64String(($match.Groups['b64'].Value -replace '\s', ''))
    $cert  = [System.Security.Cryptography.X509Certificates.X509Certificate2]::new($bytes)

    # An expired borrowed certificate presents on the phone as a vague page-load
    # failure, which reads like a config mistake. Catch it here where it is legible.
    $daysLeft = [int]($cert.NotAfter - (Get-Date)).TotalDays
    if ($daysLeft -lt 0) {
        Fail "The certificate expired on $($cert.NotAfter.ToString('yyyy-MM-dd'))." 'Delete the cache in the scratch directory and re-run with a working connection.'
    }
    Write-Ok "Valid until $($cert.NotAfter.ToString('yyyy-MM-dd')) ($daysLeft days)"

    $san = $cert.Extensions | Where-Object { $_.Oid.Value -eq '2.5.29.17' }
    if ($san) {
        $names = $san.Format($false)
        if ($names -notmatch '\*\.local-ip\.sh') {
            Write-Warn "SAN does not mention *.local-ip.sh: $names"
        } else {
            Write-Ok 'SAN covers *.local-ip.sh'
        }
    }
}

# ------------------------------------------------------------------------- phase D

function Test-NameResolution {
    param([string] $Name, [string] $Expected)

    Write-Step 'DNS'

    try {
        $answers = @(Resolve-DnsName -Name $Name -Type A -ErrorAction Stop |
                     Where-Object { $_.Type -eq 'A' } |
                     ForEach-Object { $_.IPAddress })
    } catch {
        Fail "$Name did not resolve: $($_.Exception.Message)" 'Some routers refuse to return private addresses for public names (DNS rebinding protection). If that is what this is, the whole approach will not work on this network.'
    }

    if ($answers -notcontains $Expected) {
        $got = if ($answers.Count) { $answers -join ', ' } else { '(nothing)' }
        Fail "$Name resolved to $got, expected $Expected." 'This is the signature of DNS rebinding protection on the router - it strips private answers from public names. The phone will not be able to load the page on this network.'
    }

    Write-Ok "$Name -> $Expected"
}

# ------------------------------------------------------------------------- phase E

function Write-Configuration {
    param([string] $SocketUrl)

    Write-Step 'Configuration'

    # Backing up our own leftovers - which -SetupOnly deliberately produces - would both
    # overwrite a genuine backup an earlier run had taken and make teardown "restore"
    # residue instead of clearing it. Recognise them and leave the backups alone.
    $ours = (Test-Path $EnvFile) -and
            ((Get-Content $EnvFile -Raw -ErrorAction SilentlyContinue) -match [regex]::Escape($ConfigMarker))

    if ($ours) {
        Write-Info 'Config left by an earlier run - it goes away on exit rather than coming back.'
    } else {
        foreach ($pair in @(@($UrlFile, $UrlBackup), @($EnvFile, $EnvBackup))) {
            $live, $backup = $pair
            if (Test-Path $backup) { Remove-Item $backup -Force }
            if (Test-Path $live) {
                Copy-Item $live $backup -Force
                Write-Info "Saved your existing $(Split-Path $live -Leaf) - it comes back on exit."
            }
        }
    }

    $script:ConfigWritten = $true

    # Write-Utf8NoBom rather than Set-Content, so both editions emit the same bytes: their
    # -Encoding UTF8 disagree about the BOM, and utf8NoBOM, which does not, is 7-only.
    #
    # This used to be justified by RelayClient's .Trim() not removing a BOM. Half true - U+FEFF
    # is not whitespace to .NET - but File.ReadAllText strips it while decoding, before the
    # .Trim() ever sees it, and ReadAllLines does the same for LocalServices. So a BOM would
    # most likely pass unnoticed; it is just nothing this needs to start writing.
    Write-Utf8NoBom -Path $UrlFile -Lines $SocketUrl

    Write-Utf8NoBom -Path $EnvFile -Lines @(
        "# $ConfigMarker. Gitignored, per-machine, safe to delete."
        "PORT=$Port"
        "TLS_CERT=$CertPath"
        "TLS_KEY=$KeyPath"
    )

    Write-Ok "relay-url.txt -> $SocketUrl"
    Write-Ok "relay-env.txt -> PORT=$Port and the certificate paths"
}

function Start-Relay {
    param([string] $HealthUrl)

    Write-Step 'Relay'

    # Reuse whatever is already answering rather than fighting it for the port. This is
    # the same courtesy LocalServices extends, and it makes a leftover relay from an
    # earlier run harmless instead of fatal.
    if (Test-Health $HealthUrl) {
        Write-Ok 'A healthy relay is already listening - reusing it'
        $script:OwnsRelay = $false
        return
    }

    $listener = Get-NetTCPConnection -LocalPort $Port -State Listen -ErrorAction SilentlyContinue
    if ($listener) {
        $owner = (Get-Process -Id (@($listener)[0].OwningProcess) -ErrorAction SilentlyContinue).ProcessName
        Fail "Port $Port is held by something that is not a healthy relay ($owner)." "Stop it, or re-run with -Port <other>."
    }

    # The relay reads these from its environment; a child inherits ours.
    $env:PORT      = "$Port"
    $env:TLS_CERT  = $CertPath
    $env:TLS_KEY   = $KeyPath

    $script:RelayProcess = Start-Process node -ArgumentList 'server.js' `
        -WorkingDirectory $RelayDir -NoNewWindow -PassThru
    $script:OwnsRelay = $true

    $deadline = (Get-Date).AddSeconds(30)
    while ((Get-Date) -lt $deadline) {
        if ($script:RelayProcess.HasExited) {
            Fail "The relay exited immediately (code $($script:RelayProcess.ExitCode))." 'Its output is above.'
        }
        if (Test-Health $HealthUrl) {
            Write-Ok "Listening on $Port with TLS (pid $($script:RelayProcess.Id))"
            return
        }
        Start-Sleep -Milliseconds 300
    }

    Fail 'The relay started but never answered its health check.' `
         "It is listening, so the address is the suspect: $HealthUrl resolves somewhere this machine is not. Check the IP, or drop -Ip and let it detect one."
}

function Test-Health {
    param([string] $HealthUrl)
    try {
        # Full chain validation, deliberately - no -SkipCertificateCheck anywhere here.
        # -UseBasicParsing for the reason given in Get-BorrowedCertificate; without it a
        # missing IE engine fails this check and the run blames the address instead.
        $response = Invoke-WebRequest -Uri $HealthUrl -TimeoutSec 4 -UseBasicParsing
        return $response.StatusCode -eq 200
    } catch {
        return $false
    }
}

function Test-Chain {
    param([string] $HealthUrl, [string] $SocketUrl)

    Write-Step 'Verifying the chain'

    if (-not (Test-Health $HealthUrl)) {
        Fail "$HealthUrl did not validate." 'The certificate did not satisfy strict validation from this machine.'
    }
    Write-Ok "$HealthUrl validates under strict TLS"

    # test-deployed.js drives the real socket: upgrade, room create/join, AIM routing.
    # Its ws client validates certificates by default, which is the same path Unity's
    # ClientWebSocket takes - a browser being happy would not have proven this.
    Push-Location $RelayDir
    try {
        & node test-deployed.js $SocketUrl
        if ($LASTEXITCODE -ne 0) { Fail "The wss smoke test failed (exit $LASTEXITCODE)." }
    } finally {
        Pop-Location
    }
    Write-Ok 'wss upgrade and message routing'
}

# ------------------------------------------------------------------------- phase F

function Show-FirewallState {
    Write-Step 'Firewall (reporting only - nothing is changed)'

    $rules = @(
        foreach ($filter in (Get-NetFirewallApplicationFilter -ErrorAction SilentlyContinue |
                             Where-Object { $_.Program -like '*node*' })) {
            foreach ($rule in ($filter | Get-NetFirewallRule -ErrorAction SilentlyContinue)) {
                if ($rule.Direction -eq 'Inbound' -and $rule.Action -eq 'Allow' -and $rule.Enabled -eq 'True') {
                    [pscustomobject]@{ Rule = $rule.DisplayName; Profile = "$($rule.Profile)" }
                }
            }
        }
    )

    $category = @(Get-NetConnectionProfile | ForEach-Object { $_.NetworkCategory })
    Write-Info "This network is categorised: $($category -join ', ')"

    if (-not $rules) {
        Write-Info 'No inbound allow rule for node.exe. Windows will prompt on first connection.'
    } else {
        $profiles = ($rules | ForEach-Object { $_.Profile -split ',\s*' } | Sort-Object -Unique)
        Write-Info "node.exe inbound allow rules exist for: $($profiles -join ', ')"

        if ($profiles -notcontains 'Private' -and $profiles -notcontains 'Any' -and $category -contains 'Private') {
            Write-Warn 'No Private-profile rule, and you are on a Private network. Expect a prompt.'
        } elseif ($profiles -notcontains 'Private' -and $profiles -notcontains 'Any') {
            Write-Warn 'No Private-profile rule. Nothing prompts here, but a home network is usually Private - so the prompt will appear there and never in testing. That measurement is still unspent on this machine (spike doc Step 5); read the prompt before dismissing it.'
        }
    }
}

# ------------------------------------------------------------------------ the hold

function Wait-ForCtrlC {
    if ([Console]::IsInputRedirected) {
        Write-Info 'stdin is redirected - stop this process to tear down.'
        while (-not ($script:RelayProcess -and $script:RelayProcess.HasExited)) { Start-Sleep -Milliseconds 400 }
        return
    }

    # Taking Ctrl+C as input rather than as a signal is what makes teardown
    # deterministic - a finally block is not reliably reached on an interrupt.
    $previous = [Console]::TreatControlCAsInput
    [Console]::TreatControlCAsInput = $true
    try {
        while ($true) {
            if ([Console]::KeyAvailable) {
                $key = [Console]::ReadKey($true)
                if ($key.Key -eq 'C' -and ($key.Modifiers -band [ConsoleModifiers]::Control)) { return }
            }
            if ($script:OwnsRelay -and $script:RelayProcess.HasExited) {
                Write-Warn "The relay exited on its own (code $($script:RelayProcess.ExitCode))."
                return
            }
            Start-Sleep -Milliseconds 200
        }
    } finally {
        [Console]::TreatControlCAsInput = $previous
    }
}

# ---------------------------------------------------------------------------- main

try {
    Test-Prerequisites

    $address   = Resolve-LanAddress
    $name      = ($address -replace '\.', '-') + '.local-ip.sh'
    $socketUrl = "wss://${name}:$Port"
    $origin    = "https://${name}:$Port"

    Get-BorrowedCertificate
    Test-NameResolution -Name $name -Expected $address
    Write-Configuration -SocketUrl $socketUrl
    Start-Relay -HealthUrl "$origin/health"
    Test-Chain  -HealthUrl "$origin/health" -SocketUrl $socketUrl
    Show-FirewallState

    Write-Host ''
    Write-Host '  Everything the PC can prove on its own has passed.' -ForegroundColor Green
    Write-Host ''
    Write-Host "  Phone:  $origin" -ForegroundColor White
    Write-Host "  Unity:  press Play in Bootstrap and scan the QR" -ForegroundColor White
    Write-Host ''
    Write-Info "If the phone cannot load that, open https://${address}:$Port/health by raw IP."
    Write-Info 'A certificate name mismatch means the network is fine. A timeout means the'
    Write-Info 'router isolates clients from each other, and the run proved nothing.'

    if ($SetupOnly) {
        Write-Host ''
        Write-Host '  -SetupOnly: stopping the relay. Unity will start its own on Play.' -ForegroundColor Yellow
        if ($script:RelayProcess -and $script:OwnsRelay) {
            # A relay that will not die is worth saying out loud, but it does not make a
            # run that has already passed everything into a failed one.
            try { Stop-ProcessTree $script:RelayProcess }
            catch { Write-Warn "Could not stop the relay: $($_.Exception.Message)" }
        }
        Write-Host ''
        Write-Warn 'relay-url.txt, relay-env.txt and the borrowed key are still in place.'
        Write-Warn "Re-run without -SetupOnly, or delete them and $Scratch by hand."
        exit 0
    }

    Write-Host ''
    Write-Host '  Ctrl+C to stop and put everything back.' -ForegroundColor Cyan
    Wait-ForCtrlC
    Invoke-Teardown
} catch {
    Write-Host ''
    Write-Host "Aborted: $($_.Exception.Message)" -ForegroundColor Red
    Invoke-Teardown
    exit 1
}

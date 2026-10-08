#requires -Version 5.1
<#
.SYNOPSIS
    Spike (#71): fence shared guest internet off the home network with a WFP forward-layer filter.

.DESCRIPTION
    The first sharing spike showed a Windows Firewall rule has no effect on forwarded (ICS)
    traffic (docs/Findings-HotspotForwardingAndInternet-2026-10-01.md). This tries the layer
    those rules never reach: a Windows Filtering Platform filter at FWPM_LAYER_IPFORWARD_V4. It
    blocks any forwarded packet whose destination is a private, link-local or CGNAT address.

    Only guest traffic is forwarded on this PC, so the host's own LAN access is untouched;
    the script checks that. Traffic from guests to the hotspot address (the game) is local
    delivery, not forwarding, so it is untouched too.

    The filter lives in a DYNAMIC WFP session. Windows deletes it the moment this script's
    engine handle closes, including if the window is closed or the script crashes, so
    nothing is left installed.

    Stages, with a phone on the hotspot and mobile data OFF:
      1  forwarding on, no filter - baseline: websites and the router both load
      2  forwarding on, filter    - expected: websites load, the router does not

    GUIDs and constants are taken from the Windows SDK headers on this machine (fwpmu.h,
    fwptypes.h, fwpmtypes.h, 10.0.26100.0). Needs administrator rights and raises its own UAC
    prompt. -DryRun only compiles the helper and prints the plan.

.EXAMPLE
    powershell -NoProfile -ExecutionPolicy Bypass -File deploy\spike\hotspot-wfp-fence-spike.ps1
#>
[CmdletBinding()]
param(
    [switch] $DryRun,
    # InboundPacket (default): match packets arriving on the hotspot, before routing or NAT.
    # Forward: the IP-forward layer, which spike 2 showed does NOT catch ICS traffic.
    [ValidateSet('InboundPacket', 'Forward')] [string] $Layer = 'InboundPacket'
)

Set-StrictMode -Version Latest
$root = Resolve-Path (Join-Path $PSScriptRoot '..\..')
$control = Join-Path $root 'hotspot\hotspot-control.ps1'
# Forward keeps spike 2's original file name; each layer gets its own so runs never overwrite.
$results = Join-Path $PSScriptRoot $(if ($Layer -eq 'Forward') { 'hotspot-wfp-fence-results.txt' } else { 'hotspot-wfp-inbound-results.txt' })

Add-Type -TypeDefinition @'
using System;
using System.Runtime.InteropServices;

public static class WfpFence
{
    [StructLayout(LayoutKind.Sequential)] struct DisplayData { [MarshalAs(UnmanagedType.LPWStr)] public string Name; public IntPtr Description; }
    [StructLayout(LayoutKind.Sequential)] struct Session { public Guid Key; public DisplayData Display; public uint Flags; public uint TxnWaitTimeoutMs; public uint ProcessId; public IntPtr Sid; public IntPtr Username; public int KernelMode; }
    [StructLayout(LayoutKind.Sequential)] struct ByteBlob { public uint Size; public IntPtr Data; }
    [StructLayout(LayoutKind.Sequential)] struct Value { public uint Type; public IntPtr Data; }
    [StructLayout(LayoutKind.Sequential)] struct Action { public uint Type; public Guid FilterType; }
    [StructLayout(LayoutKind.Sequential)] struct Condition { public Guid FieldKey; public uint MatchType; public Value ConditionValue; }
    [StructLayout(LayoutKind.Sequential)] struct AddrMask { public uint Addr; public uint Mask; }
    [StructLayout(LayoutKind.Sequential)] struct Filter
    {
        public Guid Key; public DisplayData Display; public uint Flags; public IntPtr ProviderKey; public ByteBlob ProviderData;
        public Guid LayerKey; public Guid SubLayerKey; public Value Weight; public uint NumConditions; public IntPtr Conditions;
        public Action Action; public ulong RawContext; public ulong RawContextHigh; public IntPtr Reserved; public ulong FilterId; public Value EffectiveWeight;
    }

    [DllImport("fwpuclnt.dll", CharSet = CharSet.Unicode)] static extern uint FwpmEngineOpen0(string server, uint authn, IntPtr identity, ref Session session, out IntPtr engine);
    [DllImport("fwpuclnt.dll")] static extern uint FwpmFilterAdd0(IntPtr engine, ref Filter filter, IntPtr sd, out ulong id);
    [DllImport("fwpuclnt.dll")] static extern uint FwpmEngineClose0(IntPtr engine);

    // From fwpmu.h, Windows SDK 10.0.26100.0.
    public static readonly Guid LayerIpForwardV4 = new Guid("a82acc24-4ee1-4ee1-b465-fd1d25cb10a4");
    public static readonly Guid LayerInboundIpPacketV4 = new Guid("c86fd1bf-21cd-497e-a0bb-17425c885c58");
    public static readonly Guid ConditionIpDestination = new Guid("2d79133b-b390-45c6-8699-acaceaafed33");
    public static readonly Guid ConditionIpLocalAddress = new Guid("d9ee00de-c1ef-4617-bfe3-ffd8f5a08957");
    public static readonly Guid ConditionIpRemoteAddress = new Guid("b235ae9a-1d64-49b8-a44c-5ff3d9095045");
    const uint RpcAuthnWinNt = 10, SessionDynamic = 0x1, DataV4AddrMask = 0x100, MatchEqual = 0, ActionBlock = 0x1001;

    /// <summary>Opens a dynamic session and adds one block filter; returns the engine handle to close later.</summary>
    public static IntPtr Install(Guid layer, Guid[] fields, uint[] addrs, uint[] masks)
    {
        var session = new Session { Display = new DisplayData { Name = "RockSkipping fence spike" }, Flags = SessionDynamic };
        IntPtr engine;
        var err = FwpmEngineOpen0(null, RpcAuthnWinNt, IntPtr.Zero, ref session, out engine);
        if (err != 0) throw new Exception("FwpmEngineOpen0 failed: 0x" + err.ToString("X8"));

        int condSize = Marshal.SizeOf(typeof(Condition));
        IntPtr conds = Marshal.AllocHGlobal(condSize * addrs.Length);
        for (int i = 0; i < addrs.Length; i++)
        {
            IntPtr am = Marshal.AllocHGlobal(Marshal.SizeOf(typeof(AddrMask)));
            Marshal.StructureToPtr(new AddrMask { Addr = addrs[i], Mask = masks[i] }, am, false);
            var c = new Condition { FieldKey = fields[i], MatchType = MatchEqual, ConditionValue = new Value { Type = DataV4AddrMask, Data = am } };
            Marshal.StructureToPtr(c, new IntPtr(conds.ToInt64() + (long)i * condSize), false);
        }

        // WFP ORs conditions on the same field and ANDs different fields.
        var filter = new Filter
        {
            Key = Guid.NewGuid(), Display = new DisplayData { Name = "RockSkipping: block guests from private networks" },
            LayerKey = layer, NumConditions = (uint)addrs.Length, Conditions = conds, Action = new Action { Type = ActionBlock }
        };
        ulong id;
        err = FwpmFilterAdd0(engine, ref filter, IntPtr.Zero, out id);
        if (err != 0) { FwpmEngineClose0(engine); throw new Exception("FwpmFilterAdd0 failed: 0x" + err.ToString("X8")); }
        return engine;
    }

    /// <summary>Struct sizes, to compare with the SDK's x64 layout: Filter 200, Condition 40, Session 72.</summary>
    public static string Sizes() { return "Filter " + Marshal.SizeOf(typeof(Filter)) + ", Condition " + Marshal.SizeOf(typeof(Condition)) + ", Session " + Marshal.SizeOf(typeof(Session)); }

    public static void Remove(IntPtr engine) { if (engine != IntPtr.Zero) FwpmEngineClose0(engine); }
}
'@

function V4([string] $hex) { [Convert]::ToUInt32($hex, 16) }

# Forward layer: destination ranges only. 192.168.0.0/16 includes the hotspot subnet, which is
# harmless there: guest-to-host is local delivery and guest-to-guest is bridged by the access
# point, so neither is forwarded.
$forwardRanges = @(
    @{ Name = '10.0.0.0/8';     Addr = [Convert]::ToUInt32('0A000000', 16); Mask = [Convert]::ToUInt32('FF000000', 16) }
    @{ Name = '172.16.0.0/12';  Addr = [Convert]::ToUInt32('AC100000', 16); Mask = [Convert]::ToUInt32('FFF00000', 16) }
    @{ Name = '192.168.0.0/16'; Addr = [Convert]::ToUInt32('C0A80000', 16); Mask = [Convert]::ToUInt32('FFFF0000', 16) }
    @{ Name = '169.254.0.0/16'; Addr = [Convert]::ToUInt32('A9FE0000', 16); Mask = [Convert]::ToUInt32('FFFF0000', 16) }
    @{ Name = '100.64.0.0/10';  Addr = [Convert]::ToUInt32('64400000', 16); Mask = [Convert]::ToUInt32('FFC00000', 16) }
)

# Inbound-packet layer: source is a guest AND destination is private. Here guest-to-host traffic
# IS seen, so 192.168.137.0/24 is carved out of 192.168.0.0/16 as eight masks covering the rest.
$inboundDestinations = @(
    @('10.0.0.0/8', '0A000000', 'FF000000'), @('172.16.0.0/12', 'AC100000', 'FFF00000'),
    @('192.168.0.0/17', 'C0A80000', 'FFFF8000'), @('192.168.128.0/21', 'C0A88000', 'FFFFF800'),
    @('192.168.136.0/24', 'C0A88800', 'FFFFFF00'), @('192.168.138.0/23', 'C0A88A00', 'FFFFFE00'),
    @('192.168.140.0/22', 'C0A88C00', 'FFFFFC00'), @('192.168.144.0/20', 'C0A89000', 'FFFFF000'),
    @('192.168.160.0/19', 'C0A8A000', 'FFFFE000'), @('192.168.192.0/18', 'C0A8C000', 'FFFFC000'),
    @('169.254.0.0/16', 'A9FE0000', 'FFFF0000'), @('100.64.0.0/10', '64400000', 'FFC00000')
)

if ($Layer -eq 'Forward') {
    $layerKey = [WfpFence]::LayerIpForwardV4
    $ranges = $forwardRanges
    $fields = @($ranges | ForEach-Object { [WfpFence]::ConditionIpDestination })
} else {
    $layerKey = [WfpFence]::LayerInboundIpPacketV4
    $ranges = @(@{ Name = 'from 192.168.137.0/24'; Addr = (V4 'C0A88900'); Mask = (V4 'FFFFFF00') }) +
        @($inboundDestinations | ForEach-Object { @{ Name = $_[0]; Addr = (V4 $_[1]); Mask = (V4 $_[2]) } })
    # Arriving packet: remote = the guest (source), local = where it is going (destination).
    $fields = @([WfpFence]::ConditionIpRemoteAddress) +
        @($inboundDestinations | ForEach-Object { [WfpFence]::ConditionIpLocalAddress })
}
$router = (Get-NetIPConfiguration | Where-Object { $_.IPv4DefaultGateway } | Select-Object -First 1).IPv4DefaultGateway.NextHop

if ($DryRun) {
    'DRY RUN - helper compiled, nothing changed.'
    "  struct sizes   : $([WfpFence]::Sizes())   (SDK x64 layout: Filter 200, Condition 40, Session 72)"
    "  router to test : http://$router"
    "  layer          : $Layer"
    "  conditions     : $(($ranges | ForEach-Object Name) -join ', ')"
    "  results file   : $results"
    return
}

$isAdmin = ([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole(
    [Security.Principal.WindowsBuiltInRole]::Administrator)
if (-not $isAdmin) {
    'Asking for administrator rights - approve the UAC prompt. The spike continues in that new window.'
    Start-Process powershell.exe -Verb RunAs -ArgumentList "-NoProfile -NoExit -ExecutionPolicy Bypass -File `"$PSCommandPath`" -Layer $Layer"
    return
}

function Ask([string] $question) {
    do { $a = (Read-Host "$question (y/n)").Trim().ToLower() } while ($a -ne 'y' -and $a -ne 'n')
    Add-Content $results "$question -> $a"
}

function Test-HostReachesRouter {
    try { Invoke-WebRequest "http://$router/" -UseBasicParsing -TimeoutSec 4 | Out-Null; 'yes' }
    catch { if ($_.Exception.Response) { 'yes (HTTP error page)' } else { "no: $($_.Exception.Message)" } }
}

Set-Content $results "WFP fence spike ($Layer layer), $(Get-Date -Format 'yyyy-MM-dd HH:mm'), router http://$router"
$creds = Get-Content (Join-Path $root 'hotspot-credentials.json') -Raw | ConvertFrom-Json
$engine = [IntPtr]::Zero

try {
    'Starting the hotspot the way the game does...'
    & powershell.exe -NoProfile -ExecutionPolicy Bypass -File $control -Action Start -Ssid $creds.ssid -Passphrase $creds.passphrase 2>$null | Out-Null

    $nic = Get-NetIPAddress -AddressFamily IPv4 -IPAddress 192.168.137.* -ErrorAction Stop | Select-Object -First 1
    Set-NetIPInterface -InterfaceIndex $nic.InterfaceIndex -AddressFamily IPv4 -Forwarding Enabled
    Add-Content $results 'forwarding Enabled on the hotspot interface'

    ''
    "=== STAGE 1 - sharing on, no filter. Join '$($creds.ssid)' (mobile data OFF)."
    "    Open https://example.com, then http://$router/?t=$(Get-Random)"
    Ask 'Stage 1: did https://example.com load'
    Ask "Stage 1: did the router page load (anything, even an error page)"

    $engine = [WfpFence]::Install($layerKey, [Guid[]]$fields, [uint32[]]($ranges | ForEach-Object Addr), [uint32[]]($ranges | ForEach-Object Mask))
    Add-Content $results "WFP filter added, layer $Layer`: $(($ranges | ForEach-Object Name) -join ', ')"
    $hostCheck = Test-HostReachesRouter
    Add-Content $results "host itself reaches router with filter on: $hostCheck"
    "    (PC itself still reaches the router: $hostCheck)"

    ''
    '=== STAGE 2 - filter on. Close the old tabs on the phone, then open:'
    "    https://example.com, then http://$router/?t=$(Get-Random)"
    Ask 'Stage 2: did https://example.com load'
    Ask "Stage 2: did the router page load (anything, even an error page)"
    Ask "Stage 2: did the phone's Wi-Fi still show Connected (no 'no internet')"
}
catch {
    "FAILED: $($_.Exception.Message)"
    Add-Content $results "FAILED: $($_.Exception.Message)"
}
finally {
    [WfpFence]::Remove($engine)
    & powershell.exe -NoProfile -ExecutionPolicy Bypass -File $control -Action Stop 2>$null | Out-Null
    Add-Content $results 'cleanup: WFP session closed (filter gone), hotspot stopped'
    ''
    "Done. Filter removed, hotspot stopped. Results saved to $results"
}

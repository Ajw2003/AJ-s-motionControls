# Network mode selection (LAN vs Hotspot) — ZeroSetupPlan Phase 4

> **Update — see [docs/plans/magic-domain-replacement.md](magic-domain-replacement.md) Phase 3.**
> LAN mode is now deprecated, not offered. `StartScreen` no longer shows the picker described
> below — it always launches Hotspot — and `NetworkModeStore.Load`'s fallback flipped from
> `Lan` to `Hotspot`. LAN stays selectable only by hand-editing `network-mode.txt` to `Lan`,
> and even then its certificate no longer validates: the replacement certificate is
> single-name for a fixed hostname (`MagicDomain.HostName`), not a wildcard keyed to LAN's
> varying address. Everything else below — the elevated setup, the DNS-bound race fix, the
> Hotspot mechanics — is unaffected and still accurate.

**Status:** implemented, compiles clean. Not yet tested end-to-end — see Verification below.
Elevated one-time setup (scheduled task + firewall rule) still needs to be run by hand.

## Revision — a real MainMenu scene replaced the in-session picker

The first pass (everything below this point) gated mode selection with a `NetworkModeGate`
singleton that blocked `LocalServices` mid-session on true first run, backed by `PlayerPrefs`,
with a `ModeSelectScreen` OnGUI overlay (F3 to reopen) for changing it later. Feedback after
using it: `PlayerPrefs` makes the setting hard to actually get at (Windows registry, invisible,
not cleared by deleting the game folder), and starting the network session — LocalServices,
RelayClient, and in particular HotspotService's tethering — automatically on launch isn't
acceptable; it needs to wait on a deliberate action.

Fixed by moving the gate from *inside* a session to *before* one exists at all:

- **New `Assets/Scenes/MainMenu.unity`**, scene 0 in Build Settings (ahead of `Bootstrap`).
  Holds a `Ui.StartScreen` component and nothing else network-related — `LocalServices` isn't
  touched until `Bootstrap.unity` loads, which only happens when `StartScreen.BeginMatch()`
  calls `SceneManager.LoadScene(SceneNames.Bootstrap)`. This is the actual "securely gated
  behind user interaction" boundary: no relay spawn, no certificate fetch, no hotspot, until
  that button (or Enter) is pressed.
- **`NetworkModeGate` and `ModeSelectScreen` deleted.** With the scene boundary doing the
  gating, `LocalServices.Start()` just reads the mode synchronously — `Mode =
  NetworkModeStore.Load();` — no waiting, no event, no `AwaitingModeSelection` state. Simpler
  than the first pass, not just relocated.
- **`NetworkModeStore` now backed by a plain file** (`network-mode.txt`, gitignored, same
  convention as `relay-url.txt`/`relay-env.txt`) instead of `PlayerPrefs`. `Load(fallback:
  NetworkMode.Lan)` — the safe default — never throws or blocks; `Save()` writes immediately on
  every click in `StartScreen`, no separate "apply" step.
- **`StartScreen`** shows the LAN/Hotspot picker *every time* the game launches, before Start —
  so "easily changeable" is now structural (you see it every session) rather than needing a
  hidden toggle key. Picking a mode saves it immediately; there is no "restart to apply"
  message anymore, because nothing has started yet to need one.
- `LocalServices` gained a public `Mode` property (the mode this session actually launched
  with) so `HotspotService` reads a single source of truth instead of a second gate object.
- `RelayClient`'s `OnLocalServicesStateChanged` reverted to its original three-state
  in-progress list (`AwaitingModeSelection` no longer exists).
- `JoinScreen`'s `AwaitingModeSelection` guard was removed for the same reason.

Everything from "Files" onward below describes the *first pass* and is kept for the parts that
are unchanged (LocalServices' Hotspot-mode env vars and forced `192.168.137.1` override,
HotspotService, the `/health` DNS-bound race fix, `hotspot-control.ps1`, the elevated setup) —
read it with the above in mind for anything touching mode storage or gating specifically.

## Bug found in first real Hotspot-mode test — self-deadlock, fixed

First end-to-end attempt (Editor, elevated setup already run) failed at
`[LocalServices] The relay never answered its health check.` — everything downstream (RelayClient
falling back to the dead `rockskipping-relay.fly.dev` default, `[JoinScreen] QR request failed:
Cannot resolve destination host`) was a cascading symptom of that one failure, not a separate bug.

**Root cause:** `LocalServices` forces the certificate/relay address to `192.168.137.1` in
Hotspot mode (correct — that's the fixed hotspot gateway address), but it was also *probing its
own health* at `https://192-168-137-1.local-ip.sh:8443/health` — and that address is not
reachable on this host until Hotspot tethering has actually started. `HotspotService` won't
start tethering until `LocalServices` reports `Ready`, which requires that same health check to
succeed. Self-deadlock: neither side can go first.

**Fix:** `LocalServices` now probes its own just-spawned process over `127.0.0.1` (added
`ProbeHealthUrl`), never the public hostname — always reachable regardless of hotspot state,
because this was only ever "is the process I spawned still alive," not a network question.
Loopback HTTPS needs its own certificate handling since the cert's SAN never includes
`127.0.0.1`; added `AcceptAnyCertificate`, a `CertificateHandler` scoped tightly to this one
same-machine liveness probe — nothing phone-facing uses it, every real client (a phone, or
`RelayClient`'s own eventual connection) still validates the actual certificate normally via
`SecureSocketUrl`/`JoinUrlFor`, which are untouched. `Origin`/`HealthUrl` (the public-facing
values, used in logs and the `Fail()` hint shown on the TV) are also untouched — only what gets
*connected to* for the internal check changed.

Once `LocalServices` can reach `Ready` without waiting on the hotspot, `HotspotService` proceeds
normally, and `RelayClient`/`JoinScreen`'s existing retry/backoff loops absorb the few seconds
between `Ready` firing and tethering actually finishing (both already retry indefinitely on
failure — no separate wait-for-`HotspotService.Ready` was needed there).

Not yet re-tested after this fix — next step is exactly what failed before: MainMenu → Hotspot →
Start, watch for `[LocalServices] ... Ready` and `[HotspotService]` reaching `Ready`, then confirm
a real phone can scan both QRs.

**Retested 2026-08-20 — works.** Confirmed end-to-end with Wi-Fi/Ethernet connected: relay
reaches `Ready`, hotspot starts, both QRs scan, a phone joins and plays a round.

**Separately confirmed the same day: Hotspot mode does NOT start with Wi-Fi and Ethernet both
disconnected.** This is `ZeroSetupPlan.md`'s Open Question 2, now answered — see that doc for the
detail. Not a bug in this implementation; `NetworkOperatorTetheringManager` requires an existing
connection profile to tether from, by design, with no workaround inside that API. The host PC
needs to stay attached to *some* network for Hotspot mode to start — "offline" here means guests
need no internet and no shared network with the host, not that the host itself needs zero
connectivity.

## Context

LAN mode was just fixed and confirmed working end-to-end this session (dual-homed tiebreak in
`CertificateProvider`, the LAN-mode firewall rule, and `RelayClient` deferring its URL resolution
until `LocalServices` settles). That closes out Phase 2/3 of `ZeroSetupPlan.md`.

Phase 4 (hotspot/offline mode — a PC-hosted Wi-Fi network with no router or internet required) is
still spike-only: `deploy/spike/hotspot-isolation-spike.ps1` proves the WinRT tethering + ICS
forwarding-disable mechanism works, and `relay/dns.js` is a fully production-ready local DNS
responder already wired into `relay/server.js` — but nothing in the actual game calls either of
them. The user wants this scoped in, driven by a pre-match UI that lets the host pick LAN or
Hotspot, with the choice remembered across launches (so a returning player on the same
machine/network isn't re-prompted every session) but changeable.

Confirmed decisions (already made with the user, not open questions):
- Two modes only: `Lan` and `Hotspot`. The old deployed Fly.io relay default is dead, out of scope.
- Changing mode mid-session is **not** supported — picking a different mode than the one active
  this session just persists it and shows "restart to apply." No live teardown/rebuild of a
  running relay, certificate, or hotspot.
- The WinRT tethering work happens in a **PowerShell script** (adapted from the already-proven
  spike script), not a new compiled C# helper exe — Unity/Mono cannot call WinRT directly, but a
  child `powershell.exe` process can, and this reuses validated logic instead of writing new
  interop code.
- Process-spawning code for the new short-lived hotspot-control child is extracted into a shared
  `Net/ProcessUtil.cs`, used by both the existing long-running relay spawn and the new one-shot
  hotspot-control spawn.
- The new DNS firewall rule is scoped to `192.168.137.0/24` (not the broader `LocalSubnet` used
  by the existing relay rule), matching what `SecurityReview.md` §4 and `ZeroSetupPlan.md`
  actually specify — the responder is hotspot-only and never needs to answer outside that subnet.
- Claude does not execute elevated/system-security commands. The scheduled task and new firewall
  rule are handed to the user as commands to run once by hand, same pattern as the existing relay
  firewall rule.

## Files

### New

**`Assets/Scripts/Net/NetworkMode.cs`**
```csharp
public enum NetworkMode { Lan, Hotspot }

public static class NetworkModeStore
{
    private const string Key = "RockSkipping.NetworkMode";
    public static bool HasSavedMode => PlayerPrefs.HasKey(Key);
    public static NetworkMode Load(NetworkMode fallback = NetworkMode.Lan) =>
        HasSavedMode ? (NetworkMode)PlayerPrefs.GetInt(Key) : fallback;
    public static void Save(NetworkMode mode)
    {
        PlayerPrefs.SetInt(Key, (int)mode);
        PlayerPrefs.Save(); // explicit flush — a crash between choosing and next launch must not re-prompt
    }
}
```
No existing settings-persistence pattern exists anywhere in the repo (confirmed by grep) — this
is the first use of `PlayerPrefs`. Storing the enum as an int is fine for two values; comment that
reordering the enum breaks old saves.

**`Assets/Scripts/Net/NetworkModeGate.cs`** — a singleton, same shape as `RelayClient`'s existing
wait-on-`LocalServices` pattern (`Assets/Scripts/Net/RelayClient.cs:147-213`):
```csharp
public class NetworkModeGate : SingletonBase<NetworkModeGate>
{
    public NetworkMode Mode { get; private set; }
    public bool Resolved { get; private set; }
    public event Action<NetworkMode> Chosen;

    protected override void Awake()
    {
        persistBetweenScenes = true;
        base.Awake();
        if (NetworkModeStore.HasSavedMode)
        {
            Mode = NetworkModeStore.Load();
            Resolved = true; // steady-state: settled before any Start() runs, zero added delay
        }
    }

    public void Choose(NetworkMode mode)
    {
        NetworkModeStore.Save(mode);
        if (Resolved) return; // already running this session under the old mode — restart required
        Mode = mode;
        Resolved = true;
        Chosen?.Invoke(mode);
    }
}
```
Because Unity runs every object's `Awake()` before any `Start()`, a saved preference is read and
`Resolved` before `LocalServices.Start()` ever runs — the steady-state (returning player) path has
zero added delay, no coroutine, no waiting.

**`Assets/Scripts/Net/ProcessUtil.cs`** — extracted from `LocalServices.cs`'s existing `Spawn()`
(`LocalServices.cs:318-380`), `ResolveExecutable()` (`:504-535`), `TryKill()` (`:382-399`), and
`JoinDirectories()` (`:537-542`). Same signatures, moved to a static class both `LocalServices`
and the new `HotspotService` call. `LocalServices` is updated to call `ProcessUtil.Spawn(...)`
etc. instead of its own private copies — pure refactor, no behavior change, so the LAN path just
fixed and tested this session is unaffected.

**`Assets/Scripts/UI/ModeSelectScreen.cs`** — OnGUI, namespace `Ui`, styled like
`Assets/Scripts/UI/JoinScreen.cs` (`EnsureStyles()` caching, `_panelTexture`, `toggleKey` — use
`F3`, since `F2` is JoinScreen's):
- `Update()`: toggle `_openedManually` on `F3`; lazily cache `NetworkModeGate.Instance`.
- `OnGUI()`:
  - Not resolved yet (`!_gate.Resolved`, i.e. true first run) → full-screen picker, two buttons
    (LAN / Hotspot) plus `Alpha1`/`Alpha2` keyboard shortcuts for a couch setup with no mouse.
    Picking calls `_gate.Choose(mode)`, which unblocks `LocalServices` immediately this session.
  - Resolved and `_openedManually` → same picker, but marks the currently-active mode and, if a
    different one is picked, saves it and shows "Restart the game to switch to Hotspot mode"
    instead of changing anything live.
  - Otherwise → a small corner label, `"Mode: LAN  (F3 to change)"`, hidden once
    `TurnManager.HasInstance && TurnManager.Instance.Phase != MatchPhase.Lobby` (mirrors
    `JoinScreen`'s `compactDuringMatch` gate at `JoinScreen.cs:226`).

### Changed

**`Assets/Scripts/Net/LocalServices.cs`**
- Add `AwaitingModeSelection` to `ServiceState`, between `Idle` and `ObtainingCertificate`.
- `Start()`: after the existing `launchOnStart` early-return, check
  `NetworkModeGate.Instance.Resolved`. If not resolved, `SetState(AwaitingModeSelection, ...)`,
  subscribe to `NetworkModeGate.Instance.Chosen`, and return — don't create `_group` or start the
  coroutine yet. The `Chosen` handler unsubscribes and calls a new `BeginLaunch(mode)` that does
  what `Start()` used to do unconditionally. On the steady-state path (mode already resolved),
  `Start()` calls `BeginLaunch()` directly — identical behavior to today.
- Add `private NetworkMode _mode;`, set in `BeginLaunch`.
- `ObtainCertificate()`: replace the raw `lanAddressOverride` argument with a new
  `EffectiveLanAddressOverride` property: `_mode == NetworkMode.Hotspot ? "192.168.137.1" :
  lanAddressOverride`. This forces the address in Hotspot mode unconditionally.
- `Spawn()` (via `ProcessUtil.Spawn` after the refactor above): add a `DNS_RESPOND_IP` /
  `DNS_ALLOW` / `DNS_SUFFIX` env block when `_mode == NetworkMode.Hotspot`, inserted in the same
  place as the existing `TLS_CERT`/`TLS_KEY`/`PORT` block — **before** `ReadRelayEnvironment()` is
  applied, so `relay-env.txt` keeps its existing override precedence. Values:
  `DNS_RESPOND_IP=192.168.137.1`, `DNS_ALLOW=192.168.137.0/24`, `DNS_SUFFIX` = the same suffix
  `CertificateProvider` uses (promote `CertificateProvider.Suffix` from `private const` to
  `internal const` so there's one source of truth instead of a second hardcoded string).
- **`/health` race fix (required, not optional — see Verification below for why):** the DNS
  responder's UDP bind is *initiated* inside `relay/server.js`'s `httpServer.listen()` callback
  (`relay/server.js:589-598`) but not awaited before that callback returns — so a successful
  `/health` response does not guarantee the DNS socket has finished binding. Add a `dns: {
  enabled, bound }` field to `/health`'s JSON in `relay/server.js` (the file already tracks
  `dnsResponder` and its `.start()` promise). `HotspotService` gates on this field, not just on
  `LocalServices.Ready`, before starting tethering — turning an inferred timing guarantee into a
  confirmed one, which matters because `SecurityReview.md` §4 treats "DNS must bind before
  tethering starts" as load-bearing, not best-effort.

**`Assets/Scripts/Net/RelayClient.cs`** — one-line fix to `OnLocalServicesStateChanged`
(`RelayClient.cs:176-186`): add `ServiceState.AwaitingModeSelection` to the "still in progress,
keep waiting" branch alongside `ObtainingCertificate`/`InstallingDependencies`/`StartingRelay`.
Without this, `RelayClient` would resolve and attempt to connect with a stale/default URL before
the host has even picked a mode.

**`Assets/Scripts/UI/JoinScreen.cs`**
- Add one guard at the top of `OnGUI()`: `if (_services != null && _services.State ==
  ServiceState.AwaitingModeSelection) return;` — otherwise `DrawProgress()` and
  `ModeSelectScreen`'s full-screen picker both try to own the screen during that state, with
  undefined `OnGUI` draw order between two separate components.
- Fold in the Wi-Fi-join QR: generalize the existing `_qr`/`_qrSourceUrl`/`_qrRequestInFlight`
  trio (`JoinScreen.cs:40-42`) into two instances (a small internal helper struct/class holding
  those three fields plus a `Fetch` coroutine, reused twice) so `JoinScreen` holds a `_wifiQr` and
  a `_gameQr`. Build the Wi-Fi payload client-side —
  `WIFI:T:WPA;S:<ssid>;P:<passphrase>;;`, escaping `\ ; , : "` per `deploy/spike/wifi-qr.js`'s
  `escapeValue()` — and fetch it through the same `/qr?data=...` endpoint already used for the
  join URL (`relay/server.js`'s `serveQr` already encodes an arbitrary `data` string generically —
  no relay change needed). Render only when `HotspotService.HasInstance &&
  HotspotService.Instance.State == HotspotState.Ready`, Wi-Fi QR first, game QR second, with a
  label ("Scan 1: join Wi-Fi   Scan 2: open the game") matching `ZeroSetupPlan.md`'s "two scans,
  not one" flow.

**`Assets/Scripts/Managers/Bootstrap.cs`** — in `Awake()`:
- Create `NetworkModeGate.Instance` right after `ScoreManager.Instance`, before `LocalServices` —
  unconditional (must exist even when `startLocalServices` is off, so `ModeSelectScreen` never
  hits a null instance in the dev loop).
- Create `HotspotService.Instance` unconditionally, after `RelayClient`/`PeerLink` — it self-gates
  to `NotApplicable` when the eventual mode turns out to be `Lan`, or when `LocalServices` isn't
  running at all. It has to be created before the mode is known (first run resolves the mode only
  later, via `NetworkModeGate.Chosen`), so it can't be gated the same way `LocalServices` is.
- Add `[SerializeField] private bool showModeSelectScreen = true;` and a `CreateModeSelectScreen()`
  static helper, direct copy of `CreateJoinScreen()`'s shape (`Bootstrap.cs:91-98`).

### New — hotspot control

**`hotspot/hotspot-control.ps1`** — new top-level directory, sibling to `relay/`/`controller/`,
so path resolution stays symmetric with `LocalServices.ResolveRelayDirectory()`'s existing
`Path.Combine(Application.dataPath, "..", "relay")` pattern. Adapted from
`deploy/spike/hotspot-isolation-spike.ps1` (the WinRT calls are proven — reuse them verbatim,
don't rewrite):
```
hotspot-control.ps1 -Action Start -Ssid <ssid> -Passphrase <passphrase>
hotspot-control.ps1 -Action Stop
```
- `#requires -Version 5.1` — the `[Windows.Networking...,ContentType=WindowsRuntime]` type-literal
  syntax is a Windows PowerShell 5.1 feature; `pwsh.exe` (PowerShell 7+) isn't guaranteed to
  support it. `ProcessUtil.ResolveExecutable` already won't match `pwsh.exe` (different binary
  name), but add `C:\Windows\System32\WindowsPowerShell\v1.0` as an extra search directory, same
  as the existing `node` fallback to `C:\Program Files\nodejs`.
- Exactly one JSON line to stdout as the last line (progress text elsewhere/suppressed). Success:
  `{"ok":true,"ssid":"...","passphrase":"...","warning":null}`. Failure:
  `{"ok":false,"error":"no-connection-profile","message":"..."}` — distinguish
  `GetInternetConnectionProfile()` returning null from a `StartTetheringAsync()` failure (check
  the live WinRT `TetheringOperationStatus` enum member names when implementing, don't assume).
  Exit code mirrors `ok`.
- On successful start, run `Start-ScheduledTask -TaskName "RockSkippingDisableForwarding"` before
  emitting the result JSON, so `HotspotService` gets one atomic "hotspot up and isolated" signal.
- `-Action Stop` unconditionally calls `StopTetheringAsync()` — `HotspotService` only invokes it
  when it owns the tethering session (mirrors `LocalServices._ownsRelay`).
- Invoked as: `powershell.exe -NoProfile -NonInteractive -ExecutionPolicy Bypass -File "<path>"
  -Action Start -Ssid "<ssid>" -Passphrase "<passphrase>"`.

**`Assets/Scripts/Net/HotspotService.cs`** — new singleton, `ServiceState`-shaped:
```csharp
public enum HotspotState { NotApplicable, Starting, Ready, Failed }

public class HotspotService : SingletonBase<HotspotService>
{
    public HotspotState State { get; private set; } = HotspotState.NotApplicable;
    public string StatusMessage { get; private set; }
    public string FailureHint { get; private set; }
    public string Ssid { get; private set; }
    public string Passphrase { get; private set; }
    public event Action<HotspotState> StateChanged;
    // _ownedTethering, mirrors LocalServices._ownsRelay — only Stop what we started.
}
```
- `Start()`: if `!LocalServices.HasInstance`, stay `NotApplicable`. Otherwise subscribe to
  `LocalServices.StateChanged` and also check its current state immediately (in case it's already
  past the point of interest).
- On `LocalServices.Failed` → unsubscribe, `NotApplicable`.
- On `LocalServices.Ready` **and** `/health`'s `dns.bound == true` (poll once via the existing
  health-check pattern, or extend `LocalServices` to surface the last-seen `/health` payload) **and**
  `NetworkModeGate.Instance.Mode == NetworkMode.Hotspot` → unsubscribe, start the coroutine that
  spawns `hotspot-control.ps1 -Action Start`, parses the JSON result, and sets `Ready`/`Failed`.
  Otherwise (mode is `Lan`) → `NotApplicable`.
- `OnApplicationQuit`/`OnDestroy`: if `_ownedTethering`, spawn `-Action Stop` with a bounded wait
  (~2-3s, matching `TryKill`'s existing `WaitForExit(2000)`), then dispose its own
  `ChildProcessGroup` as the unconditional backstop — same accepted-risk shape
  `LocalServices.Shutdown()` already has today.

## Elevated one-time setup (hand to the user, do not execute)

**Scheduled task** (S4U logon — runs elevated for the current user with no stored password and no
UAC prompt at trigger time; the action's command is fixed with no parameters, per
`ZeroSetupPlan.md`'s "hardcode the interface match, or it becomes a local privilege-escalation
vector"):
```powershell
$action = New-ScheduledTaskAction -Execute 'powershell.exe' -Argument @'
-NoProfile -WindowStyle Hidden -Command "Get-NetIPAddress -AddressFamily IPv4 -IPAddress 192.168.137.* -ErrorAction SilentlyContinue | ForEach-Object { Set-NetIPInterface -InterfaceIndex $_.InterfaceIndex -AddressFamily IPv4 -Forwarding Disabled }"
'@
$principal = New-ScheduledTaskPrincipal -UserId "$env:USERDOMAIN\$env:USERNAME" -RunLevel Highest -LogonType S4U
Register-ScheduledTask -TaskName "RockSkippingDisableForwarding" -Action $action -Principal $principal -Force
```

**Firewall rule for the DNS responder** (hotspot subnet only — the responder never runs on a
normal LAN):
```powershell
New-NetFirewallRule -DisplayName "RockSkipping hotspot DNS" -Direction Inbound -Protocol UDP -LocalPort 53 -RemoteAddress 192.168.137.0/24 -Action Allow -Profile Any
```

## Verification

1. **LAN mode regression check first** — confirm the just-fixed LAN flow (phone joins, plays a
   round) still works unchanged after `NetworkModeGate`/`AwaitingModeSelection` land, since a
   returning player's saved-`Lan` path must behave identically to today with zero added delay.
2. **First-run flow** — clear `PlayerPrefs` (or run on a fresh install), launch, confirm
   `ModeSelectScreen` blocks with the LAN/Hotspot picker before `LocalServices` does anything, and
   that `JoinScreen` shows nothing underneath it (the `AwaitingModeSelection` guard).
3. **Persistence** — pick LAN, relaunch, confirm no picker appears and the game reaches `Ready` at
   the same speed as before this change.
4. **Hotspot mode**, after the two elevated one-time commands above have been run by hand:
   - Pick Hotspot on first run (or via `F3` + restart). Confirm the relay's `/health` reports
     `dns.bound: true` before `HotspotService` starts tethering (add a temporary log line if
     needed to confirm the ordering isn't racing in practice, not just in theory).
   - Confirm `192.168.137.1` is the address the certificate and relay bind to (via
     `EffectiveLanAddressOverride`), not an auto-detected uplink address.
   - Confirm a real phone can join by scanning the Wi-Fi QR first, then the game QR, with the PC's
     own internet-bound traffic unaffected but the phone's non-game traffic blocked (per
     `hotspot-isolation-spike.ps1`'s original confirm-server.js test — a bare-IP request should
     still fail to leave the hotspot).
   - Quit the game and confirm tethering actually stops (`Get-NetIPInterface` no longer forwarding,
     hotspot no longer visible to phones) rather than being orphaned.
5. **Mode-change UX** — with a saved mode active, press `F3`, pick the other mode, confirm it
   shows "restart to apply" and does not touch the currently-running relay/hotspot; relaunch and
   confirm the new mode takes effect.

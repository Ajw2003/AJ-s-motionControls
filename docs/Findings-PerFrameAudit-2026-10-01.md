# Findings: per-frame code audit (2026-10-01)

**Prompted by:** Ayden asked for no code to run in `Update` (or any per-frame callback)
unnecessarily, per the C# / Unity coding standard. Where possible, work should move to
coroutines, events or one-time setup, keeping the code short, efficient and reliable.

**The standard's rules applied here:**

- no allocations, LINQ or string building in per-frame code;
- never `Find*` or `GetComponent` per frame;
- coroutines for frame- or timer-tied work;
- events between systems.

**Method:** every `Update`, `LateUpdate`, `FixedUpdate` and `OnGUI` under `Assets/Scripts` was
read in full: 24 methods in 17 files.

**What this is not:** a profiler-driven pass. Nothing here was measured as a bottleneck; these are
standards violations, fixed because the standard bans them.

## Legitimately per-frame (kept)

| Where | Why it stays |
|---|---|
| `RelayClient.Update` (`Net/RelayClient.cs:323`) | Drains the socket queue onto the main thread |
| `LocalServices.Update` (`Net/LocalServices.cs:182`) | Same, for child-process output |
| `RockController.Update` (`Game/RockController.cs:184`) | Fixed-step physics while a stone flies |
| `RockLauncher.FollowActiveRock` (`Game/RockLauncher.cs:154`) | Camera chase; already returns early once settled |
| `AimPreview.Update` (`Game/AimPreview.cs:346`) | Ghost smoothing between 20 Hz frames |
| `RockTrail.LateUpdate` (`Game/RockTrail.cs:259`) | Speed tint while flying |
| `Input.GetKeyDown` polling (TurnManager, StartScreen, the UI screens) | The legacy Input API has to be polled |

## Unnecessary per-frame work

### P1 — Polling for the relay every frame instead of binding once

These run a `HasInstance` probe and null checks every frame, forever, only to subscribe once.
Bootstrap creates every manager, `RelayClient` included, in its own `Awake`
(`Managers/Bootstrap.cs:60-121`). So `Start()` is guaranteed to see them.

**The probes:**

- `PlayerColours.Update` (`:68`)
- `PlayerNames.Update` (`:47`)
- `TurnManager.BindRelay` (`:174`)
- `ShoreRocks.EnsureBound` (`:198`)
- `RockLauncher.EnsureBound` (`:91`)
- `RelayDebugHud.EnsureBound` (`:45`)
- the `_client`/`_services`/`_turns` lookups in `JoinScreen` (`:81-82`), `MatchHud` (`:131`)
  and `ResultsScreen` (`:39`)

**Two of them break the `Find` rule outright.** `RockLauncher` and `RelayDebugHud` call
`FindFirstObjectByType<AimPreview>()`, and `ResultsScreen` calls
`FindFirstObjectByType<MatchHud>()`, every frame until it is found. If the object never exists
(say `showAimPreview` is off), the search runs every frame for the whole session.
`RockLauncher` also reads `Camera.main` per frame until it finds one.

### P2 — LINQ with closures for every lobby stone, every frame

`ShoreRocks.UpdateStoneLooks` (`Game/ShoreRocks.cs:347-368`) runs `FirstOrDefault` and `Any`
with capturing lambdas for each of the six stones, every frame. That allocates every frame in the
lobby. It also rewrites each stone's material colour and scale every frame, even when nothing
changed.

### P3 — Join-screen QR checks rebuild strings every frame

`JoinScreen.EnsureQrMatchesJoinUrl` and `EnsureWifiQrMatchesHotspot` (`UI/JoinScreen.cs:94-121`)
build the join URL and the Wi-Fi payload string every frame, only to compare them with the last
one. A one-second coroutine check is enough: the QR only changes when the room code or hotspot
does.

### P4 — Standings re-sorted every frame

- **The in-match HUD.** `MatchHud.Update` (`UI/MatchHud.cs:129-138`) re-sorts the roster every
  frame of a match, with a lambda that captures `this`, so it allocates a delegate every frame.
- **The results screen.** `ResultsScreen.Update` (`UI/ResultsScreen.cs:35-46`) rebuilds the
  standings every frame on the results screen.

The order only changes when a throw completes, a player joins or leaves, or the phase changes.
All of those are events.

### P5 — Ripple globals uploaded while the lake is idle

`WaterRipples.LateUpdate` (`Game/WaterRipples.cs:134`) zero-fills and uploads the ripple array to
the GPU every frame, even with no ripples alive. Once it has published an empty array, it can
skip until a ripple is added.

### P6 — OnGUI rebuilds its label strings on every pass

OnGUI runs at least twice a frame (layout plus repaint), and these build strings on every pass:

- `RelayDebugHud` (on by default)
- `MatchHud`
- `ResultsScreen`

Those strings only change when the underlying values do, so they can be cached and rebuilt on
change.

## Status

Tracked as #61 (parent) with #62–#67, one per finding P1–P6, in order.

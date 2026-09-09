# Aim-preview lag on the school network — confirm the cause, then fix it

**Status:** diagnosed and **confirmed by measurement** 2026-09-09 on the `ArcadePhysics` branch.
Phases 0-2 implemented; awaiting the post-fix measurement in Verification step 1. **Tier 5** (today's plan). The system it amends is
[`../systems/HostingAndCertificates.md`](../systems/HostingAndCertificates.md).

## Context

On this PC, on the school Wi-Fi (`MyBvc`), the aim preview is **both jerky and delayed**.
Disconnect from all Wi-Fi and it goes away. It never happens on the home PC.

That pattern is not a Unity performance problem — the preview's per-frame cost
(`AimPreview.Update` → `SkipPhysics.Predict`, ~1100 steps + a 1101-point `LineRenderer` rebuild)
is identical in all three cases. It is the **wireless path between the phone and the PC**, and
this machine's hardware plus one hardcoded line in the hotspot script explain it exactly.

### The mechanism

Four facts, each verified on this machine today:

1. **This adapter cannot do a real SoftAP.** `netsh wlan show wirelesscapabilities` reports
   `Soft AP : Not supported` and `netsh wlan show drivers` reports `Hosted network supported : No`.
   Windows Mobile Hotspot therefore falls back to a **Wi-Fi Direct GO** on the *same physical
   radio* (`Wi-Fi Direct GO : Supported`, `P2P Max Mobile AP Clients : 8`). The two
   `Local Area Connection* 9/10` virtual adapters are that GO; `192.168.137.244` is still cached
   on `Local Area Connection* 10` from an earlier session.
2. **The hotspot is hardcoded to 2.4 GHz.** `hotspot/hotspot-control.ps1:142` sets
   `$cfg.Band = TetheringWiFiBand::TwoPointFourGigahertz`, deliberately, for phone compatibility
   and room range.
3. **The school link is on 5 GHz.** `netsh wlan show interfaces` → `MyBvc`, WPA2-Enterprise,
   `Band : 5 GHz`, `Channel : 44`.
4. **One radio, two channels.** `Number of Concurrent Channels Supported : 2` means the radio
   *time-slices* between them (multi-channel concurrency). Serving a 5 GHz ch44 station link and
   a 2.4 GHz GO simultaneously forces a channel switch every slot — typically tens of
   milliseconds each way, worse under contention on a busy school channel.

That is what the symptom is made of: the **jerk** is packets arriving in bursts as the radio
returns to the 2.4 GHz slot; the **delay** is the mean of that same switching, on top of
`attitudeSmoothing = 18` in `AimPreview.cs:57` (a ~55 ms exponential lag that is invisible on a
regular 20 Hz stream and very visible on an irregular one).

One variable accounts for it — *is the Wi-Fi radio also holding an infrastructure link while
hosting?*

| Where | STA link | Radio serving the hotspot alone? | Result |
|---|---|---|---|
| School, connected | 5 GHz ch44 | No — splits with 2.4 GHz GO | Jerky + delayed |
| School, Wi-Fi off | none | Yes | Smooth |
| Home PC | **wired Ethernet** (`Ethernet` adapter, Intel I226-V) | Yes | Smooth |

**The evidence that isolates it is row 1 vs row 2**, not the home PC. Those two differ in exactly
one thing — the station link — on the same machine, same build, same phone. The home PC differs
on *two* axes at once (a different network **and** a wired uplink), so on its own it is
consistent with the diagnosis but does not prove it; it is corroboration, not the control. Phase 1
measures rows 1 and 2 directly for that reason.

### What this is not

Ruled out during exploration, so nobody re-checks them:

- **Not the new physics.** HEAD is `15a7557`, the *reverted* pre-rewrite solver. The expensive
  contact sub-stepping (`ContactStep` 20 µs, up to 4000 substeps/step) is only in `6d8956b` and
  is not in the working tree. It is a real future problem for the preview; it is not this one.
- **Not a hosted relay or WebRTC.** Both are gone. The relay is a `node` child process on this
  PC (`LocalServices.cs`), one wireless hop from the phone.
- **Not DNS leaking to the internet.** The hosts entry is present and
  `play.skiprocking.ca` → `192.168.137.1` resolves locally.
- **Not Unity Services.** Analytics, crash reporting, performance reporting and Unity Connect are
  all disabled in `ProjectSettings/UnityConnectSettings.asset`; no `cloudProjectId`.
- **Not `HotspotService.cs:417`'s main-thread `Dns.GetHostAddresses`, nor
  `CertificateProvider`'s GitHub fetch.** Both are real restrictive-network hazards, but both run
  **once at boot** — they cannot cause lag during aiming. They are listed under *Adjacent, out of
  scope* below.

---

## Phase 0 — Keep this document ✅

Done: this file. Linked from the "Traps" section of
[`../systems/HostingAndCertificates.md`](../systems/HostingAndCertificates.md), which is where
the next person will look for it.

---

## Phase 1 — Confirm it, on the hardware, with no code

Two measurements. If both land as predicted, the diagnosis is settled and Phase 2 is the fix.

**`tools/measure-hotspot-jitter.ps1` ✅ built and tested.** It reports the station link's band and
channel, confirms the hotspot interface holds `192.168.137.1`, finds the phone on that subnet
(preferring live neighbour entries over stale ones), pings it, and prints count / loss / min /
max / mean / stdev with a verdict. Read-only; ASCII-only for the PS 5.1 trap.

Exercised under Windows PowerShell 5.1 with `Set-StrictMode -Version Latest`: it correctly read
`Associated to 'MyBvc' on 5 GHz, channel 44` and exited cleanly on the hotspot-not-up branch, and
the measurement core returned `min 3 / max 8 / mean 4.8 / stdev 1.9 ms → CLEAN` over the station
link. **Single-digit stdev is what an uncontended link looks like** — that is the baseline the
hotspot runs are compared against. The only path not yet exercised is the hotspot-up branch,
which needs Play mode and a joined phone.

### Measured 2026-09-09, school network, hotspot up, still associated to `MyBvc`

| | Station link (baseline) | Phone over the hotspot |
|---|---|---|
| min | 3 ms | 1 ms |
| max | 8 ms | **593 ms** |
| mean | 4.8 ms | **120.8 ms** |
| stdev | **1.9 ms** | **131.9 ms** |
| loss | 0% | 0% |

60 pings. **Zero loss with ~70x the jitter** is packets being *queued*, not dropped — which is
what a radio does while it is away serving the other channel. At 20 Hz a 593 ms stall means
roughly twelve `AIM` frames arriving in one burst.

**This is not yet proof, and the reason is worth writing down.** A phone whose screen is off
parks its Wi-Fi radio and wakes only on beacons, producing min ~1 ms / huge max / 0% loss —
almost exactly the same profile. One ping run cannot separate "the host radio is time-slicing"
from "the phone was asleep". A later 5-ping run on an idle phone came back mean 288 ms, which is
consistent with either. The control below is what distinguishes them, and
`measure-hotspot-jitter.ps1` now says so in its own output rather than overclaiming.

**Measurement A — link jitter, both states.** With the game in Play and a phone joined:
run it while associated to `MyBvc`, then disconnect Wi-Fi, rejoin the phone, run it again.

Hold the phone constant: same device, screen on, controller page open, **actively aiming for the
whole run**. The station link is the only thing allowed to change between the two.

- **Confirms the diagnosis** if the Wi-Fi-off run collapses to single-digit stdev while the
  Wi-Fi-on run stays in the tens or hundreds.
- **Refutes it** if *both* runs are jittery — that is the phone's power saving, not the host's
  radio, and the fix is a keep-alive on the controller page, not the hotspot band.
- **Also refutes it** if both runs are clean — the cause is then Unity-side, and Phase 2 becomes
  instrumenting `RelayDebugHud` with AIM inter-arrival times alongside `Time.deltaTime`.

**Measurement B — the channels themselves.** With the hotspot up, `netsh wlan show interfaces`
lists the Wi-Fi Direct GO as a second interface. Confirms multi-channel concurrency if the GO
reports a 2.4 GHz channel while `WiFi` reports 5 GHz ch44.

---

## Phase 2 — The fix

### Result of Phase 1, 2026-09-09 — confirmed

Same phone, same build, 60 pings each; the station link is the only thing that changed.

| | Wi-Fi on (5 GHz ch44) | Wi-Fi off | Change |
|---|---|---|---|
| mean | 156.9 ms | 26.6 ms | **-83%** |
| stdev | 194.3 ms | 52.9 ms | **-73%** |
| max | 1161 ms | 289 ms | -75% |
| loss | 2% | 0% | gone |

**The split radio is the dominant cause.** Dropping the station link removed five sixths of the
latency and three quarters of the jitter.

The Wi-Fi-off run is not clean either (stdev 52.9 ms against a 1.9 ms station-link baseline), so
the script still calls it `JITTERY` — but the game is subjectively smooth in that state, so the
residual is not what the preview feels. 1 Hz ICMP overstates what a continuous 20 Hz WebSocket
sees, because the phone parks its radio between pings while a live AIM stream keeps that path
hot; Wi-Fi Direct GO's own periodic absence and 2.4 GHz congestion contribute too.

**So `mean 26.6 / stdev 52.9 ms` is the target, not zero.** Getting the Wi-Fi-*on* numbers down
to roughly the Wi-Fi-off figures is everything this fix can do.

### 2.1 Match the hotspot band to the station link — `hotspot/hotspot-control.ps1` ✅

`Get-StationBandName` reads the station link out of `netsh wlan show interfaces` and returns the
band the access point should ask for: `FiveGigahertz` when the host is on 5 GHz, otherwise
`TwoPointFourGigahertz`. **No station link keeps 2.4 GHz** — that is the shipping living-room
case and 2.4 GHz remains the better choice there on compatibility and range, which is what the
original comment was about. The result JSON gained `band`, `stationBand` and `bandMatched`.

Two things found while implementing:

- **The WinRT enum is now resolved with the full `[Type,Assembly,ContentType=WindowsRuntime]`
  literal**, as `Get-TetheringManager` already does. The bare short form worked only because the
  literals in that function project the whole `Windows.Networking` assembly first — incidental,
  and a trap for whoever edits either piece next.
- **The `catch` around the band assignment no longer swallows.** It was a bare `catch { }`, which
  is why it was not possible to tell from any log whether the band was ever being applied.

Verified under Windows PowerShell 5.1 against the live radio: station link on 5 GHz, function
returns `FiveGigahertz` / `Matched = True`, the enum parses, it assigns onto a real
`NetworkOperatorTetheringAccessPointConfiguration`, and the result JSON serialises. The one step
not exercised out of band is `ConfigureAccessPointAsync` itself — that needs a real hotspot.

### 2.2 Say so on screen when the radio is still split ✅

`bandMatched` is false when the host is on 6 GHz (the enum has only `Auto`,
`TwoPointFourGigahertz` and `FiveGigahertz`, so the access point cannot follow) or when the
adapter refuses the band. `HotspotService.RadioSharingWarning` carries the message and
`JoinScreen` renders it under the Wi-Fi QR. **Warning only, never fatal** — a laggy game beats
no game.

The wording says the hotspot "may" feel laggy, because this reports the band that was *asked
for*: the driver places the access point and Windows exposes no way to read back the channel it
chose. `tools/measure-hotspot-jitter.ps1` is how you find out for certain.

### 2.3 The `RelayClient` retry loop — **dropped, it was not a bug**

The plan claimed `ConnectTimeout = 8f` being longer than `MaxRetryDelay = 4f` let `Update`
re-arm a connect while one was in flight, disposing a wedged socket on the main thread. It does
not. `ConnectAsync` sets `_connecting = true` synchronously before its first `await`
([RelayClient.cs:348-349](../../Assets/Scripts/Net/RelayClient.cs:348)) and `Update` gates on
`!_connecting` ([:340](../../Assets/Scripts/Net/RelayClient.cs:340)), so attempts cannot overlap
and the `_socket?.Dispose()` at `:355` only ever runs against a finished attempt. The effective
cycle is one 8 s attempt followed by up to 4 s of backoff, which is the design.

**Implementing it as written would have caused a regression.** `MaxRetryDelay` is capped at 4 s
deliberately, so a host reclaims its room inside the relay's 60 s `HOST_GRACE_MS` window
([:156-161](../../Assets/Scripts/Net/RelayClient.cs:156)); raising it above `ConnectTimeout`
would have spent that budget on two failed attempts and lost the room.

### Deliberately not doing (yet)

- **A playout jitter buffer in `AimPreview`.** Timestamp-based interpolation with a fixed delay
  would hide residual jerk — but it *adds* delay, which is half the complaint, and 2.1 removes
  the jitter at source. Revisit if the post-fix measurement still disappoints.
- **Auto-disconnecting the host's Wi-Fi when hosting.** Effective and deterministic, but taking
  someone's internet away without asking is not ours to do. 2.2 tells them; they decide.

---

## Verification

1. Re-run `tools/measure-hotspot-jitter.ps1` after 2.1, on the school network, **still
   associated to `MyBvc`**, phone awake and aiming throughout. Target is the Phase 1 Wi-Fi-off
   figures: **mean around 27 ms, stdev around 53 ms**, 0% loss. Anywhere near the old
   `mean 157 / stdev 194` means the driver ignored the band request.
2. The Unity Console should carry `station link is on 5 GHz; asking for FiveGigahertz` from
   `hotspot-control.ps1`. If instead it carries the new `[warn] this adapter would not accept
   Band=...` line, the adapter refused and 2.2's on-screen warning is the expected outcome.
3. Play a turn on the school network with Wi-Fi still connected and aim — the preview should
   track the phone with no visible hitching.
4. Force the failure to check 2.2: hand-edit the band back to 2.4 GHz, host on the 5 GHz school
   link, and confirm the warning appears on the join screen and the game still runs.
5. `cd relay && npm test` — 46 tests, 45 pass / 1 skip with Play running. Nothing here touches
   the relay, so this is a regression guard only.
6. Sanity-check on the home PC (wired) that 2.1's no-STA branch still produces a 2.4 GHz hotspot
   a phone can see.

---

## Adjacent, out of scope — worth their own pass

Both found while mapping the stack, both specific to a *present-but-restrictive* network, neither
capable of causing lag during aiming:

- **`CertificateProvider.cs:437-467` clobbers a good cached certificate with a captive-portal
  HTML page.** `Fetch` only checks `result == Success` and `data.Length > 0`, `WriteAtomic`
  overwrites the cache, then `Ensure` (`:178-188`) hard-fails *without* the fallback the
  download-failed branch has at `:132-138`. A school network can therefore fail startup outright
  where no network at all succeeds. Only bites on a cold install or inside the 14-day renewal
  window.
- **`HotspotService.cs:417` calls `Dns.GetHostAddresses` synchronously on the main thread**
  from the `BeginTethering` coroutine (`:169`). On black-holed DNS the Windows resolver's retry
  ladder blocks the whole player loop for 10-30 s at boot.

# Project state — 2026-09-08

**Tier 3.** Where the project actually stands against [`Roadmap.md`](Roadmap.md). Update this
file when a milestone's status changes; update the roadmap only when the *definition* changes.

**Branch:** `claude/mobile-motion-controls-grant-xeg4v9` · **Tree:** clean at session start ·
**Last commit:** `cc8fe48` *Rewrite the certificate verifier in PowerShell*

---

## Headline

**Roughly 75%.** Milestones 1–7 are complete. **Milestone 8 is one authoring pass from done** —
its acceptance criterion was rewritten on 2026-09-08 so that the six rocks differentiate
visually rather than mechanically, which is a decision, not a concession. Milestone 9 is the
whole of the remaining work and now has a plan behind it. Milestone 10 has not started, and its
first blocker fails before any of the others is reached.

**The centre of gravity moved on 2026-09-08.** Everything left is presentation, and
[`plans/presentation-and-ui.md`](plans/presentation-and-ui.md) is the document that now drives
it. It settles fifteen decisions across art direction, the UI framework, the in-play HUD, rock
differentiation and sequencing. Read it before starting anything below.

| # | % | Milestone | Status |
|---|---|---|---|
| 1 | 10 | Infrastructure spike | ✅ Done |
| 2 | 20 | Motion pipeline | ✅ Done — see the caveat on the evidence record below |
| 3 | 30 | Skip physics | ✅ Done |
| 4 | 40 | Turns, scoring, multiplayer | ✅ Done |
| 5 | 50 | Local hosting and secure context | ✅ Done |
| 6 | 60 | Offline hosting | ✅ Done |
| 7 | 70 | Frictionless join | ✅ Done |
| 8 | 80 | Rock variety and selection | 🟡 Criterion rewritten 2026-09-08 — one authoring pass left |
| 9 | 90 | Presentation | 🟡 Partial — **now planned in full**, two non-code blockers |
| 10 | 100 | Zero-setup distribution | ❌ Not started |

---

**2026-09-27 — Lake Louise environment.** SVG → Blender → shader pipeline built and checked in
Blender renders; Unity shaders, `LakeLouiseEnvironment` and a scene-creation menu are written
but not yet compiled or run in the Editor. Status and first-run steps:
[`plans/lake-louise-overhaul.md`](plans/lake-louise-overhaul.md).

## The one thing that is not what it looks like

**The game has no in-play HUD, and this reads as a polish gap when it is a gameplay one.**

During a throw the TV shows no scoreboard, no turn indicator, no skip counter, no distance. The
number the whole game is *about* exists only as a `Debug.Log`
([RockController.cs:281](../Assets/Scripts/Game/RockController.cs:281)) — visible in the Editor
console and nowhere else. A spectator on the couch cannot tell whose turn it is or how the
current throw is going without asking the person holding the phone.

Everything needed to fix it already fires. `SkipOccurredEvent` is published per bounce,
`SPECTATE` already carries a per-player score map, and the chase camera settles the stone at
frame centre with empty sky above it
([RockLauncher.cs:132-148](../Assets/Scripts/Game/RockLauncher.cs:132)) — so a top-centre counter
has somewhere to live. It is not blocked on anything. It has simply never been built, and its
absence is easy to miss because every *screen* exists and only the in-match layer does not.

[`plans/presentation-and-ui.md`](plans/presentation-and-ui.md) D4 specifies it, and ranks the
live per-bounce counter as the single highest-value item in the remaining project.

### What this section used to say

It used to say milestone 8 read as nearly finished and was not, because all six rocks ship
`SpinMultiplier: 1`, `VerticalMultiplier: 1`, `HorizontalMultiplier: 1` and therefore skip
identically. That is still **factually true of the assets**, but it is no longer a gap: D5
settles that the rocks differentiate visually and stay mechanically identical, and
[`Roadmap.md`](Roadmap.md) milestone 8's acceptance criterion was rewritten to match. Mechanical
character is deferred deliberately, along one axis, when there is playtest budget for it.

---

## Milestone by milestone

### 1 — Infrastructure spike ✅

Event system ported from FactoryTM with four multi-scene fixes (`EventManager.cs:13-22`), scene
order Bootstrap → Lobby → Game → Results with `Bootstrap` at index 0, relay written with 46
passing tests across four suites.

### 2 — Motion pipeline ✅ (with a caveat about the record, not the code)

Quaternion-based throughout. The phone differences its grip-reference attitude against its
current attitude and sends four floats; `PhoneAttitude.ToUnity` performs the single basis change;
the result is applied to the stone verbatim. No Euler decomposition survives anywhere in the
path, which is what fixed "the rock's rotation doesn't match my phone".

**The caveat:** [`DeviceMatrix.md`](DeviceMatrix.md) is the authoritative cross-device record and
**every row in it is a `TODO` placeholder.** The file says so itself, prominently. iOS and
Android are both known to work from real sessions, but the specific models and OS versions exist
only in the developer's memory. This matters beyond tidiness — the grant kit cites this file as
evidence, and `grant/02-innovation-claims.md` rests a cross-platform claim on it.

### 3 — Skip physics ✅

`SkipPhysics.Step` is pure — no scene access, no events — which is what lets the aim preview run
the same solver speculatively many frames ahead without scoring imaginary throws. Energy loss is
derived from bounce geometry rather than authored. Spin holds attack angle through impact and
decays every bounce, so a run always ends. `SkipPhysics`, `PhoneAttitude`, `SkipTuning` and
`SkipConstants` sit in their own dependency-free assembly with EditMode tests against both.

### 4 — Turns, scoring, multiplayer ✅

Verified by a five-player cross-platform (iOS + Android) session: scoring, turn rotation, late
join, lobby rejoin. Scoring is push-based — gameplay publishes events and only `ScoreManager`
knows point values. The `Bootstrap` creation order that makes this correct is load-bearing and
commented as such (`Bootstrap.cs:69-73`).

### 5 — Local hosting and secure context ✅

`LocalServices` runs the relay as a child process on Play. The certificate is obtained with no
human involved, cached, and renewed structurally — every launch checks, and renews inside 14 days
of expiry. The Fly.io deployment this replaced is torn down and its files deleted; there is no
hosted fallback and nothing left to fall back to.

### 6 — Offline hosting ✅

A match runs on this machine with no internet, no router, and no Wi-Fi association.
`HotspotService` drives Windows Mobile Hotspot, waits for the relay's DNS responder to hold `:53`
before ICS can take it, and gates on both halves of name resolution — the guest's and the host's
— because they fail independently. Guest isolation is verified by reading the forwarding flag
back rather than by trusting that a scheduled task launched. One-time elevated machine setup runs
itself behind a single UAC prompt.

### 7 — Frictionless join ✅

Closed 2026-09-07 after ten rounds of hardware testing. Two QRs (Wi-Fi payload, then game URL); a
returning phone rejoins itself; SSID, passphrase and room code survive a restart; the device
funnel reports `associated ≥ pageOpen ≥ playing` so the TV can show who is stuck and where.

The captive portal was **abandoned deliberately, not forgotten**, and its code was removed on
2026-09-18 — it behaved differently on every phone and OS tested. Android resolves both probe
hosts every ~60s and issues no HTTP probe, while the same phone's browser completes a full
request to the same port seconds later. Ten rounds excluded every host-side cause by direct
measurement. Do not reopen it on a hunch; `archive/plans/join-failure-reduction.md` has the table
of what died and how. What remains on `:80` is the plain connectivity-probe answer (204 /
`Success`) that stops a phone flagging "No internet" or dropping to cellular mid-match.

### 8 — Rock variety and selection 🟡

Built: catalog, definitions, tunings, selection state and wire format, shore-pointing UI, host
settings panel, editor tooling, telemetry, offline sweep.

**Remaining, under the criterion as rewritten on 2026-09-08:** make each rock read as distinct
on the TV, and name the chosen rock on the scoreboard. The six meshes are already distinct; what
is missing is the scoreboard itself, which arrives with the HUD in milestone 9. So milestone 8
now closes *behind* part of milestone 9 rather than ahead of it.

One constraint from D5 that is easy to get wrong: **the player's colour always wins the tint.**
Do not give rocks their own colour identity — a player's blue rock rendered in the rock's own
grey reads as a bug, and colour is how five people tell themselves apart on a TV.

### 9 — Presentation 🟡

Built: `AudioManager`, the six audio events, the core SFX set (skip pitch ladder, sink, fail,
score, win), trails, water ripples, the URP migration, arc colour and ghost glow.

**2026-09-30 — trail and lobby-aim fixes** (branch `claude/rock-rework`, compiled headlessly
with 0 errors; not yet seen in Play). The trail is now the stone's own colour
(`PlayerColours.StoneColourFor`, the blend the rock uses) and `TrailTuning.Colour` supplies only
the alpha fade (#25). `TrailTuning.WidthMultiplier` is now a fraction of the stone's measured
world width, not metres, so it follows each rock and the ×2.47 catalog scale (#24). In the
lobby's shore-pointing picker the phone no longer re-zeroes itself while held still
(`MotionSource.trackingEnabled`, #23). Audio replacement and music are tracked in #26.

**2026-09-30 — party-usable TV screens, first cut** (IMGUI, owner's call; UI Toolkit port still
D3). The lobby is only the two QR codes, Wi-Fi top-left and game top-right, so the shore rocks are
the screen (`JoinScreen.DrawLobbyCorners`); the old centre panel's room code, address, player list
and start prompt are gone from the lobby. `MatchHud` shows the current/last throw top-centre in
the thrower's colour and a leaderboard top-left (`143m   9sk   418pts`, sorted by total).
`RelayDebugHud` now starts hidden (F1). Seen working in the owner's play-test on Lake Louise,
2026-09-30; screenshot `media/ui-party/lobby-lakelouise.png`. The final-scores screen is now
centred and sized to its rows, in the leaderboard's style with each player's best throw (most
skips, distance breaking ties) beside the total, and only "Tap PLAY AGAIN on your phone" as a
hint; the corner QR card is labelled "Play". Compiled; not yet seen in Play.

Not built, and this is now most of the remaining project: the entire art direction, the in-play
HUD, the UI Toolkit port, music (`MusicDirector`, crossfading), and the same-frame
sound-collision scheduler.

**Planned in full as of 2026-09-08.** [`plans/presentation-and-ui.md`](plans/presentation-and-ui.md)
carries fifteen decisions and a nine-phase sequence. The order matters and is deliberate: **art
before UI**, because the HUD's colours are chosen against the sky, and building the HUD first
means picking them against a default skybox that is about to be deleted.

The state the plan starts from, stated plainly because it is easy to under-estimate: the entire
art content of this project is two FBX files, one shader and two materials.
[`Scenes/Game.unity`](../Assets/Scenes/Game.unity) holds exactly two GameObjects — a camera and a
directional light — and the lake is a runtime `CreatePrimitive(Plane)`. Every TV screen is legacy
IMGUI rendered in Unity's built-in Arial.

**Two blockers, neither of them code:**

- **Where source clips come from** — licensed pack, commissioned, or generated. Still open. The
  current SFX are procedurally generated by `RockSkipping/Rebuild Audio Bank`, which is why the
  milestone has got this far without an answer.
- **Which typeface, and under what licence.** There is no font asset in the repository at all.
  D10 settles the shape — heavy-weight SIL-OFL, committed to `Assets/Fonts/` with its `OFL.txt`,
  never fetched at runtime because the product is built to work offline — but not the face.

### 10 — Zero-setup distribution ❌

Not started. Phase 2 (automatic certificates) is done and Phase 5's installer script exists but
has never been compiled. The four blockers in [`ZeroSetupPlan.md`](ZeroSetupPlan.md) are
unchanged:

1. Node is not bundled — hard failure on a machine without it
2. Dependencies install at runtime — needs npm *and internet*, which is what this design removes
3. **`relay/` and `controller/` are not in the build** — they live outside `Assets/`, so Unity
   never copies them
4. The installer has never been compiled

A downloaded build fails at (3) before reaching any of the others. Everything else in this
milestone is downstream of fixing it.

---

## Cross-cutting issues that belong to no milestone

### Documentation — repaired today, watch for regression

The 2026-09-08 audit ([`Findings-DocumentationAudit.md`](Findings-DocumentationAudit.md)) found
20 broken links, caused by commit `0b7c500` deleting 34 documents that the survivors still
pointed at. The decision taken was **restore nothing** — surviving content was rewritten into
this five-tier structure and dead links removed rather than repaired.

Two consequences to be aware of:

- **`Rule-RenderQR.md` is gone and was binding.** Its substance — always render a QR for
  network/URL joining, never ask someone to type or hunt for one — is preserved in
  [`Roadmap.md`](Roadmap.md) milestone 7 and in
  [`systems/HostingAndCertificates.md`](systems/HostingAndCertificates.md). The rule stands; the
  document does not.
- **The tuning-drift finding is gone.** `Findings-TuningAudit.md` recorded that 11 of 19
  `SkipTuning` fields had drifted between code and assets. That finding predates the model
  rewrite that cut `SkipTuning` to three fields, so most of it is moot by construction — but it
  was never formally closed, and nothing re-derived it.

**Source comments still cite deleted documents.** `SkipConstants.cs:12`, `SkipTuning.cs:11,25`,
`RockCatalog.cs:64`, `Bootstrap.cs:93,116` and `CertificateProvider.cs:31` all point at
`Plan-ThrowDrivenSkipModel.md`, `Plan-SkipEnergyInvariant.md`, `rock-choosing-system.md` or
`SecurityReview.md`, none of which exist. These are comments, not links, so nothing breaks — but
a reader following them finds nothing.

### Certificate expiry is silent until it is not

`CertificateProvider` falls back to the cache correctly, and renewal is a GitHub Actions cron
rather than something the host must be online for. But a machine that is *always* offline will
eventually run one party past expiry with no warning. Surfacing days-remaining on the join screen
below some threshold is still unbuilt.

### Branch sprawl

There are 33 local and 40 remote branches. Several (`RockChoosing`, `Sfx-Vfx`, `PhysicsWork`,
`urp-migration`) name work that has since landed. Not urgent, and not a documentation problem,
but it makes "where is the current work" harder to answer than it should be.

---

## What is verified versus what is believed

Worth separating, because the difference is where surprises come from.

| Claim | Evidence |
|---|---|
| Relay routing, identity stamping, reconnect, DNS, funnel, connectivity probes | `cd relay && npm test` — 45 pass |
| Event system survives scene loads | `RockSkipping/Verify/Run Event System Verification` |
| Physics energy invariant holds | `RockSkipping/Verify/Print Skip Curve` |
| `PhoneAttitude` and `SkipPhysics` behaviour | NUnit EditMode tests in `Assets/Tests/EditMode/` |
| Offline play works | Direct observation, plus `deploy/spike/hotspot-offline-spike.ps1` as an unattended regression check |
| Guest isolation | Forwarding flag read back, not assumed |
| Five-player cross-platform match | One real session — **not** recorded per-device; see the `DeviceMatrix.md` caveat |
| Six rocks play differently | **Not verified, and currently false** |
| A stranger can install and play | **Never attempted** |

There is no out-of-band C# compile check — Unity holds an exclusive project lock, so
`Unity -batchmode` cannot compile while the Editor has the project open. Verify via the open
Editor's Console. A previous attempt at a workaround was built and deleted for this reason.

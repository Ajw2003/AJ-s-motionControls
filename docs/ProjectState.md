# Project state — 2026-09-08

**Tier 3.** Where the project actually stands against [`Roadmap.md`](Roadmap.md). Update this
file when a milestone's status changes; update the roadmap only when the *definition* changes.

**Branch:** `claude/mobile-motion-controls-grant-xeg4v9` · **Tree:** clean at session start ·
**Last commit:** `cc8fe48` *Rewrite the certificate verifier in PowerShell*

---

## Headline

**Roughly 75%.** Milestones 1–7 are complete. Milestone 8 is built but not finished — the six
rocks exist and are selectable, and they are mechanically identical to each other. Milestone 9 is
partially built with a non-code blocker. Milestone 10 has not started, and its first blocker
fails before any of the others is reached.

| # | % | Milestone | Status |
|---|---|---|---|
| 1 | 10 | Infrastructure spike | ✅ Done |
| 2 | 20 | Motion pipeline | ✅ Done — see the caveat on the evidence record below |
| 3 | 30 | Skip physics | ✅ Done |
| 4 | 40 | Turns, scoring, multiplayer | ✅ Done |
| 5 | 50 | Local hosting and secure context | ✅ Done |
| 6 | 60 | Offline hosting | ✅ Done |
| 7 | 70 | Frictionless join | ✅ Done |
| 8 | 80 | Rock variety and selection | 🟡 **Built, not done** — the rocks do not differ |
| 9 | 90 | Presentation | 🟡 Partial, blocked on a non-code decision |
| 10 | 100 | Zero-setup distribution | ❌ Not started |

---

## The one thing that is not what it looks like

**Milestone 8 reads as nearly finished and is not.** Every piece of machinery exists: six
`RockDefinition` assets, six `SkipTuning` assets, a catalog in `Resources/`, `RockSelection`,
`ShoreRocks` pointing-to-pick, a host settings panel, editor tooling to rebuild the catalog, and
both an offline sweep and in-play telemetry to compare rocks.

All six tunings hold identical values:

```
Assets/Rocks/Tuning/Rock_01.asset … Rock_06.asset
  SpinMultiplier: 1
  VerticalMultiplier: 1
  HorizontalMultiplier: 1
```

This is deliberate and documented — `SkipTuning.cs:22` says the physics is being evaluated bare
first, so that every difference between two throws is the throw. It is the right call and it was
made on purpose. But it means the milestone's acceptance criterion ("each plays measurably
differently under `Print Skip Curve`") is **not met**, and cannot be met by any amount of further
code. What is left is authoring: deciding what a Skimmer is versus a Shard, and putting numbers
on it.

Six rocks that look different and skip identically is a worse player experience than one rock,
because it invites a choice that does not matter.

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

The captive portal was **abandoned deliberately, not forgotten** — Android resolves both probe
hosts every ~60s and issues no HTTP probe, while the same phone's browser completes a full
request to the same port seconds later. Ten rounds excluded every host-side cause by direct
measurement. Do not reopen it on a hunch; `archive/plans/join-failure-reduction.md` has the table
of what died and how.

### 8 — Rock variety and selection 🟡

Built: catalog, definitions, tunings, selection state and wire format, shore-pointing UI, host
settings panel, editor tooling, telemetry, offline sweep.

Not done: **the rocks do not differ.** See above. This is the single item standing between the
project and 80%.

### 9 — Presentation 🟡

Built: `AudioManager`, the six audio events, the core SFX set (skip pitch ladder, sink, fail,
score, win), trails, water ripples, the URP migration, arc colour and ghost glow.

Not built: music (`MusicDirector`, crossfading), the same-frame sound-collision scheduler, and
most of the polish list.

**Blocked on a decision, not on code:** where source clips come from — licensed pack,
commissioned, or generated. The current SFX are procedurally generated by
`RockSkipping/Rebuild Audio Bank`, which is why the milestone has got this far without an answer.

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
| Relay routing, identity stamping, reconnect, DNS, funnel, captive | `cd relay && npm test` — 46 pass |
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

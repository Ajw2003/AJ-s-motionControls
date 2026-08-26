# RockSkipping — Master Plan

**Purpose:** one entry point into a docs folder that has accumulated a lot of plans, findings,
and decisions over time. This file does not replace any of them — it says which doc is
authoritative for a given question right now, and what phase each active track is in. If a doc
listed here as superseded says something different from this file, this file (and whatever it
points to) wins.

Update this file when a phase below changes status. It is an index, not a duplicate — put detail
in the sub-document, not here.

---

## 1. Where the project stands

Couch party game: Unity PC drives the TV, phones are motion controllers reached through a web
page. Full design context: [`PLAN.md`](PLAN.md) — but read its inline "Superseded — see X" boxes
before trusting any one section; three sections (hosted-relay architecture, URP timing,
WebSocket library choice) are flagged dead there and covered by newer docs instead.

Build milestones, per [`PLAN.md`](PLAN.md) §Milestones and confirmed against `README.md`:

| Milestone | Status | Notes |
|---|---|---|
| **M0** — Infrastructure spike | ✅ Done | Event system ported, scenes scaffolded, relay hardened. |
| **M1** — Motion pipeline + aim preview | ✅ Done | Sensor normalization, calibration, aim preview. |
| **M2** — Skip physics + rocks | 🟡 In progress | Solver, single rock, water/ripple shader done. Rock variety + ring scoring is **Track A** below — the current `RockChoosing` branch. |
| **M3** — Turn-based multiplayer + scoring | ✅ Done, verified | Confirmed via a 5-player cross-platform (iOS+Android) test: scoring, turn rotation, late-join, lobby-rejoin. |
| **M4** — Polish | 🟡 In progress | Water shader, camera framing, reconnect handling done. Audio/VFX is **Track B** below. |

Three tracks are open in parallel — they don't block each other and aren't a single sequence:

- **Track A** — finish M2 (rock variety, selection UI, ring scoring)
- **Track B** — audio/VFX polish (part of M4)
- **Track C** — zero-setup distribution (ships the finished game to someone else's PC — not tied
  to a build milestone, this is "can a stranger run it")

---

## 2. Track A — Rock variety & selection (closes out M2)

**Authoritative doc:** [`plans/rock-choosing-system.md`](plans/rock-choosing-system.md) — the
plan the current `RockChoosing` branch implements. Check its **Build log** section before
assuming a part is finished; as of the last log entry, Part 1's C# had been written but not yet
confirmed compiled (Unity holds the project lock, so verify via the open Editor's Console, not a
batch build).

| Part | What it covers |
|---|---|
| Part 1 | Rock data model — `RockDefinition`/`RockCatalog` assets, `RockCatalogBuilder`/`RockTuningGenerator` editor tooling |
| Part 2 | Selection state — `RockSelection` singleton, wire format to phones |
| Part 3 | Pointing at the shore — `ShoreRocks.cs`, phone-side pointing UI |
| Part 4 | Wiring the chosen rock into play |
| Part 5 | Host settings panel — `MatchSettings`, `MatchSettingsScreen` |
| Part 6 | Verification — offline (`SkipCurve.cs`) and in-play (`RockTelemetry.cs`) comparison |

**Dependency, not yet resolved:** [`Findings-TuningAudit.md`](Findings-TuningAudit.md) found 11
of 19 `SkipTuning` fields have drifted between code and assets, and recommends reconciling that
drift (its Tier A/B/C/D migration) **before** leaning on per-rock tuning data in Parts 1 and 6 —
otherwise Track A work risks encoding the drift into six new rocks instead of one. The commissioning
doc for that finding, [`plans/tuning-audit.md`](plans/tuning-audit.md), is otherwise closed —
its ask has been answered.

---

## 3. Track B — Audio / VFX polish (part of M4)

**Authoritative doc:** [`Plan-SfxVfx.md`](Plan-SfxVfx.md), amended by
[`Plan-SfxVfx-Delta.md`](Plan-SfxVfx-Delta.md). Status as of the plan's own tracker
(2026-08-25):

| Phase | What it covers | Status |
|---|---|---|
| M1 | Foundation — `AudioManager` port fixes | ✅ Built |
| M2 | Events — six new audio events, no listeners yet | ✅ Built |
| M3 | Core SFX — skip pitch ladder, sink, fail, score, win | ✅ Built |
| M4 | Trails — `TrailTuning`, `TrailQueue`, `TrailRenderer` | ✅ Built |
| M5 | Music — `MusicDirector`, crossfading sources | ⏸️ Paused by choice (blocked on a person, not code) |
| M6 | Scheduling — the same-frame sound-collision queue (§7.1) | ⏸️ Paused by choice — do before adding more sounds |
| M7 | Polish, in ranked order (§6) | 🟡 Started early — arc colour (§6.1) and ghost glow (§6.2) built 2026-08-25; rest not started |

Open question blocking M3-onward audio content (not the code): where source clips come from —
licensed pack, commissioned, or generated (§9 of the plan). M1/M2/M4 don't need clips at all.

---

## 4. Track C — Zero-setup distribution

Goal: someone who isn't the developer can run the game on their own PC with no manual setup.
This depends on the offline-hosting groundwork below (§5) already being solid, which it is.

**Authoritative doc:** [`ZeroSetupPlan.md`](ZeroSetupPlan.md) — "designed and de-risked, not yet
implemented" as of its last spikes (2026-08-20/21).

| Phase | What it covers | Status |
|---|---|---|
| Phase 1 | Bundle a working relay inside the build | Not started |
| Phase 2 | Automatic certificates | 🟡 In progress |
| Phase 3 | Make the game's own defaults correct | Not started |
| Phase 4 | Hotspot mode | Not started |
| Phase 5 | The installer | Not started |

Related, additive-only insurance doc: [`MagicDnsFallback.md`](MagicDnsFallback.md) — a
self-operated DNS/cert fallback in case the free `local-ip.sh` service Track C's certs depend on
disappears. Design sketch only, not built, not blocking anything.

Also unresolved before Track C can ship: [`SecurityReview.md`](SecurityReview.md) §6 (relay
hardening) and §7 (distribution risks) are still 🟡 open — its priority table at the bottom is
the live checklist. §2–§5 (hotspot isolation, DNS open-resolver, HTML injection) are already
✅ fixed/resolved.

---

## 5. Supporting ground truth: hosting & offline architecture

Not an active track — this is already-decided, already-shipped infrastructure that Tracks A–C
build on. Read in this order to avoid citing dead sections:

1. [`HostingPlan.md`](HostingPlan.md) — current, authoritative architecture: relay + WebRTC
   signaling run locally on the host PC, reached via a local-HTTPS cert (required because phone
   motion-sensor APIs only unlock in a secure context). Explicitly supersedes `PLAN.md`'s
   "Why a hosted relay" section — the Fly.io-hosted design described there is gone for good.
2. [`Decision-OfflineHosting.md`](Decision-OfflineHosting.md) — why Mobile Hotspot beats Wi-Fi
   Direct for fully-offline hosting (a bug in `hotspot-control.ps1`, not a Windows limit).
3. [`OfflineSetup.md`](OfflineSetup.md) — current state of fully-offline hosting (no
   internet/router at all): prerequisites, `setup-offline.ps1`, remaining gaps.
4. [`../deploy/README.md`](../deploy/README.md) — the day-to-day one-command runbook
   (`local-https.ps1`).
5. [`../deploy/LOCAL-HTTPS-SPIKE.md`](../deploy/LOCAL-HTTPS-SPIKE.md) — history/measurements
   behind why local-HTTPS was adopted; still-open risks (cellular guests impossible, firewall
   prompt, key-distribution) are recorded here and remain true.
6. [`../deploy/RECONNECT-TEST.md`](../deploy/RECONNECT-TEST.md) — manual reconnect test runbook,
   already updated for the local-relay reality.

Abandoned along the way, kept only as records — do not build against these:

- [`Findings-WiFiDirect.md`](Findings-WiFiDirect.md) — Wi-Fi Direct got a phone connected but hit
  DNS/validation dead ends; lost to Mobile Hotspot on operational grounds (see Decision doc above).
- The `skiprocking.ca` dynamic-DNS domain mentioned in some older docs — confirmed dead/unused in
  both `HostingPlan.md` and `deploy/README.md`. Nothing in the repo depends on it.
- `PLAN.md`'s Cloudflare Pages / Fly.io hosting sections, its "URP from day 1" claim, and its
  `NativeWebSocket` recommendation — each has an inline "Superseded" box pointing at the doc that
  replaced it (`HostingPlan.md`, [`Migration-URP.md`](Migration-URP.md), and `RelayClient.cs`'s
  own deviation note, respectively).

One incidental finding not yet resolved (call it Track A/B/C-adjacent, not urgent):
[`Findings-PhoneUnreachable.md`](Findings-PhoneUnreachable.md) root-caused a firewall bug
(fixed) plus two related code issues — dual-homed LAN interface selection, and a
`RelayClient`/`CertificateProvider` desync — that were still open as of the finding. Verify
against current source before assuming either is fixed.

---

## 6. Full document index

| Doc | Status | One line |
|---|---|---|
| [`PLAN.md`](PLAN.md) | Current, with dead sections flagged inline | Original full design doc — motion pipeline, physics, event system, scoring sections still live |
| [`HostingPlan.md`](HostingPlan.md) | Current, authoritative | Local-relay hosting architecture |
| [`ZeroSetupPlan.md`](ZeroSetupPlan.md) | Current — Track C | Zero-config distributable build |
| [`MagicDnsFallback.md`](MagicDnsFallback.md) | Current — proposal, not built | DNS/cert fallback insurance |
| [`Decision-OfflineHosting.md`](Decision-OfflineHosting.md) | Current, resolved decision | Mobile Hotspot over Wi-Fi Direct |
| [`OfflineSetup.md`](OfflineSetup.md) | Current | Fully-offline hosting, no internet |
| [`Migration-URP.md`](Migration-URP.md) | Mostly executed | Built-in→URP migration runbook |
| [`Findings-URPVisuals.md`](Findings-URPVisuals.md) | Historical — fixes applied | Post-URP rendering bugs, root-caused and fixed |
| [`Plan-SfxVfx.md`](Plan-SfxVfx.md) | Current — Track B | Audio/VFX design and build order |
| [`Plan-SfxVfx-Delta.md`](Plan-SfxVfx-Delta.md) | Current — amends Plan-SfxVfx.md | Revisions to the SFX/VFX plan |
| [`SecurityReview.md`](SecurityReview.md) | Mixed — checklist per section | Threat model for local-hosting design |
| [`Findings-PhoneUnreachable.md`](Findings-PhoneUnreachable.md) | Partially resolved | Firewall bug (fixed) + 2 code issues (verify) |
| [`Findings-TuningAudit.md`](Findings-TuningAudit.md) | Current, not yet acted on | Tuning field drift across code/assets — feeds Track A |
| [`Findings-WiFiDirect.md`](Findings-WiFiDirect.md) | Superseded | Wi-Fi Direct path, lost to Mobile Hotspot |
| [`Rule-RenderQR.md`](Rule-RenderQR.md) | Standing rule, binding project-wide | Always render a QR for joining, never ask someone to type/hunt |
| [`plans/network-mode-selection.md`](plans/network-mode-selection.md) | Current, largely done+verified | LAN vs Hotspot mode selection UI |
| [`plans/rock-choosing-system.md`](plans/rock-choosing-system.md) | Current — Track A | Rock catalog, selection, tuning comparison |
| [`plans/tuning-audit.md`](plans/tuning-audit.md) | Closed | Commissioned Findings-TuningAudit.md; inert now |
| [`history/feature_documentation.md`](history/feature_documentation.md) | Historical, accurate | `DynamicSpeedRetention` design (shipped) |
| [`history/research_log.md`](history/research_log.md) | Explicitly superseded | Old fixed-retention investigation |
| [`history/rigidbody_physics_analysis.md`](history/rigidbody_physics_analysis.md) | Reference | Why not to migrate off custom `SkipPhysics` |
| [`DOCUMENTATION_AUDIT.md`](DOCUMENTATION_AUDIT.md) | Historical | 2026-08-20 audit that drove most of the above rewrites; superseded by this file as the entry point |

---

## 7. Keeping this current

When a phase in §2–§4 changes status, update its row here and in the sub-document — don't let
this file's table drift from the doc it points to. When a new plan/finding doc is created, add
one row to §6 and decide whether it belongs to an existing track or needs a new one.

# RockSkipping — Documentation Audit & Update Plan

**Prepared:** 2026-08-20
**Status:** Proposed — no documentation has been changed yet. This is for review before any edits are made.

---

## Executive Summary

The project is currently **mid-Milestone 2 (M2)**. Milestones 0 and 1 are complete. M3 (turn-based multiplayer + scoring) has already been finished and verified ahead of M2, via a 5-player cross-platform (iOS + Android) test covering scoring, turn order, late-join, and lobby-rejoin. M4 polish work (water shader, camera, reconnect handling) has also started early.

The remaining scope for M2 is rock variety and ring scoring — nothing more.

Separately, the project's hosting architecture has **deliberately pivoted away from Fly.io** to a locally-hosted relay reached via `skiprocking.ca` dynamic DNS (a Plex-style setup, required because phone motion-sensor APIs only unlock over HTTPS). This pivot is complete and is now the permanent architecture, but several documents still describe the old Fly.io-hosted design as current. Those documents need to be rewritten, not just corrected.

No documentation has been edited as part of this audit — everything below is a proposal for a follow-up pass, pending sign-off.

---

## Current Phase Status

| Milestone | Status | Notes |
|---|---|---|
| **M0** — Infrastructure spike | ✅ Done | Event system ported, scenes scaffolded, relay hardened. |
| **M1** — Motion pipeline + aim preview | ✅ Done | Sensor normalization, calibration, aim preview all working. |
| **M2** — Skip physics + rocks | 🟡 In progress | Solver, single rock, placeholder lake scene, follow camera, and water/ripple shader are all working. **Missing:** rock variety (`RockDefinition`/`RockSpawner`, the four special rock types) and ring scoring (`RingGate`/`RingPassedEvent` — no references exist anywhere in the codebase), and an authored (non-placeholder) lake scene. |
| **M3** — Turn-based multiplayer + scoring | ✅ Done, verified | Confirmed via a real 5-player test across iOS and Android: scoring, turn rotation, late-join, and lobby-rejoin all work correctly. Finished ahead of M2. |
| **M4** — Polish | 🟡 Partially started early | Water shader, camera framing, and reconnect handling are already built, ahead of the milestone sequence. |

**Bottom line:** the project is accurately described as "mid-M2, not finished" — and the remaining M2 scope is narrow: rock variety, ring scoring, and an authored scene.

---

## Hosting Architecture — What Changed

The original design (`PLAN.md`) and an earlier hosting doc (`HostingPlan.md`) describe a relay hosted on Fly.io, with a VPS-based fallback ("Route B"). That architecture has been **fully abandoned**:

- `fly.toml`, `Dockerfile`, `.dockerignore`, `deploy/Caddyfile`, `deploy/rockskip-relay.service`, and `relay/test-deployed.js` were all deleted in commit `1cd7647` ("remove unused", 2026-08-13) and were never restored.
- The Fly.io deployment itself was deliberately torn down — this isn't downtime, it's a completed architectural change.
- The VPS fallback ("Route B") is likewise strictly abandoned.

**Current, permanent architecture:** the relay and WebRTC signaling run locally on the host machine, reached through `skiprocking.ca` — a dynamic-DNS domain that resolves to the local machine's IP (the same pattern used by tools like Plex for remote access). This is required because phone motion-sensor APIs only unlock in a secure (HTTPS) context, which `deploy/local-https.ps1` sets up locally. The DNS is fully resolved and is now load-bearing infrastructure the game depends on to run — not a "pending" or experimental detail.

The documentation has not yet caught up to this pivot.

---

## Document-by-Document Findings & Proposed Changes

### 1. `HostingPlan.md` — needs a rewrite
- Currently states "Phase 1 and Phase 2 are both deployed and running on real hardware" on Fly.io — no longer true and not coming back.
- **Proposed change:** replace the hosting design section to describe the actual current architecture (local relay + `skiprocking.ca` dynamic DNS + local HTTPS cert flow), and explain why it's necessary (HTTPS requirement for phone sensors).
- Remove the Route B (VPS) section rather than presenting it as an available alternative — it's abandoned.
- Update the DNS status section: `skiprocking.ca` is resolved and required, not "pending registry delegation."
- The WebRTC DataChannel design and measured-capacity/load-test sections can stay as-is — they describe the code/protocol, not the abandoned hosting method.

### 2. `deploy/README.md` — needs a rewrite
- Currently a full Fly.io deploy walkthrough referencing files (`fly.toml`, `Dockerfile`, `.dockerignore`) that no longer exist in the repo, plus a broken reference to a deleted test script (`relay/test-deployed.js`).
- **Proposed change:** replace with a runbook for the actual current setup — running `deploy/local-https.ps1` and the `skiprocking.ca` DNS routing. This may be able to absorb/supersede `deploy/LOCAL-HTTPS-SPIKE.md` (see below) as the single source of truth for hosting instructions.

### 3. `deploy/LOCAL-HTTPS-SPIKE.md` — needs a status update
- Currently frames the local-IP/HTTPS approach as an unproven experimental spike with open questions.
- **Proposed change:** since this is now the sole, adopted, production hosting method, update its framing accordingly and note which of its originally open questions (e.g. DNS resolution) are now resolved.

### 4. `deploy/RECONNECT-TEST.md` — needs a rewrite
- The reconnect test procedure assumes a Fly-hosted relay (`fly machine restart <id> --app rockskipping-relay`), which no longer exists.
- **Proposed change:** re-author the reconnect test around the local-hosting model (e.g., restarting the local relay process) instead of patching in a new machine ID.

### 5. `PLAN.md` — targeted updates
- Already self-flags some superseded sections; extend that same pattern to:
  - The URP claim (project actually uses the built-in render pipeline).
  - The `NativeWebSocket` library recommendation (the code actually uses .NET's built-in `ClientWebSocket`).
  - The Cloudflare Pages hosting reference (abandoned, same as the Fly.io correction above).
- Update the M2 file list to clarify that `RockDefinition.cs`, `RockSpawner.cs`, and `RingGate.cs` are still not built.
- Update the M3 milestone description to reflect that it's done and verified, not just designed.
- Add a short note on the `DynamicSpeedRetention` physics model (see item 7), which isn't mentioned anywhere in this document despite being the live retention system.

### 6. `README.md` — small fixes
- A hardcoded Unity editor path references a different machine (`Ayden's mini`) than the current dev environment — generalize or update it.
- The M3 status line ("written and unit-tested but have not been exercised at a party") is now out of date — update to reflect the verified 5-player cross-platform result.
- Add a note on the current hosting model so a reader doesn't try the old compiled-in Fly.io default address expecting it to work.
- Everything else in this document (M0/M1 done, M2 in progress, open M2 items) remains accurate.

### 7. `history/research_log.md` — add a banner, don't rewrite
- This is a historical investigation log describing the old fixed speed-retention system, which has since been replaced by `DynamicSpeedRetention` (confirmed directly in code comments in `SkipPhysics.cs`).
- **Proposed change:** add a one-line "superseded" banner at the top pointing to the current implementation, and leave the rest of the log untouched as a historical record.

### 8. `history/feature_documentation.md` — add a banner, don't rewrite
- Written entirely in future tense as an open proposal for `DynamicSpeedRetention`, but the feature is now implemented and live.
- **Proposed change:** add a one-line "Status: Implemented" banner at the top.

### 9. `history/rigidbody_physics_analysis.md` — no changes needed
- Cross-checked directly against the current physics code and confirmed accurate as a decision record.

---

## Not In Scope For This Pass

- No documentation has been edited — this document is a proposal only.
- `Assets/Scripts/Verification/` still exists in the codebase despite `README.md` describing it as removable scaffolding. This is a code decision (keep vs. delete), not a documentation fix, and is called out here only for awareness.

---

## Next Step

Pending approval, the follow-up pass will apply the changes above file-by-file, matching each bullet point exactly, and will not alter any content in files marked "no changes needed."

# Findings — documentation audit, 2026-09-08

Audit of every tracked `.md` on branch `claude/mobile-motion-controls-grant-xeg4v9` against three
questions: is it **redundant**, is it **no longer relevant**, is it **factually incorrect**?

Scope: 31 tracked markdown files (`git ls-files '*.md'`). The ~75 `.md` files under
`Assets/.claude/worktrees/` are separate git worktrees of other branches, not part of this
branch's doc set, and are excluded.

**One-line answer:** the docs are unusually well maintained *in prose* — nearly every stale
section carries its own "Superseded — see X" box — but the **link graph is broken in 20 places**
because a deletion commit removed 34 documents that the survivors still point at, and several
"current state" tables describe a certificate architecture that was replaced five days ago.

---

## 1. The structural problem: a deletion that the survivors never absorbed

Commit `0b7c500` *"remove all docs and zip copies"* (plus `77a44f1`) deleted 34 documents from
`docs/`, including the two that everything else was written to depend on:

| Deleted | What it held | Who still points at it |
|---|---|---|
| `docs/PLAN.md` | Full design doc — motion pipeline, physics, event system, scoring, milestones | `Assets/CLAUDE.md`, `README.md`, `MASTER-PLAN.md` |
| `docs/HostingPlan.md` | Authoritative hosting architecture | `Assets/CLAUDE.md`, `deploy/README.md`, `deploy/RECONNECT-TEST.md`, `deploy/LOCAL-HTTPS-SPIKE.md`, `ZeroSetupPlan.md`, `MagicDnsFallback.md`, `plans/join-failure-reduction.md`, `plans/magic-domain-replacement.md` |
| `docs/SecurityReview.md` | Threat model + live hardening checklist | `ZeroSetupPlan.md`, `MagicDnsFallback.md`, `deploy/spike/README.md`, `plans/magic-domain-replacement.md` |
| `docs/Rule-RenderQR.md` | A **binding project-wide rule** | `Assets/CLAUDE.md`, `plans/magic-domain-replacement.md` |
| `docs/Reference-Constants.md`, `Reference-SkipTuning.md`, `Reference-PlayerColours.md` | Field-by-field constant/tuning reference | `Assets/CLAUDE.md`, `plans/magic-domain-replacement.md` |
| `docs/plans/rock-choosing-system.md` | The plan the `RockChoosing` branch implements | `Assets/CLAUDE.md`, `MASTER-PLAN.md` |
| `docs/Findings-TuningAudit.md` | 11-of-19 tuning-field drift, feeding Track A | `MASTER-PLAN.md` |
| `docs/Migration-URP.md`, `Plan-SfxVfx.md`, `Plan-SfxVfx-Delta.md`, `Findings-WiFiDirect.md`, `Findings-PhoneUnreachable.md`, `Findings-URPVisuals.md`, `history/*`, `plans/Plan-*` | Track B plan, migration runbooks, findings | `MASTER-PLAN.md` |

`docs/MASTER-PLAN.md` and `README.md` **have** absorbed this — both carry an explicit notice and
mark dead entries `(deleted)` rather than linking them. Nothing else has.

### 1a. `Assets/CLAUDE.md` is the worst affected, and it is the file that governs Claude's behaviour

Five of its links are dead, including one to a file it calls *"a binding rule … that applies
across the whole project"*:

```
Assets/CLAUDE.md  ->  ../CLAUDE.md                        (repo-root CLAUDE.md does not exist)
Assets/CLAUDE.md  ->  ../docs/PLAN.md
Assets/CLAUDE.md  ->  ../docs/HostingPlan.md
Assets/CLAUDE.md  ->  ../docs/Rule-RenderQR.md            (binding rule, unreadable except from git)
Assets/CLAUDE.md  ->  ../docs/plans/rock-choosing-system.md
```

`Assets/CLAUDE.md` instructs every session to "read `../docs/PLAN.md` before touching networking,
physics, or the event system." That instruction cannot be followed. This is the single
highest-cost defect in the set.

### 1b. Full broken-link inventory (20)

| File | Dead target |
|---|---|
| `Assets/CLAUDE.md` | `../CLAUDE.md`, `../docs/PLAN.md`, `../docs/HostingPlan.md`, `../docs/Rule-RenderQR.md`, `../docs/plans/rock-choosing-system.md` |
| `deploy/README.md` | `../docs/HostingPlan.md` |
| `deploy/RECONNECT-TEST.md` | `../docs/HostingPlan.md` |
| `deploy/LOCAL-HTTPS-SPIKE.md` | `../docs/HostingPlan.md` |
| `deploy/spike/README.md` | `../../docs/SecurityReview.md` |
| `docs/ZeroSetupPlan.md` | `HostingPlan.md`, `SecurityReview.md` |
| `docs/MagicDnsFallback.md` | `HostingPlan.md`, `SecurityReview.md` |
| `docs/plans/join-failure-reduction.md` | `../HostingPlan.md` |
| `docs/plans/magic-domain-replacement.md` | `../HostingPlan.md`, `../SecurityReview.md`, `../Reference-Constants.md`, `../Rule-RenderQR.md`, `../Findings-PhoneUnreachable.md` |

---

## 2. Factually incorrect content

### 2a. Three "current state" tables still describe the dead `local-ip.sh` certificate

`local-ip.sh` died at the DNS layer and was replaced on 2026-09-03 by a single-name certificate
for `play.skiprocking.ca` (`Assets/Scripts/Net/MagicDomain.cs:40`, renewed by
`.github/workflows/renew-cert.yml`). Each of these files opens with a correct update box — and
then its body table contradicts it:

| Location | Says | Actually |
|---|---|---|
| `docs/OfflineSetup.md:29` | `Certificate — Cached *.local-ip.sh, valid to 2026-11-17` | Single-name `play.skiprocking.ca`, auto-renewed |
| `docs/OfflineSetup.md:172` | "cached certificate is good to 2026-11-17 and renews only when the host is online" | Renewal is a GitHub Actions cron, not host-online-dependent |
| `docs/6-decisions/Decision-OfflineHosting.md:251` | `Cached, *.local-ip.sh, valid to 2026-11-17 (88 days)` | Same |
| `docs/ZeroSetupPlan.md:186` | "a fresh download now returns a certificate valid to 2026-11-17" | Same |

A reader who trusts the table over the box builds against a dead CA. The update boxes should be
folded *into* the tables, not stacked on top of them.

### 2b. `docs/OfflineSetup.md` hands over a hosts-file command for the dead hostname

`OfflineSetup.md:78` still gives, as a copy-pasteable elevated command:

```
Add-Content ... -Value "192.168.137.1  192-168-137-1.local-ip.sh"
```

Running it today writes a hosts entry for a name nothing resolves or validates against. The same
section's justification ("`local-ip.sh` names encode their own address, so this duplicates the
public DNS answer exactly") is no longer true of the current fixed domain, so the safety argument
does not carry over unchanged either.

### 2c. Moved and deleted code paths

| Doc claim | Reality |
|---|---|
| `hotspot-control.ps1` under `deploy/` (`OfflineSetup.md`, `HANDOFF-…`, `Decision-OfflineHosting.md`) | Lives at `hotspot/hotspot-control.ps1` |
| `docs/plans/remove-direct-transport.md` cites `Scripts/Net/PeerLink.cs` throughout | `PeerLink.cs` is deleted — the plan **executed**; it is a historical record, not a live plan |
| `docs/plans/remove-direct-transport.md:10` | Links `controller/js/app.js` as `../../../Desktop/GameDev/RockSkipping/controller/js/app.js` — machine-specific, breaks for any other clone |
| `Assets/Scripts/UI/StartScreen.cs:21` | Cites `docs/plans/remove-direct-transport.md` as the reason the LAN/Hotspot picker was removed; the picker removal is actually documented in `plans/magic-domain-replacement.md` Phase 3 |

### 2d. `docs/MASTER-PLAN.md` §1 milestone table cites a deleted source

The milestone table is sourced to "`PLAN.md` (deleted) §Milestones". The statuses themselves are
corroborated by `README.md`, but the milestone *definitions* — what M2 or M4 actually means — now
exist nowhere. This is the gap the new tier-2 document has to fill.

---

## 3. No longer relevant (correct when written, dead now)

Not errors — these are honest historical records. The problem is only that nothing distinguishes
them from live documents at a glance.

| Doc | Why it is inert |
|---|---|
| `docs/MagicDnsFallback.md` | Insurance sketch against `local-ip.sh` disappearing. It disappeared; the answer taken was different (owned domain, not a self-run VPS nameserver). Self-flagged historical, never built. |
| `docs/plans/remove-direct-transport.md` | Executed — `PeerLink.cs` and `com.unity.webrtc` are gone. |
| `docs/Findings-DirectTransportFlapping.md` | The symptom's subject no longer exists. Root cause (a phone re-joining every second) is still worth keeping. |
| `docs/Findings-AimPreviewAfterRejoin.md` | Fixed in `f3e37cc`. |
| `docs/Findings-DnsAudit.md` | Resolved; its fix (hosts entry installed and checked) is live. |
| `docs/plans/seamless-rejoin.md`, `seamless-rejoin-round2.md` | Both shipped (PR #8, #9). |
| `docs/plans/network-mode-selection.md` | Its headline feature — the LAN/Hotspot picker — was subsequently deleted. Only the elevated-setup and DNS-race sections remain live. |
| `docs/HANDOFF-join-failure-reduction.md` | Self-declares CLOSED. Its "traps" list is genuinely valuable and is the only place several of those lessons are written down. |
| `docs/plans/join-failure-reduction.md` (1,013 lines) | CLOSED. The largest doc in the repo; ~90% is a chronological record of ten hardware test rounds including conclusions later proven wrong. |
| `transcripts/README.md` | Indexes JSONL transcripts of a 2026-07-31 latency audit. |
| `deploy/spike/README.md`, `deploy/LOCAL-HTTPS-SPIKE.md` | Both self-declare "adopted, no longer a spike". |

---

## 4. Redundancy

Genuine duplication is **low** — the docs mostly cross-reference instead of repeating. Three real
overlaps:

1. **Offline hosting is described three times.** `Decision-OfflineHosting.md` (why Hotspot beat
   Wi-Fi Direct), `OfflineSetup.md` (current state and prerequisites), and `deploy/README.md` (the
   runbook) all restate the same tethering/DNS/certificate mechanism at different lengths, and the
   three have now drifted apart on the certificate (§2a).
2. **`HANDOFF-join-failure-reduction.md` vs `plans/join-failure-reduction.md`.** The handoff
   explicitly says it "deliberately does not repeat any of it" and mostly succeeds — but both now
   open with a status paragraph saying the same thing.
3. **`MASTER-PLAN.md` §6 duplicates §2–§5.** The full document index restates the status line of
   every doc already described above it, and the two halves have to be updated in lockstep. §7 of
   that file admits this is the maintenance burden.

The **grant kit** (`docs/grant/`, 11 files) is *not* redundant with the engineering docs — it
addresses a different reader — but `grant/09-fundability-roadmap.md` Week 1 contains an
engineering work list that duplicates what a project-state document should own, and
`grant/01-asset-inventory.md` carries line counts verified 2026-09-07 that will silently rot.

---

## 5. What is genuinely healthy

Worth saying plainly, because the restructure should not destroy it:

- **Superseded boxes are used well and consistently.** Six documents open with an accurate
  "Superseded — see X" pointer. This convention should survive into the new structure.
- **`MASTER-PLAN.md` already does tier-1/2/3 work** — index, milestones, and per-track status in
  one file. It is the right seed to split into the new hierarchy rather than replace.
- **Findings docs state what a measurement *cannot* show.** `HANDOFF-…`, `Findings-DnsAudit.md`
  and `plans/join-failure-reduction.md` all do this. It is rare, and it is the reason the
  ten-round captive-portal dead end is not being reopened.
- **Claims are cited to file:line.** Most assertions link to the code they describe, which is why
  this audit could check them mechanically.

---

## 6. Gaps — documents that do not exist and are needed

| Missing | Why it matters |
|---|---|
| Milestone definitions | M0–M4 statuses are tracked; what each milestone *means* died with `PLAN.md`. |
| Per-system documentation | There is no doc for the event system, skip physics, motion pipeline, scoring, audio, or the rock catalog. All six are described only inside `Assets/CLAUDE.md`, at summary depth, and `CLAUDE.md` points at deleted files for the detail. |
| A current architecture doc | `HostingPlan.md`'s role is vacant. `Decision-OfflineHosting.md` plus `plans/magic-domain-replacement.md` cover it between them, but nothing says so. |
| A restored `Rule-RenderQR.md` | A rule described as binding, that no one can read. |
| Today's-plan / working-session doc | No document holds "what am I doing next and why". `grant/09` Week 1 is currently doing this job by accident. |

---

## 7. Recommended disposition

| Action | Files |
|---|---|
| **Fix in place** (link and fact repairs) | `Assets/CLAUDE.md`, `docs/OfflineSetup.md`, `docs/6-decisions/Decision-OfflineHosting.md`, `docs/ZeroSetupPlan.md`, `deploy/README.md`, `deploy/RECONNECT-TEST.md`, `deploy/LOCAL-HTTPS-SPIKE.md`, `deploy/spike/README.md`, `docs/plans/magic-domain-replacement.md`, `docs/plans/join-failure-reduction.md`, `docs/MagicDnsFallback.md` |
| **Restore from git** (`0b7c500^`) | `Rule-RenderQR.md` (binding), `Reference-Constants.md`, `Reference-SkipTuning.md`, `Findings-TuningAudit.md` (open dependency for Track A) |
| **Move to `docs/archive/`**, unchanged | `MagicDnsFallback.md`, `plans/remove-direct-transport.md`, `plans/seamless-rejoin*.md`, `plans/network-mode-selection.md`, `Findings-*.md`, `plans/join-failure-reduction.md`, `HANDOFF-join-failure-reduction.md` |
| **Supersede** (content absorbed into the new tier 1–3 docs) | `docs/MASTER-PLAN.md` |
| **Leave alone** | `docs/grant/*` (different reader, internally consistent), `transcripts/README.md` |

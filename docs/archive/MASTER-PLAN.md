# Master plan — superseded 2026-09-08

**This file is a redirect. Do not add content here.**

`MASTER-PLAN.md` was the single entry point into `docs/`, doing three jobs at once: an index, a
milestone table, and a per-track status list. It did them well, and it is the seed the current
structure grew out of. It was split because a file that is simultaneously the index, the plan and
the status report has to be updated in three places for one change — and its own §7 said so.

Its content now lives in five tiers:

| Was in `MASTER-PLAN.md` | Now in |
|---|---|
| "Where the project stands", the document index | [`README.md`](README.md) — tier 1 |
| The M0–M4 milestone table | [`Roadmap.md`](rock-skipping/Roadmap.md) — tier 2, rewritten as ten milestones with acceptance criteria |
| Track A/B/C status | [`ProjectState.md`](rock-skipping/ProjectState.md) — tier 3 |
| §5, "supporting ground truth: hosting & offline architecture" | [`systems/HostingAndCertificates.md`](../4-systems/HostingAndCertificates.md) — tier 4 |
| Nothing — this did not exist | [`Today.md`](rock-skipping/Today.md) — tier 5 |

The `(deleted)` markers this file carried were an honest response to commit `0b7c500` removing 34
documents. That situation is now handled differently: see
[`Findings-DocumentationAudit.md`](../Findings-DocumentationAudit.md) for the audit, and
[`archive/`](./) for the documents that are inert rather than missing.

**Start at [`README.md`](README.md).**

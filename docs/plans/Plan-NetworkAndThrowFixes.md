# Fix plan — network pipeline and throw gesture

**Tier 5.** Source of the issues: [`../Findings-NetworkAndThrowStability.md`](../Findings-NetworkAndThrowStability.md).
Step-by-step outline and tests: [`Impl-NetworkAndThrowFixes.md`](Impl-NetworkAndThrowFixes.md).

## How the order was chosen

Ranked by **severity × how often it happens at a real couch session × how cheap it is**, then
bent only where one fix depends on another.

- **Small, severe fixes go first.** C1 and D1/D2 are an hour each, and D1 makes the telemetry
  trustworthy before A5 is tuned against it.
- **The multiplayer break (A2/A3) is one group.** Its fixes share a new pure `TurnOrder` class,
  and the THROW acknowledgement (A4) builds on its decision logic.
- **The relay allowlist (C2) comes after every new wire message exists** (THROW_RESULT, PING
  liveness). Doing it earlier means editing the allowlist twice.
- **A7 is last and gated on a decision.** Fixing the swing-speed inflation changes how far every
  throw goes, so it is measured first and Ayden decides before anything changes.

## Priority order

| # | Fix group | Findings | Why here |
|---|---|---|---|
| 0 | Baseline | — | Prove every suite is green before touching anything |
| 1 | Relay cannot be crashed by a URL; serve only the controller's real files | C1, C4 | One request from any guest ends the match; small change |
| 2 | SpeedScale restored at 1.0; `FakeSkip` moved out of the pure assembly | D1, D2 | Tiny; A5's thresholds get tuned against telemetry, which is currently off by 1.2× |
| 3 | Layered accidental-throw guard | A5, A6 | Ayden's direct complaint; phone-only; fully unit-testable |
| 4 | Seats survive a drop; one stone at a time; score tally can't leak | A2, A3, B11, B6 (recorded won't-fix) | The "second player breaks my round" bug |
| 5 | Host resume re-syncs phones; relay restart reconciles ghosts | B5 | Needs group 4's disconnected-seat state |
| 6 | THROW is acknowledged and retried; phone state correct across drops; no AIM after a throw | A4, B3, B4 | "Forced to re-throw"; needs group 4's admission logic |
| 7 | Phone detects a dead socket itself | B7 | Without it, group 6's retry waits on the OS TCP timeout |
| 8 | Relay routes only known message types by role; no re-registering a socket | C2, B8 | Security; allowlist now final |
| 9 | Seat token: a device id alone can't take a seat | C3 | Security; changes JOIN_ROOM, so it follows the allowlist |
| 10 | AIM is latest-wins; relay rate limit uses budgets per message class | B1, B2 | Smoothness and THROW survival under radio stalls |
| 11 | Hardening sweep | C6, C5, B9, B10, B12 | Low severity, independent of each other |
| 12 | Swing-speed inflation — **measure, then stop for a decision** | A7 | Changes throw distance for everyone |
| 13 | Docs sweep and findings status | all | Every system doc matches the code |

A1 is informational; it is resolved by groups 4 and 11 (B12).

## Out of scope, noted

- **`deploy/RECONNECT-TEST.md` is stale.** It references `local-https.ps1`, `peer.js` and WebRTC.
  The manual checks each group needs live in the implementation plan instead. Rewriting the
  runbook is a separate task.
- **How the TV lays out long names** (IMGUI) is only touched by C6's sanitising, not redesigned.

## Definition of done, for the whole plan

- `relay` and `controller` npm suites are green, with **new regression tests for every
  code-level finding**.
- EditMode tests are green in the Editor's Test Runner, including the new `RockSkipping.Match.Tests`.
- Each group's two-phone hardware check has been run by Ayden and recorded in the findings doc's
  status column.
- Tier-4 docs describe the new behaviour.

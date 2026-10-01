# Today — 2026-10-01 (handoff)

**Tier 5.** Where the last session stopped, and what the next one picks up. Written for a fresh
session after `/clear`. The 2026-09-08 design-session record that used to be here is in git
history (`git log -- docs/Today.md`).

Branch: `claude/rock-rework`, pushed. Tracking issue for finishing this work: #43
(child of #39).

## Done and waiting on the owner's play-test

| Issue | What | Commit |
|---|---|---|
| #24 | Trail width matches the rock (it was scaled twice); measured Boulder 2.03 m / 2.03 m | `fb882bc` |
| #28 | Lobby shows only the two QR codes in the top corners | `ef70e80` |
| #31 | Final scores centred, leaderboard style, best throw, phone prompt only | `c8e5b7f` |
| #41 | Shore rock names and descriptions large, full, readable | `6bbc215` |
| #42 | Shore rocks spaced 9 m / ±44° so none overlap | `6bbc215` |

Closed after the owner confirmed: #22, #23, #25, #29, #30.

**Open question for the owner from #42:** at 9 m out the rocks themselves look smaller on the
TV. Screenshot: `docs/media/ui-party/shore-after.png`. A wider fan at a shorter radius would keep
them bigger, but players would have to turn the phone further to reach the end rocks.

## In progress: the phone audit (#40)

Findings are written to [`Findings-PhoneUiAudit.md`](Findings-PhoneUiAudit.md), ranked. **Next
step:** the owner picks which to fix. Each picked fix gets its own child issue of #39, then code.
Finding 1 (the join page asks for a room code the TV no longer shows) was caused by #28, and is
the one most worth fixing first.

## After the UI: audio (#26)

The plan is in [`plans/ai-audio.md`](plans/ai-audio.md), with steps #32–#38. It is waiting for the
owner's go-ahead.

## How to see things without a phone

- **Driving Unity:** the Unity Editor can be driven with the `unity` CLI (`unity command
  editor_play`, `capture_game_view --source screen`, `eval`) when it is open on this project.
  Screenshots land under `Assets/` despite an absolute `--save_path`; move them to `docs/media/`.
- **The browser pane can't run the phone page:** it blocks the controller's `js/app.js`, so it
  is no use for testing the phone side. Use a real phone.

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

**#42 follow-up (2026-10-01):** the owner set the aiming camera to position (0, 4.5, -3),
rotation (15, 0, 0), from a screenshot of the Inspector, in `AimPreview.cs` (`2ae63a0`). Not yet
compiled or play-tested: the Unity Editor for this project wasn't reachable from the `unity` CLI.

## In progress: the phone audit (#40)

Findings are written to [`Findings-PhoneUiAudit.md`](Findings-PhoneUiAudit.md), ranked. Fixes
1, 2, 3+4 and 5+6 are drawn as now/after mockups in
[`mockups/phone-ui-fixes.html`](mockups/phone-ui-fixes.html). **Next step:** the owner picks from
those, and the owner also wants the colour picker rehauled: three directions (A palette on screen,
B stone as picker, C colour at join) are on the same page. Each picked fix gets its own child
issue of #39, then code. For fix 1 the owner chose
option (b): the relay hands out its one room when the page has no code.

## After the UI: audio (#26)

The plan is in [`plans/ai-audio.md`](plans/ai-audio.md), with steps #32–#38. It is waiting for the
owner's go-ahead.

## How to see things without a phone

- **Driving Unity:** the Unity Editor can be driven with the `unity` CLI (`unity command
  editor_play`, `capture_game_view --source screen`, `eval`) when it is open on this project.
  Screenshots land under `Assets/` despite an absolute `--save_path`; move them to `docs/media/`.
- **The browser pane can't run the phone page:** it blocks the controller's `js/app.js`, so it
  is no use for testing the phone side. Use a real phone.

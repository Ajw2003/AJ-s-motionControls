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

**#42 follow-up (2026-10-01):** the owner set the aiming camera to (0, 4.5, -3), rotation (15, 0, 0)
(`2ae63a0`), play-tested it, and found it a little too close: the stone was cut off at the
bottom. Moved back to (0, 4.5, -4.5) in `AimPreview.cs`; not yet seen in Play.

## Built, waiting on a phone play-test: the phone fixes

The owner picked every audit fix plus colour picker direction B. Each is its own child of #39.
Closed after the owner's play-test on 2026-10-01: #44–#50. #51 stays open for an iPhone test.

| Issue | What | Commit |
|---|---|---|
| #44 | Join with no room code; the relay hands out its only room | `5666292`, `47e959a` |
| #45 | Status bar shows your name | `02728de` |
| #46 | Scoreboard sorted, ranked, your row marked | `b860464` |
| #47 | Last throw in the TV's `143m 9sk` format | `02728de` |
| #48 | Debug button only with `?debug=1` | `02728de` |
| #49 | START MATCH becomes the main button after CONFIRM | `8a81ab2` |
| #50 | Bigger text | `166e95b` |
| #51 | Your stone, in your colour, is the colour picker | `8a25905`, `7772e61`, `aab1783` |

Not seen on a phone yet: the browser pane blocks `js/app.js`, so only lint, the controller and
relay tests, and static renders of the stone art and the colour panel were checked. Things to
look at on a phone: the stone colours look a little washed out; the + tile opening the colour
wheel on iOS; CONFIRM stepping down after a tap. Audit finding 8 (busy throw screen) still needs a
phone session before it has a fix.

## After the UI: audio (#26)

The plan is in [`plans/ai-audio.md`](plans/ai-audio.md), with steps #32–#38. It is waiting for the
owner's go-ahead.

## How to see things without a phone

- **Driving Unity:** the Unity Editor can be driven with the `unity` CLI (`unity command
  editor_play`, `capture_game_view --source screen`, `eval`) when it is open on this project.
  Screenshots land under `Assets/` despite an absolute `--save_path`; move them to `docs/media/`.
- **The browser pane can't run the phone page:** it blocks the controller's `js/app.js`, so it
  is no use for testing the phone side. Use a real phone.

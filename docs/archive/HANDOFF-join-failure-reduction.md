# Handoff — join-failure reduction (CLOSED)

Written 2026-09-07. Read this, then read
[`docs/plans/join-failure-reduction.md`](plans/join-failure-reduction.md) — that plan is the
source of truth for the design, the ten hardware rounds, and every conclusion reached and
discarded. **This document deliberately does not repeat any of it.** It covers only what a fresh
session cannot recover from the repo.

## The work is finished

There is no blocking task and nothing waiting on a measurement. `HEAD` is `a1fe61a` and the tree
is clean — everything from this session is committed.

If you are here to continue *this* line of work, the honest answer is that it is done and the
next thing is unrelated. What remains are three optional items, all recorded in the plan's
"CLOSED" section and none of them urgent.

## What a fresh session most needs to know

- **The captive portal is abandoned on purpose, not forgotten.** Android resolves both probe
  hosts every ~60s and issues no HTTP probe, while the same phone's browser completes a full
  request to the same port seconds later. Ten rounds excluded every host-side cause by direct
  measurement. Do not reopen it on a hunch; the plan has a table of what died and how.
- **P1's TV nudge is the mechanism now.** It keys off DNS queries the relay demonstrably
  receives, so it needs nothing from the phone.
- **Isolation is verified real for the first time.** `hotspot-control.ps1` used to call
  `Start-ScheduledTask` and report success — proving only that a task *launched*. It now reads
  the forwarding flag back. Because `HotspotService` treats that warning as fatal, an unisolated
  hotspot refuses to start rather than pretending, which is new behaviour someone should expect.

## Traps this session hit — worth not rediscovering

- **`LocalServices` reuses any relay already listening** (`StartRelay`). Stopping and starting
  Play does **not** pick up a relay code change if the previous relay outlived its session. Two
  test cycles were spent measuring stale code. Kill the node `server.js` process first;
  `diagnose-hotspot.js` step 4 is the cheapest proof of which code is running.
- **A tool that cannot fail is worse than no tool.** The first packet capture filtered on TCP 80
  only, so "no packets" meant either "no probe" or "wrong adapter" — indistinguishable. The
  rewrite filters on the hotspot address so the phone's DNS acts as a positive control, and
  refuses to conclude without it. The same class of error appeared twice more: an empty
  `$guestPrefix` silently widened a lookup to every interface, and `Start-ScheduledTask` was
  read as proof of a state change.
- **Windows PowerShell 5.1 reads a BOM-less UTF-8 `.ps1` as ANSI**, so an em dash in a string is
  a parse error. Keep new `.ps1` files ASCII-only. Several existing ones contain em dashes and
  work only because they sit in comments.
- **`ErrorActionPreference = 'Stop'` plus a native command is a trap.** PowerShell turns stderr
  into a throwing ErrorRecord, so a harmless `pktmon` notice killed a script mid-run and closed
  the elevated window on an unreadable error.
- **Clicking inside a console window pauses the process** (QuickEdit). The title changes to
  `Select ...`. This looked exactly like a hang.
- **`pktmon filter add -i` takes literal IPs, not a CIDR.** A subnet is accepted silently and
  matches nothing — a 16 MB capture file with zero packets.
- **The user commits from a parallel terminal mid-session.** Never assume `git status` reflects
  only your own edits, and never assume your work is still uncommitted.
- **There is no out-of-band C# compile check** — Unity holds the project lock. Read the Editor
  Console.

## Tools left behind, all under `relay/`

`diagnose-hotspot.js`, `watch-guests.js`, `capture-probe.ps1` (self-elevating), and
`probe-access.log` (gitignored; the `:80` listener and the DNS responder both write to it). The
plan describes what each answers and, more importantly, what each cannot.

`test-portal-fix.ps1` was deleted in `a1fe61a` — recoverable with
`git show bd22729:relay/test-portal-fix.ps1`.

## How to re-run what has been verified

Both from `C:\Users\aj\Desktop\GameDev\RockSkipping\relay`, in Git Bash:

- `npm test` — 46 pass with Play stopped. With Play running, expect 45 pass / 1 skip: the
  game's relay owns `:80`, so `test-captive.js` skips that case by design.
- `node diagnose-hotspot.js` — walks the phone's chain against a live game. Requires Play mode
  with the hotspot up; fails fast and says so otherwise.

## Working agreements observed this session

- Findings go to a `.md` and wait for a decision; do not edit source off the back of a diagnosis.
  Two code changes here were made and then reverted when new evidence arrived — that is the
  process working, not thrash.
- Commands are handed over one per step card, with the absolute directory, the named shell
  matching the fence label, and `UNTESTED:` above the fence for anything not actually run.
- Mutating git is the user's. Read-only git is fine.
- **Artifacts belong in the project directory, never a temp directory — including this file**,
  which the handoff skill wanted to put in the OS temp directory. The user's standing rule won;
  the deviation is flagged here, as it was last session.

## Suggested skills

- **`mattpocock-skills:diagnosing-bugs`** — if a new hardware symptom appears. The lesson worth
  carrying in: state what a measurement *cannot* show before trusting what it does.
- **`unity:unity-cli`** — only if Unity-side changes need verification beyond the Console, and
  only with the Editor closed, since it holds the project lock.

Do not reach for a workflow or multi-agent orchestration. Nothing here is parallelisable; the
whole task was one measurement at a time.

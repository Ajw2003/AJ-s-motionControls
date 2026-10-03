# Round 2 — fixing what the seamless-rejoin work got wrong

## Context

The first round shipped and merged (PR #8). Two of its three workstreams do what they claimed:
the hotspot SSID/passphrase and the room code now survive a restart, confirmed by playtest.

The third caused a regression and rested on a claim about phones that is false in the case that
actually matters. Both are my errors in the round-1 plan, not implementation mistakes.

### Error 1 — the rejoin screen gates naming

I specified a `tapScreen` that reads `ROCK SKIPPING / [TAP TO PLAY] / "Welcome back"`. It has no
name field and no escape hatch, and `app.js` shows it whenever a stored identity matches the room
in the URL. So once a phone has played once, **it can never reach the naming form again** — it
cannot name itself, rename itself, or join as somebody else. The `SET_NAME` plumbing shipped, but
nothing in the UI ever calls it with a new name, so the rename the plan promised does not exist.

### Error 2 — "the phone auto-joins the network on its own" is false in the normal case

The round-1 plan claimed a returning phone rejoins the hotspot with zero interaction. That only
holds where the hotspot is the *only* network the phone knows. In the case being designed for —
**home Wi-Fi in range and cellular enabled** — both platforms prefer a known network that has
internet, and the game hotspot deliberately has none (`setup-offline.ps1` disables IP
forwarding). The phone stays on the house network and will not switch. The playtest showed
exactly that, and it is OS behaviour, not something to fix in code.

Reframing around LAN mode is not available either: `NetworkMode.Lan` is deprecated because on
someone else's router there is no way to make phones resolve `play.skiprocking.ca` to the host.
Hotspot is the only working mode, so **the Wi-Fi step is real and stays**.

**What stable credentials actually bought**, stated honestly: the network now appears in the
phone's Wi-Fi list under a stable, recognisable name with a remembered password. The step goes
from *scan a QR and join a new network* to *tap a saved network* — no scan, no password. It does
not become automatic. In a no-router venue, where hotspot mode is the right tool, it does become
automatic, and that is where the zero-interaction claim holds.

### Revised, honest numbers

| | First visit | Returning, home Wi-Fi in range | Returning, no other network |
|---|---|---|---|
| Wi-Fi | scan QR + tap Join | tap the saved network (no scan, no password) | nothing |
| Page | scan QR + tap banner | tap the tab or history entry | same |
| Join | type name, tap JOIN, grant motion | tap JOIN (prefilled) | same |
| **Total** | **2 scans, ~9 actions** | **~3 taps, 0 scans** | **~2 taps, 0 scans** |

## Fix A — delete the tap screen; the join form is the welcome-back screen

The tap screen existed only to supply the iOS motion gesture. The JOIN button already is a
gesture, so the extra screen bought nothing and cost the naming route.

- **`controller/index.html`** — remove the `#tapScreen` section and `#tapToPlayBtn` entirely.
- **`controller/js/app.js`**
  - Drop `tapScreen`/`tapToPlayBtn` from the `ui` map and from `showScreen`'s screen list.
  - In `init()`, replace the tap-screen branch: when a stored identity exists, prefill
    `nameInput` from it and `codeInput` from the URL, and leave the join form showing. Do not
    auto-connect on Android either — one consistent path on both platforms is worth more than
    saving Android users a tap, and it keeps the name visible and editable before joining.
  - `attemptRejoin` collapses into `join()`: with the fields prefilled, the ordinary join path
    already does everything it did. Delete it rather than keep two routes that must agree.
  - Keep `hasJoinedOnce` and the `retryJoin` behaviour on `ERROR`/`ROOM_CLOSED` exactly as they
    are — that part is working and is what fixes the repeated-tap report.

`needsMotionGesture()` in `sensors.js` becomes unused. Delete it unless something else has picked
it up by then.

## Fix B — renaming has to be reachable

The plan promised it; nothing implements it.

- **`controller/index.html`** — on `#waitScreen`, add a `#nameBtn` styled as a `.link` (matching
  `#recentreBtn`), showing the player's current name.
- **`controller/js/app.js`** — tapping it reveals an inline input seeded with `myName`; on commit,
  update `myName`, persist through `updateIdentity`, and send `{ type: 'SET_NAME', name }` on the
  live connection. No rejoin, no reconnect — the seat is keyed by device id now, which is the
  whole point of round 1.
- Reuse the existing `SET_NAME` send at [app.js:437](../../../controller/js/app.js:437); it already fires
  on every `JOINED`, so the reconnect path keeps working unchanged.

## Fix C — the TV should name the network, since it is now stable and recognisable

With a random per-session SSID there was no point printing it; now there is, and it is what makes
"tap the saved network" possible without a scan.

- **`Assets/Scripts/UI/JoinScreen.cs`** — in `DrawJoinDetails`, when `HotspotService` is `Ready`,
  print the SSID as text beneath the Wi-Fi QR, and change the two-QR caption so it tells a
  returning player they can pick that network from Wi-Fi settings instead of scanning. The
  passphrase stays on screen for the manual case — `HotspotService.Passphrase` is already exposed.

## Fix D — sweep the controller for anything else artificially gated

The tap screen was one instance; the request was to make sure there are no others. Walk every
screen (`join`, `choose`, `wait`, `throw`) and confirm each control is reachable in the state it
belongs to — in particular that a player who rejoins straight into `wait` can still reach the
colour picker and stone choice, since those live on `chooseScreen` and are driven by the
`choosing` flag in SPECTATE rather than by anything the phone controls. Report anything found
rather than silently changing behaviour.

## Verification

Not yet run — these are the acceptance checks.

1. `cd controller && npm test` and `cd relay && npm test` — both must stay green.
2. `node --check` on every changed JS file.
3. **Naming, first visit:** clear site data, load the join URL. The form appears with an empty
   name and the code prefilled. Type a name, tap JOIN, play.
4. **Naming, returning visit:** reload the page. The form appears with the name **and** code
   prefilled; one tap on JOIN gets back in with the same seat, score and colour.
5. **Rename mid-match:** change the name from the wait screen. Score, colour and turn order stay
   put; the TV and the other phone's scoreboard show the new name; no reconnect occurs.
6. **Join as someone else:** clear the name field, type a different one, tap JOIN. That must be
   possible — it is the case the tap screen made impossible.
7. **The Wi-Fi claim, measured rather than assumed:** with home Wi-Fi in range, relaunch the host
   and confirm the phone does *not* auto-join, and that the TV now names the network clearly
   enough to find it in Wi-Fi settings in one tap. Then, with Wi-Fi forgotten for the house
   network (or out of range), confirm it *does* auto-join — that distinguishes OS priority from
   an actual fault.
8. **Regression:** the repeated-tap fix must survive. Mid-match, Wi-Fi off for more than 45
   seconds, then on — the phone rejoins with no form and no permission prompt.

# Reference — how player colour actually works

**2026-09-02.** Written after the fact, from the code as it stands — `docs/plans/Plan-SixFixes.md`
proposed this system before it existed and has already drifted from what got built; treat
this doc, not that plan, as current. If the two ever disagree, read
`Assets/Scripts/Managers/PlayerColours.cs` — that file is the source of truth.

## What this replaced

Before this system, a pointer dot's colour was
`PointerPalette[hash(playerId) + ordinal % 8]` inside `ShoreRocks`, where `ordinal` was how
many pointers existed when that one was created — and every phase change destroyed every
pointer and reset the count. So a player's colour changed between rounds, and two players
could land on the same slot. Both were open complaints in `notes.txt`. Colour is now owned by
one manager, keyed by player identity, and nothing else.

## The pieces

| piece | file | role |
|---|---|---|
| `Managers.PlayerColours` | `Assets/Scripts/Managers/PlayerColours.cs` | The one source of truth for "what colour is this player". Singleton, persists across scenes. |
| `Game.PlayerColourPalette` | `Assets/Scripts/Game/PlayerColourPalette.cs`, asset at `Resources/PlayerColourPalette.asset` | The swatches and the rules a pick is checked against. Editable without a recompile. |
| `Net.Messages` | `Assets/Scripts/Net/Messages.cs` | `PickColourMessage` (inbound) and the `colours`/`palette` fields on `BuildSpectate` (outbound). |
| `controller/js/app.js` | — | The phone's picker UI and its half of the sync. |

`docs/Reference-Constants.md` §2/§6 lists every field and default; this doc is the flow those
fields sit inside.

## The full round trip

```
phone taps a swatch                                              (app.js pickColour)
  │
  ├─ paints itself immediately, optimistically           (app.js applyMyColour)
  │
  └─ sends PICK_COLOUR {playerId, colour: "#rrggbb"} ────────────────────┐
                                                                          ▼
                                                    relay forwards to host unchanged
                                                    (relay/server.js's default case —
                                                     no relay code was written for this)
                                                                          │
                                                                          ▼
                                              RelayClient.PickColourReceived fires
                                              (Assets/Scripts/Net/RelayClient.cs)
                                                                          │
                                                                          ▼
                                        PlayerColours.OnPickColour → Pick(playerId, colour)
                                              (PlayerColours.cs:76, :158)
                                              parses the hex, then adjusts it:
                                                1. Legible()   — raise to the brightness/
                                                                 saturation floors
                                                2. Separate()  — rotate hue away from any
                                                                 other connected player
                                                                 within MinSeparation
                                                                          │
                                                                          ▼
                                        publishes PlayerColourChangedEvent(playerId, settled)
                                        (read by ShoreRocks and AimPreview — see below)
                                                                          │
                                                                          ▼
                                        next TurnManager.BroadcastSpectate() includes
                                        settled colour in SPECTATE's "colours" map
                                              (TurnManager.cs, around line 520;
                                               Messages.cs BuildSpectate)
                                                                          │
                                                                          ▼
◄─────────────────────────────────────────────────────────────────── every phone
phone's renderSpectate reads message.colours[myPlayerId]
  (app.js, in renderSpectate) and repaints if it differs from what
  it already believes — this is what makes a host-side nudge visible
```

Nothing here is instant end to end. The optimistic paint on tap is immediate; the
host-confirmed version arrives on the next `SPECTATE`, which fires on state changes rather
than a timer (see `BuildSpectate`'s remarks in `Messages.cs`) — in practice within the same
frame or two, since a colour pick itself is a state change that triggers a broadcast.

## Why two colour variables on the phone

`app.js` keeps `requestedColour` and `myColour` deliberately separate:

- **`requestedColour`** — what this phone last asked for. Set the instant a swatch is
  tapped, purely so the swatch row can show a selection before the round trip completes.
- **`myColour`** — what `SPECTATE` last said this phone's colour actually is. This is the
  one other things trust.

`applyMyColour(colour)` is the single function that paints "my" colour onto the page — sets
the `--accent` CSS variable, syncs the native `<input type="color">`, and rings the matching
swatch. It's called from both paths: once optimistically from `pickColour`, and once
authoritatively from `renderSpectate` whenever `message.colours[myPlayerId]` doesn't match
`myColour` yet. If the host nudged the pick (too dark, too close to another player), the two
calls disagree for a moment and the second one wins — visibly, since the swatch ring and the
accent colour jump to the corrected value.

Every other player's colour has no local state at all: the score rows just read
`message.colours[player]` fresh off each `SPECTATE` and paint a dot. There's nothing to
reconcile because a phone never "requests" someone else's colour.

## The Unity side: who actually gets tinted

Everything on the Unity side goes through two calls on `PlayerColours`:

- **`For(playerId)`** — the flat colour, no blending. Used for the shore pointer dots
  (`ShoreRocks.ColourFor`, line 298) and score-adjacent UI.
- **`StoneColourFor(playerId, baseColour)`** — `baseColour` blended toward `For(playerId)` by
  `PlayerColourPalette.ConfirmedBlend` (0.65 by default). This is the one blend calculation
  in the whole system — every stone-shaped thing calls it rather than keeping its own copy,
  so "the rock you picked" and "the rock you threw" can't drift apart:

  | what | where | when it's applied |
  |---|---|---|
  | Shore pointer dot | `ShoreRocks.CreateMarker` (`ColourFor`, unblended) | On first AIM frame or PICK_ROCK from that player |
  | Confirmed shore stone | `ShoreRocks.UpdateStoneLooks` → `ConfirmedColourFor` (line 386) | Once a player has claimed that stone |
  | Aim ghost | `AimPreview.TintGhost` (line 244) | `OnTurnStarted` (line 236) and on a live `PlayerColourChangedEvent` (line 261) |
  | Thrown stone | `RockLauncher.CreateRockObject` | Once, when the stone object is built, before it's ever rendered |
  | Trail | `RockTrail.ApplySpeedTint` (full `For(playerId)`, not blended — a trail has no tint of its own to preserve) | Every frame, multiplied into the existing speed fade |

Two live-update paths matter, both subscribed to `PlayerColourChangedEvent`:

- `ShoreRocks.OnPlayerColourChanged` (line 285) re-tints a pointer dot already on screen.
- `AimPreview.OnPlayerColourChanged` (line 261) re-tints the ghost, but only if the changing
  player is `TurnManager.ActivePlayer` right now — not `_lastAimPlayer`, because
  `_lastAimPlayer` only updates once that player's first AIM frame arrives, which lags
  `OnTurnStarted`. Using it would miss a colour change landing in that gap.

The thrown stone and the trail are **not** live-updatable — they're painted once at spawn
from whatever colour the thrower had at release, and nothing re-tints a stone already in
flight. That's a property of when the code runs, not a deliberate switch.

## What decides "does this get coloured"

There is currently no single flag for this — each consumer above is its own unconditional
call site. Turning one off means editing that call site, not flipping a setting.

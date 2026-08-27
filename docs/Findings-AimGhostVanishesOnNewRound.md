# Findings — aim ghost vanishes when a new round starts

**Date:** 2026-08-26
**Branch:** `RockChoosing`
**Evidence:** `Screen Recording 2026-08-26 221204 - Trim - Trim.mp4` (9.2 s, single player `BUH`,
1 round). Frames pulled at 2 fps with ffmpeg; Console timestamps read off the crops.

## What the recording shows

| t (approx) | Console | Game view |
|---|---|---|
| 22:11:48 | `[TurnManager] Match over after 1 rounds — winner: BUH` | FINAL SCORES |
| 22:11:51 | `[TurnManager] Back in the lobby` + `[AimPreview] Rejected AIM frame — phase is Lobby, not Playing` | lobby / QR screen |
| 22:11:52 | `[RockSelection] Random rocks`, `[TurnManager] Match started`, `Round 1/1: BUH's turn` | **ghost stone + arc visible for a fraction of a second** |
| 22:11:52.5 → 22:11:54 | *(no further AimPreview logs at all)* | **empty water — no ghost, no arc** |
| 22:11:54 | `[Throw] BUH: pow 0.90 spin -602 …` | stone flies |

The gap is the tell: `AimPreview.LogRejectedAim` is rate-limited to one line per second, so if
AIM frames were arriving and being rejected we would see a line every second between :52 and
:54. There are none. **AIM frames stopped arriving entirely** — this is not a rejection, and not
a Unity-side visibility bug.

## Cause

The ghost is gated on `HasSignal` in `AimPreview.Update`
([AimPreview.cs:246](../Assets/Scripts/Game/AimPreview.cs#L246)):

```csharp
HasSignal = Time.time - _lastAimTime <= staleAfterSeconds;   // staleAfterSeconds = 0.5f
...
_line.enabled = visible;
_ghost.gameObject.SetActive(visible);
```

So the ghost is visible exactly while the phone is sending AIM. The phone sends AIM from **two
independent streams**, and neither one is running in the window above:

1. **Lobby stream** — `startLobbyAim()` / `stopLobbyAim()` in
   [controller/js/app.js:126](../controller/js/app.js#L126). A bare 20 Hz `setInterval`, started
   on a `LOBBY`-phase `SPECTATE`, for the shore-pointing rock picker.
2. **Throw stream** — `ThrowController.arm()` in
   [controller/js/throw.js:416](../controller/js/throw.js#L416). Its 20 Hz timer only exists
   between `arm()` and `cancel()`/`_fire()`, i.e. only while the player has tapped
   **TAP TO AIM** and not yet thrown.

The sequence that produces the video:

1. Lobby: stream 1 is running. Unity is in `Lobby`, so `OnAim` rejects every frame — that is the
   22:11:51 rejection log.
2. Space pressed → `TurnManager` flips to `Playing` and broadcasts `TURN`.
3. For the handful of frames between Unity flipping phase and the phone processing `TURN`, the
   lobby stream is *still running* and Unity now accepts it. **That is the ghost flash at
   22:11:52.**
4. The phone handles `TURN` → `case 'TURN': stopLobbyAim();`
   ([app.js:204](../controller/js/app.js#L204)) → stream 1 dies. Stream 2 does not start,
   because it only starts on a tap.
5. 0.5 s later `HasSignal` goes false and the ghost/arc are hidden. They stay hidden until the
   player taps TAP TO AIM.

So the disappearance is the code working as written. The genuine defect is the **flash**: the
lobby stream leaks a few frames past the phase change and shows a ghost the phone is about to
stop feeding, which reads as "the ghost appeared and then broke". Underneath that is the design
question of whether a player on their turn but not yet armed should see a ghost at all.

## A second defect found while fixing this: the AIM sequence number

`AimPreview.OnAim` drops any frame whose `seq` is not greater than the last accepted one
from that player, and only resets `_lastSeq` when the *player* changes
([AimPreview.cs:306](../Assets/Scripts/Game/AimPreview.cs#L306)). The phone was counting
separately per producer — `lobbySeq` in `app.js`, `this.seq` in `ThrowController` — so handing
over from the lobby stream to the armed stream went **backwards**: after ten seconds of shore
pointing the lobby counter is past 200 while `ThrowController.seq` is in single figures, and
every armed frame was silently dropped until it climbed back past it.

That is why tapping TAP TO AIM did not reliably bring the ghost back either. Fixed by stamping
`seq` once, on the way out, in `app.js`'s new `sendAim` — the single route onto the wire for all
three producers.

## Options (option 1 implemented — see Build log)

1. **A turn-idle aim stream on the phone.** On `TURN` with `yourTurn`, start a lobby-style
   preview-only AIM timer, and stop it when `arm()` takes over (and on the non-turn branch).
   Ghost is live from the instant the turn starts; the arm/swing safety property is untouched,
   because release detection still only runs while `ARMED`. Costs a third AIM producer to keep
   straight.
2. **Auto-arm on turn start.** One line, but it hands the fire detector a live phone before the
   player has looked at the TV — a swing at turn start would throw. Contradicts the reasoning
   in `startLobbyAim`'s comment about not arming from the lobby.
3. **Do nothing on the phone; suppress the flash in Unity.** Ignore AIM in `AimPreview` until
   the active player's first *armed* frame this turn (needs a flag on the wire distinguishing
   lobby AIM from armed AIM). Keeps the current "no ghost until you tap" contract and stops it
   lying for half a second.

Option 1 is the one that matches what the recording suggests you expected to see.

## Build log

Both the ghost fix (option 1) and the phone-side advance (any connected phone) are implemented.

**Controller (`controller/`)**

- `js/app.js` — the lobby AIM timer generalised into one shared `previewAimTimer` with two
  entry points, `startLobbyAim` (guarded on `!myTurn`) and the new `startTurnAim` (guarded on
  `myTurn`). One timer, so the two can never double-feed the TV.
- `TURN` with `yourTurn` now calls `startTurnAim()`; the non-turn branch stops the stream
  explicitly, because `thrower.cancel()` only ever stopped ThrowController's own.
- `onStateChange` hands the stream over: arming stops the preview one (ThrowController's
  frames carry live spin and windup), standing down hands it back.
- `renderSpectate`'s stop is now `if (!myTurn)`. Unconditional, it killed the turn-idle stream
  a moment after `TURN` started it — SPECTATE is broadcast and goes out on every turn start.
- New `sendAim` stamps the shared `aimSeq` — the seq defect above.
- `bindAdvance`/`renderAdvance` + `index.html`'s `#advanceBtn` (choose screen) and `#againBtn`
  (wait screen). Each hides itself on press rather than waiting for the reply, so a double tap
  cannot land in the next phase.

**Unity (`Assets/`)**

- `Net/Messages.cs` — `MessageType.Advance`, `AdvanceMessage`, and a `choosing` bool on
  SPECTATE so the phone's button can say RESUME MATCH rather than START MATCH during a
  mid-match re-pick (the wire phase for Choosing stays `LOBBY`).
- `Net/RelayClient.cs` — `AdvanceReceived` + a `Dispatch` case, identity stamped like the rest.
- `Managers/TurnManager.cs` — the Space-key switch extracted to `public void Advance()`, which
  both the key and `OnAdvanceRequested` call, so keyboard and phones cannot disagree about
  where "on" is. `Playing` is deliberately not a case.
- `UI/JoinScreen.cs`, `UI/ResultsScreen.cs` — prompts now name both routes.

**No relay change.** `relayToHost` stamps `playerId` and forwards anything
([relay/server.js:268](../relay/server.js#L268)); the routing is type-agnostic by design.

**Verification status:** verified in play on 2026-08-26. `controller/js/app.js` passes
`node --check`, the C# compiles clean in the Editor, and a full match was played through:
throw, match end, PLAY AGAIN on the phone, START MATCH on the phone, with the ghost up from the
moment the turn starts rather than only after TAP TO AIM.

## Related: advancing the match without a keyboard

Raised while reading this recording — Space is currently the *only* way past three of the four
phases. [`TurnManager.cs:175`](../Assets/Scripts/Managers/TurnManager.cs#L175):

```csharp
if (Input.GetKeyDown(startMatchKey))
{
    switch (Phase)
    {
        case MatchPhase.Lobby when CanStartMatch: StartMatch();          break;
        case MatchPhase.Results:                  ReturnToLobby();       break;
        case MatchPhase.Choosing:                 ResumeAfterChoosing(); break;
    }
}
```

In the recording that is two Space presses per match cycle (Results → Lobby at 22:11:51,
Lobby → Playing at 22:11:52). Wanted: Space stays acceptable to get the very first match going,
but the loop after that should be drivable from a phone.

The wire path already has a template — `PICK_ROCK`, a phone→PC message carrying nothing but the
sender's identity ([`Messages.cs:26`](../Assets/Scripts/Net/Messages.cs#L26),
[`app.js:149`](../controller/js/app.js#L149)). A new `ADVANCE` would mirror it exactly:

- `MessageType.Advance = "ADVANCE"` + `AdvanceMessage { playerId }` in `Messages.cs`, a
  `RelayClient.AdvanceReceived` event and a `Dispatch` case — the extend-don't-sniff rule from
  `Assets/CLAUDE.md`.
- `TurnManager` subscribes and runs **the same switch** as the key press, so keyboard and phone
  can never diverge. The `Lobby when CanStartMatch` guard and the `Results`-doesn't-skip-Lobby
  reasoning in the comment above are preserved by construction.
- Controller: a button on the `wait` screen, shown when SPECTATE's phase is `RESULTS` (label
  "Play again") or `LOBBY` (label "Start match"), sending `{ type: 'ADVANCE' }` through
  `sendInput`. `Choosing` already has CONFIRM, so it may not need one.
- The relay needs nothing — it stamps identity and forwards unknown types already (confirm
  against `relay/` before relying on it).

**Open decision — who may advance?** This changes the work:

1. *Any connected phone* — simplest, matches Jackbox; one player can start on everyone else.
2. *First-joined player only* ("host phone") — needs a host concept `TurnManager` does not have
   today, and a rule for what happens when that phone drops.
3. *Everybody ready* — a per-player ready flag broadcast in SPECTATE, advance when all are set.
   Most work, best for a full couch.

Nothing implemented yet — pending that answer.

## Reproducing

```powershell
ffmpeg -v error -y -i "<recording>.mp4" -vf "fps=2,scale=960:-1" frames\f_%03d.png
```

Read `f_008`–`f_015` alongside the Console crop
(`crop=in_w*0.82:in_h*0.30:0:in_h*0.69`) to see the flash and the log gap.

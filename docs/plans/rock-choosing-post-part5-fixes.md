# Post-Part-5 fixes: random rocks + aim preview visibility

## Context

Playtesting after Part 5 (see [`rock-choosing-part5-settings-panel.md`](rock-choosing-part5-settings-panel.md))
confirmed the settings panel, JSON persistence, and rounds-per-match all work. Two separate bugs
surfaced:

### Bug A — Random rocks almost always gives everyone the base rock

**Root cause, found by reading `RockSelection.cs`:** `AssignRandomRocks()` iterates over
`_picks.Keys` — but `_picks` is *cleared* every time the match enters a choosing phase
(`OnMatchPhaseChanged`, `TurnManager.IsChoosingPhase(e.To)` branch), and in Random mode nothing
ever repopulates it before the next `AssignRandomRocks()` call: `RockSelection.Pick()` refuses
phone picks outright while `randomRocks` is on (`if (... || randomRocks) return;`), and
`OnPlayerJoined` only seeds an entry when a player *first* joins, not on every Lobby/Choosing
re-entry.

Concretely: the first match after the game boots works (players seeded `_picks` entries when
they joined, before any phase ever cleared the dict), so `AssignRandomRocks` has real keys to
iterate and assigns correctly. Every match after that — pressing start from Results calls
`ReturnToLobby()`, which clears `_picks` — has an **empty** `_picks` by the time `StartMatch()`
fires `AssignRandomRocks()`, so the loop body never runs, nobody's `_picks` entry gets written,
and `RockSelection.For(playerId)` falls back to `_catalog.At(0)` (the base rock) for everyone.
This matches what was observed: only the very first random assignment (1 in however many
matches/re-picks were played) actually varied.

**Fix:** `AssignRandomRocks()` should iterate over the actual player roster
(`TurnManager.Instance.Players`, already public), not `_picks.Keys` — the roster is what's
supposed to receive an assignment, and it survives exactly the resets that empty `_picks`.

```csharp
private void AssignRandomRocks()
{
    var seed = UnityEngine.Random.Range(int.MinValue, int.MaxValue);
    var rng = new System.Random(seed);
    Debug.Log($"[RockSelection] Random rocks — seed {seed}");

    var players = TurnManager.HasInstance ? TurnManager.Instance.Players : (IReadOnlyList<string>)new List<string>(_picks.Keys);
    foreach (var player in players)
    {
        var index = rng.Next(_catalog.Count);
        var rock = _catalog.At(index);
        if (rock == null) continue;

        _picks[player] = rock.Id;
        EventManager.Instance?.Publish(new RockPickedEvent(player, rock.Id, index));
    }
}
```

File: `Assets/Scripts/Managers/RockSelection.cs`. One-method change, no other files touched —
`TurnManager.Players` and `RockSelection.Instance` are already mutually visible (RockSelection
already references `TurnManager` elsewhere in the same file for `IsChoosingPhase`/`Pick`).

### Bug B — Aim preview (ghost + arc) inconsistently fails to show while aiming

**What was ruled out by reading the code**, so the fix isn't wasted on the wrong layer:
- Not mesh/rock-specific — `AimPreview.Update()`'s visibility gate
  (`visible = HasSignal && !flying`) runs identically regardless of which `RockDefinition` is
  active; a missing mesh would leave the ghost showing the *previous* shape, not inactive.
- Not a stuck "still flying" flag — `RockLauncher.OnThrow` gates a new throw on the exact same
  `_active.IsFlying` property AimPreview reads for `flying`; since the user confirms new throws
  fire and land correctly, that flag must already be false by the time of the next aim, so it
  can't also be the reason the ghost stayed hidden through that aim.
- Not a playerId-stamping mismatch between the direct WebRTC path and the relay fallback —
  `RelayClient.Dispatch` stamps `aim.playerId`/`thrown.playerId` from the same
  `trustedPlayerId` regardless of transport (`PeerLink.IngestFromPeer` supplies the identical
  guarantee the relay socket does). Since THROW's identical-shaped check
  (`message.playerId != turns.ActivePlayer`) reliably succeeds (throws fire correctly), AIM's
  copy of that check has no reason to disagree for the same phone.

That leaves the two guards inside `AimPreview.OnAim` — `Phase != MatchPhase.Playing` and
`aim.playerId != active` — as the only things that can hold `HasSignal` false for an entire
aiming window (nothing else feeds `_lastAimTime`), and no static read tells us which one, or
whether frames are even arriving at all. **`RelayDebugHud`** (already in the scene by default,
`F1` to toggle) already surfaces exactly the missing signal: an `Aim` line with live Hz/seq
whenever *any* AIM frame arrives from *any* player (no Phase/active-player filtering — it
subscribes to the same `RelayClient.AimReceived` event AimPreview does, unfiltered), plus a
`Turn` line showing `TurnManager.ActivePlayer`. If that HUD shows live aim frames from the
aiming player while the ghost stays hidden, the bug is confirmed inside AimPreview's filter; if
it shows no frames at all, the bug is upstream (phone/transport), outside this file.

**Decision (user, 2026-08-26):** land Bug A's fix now, and pair it with cheap, reversible
diagnostic logging on both of `OnAim`'s guards (rate-limited so it can't spam) so the *next*
repro pins the exact cause instead of staying "inconsistent, can't tell why," rather than
guessing at a speculative behavioral fix with no way to verify it addresses the real cause.

```csharp
private float _nextRejectLogTime;

private void OnAim(AimMessage aim)
{
    if (!TurnManager.HasInstance || TurnManager.Instance.Phase != MatchPhase.Playing)
    {
        LogRejectedAim($"phase is {(TurnManager.HasInstance ? TurnManager.Instance.Phase.ToString() : "no TurnManager")}, not Playing");
        return;
    }

    var active = TurnManager.Instance.ActivePlayer;
    if (aim.playerId != active)
    {
        LogRejectedAim($"from '{aim.playerId}', active is '{active}'");
        return;
    }
    // ...unchanged...
}

private void LogRejectedAim(string reason)
{
    if (Time.time < _nextRejectLogTime) return;
    _nextRejectLogTime = Time.time + 1f;
    Debug.Log($"[AimPreview] Rejected AIM frame — {reason}");
}
```

File: `Assets/Scripts/Game/AimPreview.cs`.

**2026-08-26 update:** the user reproduced it and captured the Console around the failure — but
the captured window was the Lobby transition (`"Back in the lobby"` → repeated `"phase is
Lobby, not Playing"` → `"Match started"`), which is the shore-pointing AIM stream being
*correctly* rejected, not evidence of the actual bug (that window wasn't shown). The user's
independent live observation — un-arming and re-arming during their actual turn didn't bring
the ghost back — pointed at a phone-side state issue rather than a Unity-side one, and asked for
phone-side hardening directly rather than a further diagnostic round-trip.

**What was actually hardened**, in `controller/js/app.js`: a real (if narrow) race existed
between the lobby shore-pointer's AIM stream (`startLobbyAim`, gated only on SPECTATE's `phase`
field) and the throw controller's own AIM stream (gated on the phone's local `ThrowState`).
`TURN` and `SPECTATE` are both always-relay and therefore ordered relative to each other, so a
stale LOBBY-phase SPECTATE arriving *after* `TURN{yourTurn:true}` isn't possible from the
network — but the gap between that TURN landing (which shows the throw screen) and the player
actually tapping to arm was still a window where `renderSpectate`'s existing
`thrower.state === ARMED` guard doesn't apply yet (they're not armed until they tap), and if a
SPECTATE happened to land in exactly that gap it could still call `showScreen('choose')` and
`startLobbyAim()`, silently swapping the phone back to the choose screen and starting a second,
competing AIM stream underneath a turn the phone already knew was live. Closed by:

- A new `myTurn` flag, set only from `TURN` (the personalised, authoritative "is it my turn"
  signal — unlike SPECTATE's broadcast `phase` field), reset on a fresh `JOINED`.
- `renderSpectate` bails out of the whole LOBBY branch when `myTurn` is true — TURN always wins
  over a stale-looking SPECTATE for screen selection.
- `startLobbyAim()` itself now also refuses to start while armed *or* while `myTurn` — moved
  into the one function that actually starts the timer, so no future call site can reintroduce
  the race by skipping a caller-side check.

This closes a real class of bug regardless of whether it was the *exact* mechanism behind the
original report — if the ghost still fails to appear after this, the next repro's Console output
(the `[AimPreview] Rejected AIM frame` diagnostic added above, captured *during the actual armed
throw*, not the Lobby transition before it) is what will pin the remaining cause.

## Verification

No out-of-band compile check — verify via the open Editor's Console per this repo's usual
workflow.

1. **Bug A**: play several matches back to back (Results → start → Playing) with Random rocks
   on. Confirm players get varied rocks every match, not just the first, and the
   `[RockSelection] Random rocks — seed …` log appears each time with subsequent
   `RockPickedEvent`s actually landing on non-default rocks.
2. **Bug B**: play normally with the F1 `RelayDebugHud` open. Next time the ghost/arc fail to
   appear, check the Console for a `[AimPreview] Rejected AIM frame — …` line **captured during
   the actual armed throw**, not a Lobby transition — it will say either "phase is X, not
   Playing" or "from 'A', active is 'B'", which tells us exactly which guard is firing and
   against what value. Report that back if it still happens after the phone-side hardening below.
3. **Phone-side hardening**: on a real phone, confirm the choose screen's shore-pointing AIM
   still works normally in the lobby (nothing regressed there), and that tapping to arm right as
   `TURN{yourTurn:true}` lands still shows "TAP TO AIM" → "SWING!" reliably. There's no
   reproduction recipe for the exact race this closes (it depends on message timing), so this is
   confirming no regression rather than confirming the original bug is gone — that confirmation
   comes from bug-free play over a few sessions, or from the Console staying clean per point 2.

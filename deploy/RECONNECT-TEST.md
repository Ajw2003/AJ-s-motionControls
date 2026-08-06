# Testing host reconnection

Proves that a host dropping off the network no longer ejects every player. Background:
[HostingPlan.md](../HostingPlan.md) §5; the bug was found because Fly's trial tier stops
machines every 5 minutes, which killed the host socket mid-match.

**There are three distinct paths and they behave differently.** Testing only one proves
almost nothing, because the interesting failures live in the other two.

| # | What happened | What the players should experience |
|---|---|---|
| **A** | Host socket dropped, **relay still running** | Nothing. They are not even disconnected — the room is *resumed* with everyone still in it. |
| **B** | **Relay itself restarted**, host still running | "Host reconnecting…" for a few seconds, then back in the same room, same code. |
| **C** | Host gone and not coming back | "Host reconnecting…" for 60s, then "Game ended" and the join screen. |

---

## Before you start

**On the PC, terminal** — the phone needs the new `net.js`, `peer.js` and `app.js`, and the
relay needs the grace-period logic:

```bash
fly deploy
```

**On the PC, Unity** — click the editor window so it recompiles (it will not until focused),
and confirm the Console shows no errors. Then open `Bootstrap.unity` and check
**Run Web Rtc Spike** is **un**ticked and **Use Peer Connections** is ticked.

**Second terminal, left running** — this is where you watch what the relay actually does, and
it is the difference between "it seemed to work" and knowing which path you exercised:

```bash
fly logs --app rockskipping-relay
```

Then press Play, join from your phone, and press **Space** to start a match. Throw once so
there is a score worth preserving.

---

## Test A — host socket drops, relay survives

The best case: nobody is disconnected at all.

**This is the one you cannot test by stopping Play.** Stopping Play destroys `RelayClient`,
which takes `RoomCode` with it, so the next Play asks for a fresh code — by design. You need
Unity to stay running while its socket dies.

1. **PC, Unity:** with a match in progress, note the room code and the current scores.
2. **PC, system tray:** turn Wi-Fi **off**. Leave it off for ~10 seconds.
3. **Phone:** the status line should read **"Host reconnecting…"**. The player stays on the
   wait or throw screen — it must **not** drop to the join screen.
4. **PC, system tray:** turn Wi-Fi back **on**.
5. **PC, Unity Console:** expect

   ```
   [RelayClient] Resumed room ABCD with 1 players still in it
   ```

6. **Terminal (fly logs):** expect

   ```
   room ABCD: host disconnected, holding for 60000ms
   room ABCD resumed by a reconnecting host (1 players kept)
   ```

7. **Phone:** status returns to **"Connected as …"**. Throw again — it should work with no
   rejoin, and **the score from step 1 is still there.**

**What proves it:** the words `resumed` and `players kept`. If you instead see `room WXYZ
created`, the host did not reclaim its code and something is wrong with `preferredCode`.

---

## Test B — the relay restarts underneath a running game

This is the failure you actually hit. The relay process dies, so every room dies with it —
the host has to rebuild one under the same code, and the phones have to rejoin it.

1. **PC, Unity:** with a match in progress, note the room code and scores.
2. **PC, terminal:**

   ```bash
   fly machine restart 8d1ed0b2647278 --app rockskipping-relay
   ```

3. **Phone:** **"Host reconnecting…"**, staying on its current screen.
4. **PC, Unity Console:** expect

   ```
   [RelayClient] Reclaimed room code ABCD — phones can reconnect
   ```

   Note this says *reclaimed*, not *resumed* — the relay lost the room, so the players are
   genuinely rejoining rather than being carried across.

5. **Terminal:** expect `room ABCD created (code reclaimed after host restart)`, then
   `room ABCD: AJ joined` within a few seconds, **without anyone touching the phone.**
6. **Phone:** back to "Connected as …". Throw. Scores survive because they live in Unity's
   `ScoreManager`, not in the relay.
7. **TV:** the room code on screen is unchanged. That is the whole point — a code on a TV
   that stops working is what made this a party-ending bug.

**Timing note:** the phone retries every 1.5s for up to 45s, and Fly takes a few seconds to
restart, so expect roughly 5–15 seconds of "Host reconnecting…".

---

## Test C — the host really is gone

The grace period must not become "forever", or a genuinely dead game strands its players.

1. **PC, Unity:** stop Play.
2. **Phone:** **"Host reconnecting…"**.
3. Wait **60 seconds** without pressing Play.
4. **Terminal:** expect `room ABCD closed (Host disconnected)`.
5. **Phone:** switches to **"Game ended"** and the join screen, with the Join button enabled.

**What proves it:** that the phone eventually gives up on its own. A phone stuck on
"Host reconnecting…" indefinitely is a worse bug than the one being fixed.

---

## Test D — the transport survives a reconnect

Phase 2 adds a peer connection that dies with the host, so it has to renegotiate.

1. Run **Test A** with the phone on the **same Wi-Fi** as the PC.
2. **PC, Unity:** press **F1** before the drop. Expect `Input  1/1 direct`.
3. After the reconnect, press F1 again. It should return to `1/1 direct` within a few
   seconds — the phone re-offers on `HOST_RESUMED`.
4. Repeat with the phone on **mobile data**. It should read `0/1 direct` throughout, and the
   game should behave identically. That is the fallback doing its job.

---

## If something looks wrong

| Symptom | Meaning | Where to look |
|---|---|---|
| **Host never reconnects after the network returns** | A connect attempt wedged. This was a real bug: `ConnectAsync` had no timeout, so a TCP connect against a half-torn-down network could hang indefinitely with `_connecting` stuck true, and `Update()` will not start a second attempt while it is. | Unity Console for `Connect … failed (timed out after 8s)` — that line means the timeout is working. `Connect attempt wedged past its timeout` means the watchdog caught one that outlived even that. |
| Host reconnects, but too slowly | Backoff outran the phone's 45s window | Retry delay is capped at 4s on purpose; anything longer than ~15s total suggests repeated connect failures — read the reasons in the Console |
| Phone drops to the join screen immediately | The rejoin window never opened | `net.js` — `expectHostBack` fires on `HOST_LEFT`, `ROOM_CLOSED`, *and* an unexpected socket close |
| Host comes back with a **different** code | `preferredCode` was not sent or was refused | Unity Console for `Reclaimed`/`Resumed`; relay log for `room … created` with no note |
| Relay says `resumed` but the phone still rejoined | Harmless — the phone's socket died independently | Nothing to fix |
| Phone stuck on "Host reconnecting…" past 60s | Give-up path broken | Relay should log `closed (Host disconnected)`; check the grace sweep |
| Everything works, then breaks ~5 minutes later | **Fly trial tier stopping the machine** | `fly logs` for `Trial machine stopping`. Add a card; this is not a code fault |

**One caveat on a passing run.** Tests A and C both start by dropping the host, and they only
differ in whether it returns. If you run A and are slow turning Wi-Fi back on, you are running
C and will see "Game ended" — which is correct behaviour, not a failure. Watch the clock.

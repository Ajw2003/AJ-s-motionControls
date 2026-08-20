# Testing host reconnection

Proves that a host dropping off the network no longer ejects every player. Background:
[HostingPlan.md](../HostingPlan.md) §5. The bug was originally found on the old Fly.io
deployment, whose trial tier stopped machines every 5 minutes and killed the host socket
mid-match. That deployment is gone — the relay now runs on this machine
([README.md](README.md)) — but every failure path below still exists, because a local relay
can be restarted, and Wi-Fi can still drop.

**There are three distinct paths and they behave differently.** Testing only one proves
almost nothing, because the interesting failures live in the other two.

| # | What happened | What the players should experience |
|---|---|---|
| **A** | Host socket dropped, **relay still running** | The room is *resumed* with everyone still in it. Against a remote relay this was invisible to players; with the relay on the host's own machine they lose their socket too and reconnect — see the note in Test A. |
| **B** | **Relay itself restarted**, host still running | "Host reconnecting…" for a few seconds, then back in the same room, same code. |
| **C** | Host gone and not coming back | "Host reconnecting…" for 60s, then "Game ended" and the join screen. |

---

## Before you start

**On the PC, terminal** — start the relay over HTTPS and leave it running. The controller
page is served straight from `controller/` on disk, so `net.js`, `peer.js` and `app.js` are
whatever is checked out; a hard-reload on the phone is enough to pick up changes:

```bash
pwsh ./deploy/local-https.ps1
```

**This window is also your relay log** — it is where you watch what the relay actually does,
and it is the difference between "it seemed to work" and knowing which path you exercised.
Keep it visible.

**On the PC, Unity** — click the editor window so it recompiles (it will not until focused),
and confirm the Console shows no errors. Then open `Bootstrap.unity` and check
**Run Web Rtc Spike** is **un**ticked and **Use Peer Connections** is ticked.

**The phone must be on the same network as the PC** — a local relay is unreachable from
mobile data, so the cellular half of Test D no longer applies. See below.

Then press Play, join from your phone, and press **Space** to start a match. Throw once so
there is a score worth preserving.

---

## Test A — host socket drops, relay survives

The best case: nobody is disconnected at all.

**This is the one you cannot test by stopping Play.** Stopping Play destroys `RelayClient`,
which takes `RoomCode` with it, so the next Play asks for a fresh code — by design. You need
Unity to stay running while its socket dies.

> **This test is less clean against a local relay than it was against Fly, and it is worth
> knowing why before you read the result.** The relay used to be somewhere else, so dropping
> the PC's Wi-Fi killed only the host's socket. Now the relay is on the PC and the phone
> reaches it over that same Wi-Fi, so dropping the network takes the phone's socket down too.
> The relay *process* still survives, which is the thing being tested — the room is held and
> resumed rather than rebuilt. Expect the phone to reconnect on its own as part of this;
> that does not invalidate the run. The assertion is still `resumed` and `players kept`.

1. **PC, Unity:** with a match in progress, note the room code and the current scores.
2. **PC, system tray:** turn Wi-Fi **off**. Leave it off for ~10 seconds.
3. **Phone:** the status line should read **"Host reconnecting…"**. The player stays on the
   wait or throw screen — it must **not** drop to the join screen.
4. **PC, system tray:** turn Wi-Fi back **on**.
5. **PC, Unity Console:** expect

   ```
   [RelayClient] Resumed room ABCD with 1 players still in it
   ```

6. **Terminal (the relay window):** expect

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

**Set this one up differently.** You need to restart the relay without tearing down the
certificate and config around it, so do not use the long-running script for this test. Run
`local-https.ps1 -SetupOnly` once to write `relay-url.txt` and `relay-env.txt` and verify the
chain, then start the relay yourself in its own window using the values it wrote:

```bash
cd relay && PORT=8443 TLS_CERT=%TEMP%\rockskip-local-https\server.pem TLS_KEY=%TEMP%\rockskip-local-https\server.key node server.js
```

That window is now the thing you restart, and nothing else moves when you do.

1. **PC, Unity:** with a match in progress, note the room code and scores.
2. **PC, that relay window:** Ctrl+C, then run the same command again.
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

**Timing note:** the phone retries every 1.5s for up to 45s. A local relay restarts in about a
second, so expect only 2–5 seconds of "Host reconnecting…" — much faster than the 5–15s the
Fly deployment used to take. If you are quick with Ctrl+C and the up arrow, the window can be
short enough to miss; watch the phone, not the clock.

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
4. Repeat with `?direct=0` on the controller URL, which forces the phone onto the relay
   transport. It should read `0/1 direct` throughout, and the game should behave identically.
   That is the fallback doing its job.

**The mobile-data half of this test is gone.** It used to be the honest way to force the
fallback — a phone on cellular could reach the Fly relay but never negotiate a DataChannel.
A local relay is unreachable from cellular entirely, so `?direct=0` is the substitute, and it
is a weaker one: the fallback is now also *local*, so it will feel indistinguishable from
direct rather than merely close. Do not read "it felt fine" as evidence about latency —
[LOCAL-HTTPS-SPIKE.md](LOCAL-HTTPS-SPIKE.md) records a run where WebRTC silently never
established and nobody could tell.

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
| Everything breaks at once, mid-session | **The PC's LAN IP changed** — DHCP moved it, so the name in `relay-url.txt` now points elsewhere | Re-run `local-https.ps1`; a DHCP reservation for the PC prevents a repeat |
| Phone cannot reach the relay at all | It is on mobile data, or a guest network isolated from the PC | Mobile data is fatal by design. Isolation is not — host a hotspot from the PC and have the phone join that ([README.md](README.md)) |

**One caveat on a passing run.** Tests A and C both start by dropping the host, and they only
differ in whether it returns. If you run A and are slow turning Wi-Fi back on, you are running
C and will see "Game ended" — which is correct behaviour, not a failure. Watch the clock.

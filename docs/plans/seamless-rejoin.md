# Making the join seamless: one scan per phone, ever

## Context

Hotspot mode now works end to end. What remains is the join ritual. In Hotspot mode a guest
today performs **two camera scans and about nine interactions** — and pays all nine again at
every session, forever:

| # | Interaction | Where it comes from |
|---|---|---|
| 1 | Scan the Wi-Fi QR | `JoinScreen.BuildWifiPayload` |
| 2 | Tap "Join network" | OS |
| 3 | Re-open the camera, scan the game QR | `JoinScreen.EnsureQrMatchesJoinUrl` |
| 4 | Tap the URL banner | OS |
| 5–7 | Tap the name field, type a name, dismiss the keyboard | `app.js` `join()` |
| 8 | Tap JOIN | `app.js` `join()` |
| 9 | Tap Allow on the motion sheet (iOS) | `sensors.js` `requestMotionPermission` |

The question asked was whether one QR could replace the first two scans. **It cannot** — see
Findings. But the scans are only 4 of the 9, and the reason all 9 repeat every session is that
**three things are regenerated on every launch that need not be**: the Wi-Fi passphrase, the room
code, and the player's identity. Make all three stable and a returning phone re-joins itself.

**Intended outcome.** First visit: unchanged, two scans. Every visit after that: **one scan and
no typing**, or **no scan at all** if the page is still in a tab.

## Findings (fresh research, September 2026)

1. **No QR format carries both.** The `WIFI:` payload both platforms' cameras understand has
   fields for type, SSID, passphrase and hidden-flag only. No URL field, and no second format
   either OS acts on as "join this, then open that".

2. **The only mechanism that opens a page after a Wi-Fi join is captive-portal detection, and
   neither platform's portal browser can host this game.** iOS's Captive Network Assistant has
   limited JavaScript and no `localStorage`; links inside it open in the CNA rather than handing
   off to Safari (that handoff worked in iOS 10.0.x and was removed). The Wireless Broadband
   Alliance's behaviour matrix records that the portal browser closes — and the device can drop
   the network — as soon as focus moves to another app, which is exactly the app-switch
   resilience just fixed. Android's `CaptivePortalLoginActivity` does have a browser bail-out,
   but it is gated on an internally generated token our page cannot produce.

3. **The native portal API (RFC 8908, DHCP option 114) is out of reach.** It is what would give
   iOS 15+ and Android 12+ a real-browser affordance, but the option must come from the DHCP
   server, and Windows ICS's DHCP allocator has no configuration surface.

4. **Turning the portal on would be a regression.** `relay/server.js` answers `/generate_204`
   and `/hotspot-detect.html` with success specifically so no portal sheet appears. Flipping that
   trades a quiet join for a sheet the guest must dismiss, and buys nothing that survives into a
   real browser.

## What the workflow actually becomes

### Host, every session (unchanged in shape)

1. Launch the game. `Bootstrap` → `LocalServices` starts the relay → `HotspotService` runs.
2. **Changed:** the hotspot comes up with the *same* SSID and passphrase as last time, read from
   `hotspot-credentials.json`; the relay is asked for the *same* room code as last time, read
   from `room-code.txt`.
3. TV shows the same two QRs and the same 4-letter code it showed last session.
4. Press the start key once players are in. No new host-side steps, no new prompts.

### Player, first visit — **2 scans, ~9 interactions (unchanged)**

Identical to the table above. Nothing about a first-time guest gets easier; the OS will not allow
it. What changes is that this is now paid **once per phone, ever**, instead of once per session.

### Player, every later visit — **1 scan, 2 interactions (or 0 scans, 1 interaction)**

1. **Wi-Fi: nothing.** The phone recognises a network it has saved and joins it on its own,
   walking into the room. *(0 interactions, down from 2.)*
2. **Getting to the page.** Two routes, and which one you get depends on the phone, not on us:
   - The tab is still open, or the URL is one tap down in browser history/recents → **tap it.**
     *(1 interaction, 0 scans.)*
   - Otherwise → **scan the game QR from the TV.** *(1 scan + 1 tap on the banner.)*
   This step is the one that cannot be driven to zero without a home-screen icon — see
   *Not doing*.
3. **Joining: nothing.** The page opens, finds a stored identity and a room code that is still
   valid, and rejoins its own seat silently. No name to type, no JOIN button.
   *(0 interactions, down from 4.)*
4. **iOS motion permission: one tap** on a full-screen "Tap to play". The OS requires a gesture;
   it cannot be removed. Android: nothing. **Once per page load, never mid-match** — see below.

So a returning player goes from **2 scans + 7 taps/keystrokes** to **at most 1 scan + 2 taps**.

### The repeated "tap to play" — three causes, and what this plan does about each

Reported weeks ago and possibly already partly fixed by the reconnect work. Reading the code
today, there are three separate ways a player gets bounced back to a screen that demands the tap
again. They are not the same bug and need to be handled separately.

1. **The page reloads.** iOS discards backgrounded tabs under memory pressure; the return is a
   cold load. Today that means an empty name field, a retype, a JOIN tap and a fresh permission
   gesture. *Workstream C removes all but the gesture* — the reload rejoins silently, so this
   costs one tap instead of four interactions. The gesture itself cannot be removed: iOS requires
   `requestPermission()` to be called inside a user gesture on every page load, whatever the
   site's remembered setting under Settings → Safari → Motion & Orientation Access.

2. **Any relay `ERROR` outside the 45s rejoin window bounces to the join form.**
   [app.js:419](controller/js/app.js:419) runs `showScreen('join')` and re-enables the button for
   *any* error that `RelayConnection` did not swallow — and `REJOIN_WINDOW_MS` is 45 seconds, so a
   Wi-Fi blip longer than that lands here mid-match. This is the most likely source of the
   original report. *Fix:* with a stored identity, this path must retry the rejoin rather than
   render a form — the join screen becomes the fallback for a genuinely unknown player, not the
   response to a slow reconnect.

3. **`ROOM_CLOSED` does the same** at [app.js:475](controller/js/app.js:475). Correct when the
   match really has ended; wrong when the host is merely restarting. With Workstream B the code
   survives a host restart, so this should hold the seat and retry instead of ejecting.

**The acceptance rule for the implementation:** the "Tap to play" gesture is shown *only* when the
page has no live motion stream — never on a reconnect, a `TURN`, a `HOST_RESUMED`, or a swallowed
error. Gate it on the sensor state, not on the join event. A player must never see it twice
without the page having reloaded in between.

### And separately: renaming stops costing you your seat

Because identity moves off the name, changing your display name mid-match keeps your score,
colour and turn position. Today it silently makes you a new player.

## Workstream A — hotspot credentials that stay the same on this PC

`HotspotService.GenerateSsid`/`GeneratePassphrase` mint fresh values every launch
([HotspotService.cs:182](Assets/Scripts/Net/HotspotService.cs:182)), so no phone can ever
remember the network.

- **New `Assets/Scripts/Net/HotspotCredentialsStore.cs`**, modelled directly on
  [MatchSettingsStore.cs](Assets/Scripts/Managers/MatchSettingsStore.cs) — same `JsonUtility` +
  file-beside-the-executable convention as `network-mode.txt` and `match-settings.json`, so a host
  can read the passphrase by opening a file. One method, `LoadOrCreate()`.
- **`HotspotService.cs`** — replace the two generator calls with `LoadOrCreate()`. Keep
  `GenerateSsid`/`GeneratePassphrase`/`RandomAlphanumeric` as the store's first-run path; the
  ambiguous-character exclusion in `Alphabet` still matters for a hand-typed passphrase.
- **`.gitignore`** — add `hotspot-credentials.json` beside the `match-settings.json` entry.

Security note that belongs in the code comment: a guest who once played keeps access to this
hotspot. `setup-offline.ps1` disables IP forwarding every session, so what they can reach is a
game server and each other — not the host's internet or LAN. That isolation is what makes stable
credentials acceptable, and re-enabling forwarding would silently change the trade.

## Workstream B — a room code that survives a restart

Without this, Workstream C buys nothing across sessions: a remembered page URL carries last
session's code and the phone lands on the join form anyway.

`RelayClient` already knows how to ask for a code back — it sends `preferredCode` at
[RelayClient.cs:350](Assets/Scripts/Net/RelayClient.cs:350) — but only when `RoomCode` is already
set in-process, which on a cold start it is not. The relay honours the request whenever the code
is free (`handleCreateRoom`), so nothing server-side changes.

- **New `Assets/Scripts/Net/RoomCodeStore.cs`** — `room-code.txt` beside the executable, matching
  `network-mode.txt` exactly in shape and reasoning. Gitignored alongside it.
- **`RelayClient.cs`** — seed `RoomCode` from the store before the first `CREATE_ROOM`, and save
  whatever the relay actually assigns in the `ROOM_CREATED` handler
  ([RelayClient.cs:449](Assets/Scripts/Net/RelayClient.cs:449)). The existing "reclaimed /
  resumed / fresh" logging already distinguishes the three cases, so a code that could not be
  reclaimed is visible rather than silent.

## Workstream C — the seat belongs to the device, not to the name

Today `playerId` **is** the typed name, end to end: the relay's routing key, the key in
`PlayerColours._colours` and `ScoreManager`, the entry in `TurnManager._players`. So renaming
creates a new player, and a cold page load with no name typed cannot rejoin anything.

**Constraint that keeps this small:** generate the device id from the same uppercase alphabet the
relay already uses, 12 characters. `sanitizePlayerId` uppercases and caps at 16, and
`relayToPlayers` uppercases `targetPlayerId` — an uppercase id passes both untouched, so
**`relay/server.js` needs no changes at all**, and the wire field `playerId` keeps its documented
name.

- **New `controller/js/identity.js`** — one `localStorage` record, `{ deviceId, name, room }`.
  Mint `deviceId` on first run from `crypto.getRandomValues`, folded into `A–Z2–9`. Every read and
  write in try/catch: private-mode Safari throws on access, and the controller must degrade to
  today's behaviour rather than break.
- **`controller/js/app.js`**
  - `join()` sends `connection.joinRoom(code, deviceId)` and, on `JOINED`, sends
    `{ type: 'SET_NAME', name }`. `relayToHost` already forwards unknown types with `playerId`
    stamped server-side, so this needs no relay work — the reason the file's header gives for
    routing being generic.
  - `init()` — when a stored identity exists and the `?room=` code matches the stored one, skip
    the join form. iOS still needs a gesture for motion, so this lands on a single full-screen
    "Tap to play" button rather than auto-connecting; Android can connect immediately.
  - Changing your name during play sends `SET_NAME` again — a rename, not a rejoin.
- **New `Assets/Scripts/Managers/PlayerNames.cs`** — mirror
  [PlayerColours.cs](Assets/Scripts/Managers/PlayerColours.cs) exactly: dictionary keyed by player
  id, seeded with a fallback label on `PlayerJoinedEvent`, held across a disconnect, cleared when
  the roster empties. `DisplayNameFor(id)` is the single lookup everything else uses.
- **`Messages.cs` + `RelayClient.cs`** — add the `SET_NAME` type, DTO, `RelayClient` event and
  `Dispatch` case, following the extension path `Assets/CLAUDE.md` prescribes. New
  `EventTypes/MatchEvents/PlayerNameChangedEvent.cs` alongside `PlayerColourChangedEvent.cs`.
- **`Bootstrap.cs`** — create `PlayerNames` before `TurnManager`, for the same broadcast-ordering
  reason `PlayerColours` is.
- **Display sites** switch from printing the id to `PlayerNames.Instance.DisplayNameFor(id)`: the
  roster line in `JoinScreen.DrawJoinDetails`, `TurnManager`'s SPECTATE payload (score rows and
  the active-player label), `ResultsScreen`, `RelayDebugHud`.

## Not doing

- **The captive-portal flip** — for the reasons in Findings.
- **A home-screen icon (PWA manifest)** — this is the only thing that would take step 2 of the
  returning-player flow to zero, turning the controller into a tappable icon. Not selected for
  this pass; nothing here blocks adding it later.
- **Swapping the TV between one QR at a time** — not selected.

## Verification

None of these have been run yet — they are the acceptance checks for the implementation.

1. `cd relay && npm test` — must stay green. The relay is deliberately untouched, so a failure
   here means the device-id format broke an assumption in `sanitizePlayerId` or the routing tests.
2. `deploy/verify-stack.ps1` — unchanged expectations; confirms the hotspot path still comes up.
3. **Stable credentials and code:** press Play, note the SSID, passphrase and room code; quit;
   press Play again. All three identical, and the console must log the code as *reclaimed*, not
   freshly minted.
4. **Returning player, the actual claim:** join a phone, play a round, quit the host. Relaunch.
   The phone must join the Wi-Fi with no interaction; reopening the same tab must land straight in
   the lobby under the same name and colour with no typing.
5. **Rename:** mid-match, change the name on one phone. Score, colour and turn order stay put; the
   TV and the other phone's scoreboard both show the new name.
6. **Regression on what was just fixed:** background the controller, open another app, return.
   The existing auto-reconnect must still work — the auto-rejoin path must not double-join or
   reset the seat.
7. **The repeated-tap report, reproduced deliberately.** Mid-match on an iPhone, turn Wi-Fi off
   for **more than 45 seconds** (past `REJOIN_WINDOW_MS`), then back on. Before: the join form
   returns and the permission tap is demanded again. After: the phone rejoins silently with no
   tap. Repeat with the host quit and relaunched mid-match, which exercises the `ROOM_CLOSED`
   path. Then force the page to reload (close and reopen the tab) and confirm the tap appears
   exactly once.

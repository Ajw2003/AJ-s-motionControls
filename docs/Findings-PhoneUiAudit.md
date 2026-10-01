# Findings — phone controller UI/UX audit

**2026-10-01. Issue #40.** Findings only. **No code has been changed.** The owner picks which to
fix; each picked fix becomes its own child issue of #39.

## How this was audited, and what it could not see

Read from the code: `controller/index.html` (markup and CSS) and `controller/js/app.js` (screen
flow, text, rendering). A live walk-through was attempted in the Claude desktop app's browser
pane against the real relay (`https://play.skiprocking.ca:8443/?room=ULUX`, Unity in Play mode on
Lake Louise). **The pane blocked the page's script** (`js/app.js` → `net::ERR_BLOCKED_BY_CLIENT`),
so nothing dynamic ran there, and its static renders were not reliable evidence. An early "room
code not prefilled from the QR" finding came from that artefact and was **withdrawn**:
`app.js:1227` does prefill it.

Anything marked **confirm on a phone** below rests on code reading alone.

## Findings, most important first

### 1. The join page asks for a room code the TV no longer shows — high

`index.html:585` says "Enter the code shown on the TV", and `join()` refuses without one
(`app.js:1113`). Since #28 the lobby shows only the two QR codes; the room code appears on the TV
only if a QR image fails to load (`JoinScreen.DrawCornerQr`). A QR scan pre-fills the code
(`app.js:1227`), so that path is fine. But a player who arrives any other way — from Wi-Fi
settings then typing the address, from a bookmark, or with a stale stored room — has nowhere to
read the code from. This one was caused by #28.

**Options:** (a) print the room code small under the TV's "2 Play" QR; or (b) have the relay hand
out its one room when the page has no code, so a code is never needed on a single-room local
relay. (b) removes the step entirely; (a) is the smaller change.

### 2. The status bar shows a raw device id — high

`setStatus(\`Connected as ${myPlayerId}\`)` at `app.js:817` and `app.js:898`. `myPlayerId` is the
relay's device id (`JOINED.playerId`), e.g. `Connected as 4XO7R9DH4R33`, not the player's name.
**Fix:** show the display name (`myName`), or nothing once connected.

### 3. The phone's scoreboard is unsorted and unranked — medium

`renderSpectate` draws rows in `Object.entries(message.scores)` order (`app.js:1040` onward), which
is join order, while the TV sorts by total. On the results screen the phone says who won but lists
everyone in join order. **Fix:** sort by score descending, number the rows, and highlight this
phone's own row.

### 4. Phone and TV show the same numbers differently — medium

Phone: `You: 9 skips · 143.0 m` (`renderLastThrow`, `app.js:939`). TV: `143m   9sk`
(`MatchHud.FormatThrow`). **Fix:** use the TV's format on the phone so one throw reads the same in
both places.

### 5. A "debug" button is shown to every player — medium

`index.html:575`, top-right on every screen; it opens a sensor readout. **Fix:** show it only
with `?debug=1` in the URL.

### 6. START MATCH is styled as a small text link — medium (confirm on a phone)

On the choose screen CONFIRM is the big primary button, and START / RESUME MATCH is a `.link`
(`index.html:629`) under the colour picker. Starting the match is the one thing the room is waiting
for. **Fix:** promote it to a primary button once this phone has confirmed a rock.

### 7. Text is small for a phone held at arm's length — low (confirm on a phone)

The status bar is 12 px (`index.html:48`), body text 15 px (`:90`), headings 20 px (`:82`), score
rows 15 px (`:353`), the debug toggle 10 px (`:491`). The waiting screen is mostly empty space
around a small block of text. **Fix:** raise headings to about 26 px and scores to about 20 px,
and let the scoreboard use the empty space.

### 8. The throw screen carries a lot at once — low (confirm on a phone)

A three-step instruction line, a Settling/Aim-locked badge, a tilt bubble with a lock ring, the
grip button with its own fill ring, then a line like `soft swing · 3° low · light spin` after the
throw (`describeThrow`, `app.js:147`). Each piece is documented as deliberate. Only a phone
session can say whether it reads as one instruction or as five.

### Checked and fine

- **Name stays put across rejoins.** It is pre-filled from storage (`app.js:1259`).
- **Choose-screen pointer.** The re-centre button stays, and continuous re-zeroing is off in the
  lobby (#23).
- **Text safety.** Display names go through `textContent`, never markup (`app.js:1047`).
- **Haptics.** They are Android-only by design (`navigator.vibrate`); iOS has no equivalent.

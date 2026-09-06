# Making join failure hard to achieve

**Status:** P1, P2 and the `:80` fix are **implemented** — see the Build log at the end. P3 and
P4 are still proposals. Written 2026-09-06 in response to two
questions: (1) if the host has Wi-Fi at boot, can the game reach phones on that same network and
push them onto the hotspot? (2) since LAN mode worked, can we nudge someone who scans QR #1 and
then fails?

The short answers are **no** to (1) as literally stated, **yes** to a useful weaker version of
it, and **yes, decisively** to (2) — the host already receives a signal for every stage of the
join and currently throws all of it away.

---

## 1. Why the host cannot talk to phones on the house LAN (and what it *can* do)

The premise "LAN mode worked" is true but stale. It worked because `local-ip.sh` published a
wildcard certificate and wildcard DNS that mapped `192-168-1-57.local-ip.sh` back to a
DHCP-assigned address. That service is dead at the DNS layer, and
[magic-domain-replacement.md](magic-domain-replacement.md) verified there is no surviving
equivalent — `traefik.me` 404s on every cert path, `sslip.io`/`nip.io` explicitly never publish
one.

Reviving it is not a small ask. A phone on the house LAN needs to reach the host at an address
that varies per house, over HTTPS (no secure context, no `DeviceMotion`, no game). A publicly
trusted certificate for a varying address needs a **wildcard cert plus a DNS responder that
answers with a different IP per query** — precisely the public server the project deliberately
deleted to get to $0/month. A static wildcard `A` record cannot do it: it answers one address.
Updating a real record at launch via the Porkbun API means shipping DNS credentials in the
client, already rejected in [HostingPlan.md §1](../HostingPlan.md).

**So: the house LAN can never carry gameplay again without re-introducing a server.**

What it *can* carry is **plain HTTP**, which needs no certificate and no name. That is useless
for the controller (sensors require a secure context) but entirely sufficient for a **nudge
page** — a page whose only job is to say "you're on the wrong network, tap here to join the
right one". That is the weaker version of question 1 that is worth building, and section 3
describes it.

### A real bug found while checking this

`startHttpProbeListener` ([relay/server.js:720](../../relay/server.js:720)) reuses
`handleRequest`, whose final branch is `serveStatic`. So the `:80` listener does not serve "the
probes only" as its comment claims — **it serves the entire controller app over insecure HTTP**,
on `0.0.0.0`, on every interface the host has. Today the `:80` firewall rule is scoped to
`192.168.137.0/24`, so only a hotspot guest can reach it, and only by typing the host name
without `https://`. What they get is a controller page that loads, looks correct, and then
silently cannot read a single sensor.

That is exactly the "scanned something, ended up broken, no idea why" failure this document is
about, and it should be fixed regardless of which proposal below is adopted: `:80` should serve
the probe routes and the nudge page, and answer everything else with a redirect to
`https://play.skiprocking.ca:8443/`.

---

## 2. The join funnel — three signals the host already gets

This is the important finding. A join is not one event, it is three, and **the host can observe
all three per device today** without any phone-side cooperation:

| Stage | What it means | Where the signal already exists |
|---|---|---|
| **A. Associated** | Phone is on the hotspot Wi-Fi | `relay/dns.js` sees a query from `192.168.137.x` — captive-portal probes fire within ~1s of association, unprompted ([dns.js:286](../../relay/dns.js:286) already logs the client IP) |
| **B. Page open** | Phone loaded the controller | `serveStatic` serves `index.html` to that IP ([server.js:323](../../relay/server.js:323)) |
| **C. Playing** | Phone joined a room | `PLAYER_JOINED` is already sent to the host ([server.js:263](../../relay/server.js:263)) |

Only **C** reaches Unity. **A** and **B** are logged to a child process's stdout and discarded.

Every drop-off between them is a distinct, diagnosable failure with a *different* fix, and right
now all of them look identical on the TV: the player count simply does not go up.

- **A but not B** — scanned the Wi-Fi QR, joined the network, never scanned the second code. This
  is the single most likely failure and the one the current UI does nothing about.
- **B but not C** — page is open but they are stuck on the name/room screen.
- **Neither** — the QR never scanned at all, or the phone refused the network.

The whole proposal below is: get A and B up to Unity, and let the TV say something specific.

---

## 3. Proposals, in order of value per unit of work

### P1 — Surface stage A on the TV *(highest value, smallest change)*

Wire the DNS responder's "a new client IP appeared on the hotspot subnet" event through to
Unity, as a new relay→host message. This follows the extension path `Assets/CLAUDE.md` already
prescribes: message type + DTO in `Messages.cs` + a `RelayClient` event + a `Dispatch` case.

`JoinScreen` then knows the difference between "nobody has done anything" and "two phones are on
the Wi-Fi but neither opened the game", and can say so:

> **Phone connected to the Wi-Fi — now scan the second code** *(with QR #2 enlarged and QR #1
> dimmed)*

De-duplicate per IP, and stop counting a device once it reaches stage C. Note the count is
devices-on-the-network, not people; treat it as a hint, never as a roster.

### P2 — Trigger the captive portal deliberately, per device, until it has joined

Phase 4 answers the captive-portal probes with `204`/`Success` specifically to *suppress* the
portal so phones do not flag "No internet". That is correct once a phone is playing — but before
it is playing, the portal is the best nudge surface that exists: the OS opens a sheet on the
phone by itself, with no scanning at all.

So make the probe answer conditional on the funnel stage of the requesting IP:

- IP has **not** reached stage B → answer the probes so the OS opens its portal sheet, and serve
  that sheet a page saying "Rock Skipping — open the game" with a prominent link.
- IP **has** reached stage B (or C) → answer `204`/`Success` exactly as today, so the phone stops
  flagging "No internet" and stops preferring cellular mid-match.

Two caveats worth stating before anyone builds this:

- **The captive sheet is not a place to run the game.** iOS's CNA browser and Android's portal
  webview are sandboxed and unreliable for `DeviceMotion` permission prompts. The sheet must be a
  one-tap handoff to the real browser, not the controller itself.
- The "No internet" suppression must genuinely come back once a device is playing, or P2 trades a
  join failure for a mid-match disconnect — the exact failure Phase 4 was written to prevent.

This is the change that gets the common case to **zero scans**: known SSID auto-joins in range,
portal sheet opens itself, one tap into the game.

### P3 — The house-LAN nudge page *(the salvageable half of question 1)*

For a guest whose phone is already on the house Wi-Fi and stays there: serve a plain-HTTP page on
the host's LAN address whose entire content is the hotspot's Wi-Fi QR, rendered on the guest's
own screen, plus the SSID in text. Tapping/scanning something on your own phone beats scanning
across a living room.

Requires:
- a LAN-scoped `:80` firewall rule in `setup-offline.ps1` (a deliberate widening of surface —
  today `:80` is hotspot-only, and this is the one place this proposal costs security review),
- the `:80` route fix from section 1 so this page, not the controller, is what LAN visitors get,
- a way to reach it: a third QR on the TV encoding `http://<hostLanIP>/`, which means resurrecting
  LAN address discovery that Phase 3 marked for deletion. mDNS (`skiprocking.local`) is the
  nicer-looking alternative and is unreliable enough on Android that it should not be the only
  route.

Judgement: **P3 is worth doing only after P1 and P2 are measured.** It serves a narrower case
than it first appears — most guests' phones are not already on the host's Wi-Fi — and it is the
only proposal here that widens the firewall.

### P4 — Say what failed, not that something failed

`DrawProgress` currently ends every wait with the same line: *"Retrying automatically — check
this machine's internet connection."* With the funnel from section 2 available, each stall has a
truthful message instead. Cheap, and it removes the worst thing the TV currently does, which is
blame the host's internet for a phone-side problem.

---

## What to decide

1. **P1 and P2 together, or P1 first?** They share the per-IP funnel state; building P1 alone and
   adding P2 later costs nothing extra.
2. **Is the `:80` route fix (section 1) urgent on its own?** It is a live footgun today — a
   hotspot guest who omits `https://` gets a plausible-looking controller with dead sensors — and
   it is a small, isolated change independent of everything else here.
3. **P3's firewall widening** — worth it, or is the house-LAN guest better served by simply
   walking to the TV?

Question 3 (P3's firewall widening) is still open. Questions 1 and 2 were answered by building
them — see below.

---

## Build log

**2026-09-06 — P1, P2 and the `:80` fix landed together.** P4 was not built; the funnel it needs
now exists, so it stays a small follow-up.

### New

**`relay/funnel.js`** — the per-device funnel from §2. `createFunnel({ isTracked, onChange, now })`
returns `record(address, stage)` / `stageOf(address)` / `counts()`. Keyed by IP, normalized so
`::ffff:192.168.137.5` and `192.168.137.5` are one device. Three decisions worth keeping:

- **Stages never regress.** The signals arrive out of order in practice — a phone rejoining a
  known network sends its captive probes *after* the browser has restored the controller — and a
  late `ASSOCIATED` pulling a playing device back down would make the TV nag someone mid-throw.
- **No `reset()`.** A host restart (stopping and starting the Editor) moves no phone off the
  hotspot, so the recorded stages are still true. Devices leave by going quiet for
  `IDLE_TIMEOUT_MS` (10 min), which is the only event that actually means "that phone is gone".
- **Tracking is confined to `DNS_ALLOW`.** Without it every request on a developer's machine
  comes from `127.0.0.1` and the TV would report a phantom phone waiting to scan.

**`relay/test-funnel.js`** — 16 tests, wired into `npm test`. Covers the regression guard, the
subnet confinement, expiry, and the change-notification debounce.

### Changed

- **`relay/dns.js`** — new `onQuery(address)` option, called for every query that passes the
  allow-list and rate limit, deliberately *before* `decodeQuery`: a packet the responder cannot
  parse is still proof a device is on the hotspot. Wrapped in try/catch because it runs inside a
  UDP handler, where a callback throw would take the relay down over a bookkeeping error.
- **`relay/server.js`** — funnel wired to all three stages (`onQuery` → `ASSOCIATED`,
  `serveStatic` of `index.html` → `PAGE_OPEN`, `handleJoinRoom` → `PLAYING`; the websocket's
  `remoteAddress` is captured at connection time, since by JOIN the socket may be mid-close and
  read back undefined). Counts go to hosts as `DEVICE_FUNNEL`, on change and once on
  `ROOM_CREATED`/resume so a restarted host is not blind until the next phone moves.
- **P2** is `serveCaptiveProbe`: below `PAGE_OPEN` the probes report a portal (302 → `/portal`
  for Android, the portal page itself for Apple); at `PAGE_OPEN` and above they answer exactly as
  Phase 4 always did. `/portal` is an instruction page with one link out, **not** the controller —
  iOS's CNA sheet and Android's portal webview are sandboxed and `DeviceMotion.requestPermission()`
  is unreliable inside them.
- **The `:80` bug** is fixed by giving that listener its own `handleProbeRequest` instead of
  reusing `handleRequest`. Everything that is not a probe or `/portal` now 302s to the HTTPS
  origin, so the controller can no longer be served over plain HTTP.
- **`Assets/Scripts/Net/Messages.cs`** — `MessageType.DeviceFunnel` + `DeviceFunnelMessage`.
- **`Assets/Scripts/Net/RelayClient.cs`** — a `DeviceFunnel` dispatch case setting
  `DevicesAwaitingPage` / `DevicesAwaitingJoin`. Stored as properties rather than published as an
  event: the only consumer redraws every frame anyway, so a property is less machinery than an
  event type plus a subscription to unwind.
- **`Assets/Scripts/UI/JoinScreen.cs`** — when `DevicesAwaitingPage > 0`, the instruction line
  becomes "*A phone is on the Wi-Fi — now scan the second code*", the game QR grows to 1.3×, and
  the Wi-Fi QR shrinks to 0.7× and fades to 35% alpha. Total row width is unchanged, so the pair
  still fits the panel.

### Verified

- `cd relay && npm test` — 38/38 pass (22 pre-existing + 16 new). `npx eslint .` clean.
- **P2 end-to-end against a spawned relay:** before the controller page is fetched,
  `/generate_204` → `302 http://play.skiprocking.ca/portal` and `/hotspot-detect.html` → the
  portal HTML. After fetching `/`, the same two → `204` and the literal `Success`. `/portal` → 200.
- **The `:80` fix end-to-end:** with the listener bound, `GET /` and `GET /controller.js` on `:80`
  both return `302` to the HTTPS origin instead of serving the file.

### Not verified here

The Unity half has not been compiled or run — Unity holds an exclusive project lock, so there is
no out-of-band compile check (`Assets/CLAUDE.md`). Check the Editor Console, then confirm on real
hardware: a phone that joins the Wi-Fi and stops should raise the nudge on the TV within a second
or two, and should stop flagging "No internet" once it is playing.

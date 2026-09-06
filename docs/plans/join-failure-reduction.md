# Making join failure hard to achieve

**Status:** analysis + proposal. No code changed. Written 2026-09-06 in response to two
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

Nothing above has been implemented, and none of it should be until these are answered.

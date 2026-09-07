# Making join failure hard to achieve

**Status:** CLOSED 2026-09-07. Defects 1, 2 and 3 are fixed; hotspot isolation is verified real
for the first time; and P2's captive portal is abandoned as platform-dependent, with P1's TV nudge
carrying the drop-off it was meant to cover. See "CLOSED" below for what the evidence excluded.
P1, P2 and the `:80` fix are implemented, and Defects 1, 2 and 3 are all closed. The hardware
tests that got there are recorded in order below, including the ones whose conclusions were
wrong. P3 and P4 are still proposals. Written 2026-09-06 in response to two
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

**`relay/capture-probe.ps1`** - reads the NIC with `pktmon`, below the firewall, to settle
whether the phone's probe packet arrives at all. Every other tool in this diagnosis reads a layer
above the wire: `netstat` shows sockets, so a dropped SYN leaves no trace, and the relay log only
sees requests that reached a handler. Both go silent in the same way whether the phone sent
nothing or the host discarded it. Self-elevates (one UAC prompt, the pattern `HotspotService`
already uses for `setup-offline.ps1`) and always removes its capture filter, including on
failure.

Its verdict logic is deliberately split from the capture so it can be tested without the driver,
and it is guarded against the one failure that would matter most: the line filter is written
against an assumed `etl2txt` format, so an empty result must first prove the dump was non-empty
and did contain guest traffic before it is allowed to read as "the phone sent nothing". A dump it
cannot parse reports `INCONCLUSIVE`, never a conclusion. All six branches were exercised against
synthetic dumps.

**`relay/watch-guests.js`** — prints every TCP connection a hotspot guest opens to this host,
live, as it appears. `diagnose-hotspot.js` asks "does the relay serve the right thing?" from the
host; this asks the one thing that cannot be asked from the host's own traffic, "did the phone's
request arrive at all?". Read-only — it shells out to `netstat` and prints. It is the tool that
closed the firewall branch, and it is the ground truth for every remaining phone test, because a
phone-side failure looks identical whether the request was blocked, answered wrongly, or never
sent over the Wi-Fi.

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

- `cd relay && npm test` — 43 tests, 42 pass, 1 skipped. `npx eslint .` and
  `npx prettier --check` clean.
- **P2 end-to-end**, codified in `relay/test-captive.js` against a spawned relay: before the
  controller page is fetched, `/generate_204` → `302 http://play.skiprocking.ca/portal` and
  `/hotspot-detect.html` → the portal HTML, which links out to the game and does not embed the
  controller. After fetching `/`, the same two → `204` and the literal `Success`.
- **The `:80` fix end-to-end:** `GET /`, `/controller.js` and `/index.html` on `:80` all return
  `302` to the HTTPS origin with an empty body, instead of serving the file.
- The `:80` case **skips when another process owns `:80`** — most often the game's own relay,
  running because someone pressed Play in Unity, which is what happened on the machine this was
  written on. It identifies its own listener by the port it redirects to rather than by "did
  anything answer", so it never silently grades someone else's server. To run it, stop Play first.

### First hardware test — two defects, both open

Tested 2026-09-06 with an Android phone against a live hotspot. Neither defect is in the parts
the automated tests cover, which is worth noting on its own: `test-captive.js` asserts what the
relay *answers*, and both failures are elsewhere.

#### Defect 1 — the device count is wrong, and the TV shows it

**Observed:** one phone left the network and rejoined; the TV went to *"2 phones are on the
Wi-Fi"* with zero players. Leaving never decremented it.

**Cause:** the funnel is keyed by IP, and a rejoining phone gets a fresh DHCP lease, so it
becomes a second entry. The first entry is not wrong so much as stale — nothing tells the host a
device left, and `IDLE_TIMEOUT_MS` holds it for ten minutes. `DeviceFunnelMessage` already
documents "a count of devices, never a roster of people", but `JoinScreen` then prints the number
on a TV, where it reads as a roster to everyone looking at it.

**Fixed 2026-09-06, in the two parts below.**

1. **Stop printing the number.** "A phone is on the Wi-Fi — now scan the second code" is true
   whether it is one device or four; "2 phones" was simply false. The count is unreliable by
   construction — IP-keyed, so a rejoin double-counts and two phones behind one address count
   once — and no timeout tuning fixes that. Keep the count internally to decide *whether* to
   nudge; never render it.
2. **Expire an ASSOCIATED-only device much faster.** Both platforms re-probe every 30–60s while
   connected, so a device that has said nothing for ~2 minutes has almost certainly gone. Ten
   minutes is right for a device that reached PLAYING (it may simply be idle mid-match) and far
   too long for one that only ever appeared in a DNS log. A per-stage timeout self-corrects the
   nag within a couple of minutes instead of ten.

**How it landed.**

- `JoinScreen.DrawJoinDetails` now renders one count-free string,
  *"A phone is on the Wi-Fi — now scan the second code"*, and the count decides only whether to
  show it. The two-branch singular/plural label is gone, so there is no longer a number on the
  TV that can be wrong.
- `funnel.js` replaces the single `IDLE_TIMEOUT_MS` with a per-stage `idleTimeoutFor(stage)`:
  `ASSOCIATED_IDLE_TIMEOUT_MS` (2 min) below `PAGE_OPEN`, the existing 10 min at or above it.
  A phone mid-match sends nothing for minutes and must not be swept; a phone seen only in a DNS
  log re-probes every 30–60s, so two minutes of silence means it left.

Verified behaviourally against the real module — an `ASSOCIATED` device is counted at 119s and
gone at 121s, while a `PAGE_OPEN` device survives 121s. `test-funnel.js` grew three tests for
the asymmetry and its two existing expiry tests were rewritten: one of them had started passing
for the wrong reason, advancing ten minutes on a device that now expires at two. `npm test` is
46 tests, 45 pass, 1 skip.

The Unity half is not compile-checked — Unity holds the project lock — so confirm the Console is
clean on the next Play.

#### Defect 2 — Android never showed a portal, only "connected without internet"

**Observed:** after *Forget This Network* and rejoining, the phone showed its ordinary
no-internet notification. No sign-in prompt.

**What the evidence rules out:** the TV counted the phone, and that count comes *only* from DNS
queries arriving from the hotspot subnet — so the phone did reach our resolver and did get
answers. That eliminates the usual Android suspect, Private DNS/DoT suppressing plain resolution.
The failure is downstream of DNS.

**What could not be determined, and why:** the relay's stdout only reaches the Unity Console when
`LocalServices.verboseLogging` is on (`LocalServices.cs:185`), so nothing recorded whether
`startHttpProbeListener` bound `:80` during that session. It degrades quietly by design, which
means **one transient bind failure disables the portal for an entire session with no visible
sign** — the same silent-breakage class this whole document is about, reintroduced by the fix for
it. The firewall rule is present and correct on this machine (checked: `RockSkipping captive
portal`, TCP 80, remote `192.168.137.0/24`), so that is not it.

**Next step is measurement, not a code change:** `relay/diagnose-hotspot.js` (new) walks the chain
a phone walks — `/health`, a real DNS query for `connectivitycheck.gstatic.com`, `:80` on both
loopback and the hotspot address, and the Android probe with its true `Host` header — and reports
each link separately, because all of them produce the same symptom on the phone. Run it while
Play is running.

**Diagnostic run against a live hotspot in Play mode — every link PASSES:**

```
1. /health answered                     dns: {"enabled":true,"bound":true}
2. connectivitycheck.gstatic.com   ->   192.168.137.1
3. :80 bound on loopback; answers on 192.168.137.1
4. Android probe -> 302 http://play.skiprocking.ca/portal
```

So the relay is serving exactly what P2 intends. The `:80` bind theory above is **dead** — the
listener is up, and it returns the portal redirect with Android's own `Host` header. The stale
count also self-corrected between runs (the TV now reads "A phone is on the Wi-Fi", singular),
which is consistent with `IDLE_TIMEOUT_MS` doing its job slowly rather than not at all — Defect 1
stands as written.

**The remaining hypothesis space, and the flaw in the check above.** Step 3's hotspot-address
probe originates on the host and is destined for the host's own address, so Windows does not
evaluate it against the inbound firewall rules the way it evaluates a packet arriving over the
wireless interface from a phone. **A PASS there proves the socket answers; it does not prove a
guest can reach it.** The script now says so in its own output rather than implying more than it
measured. That leaves two possibilities, and one test separates them:

- **`:80` is unreachable from the phone** even though it answers locally — an interface or
  firewall-scope problem rather than a listener problem. Then Android's HTTP probe times out,
  both probes fail, and the phone reports plain "no internet" with no portal, exactly as seen.
- **`:80` is reachable and Android simply declined to show the prompt.** Android only concludes
  "portal" when the HTTP probe indicates one *and* the HTTPS probe fails; it also suppresses the
  prompt for a network the user has previously dismissed. In that case the serving is right and
  the fix is on the detection side, not ours.

**The separating test needs no code and no host access:** open the portal page in the phone's
browser while on the hotspot. Page loads → reachability is fine, the problem is Android's
detection. Times out → `:80` is blocked for guests, and the firewall/interface scope is the thing
to fix.

#### Defect 3 — the portal redirect target gets force-upgraded to HTTPS

Found while attempting that test. Typing `http://play.skiprocking.ca/portal` on the phone did not
connect, and the browser kept rewriting the address back to `https://` even after hand-editing
the scheme out.

**Cause:** Chrome's "always use secure connections" upgrades typed `http://` navigations to
HTTPS. That targets **port 443, where nothing listens** — the relay serves TLS on 8443 — so the
request fails before it is ever sent to `:80`. We never send HSTS (checked: no
`Strict-Transport-Security` anywhere in `relay/`, `controller/` or `Assets/Scripts/`), so this is
purely the browser's own default, and it applies to any hostname a user types.

**Why this is a product bug and not just a testing nuisance:** `serveCaptiveProbe` answers
Android's probe with `302 → http://play.skiprocking.ca/portal`. Android's CaptivePortalLogin
webview then has to load that URL. Anything that upgrades it to HTTPS hits port 443 and fails, so
the portal page never renders — and a portal whose page fails to load can push the platform back
to "no internet", which is exactly the observed symptom. **The redirect target is chosen badly:**
a hostname we also serve over TLS invites the upgrade.

**Proposed fix:** point the redirect at the hotspot IP literal — `http://192.168.137.1/portal` —
rather than the hostname. Browsers do not apply HTTPS-upgrade heuristics to private IP literals
the way they do to registrable names, so the request stays on `:80` where the portal actually
lives. The address is already a constant everywhere else in the system
(`magic-domain-replacement.md`), so this costs nothing in configurability.

**Implemented 2026-09-06**, once the reachability precondition was met (see "Second hardware
test" below). `PORTAL_URL` in [server.js](../../relay/server.js) is now
`http://${DNS_RESPOND_IP || DNS_NAME}/portal` and `serveCaptiveProbe` redirects to it; the name
survives only as the fallback for off-hotspot runs, which have no captive portal to reach.
`test-captive.js` asserts the literal, so a regression to the hostname fails the suite.
`npm test` — 42 pass, 1 skip (the documented skip with Play running).

**Verifying it took effect needs an Editor restart, not just a file save.** Unity's
`LocalServices` owns the relay child process, so a running game keeps serving the old code.
`diagnose-hotspot.js` step 4 is the check: it must print
`302 -> http://192.168.137.1/portal`. While it still prints the hostname, the phone is being
answered by the pre-fix relay and any phone-side result is about the old code.

### Second hardware test — `:80` is reachable; the phone's browser is not the way to prove it

Tested 2026-09-06, same Android phone, same live hotspot. `diagnose-hotspot.js` passed all four
checks. The phone was correctly associated (the TV counted it), and
`http://192.168.137.1/portal` in the phone's browser still failed with *took too long to
respond*.

That looks like the "`:80` is blocked for guests" branch. It is not. **The host's TCP connection
table shows the phone reaching `:80` repeatedly:**

```
TCP    192.168.137.1:80    192.168.137.45:44254    FIN_WAIT_2    <relay pid>
```

`192.168.137.45` is a guest address — not the host talking to itself, which is the flaw that
invalidated the earlier `:80` check. A connection in `FIN_WAIT_2` completed a handshake and was
closed by our side. The firewall rules were re-read at the same time and are correct
(`RockSkipping captive portal`, TCP 80, remote `192.168.137.0/24`, profile `Any`, enabled), the
listener is on `0.0.0.0:80`, and the only enabled inbound block rules covering TCP 80 belong to
`adb` and an unrelated Steam game. **The firewall branch is closed. `:80` is reachable from a
hotspot guest.**

`relay/watch-guests.js` (new) makes this observable rather than inferred — it polls the
connection table and prints every guest connection as it appears. Run against the live game it
shows the phone re-probing `:80` on Android's own 30–60s schedule, unprompted:

```
  8:00:31 p.m.  192.168.137.45 connected to :80
             captive-portal probe or portal page (plain HTTP)
```

**So the probe arrives and the browser does not, from the same phone, at the same moment.** The
explanation that fits both facts is Android's network selection: a Wi-Fi network flagged "no
internet" is not promoted to the device's default network, so ordinary apps keep routing to
cellular, while the platform's own captive-portal probe stays explicitly bound to the Wi-Fi.
Chrome's request for `192.168.137.1` then leaves over mobile data, where that address does not
exist, and times out exactly as observed.

Two consequences:

1. **The separating test proposed under Defect 2 does not separate anything on Android.** A
   browser timeout is the expected result whether or not `:80` is reachable, so it cannot be
   used as evidence either way. Watch the connection table instead. To make the browser itself a
   valid probe, cellular has to be off — airplane mode on, then Wi-Fi back on.
2. **Defect 2 is now narrowed to the detection side, as its second bullet predicted.** We serve
   the right answer, the phone receives it, and Android still declines to raise the prompt. The
   remaining candidates are the dismissed-portal cache for that SSID and the redirect target
   itself — which is Defect 3, now unblocked: the reachability precondition it was waiting on is
   confirmed.

### Third hardware test — the redirect is fixed and the phone still does not join

Tested 2026-09-06, after killing a stale relay (below) so the fix was actually running.
`diagnose-hotspot.js` step 4 now reads `302 -> http://192.168.137.1/portal`, and
`watch-guests.js` shows the phone hitting `:80` about every 15 seconds:

```
  8:18:35 p.m.  192.168.137.45 connected to :80
  8:18:50 p.m.  192.168.137.45 connected to :80
```

A 15-second cadence is Android actively re-validating, not a settled network — so it is
receiving our answers and rejecting the conclusion. Defect 3's fix was necessary and is not
sufficient.

**A trap that cost a whole test cycle: `LocalServices` reuses a relay that is already
listening.** `StartRelay` ([LocalServices.cs:251](../../Assets/Scripts/Net/LocalServices.cs:251))
probes `/health` first and, if anything answers, logs *"Relay already listening — reusing it"*
and never spawns a child. The comment is explicit that this is deliberate — it keeps `npm start`
in a terminal a valid dev loop. The consequence is that **stopping and starting Play does not
pick up a relay code change** if the previous relay outlived its session, and the game then
serves stale code with no visible sign. Kill the orphan first; `diagnose-hotspot.js` step 4 is
the cheapest proof of which code is actually running.

**Why the next measurement needed a code change.** `netstat` proves a connection arrived but not
what it asked for, and every remaining hypothesis differs only in the path requested and the
answer returned. The relay's stdout cannot supply that — it is a child of the Editor and reaches
the Console only under `verboseLogging`. So the `:80` listener now writes
`relay/probe-access.log` (gitignored), one line per request: timestamp, client address, method,
path, `Host` header, and what we answered. Best-effort and error-swallowing, because a
diagnostic must never fail a guest's request.

That log answers the three surviving questions directly:

- **`/generate_204` present, answered `302`** — we are doing our job and Android is declining to
  raise the prompt. The suspect is then the platform's per-SSID dismissed-portal state, not the
  relay.
- **`/generate_204` present but answered `204`** — the funnel has the device at `PAGE_OPEN` or
  above and is suppressing the nudge for a phone that never actually opened the controller.
  That is a funnel-keying bug, and Defect 1's IP-keying is the obvious cause.
- **`/portal` never requested** — the phone is being told about the portal and never fetching it,
  which points back at the platform rather than at anything served here.

### Fourth hardware test — the phone never probes at all

Tested 2026-09-07 with the logging build actually running (`diagnose-hotspot.js` step 4 confirmed
`302 -> http://192.168.137.1/portal`). `probe-access.log` after a forget-and-rejoin:

```
02:25:35 192.168.137.184 GET /         Host=192.168.137.1  302 -> https://play.skiprocking.ca:8443/
02:26:16 127.0.0.1       GET /portal   Host=127.0.0.1      answered 200
02:26:16 192.168.137.1   GET /portal   Host=192.168.137.1  answered 200
02:26:16 192.168.137.1   GET /generate_204 Host=connectivitycheck.gstatic.com answered 302
```

The last three are `diagnose-hotspot.js` running from the host. **The phone contributes exactly
one line, and it is not a probe.** `192.168.137.184` is a fresh lease, so the rejoin took.

**None of the three branches the log was built to separate is the answer.** They all assumed a
probe arrived. Android sent no `/generate_204`, so it never made a portal determination, and no
answer we serve can produce a prompt. Serving is not the problem; being asked is.

This also retires the cellular-routing explanation for this attempt — the `GET /` arrived over
the hotspot, so the browser was on Wi-Fi at that moment.

**What the surviving line says on its own.** The phone asked for `/` on the hotspot IP and was
redirected to `https://play.skiprocking.ca:8443/` — a *hostname*, which the phone can only reach
if it resolves through our responder. That is the same fragility Defect 3 was about, and it
cannot be fixed the same way: :8443 is TLS with a single-name certificate, so the IP literal is
not an option there. Whether the phone resolves that name is now the open question.

**Next measurement.** `dns.js` already logs every answered query but only to the relay's stdout,
which is invisible in the Editor. It accepts an injectable `log`, so `server.js` now passes one
that tees `dns …` lines into the same `probe-access.log`. The next run shows which names the
phone asks for, which separates:

- **No `dns` lines from the phone's address** — it is not using our resolver (Private DNS/DoT, or
  a hardcoded one), which explains the missing probe outright and is the whole ballgame.
- **`dns` lines for `play.skiprocking.ca` but none for the probe domains** — the phone resolves
  us but has decided this SSID needs no captive check, i.e. the per-network dismissed/validated
  cache the plan has suspected since Defect 2.

### Fifth hardware test — Android resolves us and never connects

Tested 2026-09-07 with DNS teed into `probe-access.log`. The phone (`192.168.137.214`) asks our
resolver for the Android probe host on a steady ~62-second cycle and is answered every time:

```
02:42:21 dns 192.168.137.214 A connectivitycheck.gstatic.com -> 192.168.137.1
02:43:24 dns 192.168.137.214 A connectivitycheck.gstatic.com -> 192.168.137.1
...
02:47:41 dns 192.168.137.214 A connectivitycheck.gstatic.com -> 192.168.137.1
```

`watch-guests.js` ran for 75 seconds spanning the 02:46:37 and 02:47:41 queries and recorded
**zero** TCP connections. So the phone resolves our address, accepts the answer, and does not
open a socket to it.

**What this rules out.**

- **Not reachability.** The same phone's browser reached `:80` at 02:42:22
  (`GET / Host=192.168.137.1`), logged and answered. `:80` is bound on `0.0.0.0` and the
  firewall rule is correct.
- **Not the DNS reply path.** `Find-NetRoute` for the phone's address selects source
  `192.168.137.1` on the hotspot interface, so replies leave from the address the phone queried
  — a mismatch there would have made the client discard them. The steady 62-second cadence is
  Android's validation retry, not a DNS retry, which independently confirms the answer is being
  accepted.
- **Not a black-holed second probe.** No `DNS_UPSTREAM` is configured
  ([LocalServices.cs:439](../../Assets/Scripts/Net/LocalServices.cs:439) sets only `DNS_ALLOW`
  and `DNS_NAME`), so unmatched names get an immediate NXDOMAIN rather than hanging.

**A blind spot closed while checking that.** `dns.js` logged only *answered* queries, so the
names we do not serve — `www.google.com`, the HTTPS half of Android's validation — were
invisible. The NXDOMAIN branch now logs too. What a phone asks for and is refused is as
diagnostic as what it is given, and a captive-portal check probes several names of which only
some are ours.

**The remaining question is narrow:** is the probe's SYN sent and dropped, or never sent at all?
`netstat` cannot answer it — a dropped SYN creates no socket entry — so the next measurement is
a host-side packet capture, which needs elevation.

**Worth weighing before spending that cycle.** Five hardware cycles have gone into making a
platform raise a sheet we do not control, and P1 already covers this exact drop-off from the TV
without the phone's cooperation. The portal is an accelerator, not the mechanism. If the capture
does not immediately implicate something of ours, the honest call is to stop here and let P1 do
the job it was built for.

### Sixth hardware test - the first capture proved nothing, and that is the finding

Ran `capture-probe.ps1` against a live hotspot. The capture worked (43 KB of `.etl`, 770 lines of
text) but **the report never appeared**, and the first reading of the dump was wrong. Both
failures are worth recording because both were nearly missed.

**Why no report.** Windows PowerShell turns a native command's stderr into an ErrorRecord, and
the script ran under `$ErrorActionPreference = 'Stop'`. `pktmon` writing a harmless notice -
removing a filter that was not there - threw, and the elevated window closed on an error nobody
could read. The script now runs the capture under `Continue` and writes any error into the
report instead of losing it with the window.

**Why the dump proved nothing.** The filter was `-t TCP -p 80`, so the phone's DNS was excluded
by construction. That leaves "no packets" meaning two different things - the probe never
arrived, or the hotspot adapter was never in scope - with no way to tell them apart. The dump
contained only two `192.168.137.*` lines and both were pktmon component metadata, not packets.

**The fix is a positive control.** The filter is now the guest subnet, every protocol, so the
phone's DNS - which the relay log independently proves is flowing constantly - appears in the
capture. Only a dump containing that DNS is permitted to conclude anything from the absence of
TCP 80; without it the script reports `INCONCLUSIVE - NO CONTROL` or
`INCONCLUSIVE - ADAPTER NOT CAPTURED`. Re-judging the original dump under the new logic returns
`INCONCLUSIVE - ADAPTER NOT CAPTURED`, which is the correct answer and was not the one the first
pass gave.

**What the relay log did establish during that window.** At 03:07:38, inside the capture, Android
ran a complete validation:

```
dns 192.168.137.85 A www.google.com                -> NXDOMAIN
dns 192.168.137.85 A connectivitycheck.gstatic.com -> 192.168.137.1
```

Both probe hosts, resolved together - the signature of a real captive-portal check, not
incidental traffic. The phone was also resolving Facebook, Instagram and `mtalk.google.com`
throughout, so apps are actively using the network.

**A lead this exposes, not yet tested.** `www.google.com` is the host of Android's HTTPS probe
and is not in our answered names, so it gets NXDOMAIN. A DNS failure and a refused connection
are not equivalent to Android's `NetworkMonitor`: answering it with the hotspot IP would make
the HTTPS probe fail by connection-refused on :443 instead of failing to resolve at all. That is
a one-line change to `CAPTIVE_PROBE_DOMAINS` and a plausible reason the portal verdict is never
reached - but it stays a hypothesis until the capture with its control says whether TCP 80 is
being sent.

**Also observed:** the phone took a new lease on every rejoin (`.45`, `.214`, `.85`), which is
Defect 1's IP-keying problem in the wild. The 2-minute associated-stage expiry now bounds how
long each ghost lingers.

### Seventh hardware test - CONCLUSIVE: the phone never sends the probe

Ran `capture-probe.ps1` with the corrected filter. **The control is satisfied and the answer is
unambiguous.**

A second pktmon gotcha had to be fixed first: `-i` takes one or two literal IPs, not a CIDR
(`pktmon filter add --help` spells it `-i <ip> [ip2]`). A subnet passed there is accepted
silently and matches nothing, which produced a 16 MB capture file containing zero packets and
looking exactly like a quiet network. The filter is now the host's own hotspot address, which
every packet between a phone and this machine carries as one endpoint.

Capture over 90 seconds, destination ports on hotspot traffic:

```
443 -> 476    (192.168.137.85 -> 57.144.216.192/196, public Meta addresses)
53  -> 140    (192.168.137.85 -> 192.168.137.1, our resolver)
80  ->   0
```

- **Control satisfied.** 140 DNS packets from the phone to us, with replies. The adapter was
  fully in scope, so absence of TCP 80 is now evidence rather than an artefact.
- **Zero TCP 80 packets.** Not a dropped SYN - a SYN would appear here, below the firewall.
  The phone resolves `connectivitycheck.gstatic.com` to our address and then never opens the
  connection.

**Defect 2 is closed as not-ours.** Nothing served, bound, redirected or firewalled on this host
can cause a connection the phone declines to make. Every earlier hypothesis - the redirect
target, the firewall scope, the `:80` bind, cellular routing, the DNS reply path - is now
excluded by direct measurement.

**The one lead the capture supports.** Android's validation resolves two hosts together, and we
answer them differently:

```
dns 192.168.137.85 A www.google.com                -> NXDOMAIN
dns 192.168.137.85 A connectivitycheck.gstatic.com -> 192.168.137.1
```

`www.google.com` is the HTTPS probe host and is absent from `CAPTIVE_PROBE_DOMAINS`
([server.js:449](../../relay/server.js:449)), so it gets NXDOMAIN. To Android's `NetworkMonitor`
a DNS failure and a refused connection are not equivalent, and a validation attempt that cannot
resolve its HTTPS probe can conclude "no internet" without ever running the HTTP probe - which
is exactly the packet pattern captured. Adding `www.google.com` to that list would make the
HTTPS probe fail by connection-refused on :443, where nothing listens, instead of failing to
resolve.

That is a one-line change and the only remaining candidate that the evidence points at. It is a
hypothesis, not a diagnosis: the capture proves what the phone does not do, not why.

**Implemented 2026-09-07.** `www.google.com` is now in `CAPTIVE_PROBE_DOMAINS`, commented with
why it is there for the opposite reason to the others - nothing listens on :443, so the answer
produces a fast refused connection rather than a working probe. `npm test` unchanged at 46 tests,
45 pass, 1 skip. The test of it is the next capture: TCP 80 appearing at all falsifies the
"phone never connects" behaviour, and `probe-access.log` showing `www.google.com -> 192.168.137.1`
instead of NXDOMAIN confirms the new relay is the one running.

**Also confirmed incidentally:** the phone sends hundreds of packets to public Meta addresses on
:443 over this network, so it treats the hotspot as usable and routes app traffic over it. The
"no internet" state is not stopping ordinary use.

### Eighth finding - IP forwarding is ON, and that reframes everything above

Reported from the phone: **Facebook and Instagram load on the hotspot; Reddit does not.** That
single observation broke the case open, because it is impossible under the model everyone had
been working from.

Measured on the host:

```
Local Area Connection* 12   Forwarding: Enabled
SharedAccess (ICS)          Running
```

**Guests reach the internet at the IP layer.** `HotspotService` logs "Tethering is on. Disabling
IP forwarding on the hotspot interface" and `Decision-OfflineHosting.md` assumes isolation, but
on this machine forwarding is enabled and ICS is NATting. Whatever disables it is not taking
effect, or is being undone.

**So the "no internet" symptom was never about connectivity - it is our own resolver.** We answer
NXDOMAIN for every name we do not serve, so:

- Meta apps work: they connect to cached addresses and need no lookup.
- Reddit fails: it needs a fresh lookup and gets NXDOMAIN.
- Android's HTTPS validation probe for `www.google.com` gets NXDOMAIN and cannot run, which is
  consistent with every capture: the phone resolves, then never opens the HTTP probe connection.

**This reverses the fix.** Adding `www.google.com` to `CAPTIVE_PROBE_DOMAINS` - implemented
earlier the same day - would point Android's HTTPS probe at our own `:443`, where nothing
listens, guaranteeing the probe fails. It was reverted. The change that fits the evidence is the
opposite one: forward unmatched names upstream, let the HTTPS probe reach real Google, and the
network should validate outright. No captive portal, no "no internet", and the phone keeps Wi-Fi
as its default route - a better outcome than the portal was ever going to produce.

`dns.js` already supports `upstream`, and `LocalServices` already reads a gitignored
`relay-env.txt` at the repo root into the relay child's environment, so this needs **no code
change**: `DNS_UPSTREAM=64.59.135.148` (this host's own configured resolver) in that file, and
deleting the file puts everything back.

**Open question this raises, deliberately not answered here:** if forwarding is meant to be off
and is not, that is either a bug in `HotspotService` or a decision that has quietly changed. The
guest-isolation rationale in `Decision-OfflineHosting.md` is not currently in force on this
machine either way, and somebody should decide which of the two is the intended state.

### RESOLVED - forwarding DNS upstream fixes it

Confirmed on the phone, 2026-09-07: **Reddit loads and the "no internet" flag is gone.** Android
validated the network on its own once its HTTPS probe could resolve. No captive portal was
needed, and none of the portal machinery had to change.

Name resolution through the relay now reads:

```
play.skiprocking.ca           -> 192.168.137.1     answered locally
connectivitycheck.gstatic.com -> 192.168.137.1     answered locally
www.google.com                -> 142.251.152.119   forwarded upstream
www.reddit.com                -> 151.101.21.140    forwarded upstream
```

Precedence is correct and there is no regression: `dns.js` matches its own names before
consulting `upstream`, so the game's hostname and the captive-probe domains still resolve to the
host exactly as before, and everything else reaches a real resolver.

**The root cause, stated plainly.** The relay's DNS responder answered NXDOMAIN for every name it
did not serve. On a hotspot that genuinely had no internet that was harmless. On this machine IP
forwarding is enabled and ICS is NATting, so the phone *did* have internet - and our resolver was
the only thing breaking it. Every symptom chased through eight rounds of hardware testing follows
from that one behaviour:

- "Connected, no internet" - Android's HTTPS validation probe could not resolve.
- No captive-portal sheet - a validation that fails at DNS never runs the HTTP probe, which is
  why packet capture found the phone resolving our address and never opening a TCP 80 connection.
- Meta apps working, Reddit not - cached addresses need no lookup; fresh ones got NXDOMAIN.

Defects 2 and 3 were both real code issues and both are fixed, but neither was the cause. The
`:80` listener, the redirect target, the firewall scope and the funnel were all working.

**Still machine-local.** The fix is `DNS_UPSTREAM=64.59.135.148` in a gitignored `relay-env.txt`,
which means it works on exactly one machine and no shipped build. Making it permanent means
`LocalServices` passing an upstream to the relay child - derived from the host's own configured
resolver rather than a hardcoded address, since that one belongs to this ISP. That is a small
change and it is not yet made, because it reverses a stated decision (see below).

**Decision required, not taken here.** `Decision-OfflineHosting.md` argues for guest isolation
and `HotspotService` logs that it disables IP forwarding. On this machine forwarding is enabled
regardless, so the isolation rationale is already not in force, and this fix leans further the
other way by giving guests working DNS. Two coherent positions:

1. **Accept connectivity.** Guests get internet; forward DNS; the captive-portal machinery
   becomes a fallback for hosts that genuinely have no uplink. Simplest, and matches what the
   machine already does.
2. **Restore isolation.** Find why forwarding is still enabled and fix it - at which point the
   phone really has no internet, this fix stops helping, and the portal path matters again.

Which one is chosen decides whether the `DNS_UPSTREAM` change gets promoted into `LocalServices`
or stays a per-machine escape hatch.

### Decision taken: restore isolation, and the bug that hid its absence

Chosen 2026-09-07: **isolation, not connectivity.** The target is a party where neither device has
internet and the two talk over the hotspot link alone, so guests reaching the internet was never
the goal - it was masking a defect. `relay-env.txt` was deleted, and the `DNS_UPSTREAM`
experiment with it.

**Why forwarding was still on: the confirmation was not a confirmation.**
`hotspot/hotspot-control.ps1` did this and reported success:

```powershell
Start-ScheduledTask -TaskName $ForwardingTaskName -ErrorAction Stop
```

`Start-ScheduledTask` returns as soon as the task is *launched*. It waits for nothing and checks
nothing. Since `HotspotService` treats a null warning as "isolation confirmed"
([HotspotService.cs:217](../../Assets/Scripts/Net/HotspotService.cs:217)), the system asserted an
isolation property it had never once verified - and asserted it correctly-formatted, every
session, on a machine that measurably had forwarding enabled. The scheduled task's own
`LastTaskResult 0` was equally worthless: it reports that a process exited, not that a flag moved.

**The task itself works.** Running it and reading the flag back:

```
Start-ScheduledTask RockSkippingDisableForwarding   ->  Forwarding: Disabled
```

So this was never a broken disable. It is a race nobody was watching: ICS re-enables forwarding on
every tethering start, the disable is asynchronous, and the caller declared victory immediately.

**Fixed** by replacing the launch-check with a fact-check: after starting the task,
`hotspot-control.ps1` polls `Get-NetIPInterface` for up to 10s and only reports success once
forwarding is actually `Disabled` on every `192.168.137.*` interface, warning
`forwarding-still-enabled` otherwise. Reading the flag needs no elevation. Because
`HotspotService` treats that warning as fatal, a genuinely unisolated hotspot now refuses to
start instead of pretending - which is the behaviour the code already claimed to have.

**`www.google.com` is back in `CAPTIVE_PROBE_DOMAINS`, and under this decision it is correct.**
With no internet, Android's HTTPS probe must fail by *refused connection* on our :443 rather than
by NXDOMAIN, or validation never runs the HTTP probe and the portal never appears - the exact
behaviour the seventh hardware test captured. The list now mixes two kinds of entry for opposite
reasons, so the comment above it spells out which is which; that distinction is silent when got
wrong.

**`test-portal-fix.ps1` was the one-command orchestrator for this test** - it killed the stale
relay, waited until the relay provably ran current code, self-healed the forwarding flag, then
captured. It has since been deleted (see below); the isolation check it carried is the part worth
rebuilding if this is ever picked up again.

**A false positive in the gate itself, caught on the first real run.** It reported
`NOT ISOLATED: forwarding is Enabled on Ethernet 4, Local Area Connection* 12` - naming the
machine's uplink, which has nothing to do with hotspot isolation and is legitimately forwarding.
Cause: the `$guestPrefix` definition had been lost in an edit, so the lookup ran as
`-IPAddress "*"` and matched every interface on the machine. An empty prefix silently widens the
question from "the hotspot interface" to "everything", and the answer still looks plausible,
which is the failure mode worth guarding. The definition is restored and the script now refuses
to run at all if the prefix cannot be derived from `HotspotIp`.

**The gate no longer hands work back.** Its first version printed the fix and stopped, which is
the same manual-step problem the orchestrator existed to remove. It now starts the disable task
itself, polls up to 15s, and only fails if forwarding will not stay down - the task is idempotent
and needs no elevation to start, so losing that race is something to fix in place.

**Still unverified:** whether the portal now appears on the phone with isolation genuinely in
force. That is the end-to-end test this all exists for, and it is the next thing to run.

### Ninth hardware test - the HTTPS-probe hypothesis is falsified, and the behaviour changed anyway

With isolation genuinely in force (forwarding `Disabled` and confirmed; the phone's Facebook shows
only cached content while Marketplace fails like Reddit, which is the correct offline signature)
and `www.google.com` answered locally:

```
04:05:23.346 dns 192.168.137.85 A www.google.com                -> 192.168.137.1
04:05:34.773 dns 192.168.137.85 A connectivitycheck.gstatic.com -> 192.168.137.1
```

The phone resolves both probe hosts on a ~60s cycle and **still issues no `/generate_204`**. Every
HTTP request in the log is from `192.168.137.1` or `127.0.0.1` - the diagnostic, never the phone.

**So the hypothesis is falsified.** Making Android's HTTPS probe fail by refused connection rather
than NXDOMAIN did not cause it to run the HTTP probe. Worth stating plainly because it was the
only remaining candidate the evidence pointed at, and it was wrong.

**But the behaviour did change, and the new observation is sharper.** `watch-guests.js` caught
this in the same second as the `www.google.com` lookup:

```
  10:05:23 p.m.  192.168.137.85 connected to :80
```

A TCP connection to `:80` now arrives - where previously a 90-second packet capture found zero
TCP 80 packets of any kind. And no request was logged for it, though both branches of
`handleProbeRequest` log unconditionally, so Node never raised a `request` event: **the phone
opens the socket and sends no complete request.**

That is a genuinely different failure from "never connects", and it is the thing to capture at
packet level next - the handshake flags say whether Android abandons the connection itself or
something resets it.

### Tenth finding - the phone completes HTTP to :80 perfectly; only the probe never fires

A capture taken at 03:42, while the browser was in use, recorded a complete and healthy
transaction from the phone:

```
192.168.137.85.38388 > 192.168.137.1.80: Flags [S]   ...
                                         Flags [P.]  ... HTTP: GET / HTTP/1.1
                                         Flags [F.]  ...
```

Handshake, request, response, clean close - 25 TCP 80 packets, of which the single "dropped" one
is a duplicate SYN. The relay log has the matching line
(`03:41:19 192.168.137.85 GET / Host=192.168.137.1 302`).

**This closes the network question for good.** The same phone, over the same interface, to the
same port, seconds apart, can complete an HTTP request end to end. There is no firewall scope
problem, no interface binding problem, no reachability problem, and no dropped SYN. The verdict
string "Investigate firewall scope and interface binding" is wrong for this case and reflects the
script's assumption that TCP 80 traffic implies a probe; the traffic was the browser.

**What remains is narrow and is not ours.** Android's captive-portal client specifically declines
to issue `/generate_204`, on a path its own browser uses successfully in the same minute. It
resolves both probe hosts every ~60s and sends nothing. Nothing served, bound, redirected,
resolved or firewalled on this host distinguishes those two clients - the difference is inside
the platform.

### CLOSED - the captive portal is abandoned; P1 is the mechanism

Decided 2026-09-07 after ten hardware rounds. **P2's captive-portal nudge is closed as
platform-dependent and will not be chased further.** The relay's half of it stays in place and
provably serves the right answers; whether a phone acts on them is not ours to control.

**What the evidence forced.** Android's captive-portal client resolves both probe hosts every
~60s and issues no HTTP probe, while the same phone's browser completes a full request to the
same port seconds later. Every host-side explanation was excluded by direct measurement, not by
argument:

| Hypothesis | How it died |
|---|---|
| `:80` blocked for guests | A guest's completed TCP connection in the host's own connection table |
| Redirect target upgraded to HTTPS | Fixed (Defect 3), behaviour unchanged |
| `:80` never bound | `diagnose-hotspot.js`, four PASSes, repeatedly |
| Phone routing to cellular | Its `GET /` arrived over the hotspot |
| DNS reply from the wrong source | `Find-NetRoute` selects the hotspot address |
| Probe SYN silently dropped | Packet capture with a DNS positive control: zero TCP 80, then a complete transaction from the browser |
| HTTPS probe failing at DNS | `www.google.com` answered locally; probe still never sent |

**What we are relying on instead.** P1 - the TV nudge - covers the same drop-off and needs no
cooperation from the phone. It keys off the DNS queries the relay demonstrably receives from
every associated device, and it is now count-free with a two-minute expiry for devices that never
got past ASSOCIATED. A player who joins the Wi-Fi and stops is told, on the TV, to scan the second
code. That was always the reliable half.

**The join path that matters is proven working**: the phone associates, resolves
`play.skiprocking.ca` to the host, and loads the controller over HTTPS on 8443. The packet capture
of a browser request to `:80` and its redirect to that address is the end-to-end evidence.

**`www.google.com` removed from `CAPTIVE_PROBE_DOMAINS`** once the decision was taken - it
existed only to serve a hypothesis that hardware testing falsified, and every other name in that
list is one we intend to actually serve. The comment above the list now says so, and points here
before anyone re-litigates it.

**`test-portal-fix.ps1` deleted as a consequence.** It used that name as its "is the relay
running current code?" marker, since it was the only name unique to the fixed build; with the
name gone the marker could never match and the script would time out every run. Rather than
leave a tool that always fails, it was removed - the portal chase it existed for is closed, and
`capture-probe.ps1` and `watch-guests.js` stand on their own. Recoverable from history:
`git show bd22729:relay/test-portal-fix.ps1`.

Two ideas in it are worth salvaging if this is ever reopened: refusing to measure until the
relay is provably running current code (two test cycles were wasted on a stale relay before that
check existed), and reading the forwarding flag back rather than trusting that a disable
happened.

**Not done, deliberately.** `capture-probe.ps1`'s `ARRIVED AND DROPPED` text advises
"investigate firewall scope and interface binding", which assumes TCP 80 traffic is a probe. When
the traffic is the browser - as it was - that advice is wrong. Left as-is because the tool has
served its purpose and the plan records the caveat.

### How to verify P2 on real hardware

`relay/test-captive.js` proves the relay's *answers* flip with the funnel stage. It cannot prove
the thing P2 actually claims — that a phone's OS reacts to those answers. That needs a phone, and
the procedure below is the whole of it. Two levers make it repeatable:

- **Resetting a device's stage:** the funnel is in-memory, so stopping and restarting the relay
  (stop Play in Unity, press Play again) puts every device back to UNKNOWN. Far faster than
  waiting out the 10-minute `IDLE_TIMEOUT_MS`.
- **Resetting the phone's captive-portal state:** both platforms cache "I already signed in to
  this SSID" per network, so a phone that has joined before may not re-probe at all. Use
  *Forget This Network* on the phone, or a phone that has never seen this SSID, before step 2.

1. **Watch the relay log.** Every DNS query logs its source address, so a phone associating shows
   up there within a second. That log is the ground truth for what stage the relay thinks a
   device is at, and every step below should be read against it.
2. **Join the Wi-Fi and touch nothing else.** Within a few seconds the phone should raise its own
   portal UI unprompted — iOS shows the CNA sheet, Android a *Sign in to Wi-Fi network*
   notification — carrying the "You're on the game's Wi-Fi" page. **This is the P2 claim.** If no
   sheet appears, check the phone had genuinely forgotten the network (the cache above), and that
   `:80` bound at all: `startHttpProbeListener` degrades quietly and logs loudly when it cannot.
3. **Check the TV at the same moment.** It should read *"A phone is on the Wi-Fi — now scan the
   second code"*, with the game QR grown and the Wi-Fi QR faded. That is P1, and it is the
   fallback for anyone whose phone shows no sheet.
4. **Tap the button on the portal page.** On iOS the CNA sheet may refuse to hand off directly —
   use its menu → *Open in Browser*. Confirm the controller opens **in the real browser**, and
   that the motion-permission prompt appears. If sensors do not work, the page opened inside the
   sandboxed sheet, which is exactly what the handoff design exists to avoid.
5. **Play a round, then check "No internet" is gone.** This is the half that trades one failure
   for a worse one if it is wrong. With the phone in a match, it must not flag "No internet" and
   must not drift back to cellular. If it does, the funnel never recorded PAGE_OPEN for that
   device — check the relay log for its address.
6. **Rejoin without forgetting the network.** A phone that is already at PAGE_OPEN should get
   `204`/`Success` and no sheet at all. This is the returning-guest path, and the one that should
   feel like zero scans: known SSID auto-joins, no portal, browser still has the page.

### Not verified here

The Unity half has not been compiled or run — Unity holds an exclusive project lock, so there is
no out-of-band compile check (`Assets/CLAUDE.md`). Check the Editor Console, then confirm on real
hardware: a phone that joins the Wi-Fi and stops should raise the nudge on the TV within a second
or two, and should stop flagging "No internet" once it is playing.

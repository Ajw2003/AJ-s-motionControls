# Hosting — one small service, peer-to-peer gameplay

**Status: Phase 1 and Phase 2 are both deployed and running on real hardware.** The relay runs
on Fly at `rockskipping-relay.fly.dev`, the game connects out to it, and `cloudflared` is gone
from the codebase. Phone-to-PC WebRTC DataChannels carry `AIM` and `THROW` directly over the
LAN, and both ends now recover from a wifi drop —
[deploy/RECONNECT-TEST.md](deploy/RECONNECT-TEST.md). `play.skiprocking.ca` is still pending
registry delegation and is cosmetic; the `fly.dev` name works fully. Deployment steps:
[deploy/README.md](deploy/README.md).

This document supersedes the "Why a hosted relay rather than direct LAN connection" section of
[PLAN.md](PLAN.md).

---

## 1. Context — and the requirement that broke the previous design

Two earlier attempts at this document are worth summarising, because the path matters more
than the destination:

1. **Tunnel-per-host** (what the code used to do, now deleted). Every host spawned
   `cloudflared` and got a random `trycloudflare.com` address. Rejected: a third party in the
   path of every packet.
2. **Self-hosted, own domain.** The relay obtains its own certificate via ACME DNS-01 and
   phones connect straight to the host machine. Rejected: it only works when there is exactly
   **one** host. A DNS API credential is account-wide, and a credential shipped inside a
   distributed binary is extractable, not secret — every player would hold DNS control over
   the whole account, and rotating it would break every installed copy at once.

Design 2 died on a requirement that had been implicit: **anyone who installs the game can host
a party, and they configure nothing.** That forces the conclusion this document is built on.

### Requirements, in priority order

1. **Joining is: scan the QR, play.** Any step that makes a guest leave the browser is
   disqualifying (§9).
2. **Hosting is: install and play.** No domain, no credential, no port, no router page, no
   configuration of any kind — on *any* host's machine, not just the developer's (§8).
3. **Minimise third parties**, and never put one in the gameplay path.
4. An internet connection is fine to require.

### The conclusion this forces

Requirement 1 needs a secure context, which needs a publicly-trusted certificate, which needs
a registered domain (§2). Requirement 2 says no host may configure one. Therefore **somebody
must operate a service that hands out a name and a certificate**, and there is no arrangement
of self-hosting, tunnelling, or clever certificate work that avoids it.

That service is ours. The question this document answers is how *little* it can carry.

**Answer: the page and the introductions, and nothing else.** Gameplay goes phone-to-PC
directly over the LAN, so the service never sees an aim frame.

## 2. Why a public CA is forced

Unchanged from the previous design, and still the load-bearing derivation.

Phone motion sensors require a **secure context**. Chrome has refused `DeviceMotionEvent` and
`DeviceOrientationEvent` on insecure origins since Chrome 76; Safari never allowed them; the
Generic Sensor API is stricter still, and camera-based motion needs `getUserMedia`, which is
also gated. Touch and pointer events are the only ungated input. The controller already checks
this — `isSecureContextOk()` in [controller/js/sensors.js](controller/js/sensors.js).

A secure context means HTTPS with a certificate chaining to a root **already in the phone's
trust store**. Trust stores hold public CA roots and user-installed roots. Requirement 1
eliminates user-installed roots, because installing one cannot be automated and never will
be — mobile OSes deliberately route root installation through Settings so a web page cannot do
it. Android 11+ no longer even launches an installer from a downloaded `.crt`; iOS splits the
job across two Settings screens.

> sensors → secure context → publicly-trusted certificate → public CA → a domain we own

**But only the page needs that certificate.** This is the insight the whole architecture turns
on: WebRTC's DTLS uses self-signed keys by design, and every browser accepts them. So the
*page* comes from our HTTPS origin, and the *data* goes direct with no certificate anywhere
near it.

### Alternatives considered and rejected

Each was investigated properly and each fails a numbered requirement from §1. Recorded so the
next person does not spend the same afternoon — these keep getting re-proposed, because every
one of them looks like it should work.

| Alternative | Third parties | Why rejected |
|---|---|---|
| **Private CA** — relay mints its own root and leaves | **Zero.** No registrar, no CA, no DNS, no internet. | Every guest installs a root by hand, and that **cannot be automated by design**. Fails requirement 1 outright. Tempting otherwise: it satisfies 2, 3 and 4 perfectly. |
| **Let's Encrypt IP-address certificate**, no domain | CA only — fewer than the chosen design, and $0. | Needs a routable public IP, port 443 forwarded, and working NAT hairpin for phones *inside* the house. CGNAT or a router without loopback means it fails **at party time**. |
| **Self-hosted authoritative DNS** | Registrar only. | Needs port 53 forwarded and the PC permanently online; if the machine is off, the domain stops resolving entirely. More fragility for one less vendor. |
| **Bundle a reverse proxy** (Caddy) and serve the page from the host machine | Depends entirely on where its certificate comes from | **The proxy was never the blocker.** A locally-run Caddy can use ACME (needs a per-host domain, or a shared credential that is extractable from a shipped binary), its internal CA (a self-signed root every guest installs by hand), or plain HTTP (no secure context). All three are rejected above. Costs a ~45 MB binary and a listening socket on every player's machine, which reintroduces the firewall prompt §8 exists to avoid. |
| **Certificate broker** — our service issues each host its own cert for `<id>.play.skiprocking.ca` pointing at that host's LAN IP, served by a bundled proxy | Ours, for issuance and DNS | The only version of the above that actually works, and it fails on a number: **Let's Encrypt allows 50 certificates per registered domain per week**, capping new hosts at 50/week permanently — fatal for the Steam-scale ambition in §13. Also reintroduces DNS-rebinding failures on routers that strip private-IP answers. What it buys is a locally-served page, which after Phase 2 is a few hundred cached KB per player per session. It does not enable offline play either: the phone still needs public DNS to resolve the name. |
| **A different tunnel vendor** — localtunnel, ngrok, bore, etc., bundled and run per host | One, in the path of every packet | This is design #1 from §1 with the vendor swapped, and usually swapped downward: `localtunnel.me` is free, hobby-run and has no SLA, so an outage breaks every host at once with nobody to call. Bundling it also points an entire playerbase at someone else's free infrastructure, which is a rate-limit problem and a free-riding one. Self-hosting the tunnel server needs a VPS with a wildcard domain — the infrastructure we already run, plus a tunnel server *and* the relay instead of just the relay. |
| **Touch controls only**, plain HTTP on the LAN | **Zero.** | Satisfies every requirement except the game. Kept as the *fallback* (§6), never as the design. |

## 3. Architecture

```
   Phone (browser)                  skiprocking.ca                Unity 6 Desktop
   ──────────────                   ─────────────                 ───────────────
   loads page ────── HTTPS ────────▶  Fly machine
   signalling ◀───── WSS ──────────▶  relay + page  ◀──── WSS ────── outbound only
                                      (kilobytes)
        │                                                                │
        └──────────── WebRTC DataChannel, direct over LAN ───────────────┘
                      AIM @20Hz · THROW · no certificate, no server
```

Three properties are worth naming, because each kills a category of problem:

- **The host connects *outbound*.** No inbound ports, no UPnP, no port forwarding, no address
  discovery, no DHCP reservation, no CGNAT problem, no router page. This is what makes
  requirement 2 achievable rather than aspirational.
- **The service holds one domain and one certificate**, on a machine we control. Fly issues and
  renews it today; on the VPS route Caddy does, over ACME **HTTP-01**. Either way the machine
  has a public IP and port 80, so there is no DNS challenge and therefore **no DNS API
  credential anywhere in the system**. The entire Porkbun module, token handling and
  secret-distribution problem from the previous design simply ceases to exist.
- **Gameplay is peer-to-peer.** Bandwidth cost is near zero, latency is LAN-local, and the
  service being slow or distant cannot affect how a throw feels.

## 4. Phase 1 — the relay path (built)

**Deliberately built before any WebRTC.** It is a complete, shippable product on its own, it
was mostly existing code, and it is the fallback that makes Phase 2 safe to attempt: if a peer
connection fails to establish, traffic falls back to this path rather than to nothing.

What shipped:

1. [relay/server.js](relay/server.js) deployed to **Fly.io** from the repo root — the image
   needs `controller/` alongside `relay/`, since `STATIC_DIR` resolves as `../controller`.
   **Fly terminates TLS and renews the certificate itself**, so every line of ACME code this
   document once specified went unwritten. A Caddy-in-front-of-a-VPS route is documented as the
   migration path if Fly ever stops suiting us.
2. `server.js` binds `BIND_HOST || '0.0.0.0'`. The default matters: Fly's proxy reaches the
   process from outside its VM, so loopback would refuse every connection. The VPS route sets
   `BIND_HOST=127.0.0.1` because there the proxy is on the same host.
3. `RelayClient` connects out to the service, and **derives `PublicOrigin` and `JoinUrlFor()`
   from the socket URL** rather than taking them as separate configuration — which is what
   makes it structurally impossible for the page and the socket to drift onto different hosts.
   A `relay-url.txt` beside the executable overrides the address without a rebuild (§12.6).
4. `cloudflared`, the `TunnelUrlPattern` regex and the stderr scraping are deleted from
   `LocalServices`, which is now a development-only local relay, **off by default**.
   `JoinScreen` is driven by the relay connection rather than by `LocalServices`, which no
   longer exists on a normal host.

The result: hosts install and play, guests scan and play, motion controls work, and AIM/THROW
route through the service. That last part is the only cost, and it is tolerable because the
two-channel split in PLAN.md was designed for exactly this — AIM is cosmetic and lossy, and
THROW is computed phone-side, so no amount of latency can change a throw's outcome.

**The deployment must run exactly one machine** (§12.7). `relay/test-deployed.js` probes for
this along with the rest of the routing: `cd relay && npm run test:deployed`.

## 5. Phase 2 — WebRTC DataChannel (built)

An optimisation layered on Phase 1, not a replacement for it.

**What shipped:** [Assets/Scripts/Net/PeerLink.cs](Assets/Scripts/Net/PeerLink.cs) holds one
peer connection per phone; [controller/js/peer.js](controller/js/peer.js) is its other half.
`AIM` and `THROW` go direct; everything else stays on the relay. The relay itself was not
touched — `npm test` is green at 23/23 without a single change, exactly as predicted below.

Three decisions worth knowing before reading the code:

- **Nothing downstream knows peer connections exist.** `PeerLink` feeds received messages
  into `RelayClient.IngestFromPeer`, which queues them into the *same* dispatch as relay
  traffic. `AimPreview`, `RockLauncher` and `RelayDebugHud` still subscribe to `RelayClient`
  and cannot tell which transport delivered a frame.
- **Identity still comes from the transport, never the payload.** The relay stamps `playerId`
  server-side so a phone cannot label its throws with someone else's name. There is no relay
  in a peer path to do that, so `IngestFromPeer` takes the id from *which channel the message
  arrived on* and overwrites whatever the payload claimed. Dropping that guarantee silently
  would have been an easy regression.
- **Fallback is per-phone and automatic.** `send()` on the controller uses the data channel
  when open and the relay socket otherwise, so a phone that never negotiates simply plays on
  Phase 1. Callers do not branch — one code path, or the fallback rots.

Turn it off with **Use Peer Connections** on `Bootstrap`, or per-phone with `?direct=0` on the
controller URL. The TV's F1 readout shows `N/M direct` and names which phones are on which
transport, so a silent downgrade is visible rather than merely slower.

### The relay needs no changes to carry signalling

Worth checking before writing any: `server.js` already documents its routing as deliberately
generic — anything a player sends goes to the host with `playerId` stamped server-side,
anything the host sends goes to all players or to one when `targetPlayerId` is set. That is
exactly the shape SDP offer/answer and trickle ICE need. `RTC_OFFER`, `RTC_ANSWER` and
`RTC_ICE` route correctly through the existing `default:` branch with **zero relay changes**.

Two constants needed checking, and both are now **measured rather than assumed**. A real
DataChannel-only offer generated in a browser against the deployed page:

- **SDP is 458 bytes.** `MAX_MESSAGE_BYTES` is 4096, so it fits nine times over. Trickle ICE
  keeps candidates out of the offer, which is what keeps it that small. **No relay change.**
- `RATE_MAX_MESSAGES` is 60/second, and gathering produced **2 candidates**. Not close.

### Channel split

| Traffic | Transport | Why |
|---|---|---|
| Room code, roster, turn state, scores, `SPECTATE` | WSS to the service | Low rate, ordering and reliability matter, and it must work before any peer connection exists |
| `AIM` at 20Hz, `THROW` | DataChannel, direct | The only traffic where latency is felt |

### The mDNS question — RESOLVED. Phase 2 is viable.

**The spike passed on real hardware**: Unity 6000.3.10f1 on this PC, Android Chrome on a phone,
same Wi-Fi. A DataChannel opened and the winning pair was `local=host remote=host`.

The result was better than the theory required, and it is worth recording why, because the
theory was built on a claim that turned out to be **browser-dependent rather than universal**.

A desktop Chromium, asked for an offer, produced nothing but obfuscated candidates:

```
candidate:858858120 1 udp 2113937151 2a456251-…-9e659d996c56.local 61474 typ host
```

From that, this document previously asserted that a browser *will never* hand over a dialable
address. **The phone did exactly that:**

```
remote candidate: host 10.0.0.55
remote candidate: host 2604:3d09:e60:2800:7a3d:6de8:c80f:56a5
```

Real IPv4 and real IPv6. So mDNS obfuscation (RFC 8828) is applied by some browsers in some
configurations, and generalising from one sample was wrong.

**What the spike proved:**

- A Unity peer and a phone browser establish a DataChannel over the LAN, `host`↔`host`.
- **No STUN, no TURN** — only host candidates were gathered, and they were enough.
- Both peers gathered IPv4 and IPv6; the pair that won was direct.
- The real offer was **457 bytes**, matching the 458 measured beforehand and sitting far
  inside `MAX_MESSAGE_BYTES`.

**What it did not prove, and why that no longer blocks anything:** the peer-reflexive path is
still untested, because this phone never forced it. A browser that *does* obfuscate would
exercise it. If that theory turns out to be wrong on some device, that device falls back to
the WSS path in §6 — a slightly softer aim preview, not a broken game. The risk moved from
"could invalidate Phase 2" to "one class of phone may not get the fast path."

### No STUN, no TURN

Confirmed by the same measurement: gathering completed with **two candidates, both `typ host`,
no `srflx`** — because no STUN server was configured and none was needed. A LAN-only peer
connection gathers everything it needs locally, keeping the external dependency count at
exactly one, our own service. Remote play would need STUN (cheap, self-hostable) and possibly
TURN (expensive), which is why it stays out of scope until LAN play works.

### Unity side

**`com.unity.webrtc` 3.0.0 declares `unity: 6000.0`**, so it supports 6000.3.10f1 — checked
against Unity's package registry rather than assumed. Official package, native binaries,
DataChannel support.

`RelayClient` keeps its `ClientWebSocket` (see the DEVIATION note in that file: it ships with
.NET and needs no package), and a new `PeerLink` handles offer/answer/ICE and the DataChannel,
publishing received messages through the same path `RelayClient` uses today so nothing
downstream knows the difference.

### The spike — built, run, passed, deleted

A throwaway Unity peer and a standalone `spike.html` answered the question above: a Unity peer
answers an offer from a phone browser, opens a DataChannel, and reports which candidate pair
won. It did, so the spike was superseded by the real implementation —
`Assets/Scripts/Net/PeerLink.cs` and [controller/js/peer.js](controller/js/peer.js) — and both
the spike and its test document have been removed.

One caveat worth keeping from that exercise: some routers isolate clients from each other on
the same Wi-Fi, and a phone that cannot reach the PC at all fails in a way indistinguishable
from a peer negotiation failure. Confirm the phone can load the join page before diagnosing
anything about WebRTC.

**How to read it.** Both screens print the winning pair:

| Remote candidate type | Meaning |
|---|---|
| `prflx` | **The theory held exactly.** Unity learned the phone's address from the incoming connectivity check. |
| `host` | Also a pass — Unity resolved the mDNS name itself. Different route, same outcome. |
| never connects | **The finding that sends Phase 2 back to the drawing board.** Phase 1 keeps working; §6 already covers it. |

Delete `Assets/Scripts/Spike/` and `controller/spike.html` once the question is answered.

### Two things the spike already settled

- **No manual initialisation.** `WebRTC.Initialize()` is gone in 3.0 — it runs from
  `RuntimeInitializeOnLoadMethod`.
- **`WebRTC.Update()` is not needed.** Every sample calls it and the documentation is explicit
  that it exists to copy textures into the video buffer each frame — and the package's own
  DataChannel sample does not call it. **A DataChannel-only integration costs no per-frame
  coroutine**, which is what §12.2 wanted checked.

### Verifying Phase 2 on hardware

1. `fly deploy` — the phone needs the new `controller/js/peer.js`.
2. Press Play with **Use Peer Connections** ticked on `Bootstrap` (the default).
3. Join from an Android phone on the same Wi-Fi. The phone's status line should change from
   `Connected as AJ` to `Connected as AJ · direct` within a couple of seconds.
4. **F1 on the TV** shows `Input  1/1 direct`. That is the check that matters — the phone
   saying `direct` only proves its own channel opened.
5. Throw. Aim tracking should feel tighter than before, because the frames no longer round-trip
   to Toronto.
6. **Prove the fallback**, which is the part most likely to rot unnoticed: rejoin with
   `?direct=0` on the URL and confirm the game plays identically, with F1 reporting `0/1
   direct`. A throw must behave the same on both transports.

## 6. Fallbacks, in order

Each degrades to the one below it, and each is honestly labelled on the TV rather than failing
silently:

1. **DataChannel** — normal operation.
2. **WSS relay** — if the peer connection does not establish within a few seconds, AIM and
   THROW route through the service. This is Phase 1, so it is proven code by the time Phase 2
   exists. Players notice nothing except slightly softer aim tracking.
3. **Touch controls on the LAN** — if the service is unreachable entirely (no internet, service
   down), the bundled relay serves plain HTTP on the local network and the controller switches
   to touch aiming, reusing the `computeSimulatedThrowDescriptor` seam already in
   [controller/js/throw.js](controller/js/throw.js). No certificate is possible here, so no
   motion sensors — but a party still happens.

## 7. What this deletes

The previous designs accumulated machinery that this one does not need. Removing it is most of
the implementation work:

- `cloudflared` spawning, the `TunnelUrlPattern` regex, and the stderr-scraping `Update()`
  loop in [Assets/Scripts/Net/LocalServices.cs](Assets/Scripts/Net/LocalServices.cs). The join
  URL is now a constant.
- The entire DNS-01 / Porkbun / API-token design: `hosting.config.json`, the credential
  handling, the env-var fallback, the address discovery, the A-record upsert. HTTP-01 on a
  machine with a public IP needs none of it.
- UPnP / NAT-PMP port mapping, CGNAT detection, NAT hairpin handling. All artefacts of
  inbound connections, and there are none.

**Done:** `relay/hosting.config.json`, `relay/hosting.config.example.json` and their
`.gitignore` entries are deleted. They described a design that no longer exists, and a config
file nothing reads is worse than no config file at all.

One more, and it is a real simplification: **the relay does not terminate TLS.** Fly's proxy
holds the certificate today, and Caddy would on the VPS route — either way `server.js` keeps
serving plain HTTP exactly as it does today, and the whole `acme-client` dependency, the
`setSecureContext` hot-swap and the port-fallback ladder never get written (§4).

## 8. Host-setup audit

| # | Step | When |
|---|---|---|
| — | — | — |

**Empty, deliberately.** A host installs the game and presses Play. There is no credential, no
domain, no port, no record, no router page, and no firewall prompt — because nothing listens.

The one-time actions that remain belong to *us*, once, for the whole project: register the
domain (done), point one CNAME at the Fly app, and run `fly certs add`. Not per host, not per
party. Both remaining steps are blocked on `.ca` delegation and are cosmetic until then — the
`fly.dev` name already works, certificate included.

**The test that catches regressions:** run a build on a machine that has never seen the
project, on a network nobody configured — a phone hotspot will do — and host a game. Any human
input beyond pressing Play is a regression against requirement 2.

## 9. Join-flow audit

| # | Step | Where |
|---|---|---|
| 1 | Scan the QR on the TV | Camera app |
| 2 | Page loads, room code pre-filled from `?room=CODE` | Browser |
| 3 | Type a name, tap Join | Browser |
| 4 | Tap to allow motion — **iOS only** | Browser, in-page |

Four steps, one of them typing, none leaving the browser. Step 2 already works — `JoinUrlFor`
in `LocalServices` appends the code and the QR encodes the whole URL.

**Any change that adds a fifth step, or sends a guest to an OS settings screen, an app store,
or a third-party page, is a regression against the top requirement** — not a UX nitpick to be
traded away. This is why the private-CA design was rejected despite being the only option with
no third parties at all.

## 10. The service

Small enough to describe in a paragraph. A single `shared-cpu-1x` Fly machine running the
existing relay, with TLS terminated by Fly's proxy — see [fly.toml](fly.toml). Route B in
[deploy/README.md](deploy/README.md) swaps that for a VPS running Caddy, which obtains and
renews the certificate itself — an identical result that nothing in the game can distinguish.
Gameplay traffic is peer-to-peer, so the only bandwidth is signalling and lobby state —
kilobytes per room — and PLAN.md's own figures (AIM at ~1.5 KB/s for the single active player,
4Hz spectator text) are the ceiling even in the Phase 1 fallback where everything routes
through it. A $5/month instance carries a great many parties.

**It is a single point of failure for every host**, which is the honest cost of requirement 2
and the reason §6 exists. Keep it boring: one process, one certificate, automatic renewal, and
no state worth losing — rooms are ephemeral and rebuilt on reconnect.

## 11. Self-hosted mode

Two different things get called "self-hosted" here, and only one of them is unbuilt.

**Running the service on hardware you control is built.** Route B in
[deploy/README.md](deploy/README.md) is a complete runbook with a working
[Caddyfile](deploy/Caddyfile) and [systemd unit](deploy/rockskip-relay.service). The relay is a
stock `node:22-alpine` image with no Fly-specific code in it, so moving hosts is a deployment
change and nothing in the game knows the difference. The only coupling is `BIND_HOST`, which
the systemd unit sets to `127.0.0.1` because Caddy is on the same host and the default of
`0.0.0.0` exists for Fly's proxy reaching the process from outside its VM.

**Per-host self-hosting is not built** — letting an arbitrary player's machine serve the page
under its own certificate, depending on no service of ours. The whole ACME/DNS-01/self-signed
design from the previous revision of this document remains valid for a single known operator
who would rather not depend on our service, but it is **not on the critical path** — build it
only if someone asks. Its full reasoning lives in this file's git history rather than being
carried here, because carrying it was what made the previous version misleading about what
actually ships.

## 12. Risks

1. ~~The mDNS / peer-reflexive assumption.~~ **Resolved by the spike (§5)** — Unity and Android
   Chrome connect `host`↔`host` over the LAN with no STUN. What remains is a much smaller
   risk: a browser that *does* obfuscate its candidates has not been tested, and would fall
   back to the WSS path rather than failing.
2. **`com.unity.webrtc` is a native-binary dependency** — now installed at 3.0.0, which declares
   `unity: 6000.0`. Startup cost for DataChannel-only turned out to be nil: initialisation is
   automatic and the per-frame `WebRTC.Update()` coroutine is video-only (§5). What remains
   unmeasured is the native library's contribution to build size.
3. **Our service going down takes every host with it.** Mitigated by §6 tier 3, bounded by
   keeping the service trivial.
4. **Cost scales with popularity**, though from a very low base. Phase 1 fallback traffic is
   the thing to watch, since it is the only path that scales with players rather than rooms.
5. **iOS remains unverified** — no device, per PLAN.md's standing test-device policy. WebRTC
   on iOS Safari adds local-network-privacy behaviour that cannot be checked here.
6. **A shipped build hardcodes our hostname.** If the domain ever moves, old builds break.
   Cheap insurance: read the signalling URL from a config file next to the executable, with
   the hostname as the default.
7. **The relay cannot be horizontally scaled**, and this is a property of the design rather
   than an oversight. Rooms live in a per-process `Map`, so a second instance is a second
   universe: a host creates a room on one and a phone joins the other, which answers
   `ROOM_NOT_FOUND` for a code that is visibly on the TV. It fails intermittently, only under
   real load, and only with guests spread across networks — the worst possible combination.
   **The deployment must run exactly one instance** (`fly scale count 1`; `--ha=false` on a
   fresh app), and `relay/test-deployed.js` probes for it on every run. Nothing is lost: a
   restart drops every room regardless, so there is no availability that a second instance
   would preserve. Sharing room state across instances would mean Redis and is not worth it
   at this scale.

## 13. If this ships on Steam

Recorded because "just use Steam's networking" is the obvious question and the answer is not
obvious. **Steamworks cannot replace any part of this design**, and the reason is structural
rather than a limitation to work around.

### Why Steam networking does not apply

`ISteamNetworkingSockets`, Steam Datagram Relay and Steam lobbies all connect **Steam users to
Steam users**. They authenticate against a Steam ID and require the Steam client and the
Steamworks SDK at both ends.

Our controllers are **web pages in a phone browser**. A phone browser has no Steam ID, cannot
load the SDK, and cannot open an SDR connection. The phone-to-PC link is a web problem, and
Steam has no web product — it does not host arbitrary web content for your game, so there is
nowhere for the controller page to live either. The service in §10 stays exactly as it is.

Everything Steam offers here applies to a link this game does not currently have.

### What Steam would genuinely change

| Area | Effect |
|---|---|
| Distribution, updates, storefront | Solved for you. Does not touch hosting. |
| **PC-to-PC play**, if ever added | This is the one place Steam fits perfectly. Two households each with their own TV is Steam-user to Steam-user, and SDR handles NAT traversal with free bandwidth for partners. It would not replace the relay; it would sit alongside it. |
| **Remote Play Together** | A real alternative to the opt-in remote mode in §4 — it streams the TV to a friend and forwards their input, so a distant player needs no port forwarding and no public address at all. **But it forwards gamepad/keyboard input, not phone motion data**, so a remote player would be on a virtual controller rather than swinging their phone. Verify before relying on it. |
| Offline expectations | The sharpest risk. Steam players expect a game to run on a plane, and this one requires internet for the page and the certificate. The touch fallback (§6 tier 3) stops being a nicety and becomes the answer to a refund reason. |
| Steam Deck | Fine — it is a Windows build under Proton, and with `LocalServices` off by default there is no Node process to spawn. |
| Compliance | The relay logs player-chosen names. A shipped product wants a privacy policy and a disclosure that an online service is required. |

### The scaling problem Steam would expose

§12.7 says the relay must run exactly one instance because rooms live in a per-process `Map`.
That is comfortable for a hobby game and **becomes the binding constraint at Steam scale**,
where one machine is both a capacity ceiling and a single point of failure for every player.

Two ways out, in preference order:

1. **Finish Phase 2 first.** With gameplay peer-to-peer, the service carries only signalling
   and lobby state — so a single instance goes a very long way, and bandwidth cost stays flat
   as the player count grows. **A Steam release makes Phase 2 load-bearing rather than a
   latency optimisation**, which is the most important thing on this page.
2. **Shard by room code** rather than sharing state. The relay assigns codes, so it can encode
   which instance owns a room in the code itself and route on it — Fly's `fly-replay` header
   does this natively. That keeps the per-process `Map` and avoids introducing Redis for what
   is, per room, a few hundred bytes.

### What would not change

The four requirements in §1, the certificate derivation in §2, the four-step join in §9, and
the zero-setup host in §8 are all independent of how the game is distributed. A Steam build
scans the same QR and loads the same page from the same origin.

## 14. Measured capacity, and what it costs to scale

Numbers rather than adjectives. Produced by `relay/loadtest.js`, which drives the real message
rates — AIM at 20Hz from the single active player, SPECTATE at 4Hz to everyone — against a
local relay. **Reproduce with `cd relay && npm run loadtest -- 250 8 20`.**

### What one process actually carries

| Rooms | Players | Sockets | RSS | msg in/s | msg out/s |
|---:|---:|---:|---:|---:|---:|
| 0 | 0 | 0 | 43 MB | 0 | 0 |
| 25 | 100 | 125 | 48 MB | 493 | 787 |
| 100 | 600 | 700 | 56 MB | 1,965 | 3,921 |
| 250 | 2,000 | 2,250 | 87 MB | 4,954 | 11,855 |

Read off the marginals, which is the part that scales:

- **~176 KB of RSS per room**, about **20 KB per socket**. Baseline is a flat 43 MB.
- **~20 messages in and ~47 out per room per second** at 8 players. Fan-out is the asymmetry:
  every SPECTATE is sent once by the host and delivered eight times.
- **26% of one desktop core at 250 rooms** — 5.1 CPU-seconds over 20 seconds, handling
  ~16,800 messages/second.

### The ceiling on today's machine

That CPU figure was measured on a full modern desktop core. **`shared-cpu-1x` is a fraction of
one**, so divide accordingly rather than reading 250 rooms as the answer — the honest estimate
for the current 256 MB `shared-cpu-1x` is **tens of concurrent parties, CPU-bound before
memory-bound**. Memory would allow roughly 500–1,000 rooms; CPU will give out long first.

This is Phase 1 traffic. It is the pessimal case, and it is temporary.

### Why Phase 2 changes the shape, not just the size

Phase 2 moves AIM and THROW — and SPECTATE fan-out with them — onto the DataChannel. What
remains on the server is connection setup, a handful of signalling messages per join, and
lobby/turn state. Per-room steady-state traffic goes from ~67 messages/second to **roughly
zero**.

The consequence is worth stating precisely: **the service stops being CPU-bound and becomes
memory-bound**, at ~176 KB per room for sockets that now sit almost idle. That is a change of
kind, not degree — capacity per machine goes up by more than an order of magnitude, and the
cost per additional player stops tracking traffic at all.

**This is the strongest argument for Phase 2, stronger than latency.** It is also §13's
conclusion arrived at from a different direction.

### Scaling beyond one machine

§12.7 stands: rooms live in a per-process `Map`, so the deployment runs exactly one instance.
In preference order, when that stops being enough:

1. **Vertical.** 256 MB `shared-cpu-1x` → `shared-cpu-2x`/`performance-1x` with 512 MB–1 GB.
   One config line, no design change, and it is the correct first move by a wide margin.
2. **Phase 2**, which multiplies whatever that box can hold.
3. **Shard by room code.** The relay assigns codes, so it can encode the owning instance in
   the code and route on it — Fly's `fly-replay` header acts on the WebSocket upgrade request,
   which is exactly the right moment. Keeps the per-process `Map`, needs no Redis, and scales
   horizontally without shared state. This is the design to reach for, not a Redis session
   store.

### Gaps to close before this is "more than a hobby"

Found while measuring, and all currently real:

- **No cap on rooms and no per-IP limit.** `MAX_PLAYERS_PER_ROOM` is 8, but anything may call
  `CREATE_ROOM` without limit. At ~176 KB a room, a trivial script exhausts the machine. The
  existing rate limiter is per-socket, which does not help when the attack is *more sockets*.
  A public service needs a per-IP connection cap and a global room ceiling that sheds load
  rather than dying.
- **Room codes are 4 characters from a 24-letter alphabet — 331,776 combinations.** Fine to
  tens of thousands of concurrent rooms with the existing 100-attempt retry, and it degrades
  gracefully rather than breaking. Move to 5 characters if that is ever in sight.
- **`/health` is the only telemetry**, and nothing scrapes it. It now reports rooms, players,
  connections, message counters, RSS and heap — enough to alert on, but an alert has to exist.
- **One machine is a single point of failure for every player**, which §13 flags as the
  Steam-scale problem.

## 15. Verification

- **Phase 1 end to end** — a real Android phone scans the QR, joins through
  `wss://play.skiprocking.ca`, `window.isSecureContext === true`, and a real swing produces a
  `THROW`. This is the acceptance test for shipping.
- **Zero-setup claim** — §8's test: unfamiliar machine, unconfigured network, press Play.
- **Certificate renewal** — not ours to test on Fly, which renews its own; `fly certs show`
  reports the expiry. On the VPS route, force Caddy to renew and confirm the relay keeps open
  sockets alive through the reload.
- **Peer connection establishes on a real LAN** — Android phone to this PC, and confirm from
  ICE state which candidate pair won. A relayed or reflexive pair where a host pair was
  expected means §5's assumption is wrong.
- **Fallback ladder** — block UDP to force the DataChannel to fail and confirm AIM/THROW move
  to WSS with the TV labelling it; then take the service unreachable and confirm the touch path.
- **Signalling size** — capture a real SDP offer and check it against `MAX_MESSAGE_BYTES`.
- **Regression** — `cd relay && npm test` keeps passing. The relay is barely changing, which
  is the point.

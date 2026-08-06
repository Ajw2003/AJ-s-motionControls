# Hosting — one small service, peer-to-peer gameplay

**Status: design, not yet implemented.** [README.md](README.md) describes the code as it
stands today, which still requires a Cloudflare quick tunnel. This document supersedes the
"Why a hosted relay rather than direct LAN connection" section of [PLAN.md](PLAN.md).

---

## 1. Context — and the requirement that broke the previous design

Two earlier attempts at this document are worth summarising, because the path matters more
than the destination:

1. **Tunnel-per-host** (what the code does now). Every host spawns `cloudflared` and gets a
   random `trycloudflare.com` address. Rejected: a third party in the path of every packet.
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

## 3. Architecture

```
   Phone (browser)                  skiprocking.ca                Unity 6 Desktop
   ──────────────                   ─────────────                 ───────────────
   loads page ────── HTTPS ────────▶  tiny VPS
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
- **The service holds one domain and one certificate**, on a machine we control, renewed by
  ACME **HTTP-01** — the VPS has a public IP and port 80, so there is no DNS challenge and
  therefore **no DNS API credential anywhere in the system**. The entire Porkbun module, token
  handling and secret-distribution problem from the previous design simply ceases to exist.
- **Gameplay is peer-to-peer.** Bandwidth cost is near zero, latency is LAN-local, and the
  service being slow or distant cannot affect how a throw feels.

## 4. Phase 1 — ship the relay path first

**Build this before any WebRTC.** It is a complete, shippable product on its own, it is mostly
existing code, and it becomes the fallback that makes Phase 2 safe to attempt.

1. Deploy the current [relay/server.js](relay/server.js) to a small VPS behind
   `play.skiprocking.ca`. One A record, set by hand, once, forever.
2. **Caddy in front of it**, not certbot. Caddy obtains and renews the certificate over
   HTTP-01 with no configuration beyond a three-line file, proxies WebSocket upgrades
   transparently, and reloads without dropping connections. Choosing it means the relay never
   learns what TLS is: `server.js` keeps listening on plain HTTP on `127.0.0.1:3000` and every
   line of ACME code this document previously specified goes unwritten.
3. Point `RelayClient` at `wss://play.skiprocking.ca` — an outbound connection, replacing
   `ws://localhost:3000`, and read it from a file so a hostname change does not need a rebuild
   (§12.6).
4. Delete the `cloudflared` spawn and the stderr scraping from `LocalServices` (§7), and point
   `JoinScreen`'s QR fetch at the service instead of `127.0.0.1`.

At the end of Phase 1: hosts install and play, guests scan and play, motion controls work,
and AIM/THROW route through the VPS. Latency is a round trip to the VPS, which is tolerable
because the two-channel split in PLAN.md was designed for exactly this — AIM is cosmetic and
lossy, and THROW is computed phone-side, so no amount of latency can change a throw's outcome.

## 5. Phase 2 — WebRTC DataChannel

An optimisation layered on Phase 1, not a replacement for it.

### The relay needs no changes to carry signalling

Worth checking before writing any: `server.js` already documents its routing as deliberately
generic — anything a player sends goes to the host with `playerId` stamped server-side,
anything the host sends goes to all players or to one when `targetPlayerId` is set. That is
exactly the shape SDP offer/answer and trickle ICE need. `RTC_OFFER`, `RTC_ANSWER` and
`RTC_ICE` route correctly through the existing `default:` branch with **zero relay changes**.

Two constants do need a look:

- `MAX_MESSAGE_BYTES` is 4096. A DataChannel-only SDP with no audio or video is typically well
  under that, and trickle ICE keeps candidates out of the offer — but verify against a real
  offer rather than assuming, and raise it if needed.
- `RATE_MAX_MESSAGES` is 60/second, which comfortably absorbs a trickle-ICE burst.

### Channel split

| Traffic | Transport | Why |
|---|---|---|
| Room code, roster, turn state, scores, `SPECTATE` | WSS to the VPS | Low rate, ordering and reliability matter, and it must work before any peer connection exists |
| `AIM` at 20Hz, `THROW` | DataChannel, direct | The only traffic where latency is felt |

### The mDNS question, which is the main technical risk

Browsers obfuscate their local IP addresses as randomised `.local` mDNS candidates (RFC 8828),
so a naive reading says the Unity side must run an mDNS resolver to reach them.

It probably does not, and the reason is worth writing down. **Unity is native and does not
obfuscate** — it advertises its real LAN address as a host candidate. The browser therefore
sends its connectivity checks straight to Unity, and Unity learns the phone's real address
from the *source address of those incoming checks*, as a **peer-reflexive candidate**. That is
ordinary ICE behaviour, not a workaround.

**Verify this on real hardware early**, before building anything on top of it. If it does not
hold, the fallback in §6 already covers it and Phase 1 still ships.

### No STUN, no TURN

ICE host candidates are gathered locally, so a LAN-only peer connection needs neither. That
keeps the external dependency count at exactly one — our own VPS — for the default case.
Remote play would need STUN (cheap, self-hostable) and possibly TURN (expensive), which is
why it stays out of scope until LAN play works.

### Unity side

Add `com.unity.webrtc` — the official package, native binaries, DataChannel support.
`RelayClient` keeps its `ClientWebSocket` (§ the existing DEVIATION note in the file: it ships
with .NET and needs no package), and a new `PeerLink` handles offer/answer/ICE and the
DataChannel, publishing received messages through the same path `RelayClient` uses today so
nothing downstream knows the difference.

## 6. Fallbacks, in order

Each degrades to the one below it, and each is honestly labelled on the TV rather than failing
silently:

1. **DataChannel** — normal operation.
2. **WSS relay** — if the peer connection does not establish within a few seconds, AIM and
   THROW route through the VPS. This is Phase 1, so it is proven code by the time Phase 2
   exists. Players notice nothing except slightly softer aim tracking.
3. **Touch controls on the LAN** — if the service is unreachable entirely (no internet, VPS
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

One more, and it is a real simplification: **the relay does not terminate TLS.** A reverse
proxy on the VPS handles the certificate, so `server.js` keeps serving plain HTTP exactly as
it does today and the whole `acme-client` dependency, the `setSecureContext` hot-swap and the
port-fallback ladder never get written (§4).

## 8. Host-setup audit

| # | Step | When |
|---|---|---|
| — | — | — |

**Empty, deliberately.** A host installs the game and presses Play. There is no credential, no
domain, no port, no record, no router page, and no firewall prompt — because nothing listens.

The one-time actions that remain belong to *us*, once, for the whole project: register the
domain (done), point one A record at the VPS, and let certbot run. Not per host, not per party.

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

Small enough to describe in a paragraph. A VPS running the existing relay plus certbot.
Gameplay traffic is peer-to-peer, so the only bandwidth is signalling and lobby state —
kilobytes per room — and PLAN.md's own figures (AIM at ~1.5 KB/s for the single active player,
4Hz spectator text) are the ceiling even in the Phase 1 fallback where everything routes
through it. A $5/month instance carries a great many parties.

**It is a single point of failure for every host**, which is the honest cost of requirement 2
and the reason §6 exists. Keep it boring: one process, one certificate, automatic renewal, and
no state worth losing — rooms are ephemeral and rebuilt on reconnect.

## 11. Self-hosted mode (opt-in, not built yet)

The whole ACME/DNS-01/self-signed design from the previous revision of this document remains
valid for a single known operator who would rather not depend on our service. It is now an
opt-in path rather than the default, and it is **not on the critical path** — build it only if
someone asks. Its full reasoning lives in this file's git history rather than being carried
here, because carrying it was what made the previous version misleading about what actually
ships.

## 12. Risks

1. **The mDNS / peer-reflexive assumption in §5** is the main unknown. Test it on an Android
   phone and this machine before committing to Phase 2. Phase 1 ships regardless.
2. **`com.unity.webrtc` is a native-binary dependency.** Confirm Windows standalone support on
   6000.3.10f1 and check what it costs at startup for a DataChannel-only use case — the
   package is built primarily for video streaming.
3. **Our service going down takes every host with it.** Mitigated by §6 tier 3, bounded by
   keeping the service trivial.
4. **Cost scales with popularity**, though from a very low base. Phase 1 fallback traffic is
   the thing to watch, since it is the only path that scales with players rather than rooms.
5. **iOS remains unverified** — no device, per PLAN.md's standing test-device policy. WebRTC
   on iOS Safari adds local-network-privacy behaviour that cannot be checked here.
6. **A shipped build hardcodes our hostname.** If the domain ever moves, old builds break.
   Cheap insurance: read the signalling URL from a config file next to the executable, with
   the hostname as the default.

## 13. Verification

- **Phase 1 end to end** — a real Android phone scans the QR, joins through
  `wss://play.skiprocking.ca`, `window.isSecureContext === true`, and a real swing produces a
  `THROW`. This is the acceptance test for shipping.
- **Zero-setup claim** — §8's test: unfamiliar machine, unconfigured network, press Play.
- **Certificate renewal** — force certbot to renew and confirm the relay picks up the new
  certificate without dropping open sockets.
- **Peer connection establishes on a real LAN** — Android phone to this PC, and confirm from
  ICE state which candidate pair won. A relayed or reflexive pair where a host pair was
  expected means §5's assumption is wrong.
- **Fallback ladder** — block UDP to force the DataChannel to fail and confirm AIM/THROW move
  to WSS with the TV labelling it; then take the VPS unreachable and confirm the touch path.
- **Signalling size** — capture a real SDP offer and check it against `MAX_MESSAGE_BYTES`.
- **Regression** — `cd relay && npm test` keeps passing. The relay is barely changing, which
  is the point.

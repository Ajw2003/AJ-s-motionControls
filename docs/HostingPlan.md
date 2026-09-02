# Hosting — one small service, peer-to-peer gameplay

> ## Status: the hosted service is gone. The relay runs locally.
>
> **This document's central conclusion — that somebody must operate a service — was correct
> for the requirements it was written against, and the project has since changed those
> requirements.** Read §1 and §2 as the reasoning they are; read this box for what actually
> runs.
>
> The Fly.io deployment has been torn down. `fly.toml`, `Dockerfile`, `.dockerignore`,
> `deploy/Caddyfile`, `deploy/rockskip-relay.service` and `relay/test-deployed.js` were deleted
> in `1cd7647` and never restored. The VPS migration path (Route B) is abandoned with it.
>
> **What runs now:** the relay and the controller page are served from the host PC over HTTPS,
> on a borrowed `*.local-ip.sh` wildcard certificate for a name that encodes the machine's LAN
> IP (`10.0.0.213` → `10-0-0-213.local-ip.sh`). `deploy/local-https.ps1` sets the whole thing
> up. Phone-to-PC WebRTC DataChannels still carry `AIM` and `THROW`, and both ends still
> recover from a wifi drop — [deploy/RECONNECT-TEST.md](../deploy/RECONNECT-TEST.md).
>
> **What that cost:** requirement 2 below ("hosting is: install and play") is no longer met —
> hosting now means running a script, and something listens on the host machine. Requirement 1
> is met only *on the local network*: a guest on mobile data can no longer join at all. Both
> were knowingly traded for removing the single point of failure and the hosting bill. The
> objections §2 raises against this design were never refuted — see
> [deploy/LOCAL-HTTPS-SPIKE.md](../deploy/LOCAL-HTTPS-SPIKE.md).
>
> **`skiprocking.ca` is not part of this.** It is registered and resolves to registrar parking;
> nothing in the codebase references it. It was a cosmetic vanity name for the Fly app.
>
> Runbook: [deploy/README.md](../deploy/README.md).

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
this — `isSecureContextOk()` in [controller/js/sensors.js](../controller/js/sensors.js).

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

**As built today** — everything is on one network, and there is no third party in any path
except DNS and the CA that signed the borrowed certificate:

```
   Phone (browser)                                        Unity 6 Desktop + relay
   ──────────────                                         ───────────────────────
   loads page ────── HTTPS ──────────────────────────────▶ 10-0-0-213.local-ip.sh:8443
   signalling ◀───── WSS ───────────────────────────────▶  relay + page (same process)
        │                                                                │
        └──────────── WebRTC DataChannel, direct over LAN ───────────────┘
                      AIM @20Hz · THROW · no certificate, no server
```

<details>
<summary>The hosted design this replaced, kept because §§4–14 are still written against it</summary>

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

</details>

What survived the change, and what did not:

- **The host no longer connects outbound — it listens.** This was the property that made
  requirement 2 achievable, and giving it up is what brings back the firewall prompt (§8). What
  it does *not* bring back is UPnP, port forwarding, CGNAT and the router page: the phone is on
  the same LAN, so nothing has to traverse NAT.
- **The certificate is borrowed, not held.** `local-ip.sh` publishes a real Let's Encrypt
  wildcard for `*.local-ip.sh` and resolves IP-encoded names back to the address they encode.
  There is still **no DNS API credential anywhere in the system** — the entire Porkbun module
  and secret-distribution problem stays dead — but the private key is *published*, so this is
  sound for a machine on your own network and unsound for anything distributed. §2's certificate
  broker row is the version of this that would scale, and it is still capped at 50/week.
- **Gameplay is peer-to-peer.** Unchanged, and now belt-and-braces: the relay fallback is also
  local, so a failed DataChannel costs nothing measurable. That is a hazard as much as a
  benefit — it means a silent WebRTC failure is invisible. §5's `N/M direct` readout is the only
  thing that catches it.

## 4. Phase 1 — the relay path (built)

**Deliberately built before any WebRTC.** It is a complete, shippable product on its own, it
was mostly existing code, and it is the fallback that makes Phase 2 safe to attempt: if a peer
connection fails to establish, traffic falls back to this path rather than to nothing.

What shipped:

1. [relay/server.js](../relay/server.js) serves both the WebSocket and the controller page —
   `STATIC_DIR` resolves as `../controller`, so the two directories have to sit side by side.
   It was deployed to **Fly.io**, which terminated TLS and renewed the certificate itself, so
   every line of ACME code this document once specified went unwritten.
   **Now:** it runs on the host PC and terminates TLS itself, via the env-gated branch that
   builds an `https` listener when `TLS_CERT` and `TLS_KEY` are set. Still no ACME code — the
   certificate is downloaded ready-made from `local-ip.sh` rather than requested.
2. `server.js` binds `BIND_HOST || '0.0.0.0'`. The default mattered because Fly's proxy reached
   the process from outside its VM, so loopback would have refused every connection. **It still
   matters, for a different reason:** the phone reaches the relay across the LAN, so binding
   loopback would now break the phone rather than the proxy. It also means a new interface —
   plugging in, or joining a hotspot — is picked up live without a restart.
3. `RelayClient` connects out to the service, and **derives `PublicOrigin` and `JoinUrlFor()`
   from the socket URL** rather than taking them as separate configuration — which is what
   makes it structurally impossible for the page and the socket to drift onto different hosts.
   A `relay-url.txt` beside the executable overrides the address without a rebuild (§12.6).
4. `cloudflared`, the `TunnelUrlPattern` regex and the stderr scraping are deleted from
   `LocalServices`, which is now a development-only local relay, **off by default**.
   `JoinScreen` is driven by the relay connection rather than by `LocalServices`, which no
   longer exists on a normal host.

The result, as designed: hosts install and play, guests scan and play, motion controls work,
and AIM/THROW route through the service. That last part was the only cost, and it was tolerable
because the two-channel split in PLAN.md was designed for exactly this — AIM is cosmetic and
lossy, and THROW is computed phone-side, so no amount of latency can change a throw's outcome.

**As it actually runs now:** the host runs `deploy/local-https.ps1` first, and only guests on
the same network can join. The relay-path latency this section worries about is gone, because
the relay is on the same machine.

**One process, one room map** (§12.7) — the reason the deployment had to be pinned to a single
machine. Locally that constraint is satisfied by construction: there is only ever one relay, and
`LocalServices` reuses one that is already answering rather than starting a second. The probe
that used to check this, `relay/test-deployed.js`, was deleted with the Fly config;
`cd relay && npm test` is what remains.

## 5. Phase 2 — WebRTC DataChannel (built)

An optimisation layered on Phase 1, not a replacement for it.

**What shipped:** [Assets/Scripts/Net/PeerLink.cs](../Assets/Scripts/Net/PeerLink.cs) holds one
peer connection per phone; [controller/js/peer.js](../controller/js/peer.js) is its other half.
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
`Assets/Scripts/Net/PeerLink.cs` and [controller/js/peer.js](../controller/js/peer.js) — and both
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

1. Start the relay — `pwsh ./deploy/local-https.ps1`. The page is served from `controller/` on
   disk, so a hard-reload on the phone is all it takes to pick up a changed `peer.js`.
2. Press Play with **Use Peer Connections** ticked on `Bootstrap` (the default).
3. Join from an Android phone on the same Wi-Fi. The phone's status line should change from
   `Connected as AJ` to `Connected as AJ · direct` within a couple of seconds.
4. **F1 on the TV** shows `Input  1/1 direct`. That is the check that matters — the phone
   saying `direct` only proves its own channel opened.
5. Throw. **This step no longer proves anything and should not be trusted.** It was written
   when the fallback was a round-trip to Toronto, so "tighter" was a real signal. The fallback
   is now a relay on the same machine, so both transports feel identical — step 4's `N/M direct`
   is the only evidence that survives.
6. **Prove the fallback**, which is the part most likely to rot unnoticed: rejoin with
   `?direct=0` on the URL and confirm the game plays identically, with F1 reporting `0/1
   direct`. A throw must behave the same on both transports.

## 6. Fallbacks, in order

Each degrades to the one below it, and each is honestly labelled on the TV rather than failing
silently:

1. **DataChannel** — normal operation.
2. **WSS relay** — if the peer connection does not establish within a few seconds, AIM and
   THROW route through the relay socket. This is Phase 1, so it is proven code by the time
   Phase 2 exists. Players notice nothing except slightly softer aim tracking — and now, with
   the relay on the same machine, they may notice nothing at all, which is why §5's `N/M direct`
   readout matters more than it used to.
3. **Touch controls on the LAN** — still unbuilt, and its trigger has changed. It was for the
   hosted service being unreachable; there is no hosted service now, so the failure it covers is
   the *certificate* path breaking — `local-ip.sh` down, or the borrowed cert expired. The relay
   would serve plain HTTP on the LAN and the controller would switch to touch aiming, reusing the
   `computeSimulatedThrowDescriptor` seam already in
   [controller/js/throw.js](../controller/js/throw.js). No certificate is possible here, so no
   motion sensors — but a party still happens.

   **This tier is now more load-bearing than it was**, because local hosting depends on a third
   party (`local-ip.sh`) that nobody here operates, in the one path that cannot be worked
   around. It is still unbuilt.

   **Two failures that used to belong here have been promoted out of it.** Router DNS rebinding
   protection has not been observed in practice, and *reachability* failures — AP isolation,
   guest-network segmentation, guests unable to join the venue's network — are answered by
   hosting a hotspot from the PC, which keeps motion controls rather than degrading to touch.

   **And as of 2026-08-20 this tier may be largely obsolete.** A spike
   ([deploy/spike/](../deploy/spike/)) proved the PC can answer DNS for its own hotspot clients:
   a phone resolved `play.local-ip.sh`, a name no public resolver answers. With the certificate
   already cached for up to 90 days, **the host needs no internet at play time** — which is
   precisely the case this tier existed to cover, and it is covered *with* motion controls
   rather than by degrading to touch. What is still unproven is whether Windows will start
   tethering with no network attached at all. See
   [deploy/LOCAL-HTTPS-SPIKE.md](../deploy/LOCAL-HTTPS-SPIKE.md) *Results — 2026-08-20*. Tier 3 is
   now specifically the **certificate** fallback, which is a narrower and more honest job than
   the catch-all it used to be.

## 7. What this deletes

The previous designs accumulated machinery that this one does not need. Removing it is most of
the implementation work:

- `cloudflared` spawning, the `TunnelUrlPattern` regex, and the stderr-scraping `Update()`
  loop in [Assets/Scripts/Net/LocalServices.cs](../Assets/Scripts/Net/LocalServices.cs). The join
  URL is now a constant.
- The entire DNS-01 / Porkbun / API-token design: `hosting.config.json`, the credential
  handling, the env-var fallback, the address discovery, the A-record upsert. HTTP-01 on a
  machine with a public IP needs none of it.
- UPnP / NAT-PMP port mapping, CGNAT detection, NAT hairpin handling. All artefacts of
  inbound connections, and there are none.

**Done:** `relay/hosting.config.json`, `relay/hosting.config.example.json` and their
`.gitignore` entries are deleted. They described a design that no longer exists, and a config
file nothing reads is worse than no config file at all.

One more, and it was a real simplification while it lasted: **the relay did not terminate
TLS** — Fly's proxy held the certificate, so `server.js` served plain HTTP.

**That one has reversed.** The relay terminates TLS itself now, through the env-gated `https`
branch. What did *not* come back is the machinery this section is really about: there is still
no `acme-client` dependency, no `setSecureContext` hot-swap and no port-fallback ladder,
because the certificate arrives ready-made from `local-ip.sh` instead of being requested.
Terminating TLS turned out to be the cheap half; obtaining the certificate was always the
expensive half, and that is still avoided.

## 8. Host-setup audit

**This table used to be empty, deliberately.** A host installed the game and pressed Play:
no credential, no domain, no port, no record, no router page, and no firewall prompt, because
nothing listened. That was requirement 2 met exactly.

**It is no longer empty, and that is the clearest single measure of what the pivot cost:**

| # | Step | When |
|---|---|---|
| 1 | Run `pwsh ./deploy/local-https.ps1` | Every session — it holds until Ctrl+C |
| 2 | Allow `node` through the firewall | First run on a machine, per network profile |
| 3 | Pick the right interface with `-Ip` | Only if the machine has several plausible NICs |
| 4 | Re-run after the LAN IP changes | Whenever DHCP moves the machine |
| 5 | Get everyone onto the same network | Every party — cellular guests cannot join at all. A PC-hosted hotspot covers venues whose network is unusable, so this is proximity rather than a specific network |

None of these are per *guest*, which is what §9 protects and what still holds. They are per
host, and the host is currently one person who wrote the game. **Requirement 2 is not met, and
this table is where that shows up** — a stranger could not install this and host a party.

The one-time domain actions that used to be listed here (point a CNAME at the Fly app, run
`fly certs add`) are moot: the app is gone, and `skiprocking.ca` is parked.

**The test that catches regressions** has changed shape with everything else. It used to be:
run a build on a machine that has never seen the project and host a game, where any human input
beyond pressing Play is a regression. That test now fails by design. What is worth keeping is
the weaker version — run it on a network nobody configured for it, a phone hotspot will do, and
count the steps against the table above. Any *new* row is a regression.

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

**There is no service.** This section described a single `shared-cpu-1x` Fly machine running
the relay behind Fly's TLS proxy, with a VPS-and-Caddy variant as the migration path. Both are
gone: the Fly app was torn down, and Route B was abandoned rather than taken.

The reasoning it recorded is worth keeping, because it is what the pivot traded away and what
would have to be rebuilt if the project ever wants remote guests again:

- Gameplay traffic is peer-to-peer, so the only bandwidth was signalling and lobby state —
  kilobytes per room. PLAN.md's figures (AIM at ~1.5 KB/s for the single active player, 4Hz
  spectator text) were the ceiling even in the Phase 1 fallback where everything routed through
  it. A $5/month instance carried a great many parties. **Cost was never the problem**, which is
  worth remembering before assuming the pivot saved anything material.
- **It was a single point of failure for every host** — the honest cost of requirement 2, and
  the reason §6 exists.

That last point is the one that actually reversed. There is no shared failure point now; there
is a per-host one instead, and it depends on `local-ip.sh` staying up (§6, tier 3). The failure
did not go away, it got distributed and re-vendored.

## 11. Self-hosted mode

Two different things get called "self-hosted" here, and the pivot inverted which one exists.

**Running the relay on a server you control is abandoned, not built.** Route B — a VPS with
Caddy in front of the relay — was a complete runbook with a working `Caddyfile` and systemd
unit. Both files were deleted in `1cd7647` and the route is not a migration path any more. If
remote guests ever become a requirement again, this is the design to go back and read in git
history; it needed no game-side changes, because the relay is a stock Node process with no
host-specific code in it.

**Per-host self-hosting is now what ships — for exactly one host.** The host machine serves
the page under a certificate that is not ours, borrowed from `local-ip.sh`, depending on no
service we operate. That is what §2 rejected on paper, and it works: see
[deploy/LOCAL-HTTPS-SPIKE.md](../deploy/LOCAL-HTTPS-SPIKE.md).

**What is still not built is per-host self-hosting for an *arbitrary* host**, which is the
thing §2's objections were actually about. The blocker is unchanged and unaddressed: a shared
wildcard means shipping a published private key inside every distributed binary, and the
per-host CSR that fixes it runs into Let's Encrypt's 50-certificates-per-registered-domain-per-week
cap. Nothing about adopting the local path solved that — it just stopped mattering, because
there is currently one host and they build from a checkout.

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
   The hosted deployment had to be pinned to exactly one instance (`fly scale count 1`), and
   `relay/test-deployed.js` probed for it on every run. **Local hosting satisfies this by
   construction** — one machine, one relay, and `LocalServices` reuses one that is already
   answering rather than starting a second. The risk has not been fixed, it has been made
   unreachable; it returns in full the moment anything is deployed behind a load balancer
   again. Nothing is lost either way: a restart drops every room regardless, so there is no
   availability that a second instance would preserve. Sharing room state across instances
   would mean Redis and is not worth it at this scale.

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
  `wss://<dashed-ip>.local-ip.sh:8443`, `window.isSecureContext === true`, and a real swing
  produces a `THROW`. This is the acceptance test. **Done** — exercised in a 5-player session
  across iOS and Android.
- ~~**Zero-setup claim**~~ — §8's test no longer applies: setup is not zero any more, and the
  table there is what replaced it.
- **Certificate expiry** — this was Fly's problem and is now ours. The borrowed `local-ip.sh`
  certificate is downloaded fresh on every run and its expiry and SAN are checked before the
  relay starts, so the failure mode is a refusal at setup rather than a lapse mid-party. What
  is *not* covered: a certificate that expires while a session is already running.
- **Peer connection establishes on a real LAN** — Android phone to this PC, and confirm from
  ICE state which candidate pair won. A relayed or reflexive pair where a host pair was
  expected means §5's assumption is wrong.
- **Fallback ladder** — block UDP to force the DataChannel to fail and confirm AIM/THROW move
  to WSS with the TV labelling it. The tier-3 half of this test cannot be run: touch controls
  are still unbuilt (§6).
- **Signalling size** — capture a real SDP offer and check it against `MAX_MESSAGE_BYTES`.
- **Regression** — `cd relay && npm test` keeps passing. The relay is barely changing, which
  is the point.

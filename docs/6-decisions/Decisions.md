# Decisions

The paper trail: what was decided, when, why, what alternatives were rejected, and what it
superseded. This log only grows — an entry is never rewritten or deleted. The one allowed edit is
flipping a `Status` line to `Superseded`, with a pointer, when a later entry replaces it. Newest
entry at the top.

For decisions with lengthy supporting evidence, this entry summarizes and points at the full
document rather than duplicating it.

---

## 2026-10-09 — HTTPS on the hotspot: a shared, hardened certificate for `pad.skiprocking.ca`

**Context.** #13. Phones only give motion sensors to HTTPS pages. aj's constraints:

- the phone scans two QR codes and does nothing else (no app, no install);
- no new third-party service, no always-on server of ours, and no running cost;
- internet is acceptable on first setup only.

Rock Skipping's approach publishes one certificate's key, and anyone holding a published key can
revoke the certificate. Full research: `docs/Findings-CertificateResilience-2026-10-09.md`.

**Decision.** aj, 2026-10-09:

- **Approach:** one shared public certificate, hardened. Each launcher downloads the certificate
  and key at runtime, never shipped in a build, and caches them for offline play.
- **Name:** `pad.skiprocking.ca`, with its own certificate and key, separate from Rock Skipping's
  `play.skiprocking.ca`.
- **Where it runs:** the public `Ajw2003/rockskipping-cert` repo. It hosts the published
  certificate and a watch job built on CannaScraper's self-chaining job plus watchdog. The job
  checks the revocation list about hourly and reissues with a new key when the certificate is
  revoked or near expiry.
- **First build:** runtime download and cache; the hourly revocation watch; three rotating spare
  names; and download-count nudges from launchers. Nudges only make the job run its own checks
  sooner, and a failure it can't verify opens at most one issue a day for aj. The plain-connection
  fallback (buttons and sticks with motion off) is deferred to a follow-up issue.

**Why.**

- It is the only option that meets all of aj's constraints.
- A name of its own costs nothing and stops one product's revocation taking down the other.
- The public repo makes the round-the-clock watchdog free. In a private repo it would far exceed
  the free minutes.

**Rejected:**

- **A per-PC certificate through DuckDNS:** it adds a third party and a sign-in on every PC.
- **The PC as its own certificate authority:** every phone would need a certificate install.
- **A new domain:** a yearly fee.
- **Keeping `play.skiprocking.ca`:** the two products would share every failure.
- **Hosting the job in the private RockSkipping repo:** the watchdog would use far more than the
  free minutes.

**Known risk accepted:** someone determined can still revoke each new certificate. The watch job
turns that into an outage of roughly an hour per attempt, limited by Let's Encrypt's 50
certificates a week per domain. Phones offline reportedly don't check revocation; this is
unverified on real phones.

**Status.** Standing.

## 2026-10-09 — The tiers now describe AJ's Motion Controls; the game's tier pages are archived

**Context.** After the fork, every tier described Rock Skipping. Seven GitHub issues (#2-#8,
under #1) now define the product's work.

**Decision.** New tiers 1, 2, 3 and 5 and `docs/README.md`, with one roadmap milestone and one
plan in `docs/plans/` per issue. The game's versions of those five pages moved to
`docs/archive/rock-skipping/`. The systems index now separates systems this product reuses
(motion pipeline, phone controller, networking, hosting) from game-only ones, which stay in
`4-systems/` because the game's code is still in the repo. Planned systems get no doc until built.

**Why.** Archiving rather than deleting keeps every link working and the game's record readable.
Leaving game-only systems docs in place avoids pre-empting the open decision on removing the game
code. Writing systems docs for unbuilt code was rejected because it would be invented.

**Status.** Standing.

## 2026-10-08 — Phone-as-emulator-motion-controller is its own product, forked from Rock Skipping

**Context.** Rock Skipping's phone controller already does the hard parts of a general motion
controller: no-install phone page, iOS motion permission, cross-platform sensor normalisation
(`controller/js/sensors.js`), local HTTPS and the offline hotspot. Emulators accept motion over
the DSU/CemuHook UDP protocol (port 26760).

**Decision.** Start a separate repo, Ajw2003/AJ-s-motionControls, with Rock Skipping's full
history up to `main` at `3c68ac2`. Rock Skipping's own repo is unchanged.

**Why.** A different product with different users; keeping it out of the game's code stops either
one's needs leaking into the other. Keeping the history keeps the reasoning behind the reused
phone code (`docs/4-systems/MotionPipeline.md`, `Networking.md`) traceable.

**Not yet decided.** What of the game (Unity project, relay game rooms) gets removed, and when.
Known gap for the new use: the game sends a processed 20 Hz aim value plus one throw event, while
emulators need raw gyro and accelerometer at the browser's full motion rate.

**Status.** Standing.

## 2026-10-03 — Long reasoning lives in the systems docs, not in code comments

**Context.** 234 comment blocks of 500+ characters were spread over 94 source files, and the docs lived in an older five-tier layout.

**Decision.** Docs moved into the six numbered tiers. Every long block moved verbatim into the `docs/4-systems/` doc that owns it, with a `doc-ref <id> <path>` pointer left in the code. Three systems docs are new: Audio, Presentation, Verification, plus PhoneController. Plan: `docs/plans/comment-harvest-worklist.txt`; tracking issue #82.

**Why.** The code stays lean and the reasoning stays checkable: `docref check` reports every pointer resolving, and the code was verified unchanged apart from comments. Moved text was kept verbatim, then sorted into each doc's How it works / Invariants / Traps sections (#87).

**Status.** Standing.

## 2026-10-02 — Share the host's internet with guests by default

**Context.** Guests were isolated (forwarding off). Android only treats the hotspot as a normal
network when it has real internet, so with isolation phones showed "no internet" and did not
rejoin by themselves. Three fences meant to block the LAN while sharing failed on this machine (a
firewall rule, a WFP filter at the forward layer, a WFP filter at the inbound-packet layer), so
sharing is unfenced. Evidence:
[`Findings-HotspotForwardingAndInternet-2026-10-01.md`](../Findings-HotspotForwardingAndInternet-2026-10-01.md).

**Decision.** Ayden, 2026-10-02: share the host's internet with hotspot guests by default. Guests
reaching the home router/LAN is no new exposure, since party guests already use the home Wi-Fi;
a sensitive venue can run the host offline. A lobby setting "Share internet" turns it off from
the next hotspot start. This supersedes the guest-isolation-by-default stance. No earlier entry in
this log states it, so there is no Status line to flip; it lived in
[`4-systems/HostingAndCertificates.md`](../4-systems/HostingAndCertificates.md) and
[`SecurityReview.md`](../SecurityReview.md), both updated.

**Why.** Measured on a Samsung S26: with sharing, it rejoined by itself within 5-15 s when no
other known Wi-Fi was in range, and stayed on home Wi-Fi when that was in range. Built:
`relay/dns.js` (`fallbackNames`, `fallbackMs`) and `relay/server.js` (`SHARE_INTERNET=1`) forward
other names to the host's first usable IPv4 resolver, and forward the phones' connectivity-probe
names too, answering them locally only if upstream is silent for 1.5 s; the game name is always
answered locally. `hotspot/hotspot-control.ps1 -ShareInternet` skips the forwarding-disable task
and reports `shared` (measured: Windows keeps forwarding Enabled by itself).
`MatchSettingsStore.shareInternet` (default true), the `MatchSettings` toggle, `LocalServices`
and `HotspotService` carry it through.

**Alternatives rejected.** Keep isolation as the default: phones do not rejoin by themselves.
Fence the LAN while sharing: three attempts failed (above); no positive control was run, so this
shows only that those filters did not work here.

**Status.** Standing. Not tested yet: a full Play-mode session with sharing on and a phone;
iPhones; other Android brands; a host whose upstream is Wi-Fi; rejoin after days.

---

## 2026-09-08 — Stop the relay/direct flapping by deleting the WebRTC transport

**Context.** The Unity console showed `[PeerLink] <id>: DIRECT` interleaved with join logs about
twice a second, mid-match. Root cause: a phone re-joining the room continuously, with the WebRTC
peer link faithfully renegotiating from scratch each time — the transport was the symptom, not
the cause. See [`archive/plans/remove-direct-transport.md`](../archive/plans/remove-direct-transport.md).

**Decision.** Fix the join ping-pong, then delete the WebRTC DataChannel transport (`PeerLink.cs`,
`com.unity.webrtc`) entirely rather than keep maintaining a peer-to-peer path nothing required.

**Why.** The relay-only path already carried every message the game needs. The direct transport
existed for latency that was never measured as a problem, and it was the thing renegotiating (and
therefore logging) every time a phone rejoined — removing it removes the failure mode, not just
the symptom.

**Status.** Standing. Executed — `PeerLink.cs` and the WebRTC package are gone.

---

## 2026-09-03 — Replace local-ip.sh with a single fixed domain, Hotspot-only

**Context.** `local-ip.sh`, the third-party service the certificate hostname was borrowed from,
went permanently dead at the DNS layer — no drop-in replacement exists (`local-ip.co`,
`traefik.me`, `sslip.io`, `nip.io` all checked and rejected; see
[`plans/magic-domain-replacement.md`](../plans/magic-domain-replacement.md)). A fresh install could no
longer get a certificate at all.

**Decision.** Deprecate LAN mode (it required the phone to be on the host's network, which
Hotspot already achieves with the host as the AP) and issue one **single-name** certificate for a
fixed hostname (`play.skiprocking.ca`), renewed by a GitHub Actions job, instead of borrowing a
name and cert from a third party.

**Why.** LAN mode was the only thing forcing always-on public DNS infrastructure — it needed a
public resolver to map a varying, IP-encoded hostname back to a DHCP address. Windows ICS always
puts the hotspot at the fixed address `192.168.137.1`, so once LAN is gone the address never
varies and a wildcard/dynamic-name scheme is no longer needed at all. This gets to zero public
servers and near-zero recurring cost, at the price of losing LAN mode (not seen as a real loss —
see the 2026-08-21 entry below on why Hotspot was already preferred).

**Status.** Standing. Executed and implemented in `relay/dns.js`, `CertificateProvider.cs`, and
`.github/workflows/renew-cert.yml`. Superseded the certificate design described (now stale) in
[`Decision-OfflineHosting.md`](Decision-OfflineHosting.md) and
[`OfflineSetup.md`](../OfflineSetup.md) — both carry an update notice pointing here.

---

## 2026-08-21 — Use Mobile Hotspot, not Wi-Fi Direct, for offline hosting

**Context.** The game needs the host PC to build a local network reachable by phones with zero
internet, zero router, and zero manual configuration. Wi-Fi Direct looked blocked by a
"host must stay attached to some network" ceiling. Full investigation and measured evidence in
[`Decision-OfflineHosting.md`](Decision-OfflineHosting.md).

**Decision.** Use Windows Mobile Hotspot (tethering), not Wi-Fi Direct.

**Why.** The apparent ceiling was a bug in this project's own code, not a Windows limitation:
`hotspot-control.ps1` asked `GetInternetConnectionProfile()`, which returns null by definition
when there is no internet. Switching to `GetConnectionProfiles()` showed tethering starts fine
from *any* profile, including ones with no connectivity at all (an unjoined remembered Wi-Fi
network, a disconnected Bluetooth PAN) — confirmed live, unelevated, `MaxClientCount 8`. Mobile
Hotspot also comes with DHCP and a fixed, predictable gateway address for free, which Wi-Fi Direct
does not.

**Status.** Standing. Implemented in `hotspot/hotspot-control.ps1`.

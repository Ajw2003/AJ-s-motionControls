# Decisions

The paper trail: what was decided, when, why, what alternatives were rejected, and what it
superseded. This log only grows — an entry is never rewritten or deleted. The one allowed edit is
flipping a `Status` line to `Superseded`, with a pointer, when a later entry replaces it. Newest
entry at the top.

For decisions with lengthy supporting evidence, this entry summarizes and points at the full
document rather than duplicating it.

---

## 2026-09-08 — Stop the relay/direct flapping by deleting the WebRTC transport

**Context.** The Unity console showed `[PeerLink] <id>: DIRECT` interleaved with join logs about
twice a second, mid-match. Root cause: a phone re-joining the room continuously, with the WebRTC
peer link faithfully renegotiating from scratch each time — the transport was the symptom, not
the cause. See [`archive/plans/remove-direct-transport.md`](archive/plans/remove-direct-transport.md).

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
[`plans/magic-domain-replacement.md`](plans/magic-domain-replacement.md)). A fresh install could no
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
[`OfflineSetup.md`](OfflineSetup.md) — both carry an update notice pointing here.

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

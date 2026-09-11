# Findings: audit of the pasted "Risks, Limitations & Mitigations" matrix

Dated 2026-09-11. This matrix was supplied for review — origin unclear (not authored in this
repo; reads differently in tone from [06-risks.md](06-risks.md), which is deliberately
plain-spoken and cites file paths). It describes some mechanisms this project does not use, and
claims automatic behaviors that are not implemented. Do not paste it into an application as-is —
see [../README.md](README.md)'s "easy to overstate and will be checked" warning. This doc is
findings only; no source was changed.

Verdict per item, all four checked directly against the codebase (branch `NetworkFixes`):

---

## 1. RF interference / dual-band failover + channel hopping — mostly not real

**Claimed:** dynamic DFS/6GHz channel forcing via "Windows Wi-Fi Direct APIs", RSSI monitoring,
automatic mid-session channel switching away from congestion.

**Actual:** `hotspot/hotspot-control.ps1` picks **2.4 or 5 GHz once, at hotspot start-up**, via
`Windows.Networking.NetworkOperators.TetheringWiFiBand` — matching whichever band the host's own
Wi-Fi station link is already on, not the least-congested one. Windows gives no API to read back
the actual channel chosen, so there's no DFS forcing and no 6GHz (Mobile Hotspot doesn't expose
6GHz). `Assets/Scripts/Net/HotspotService.cs:186-262` fires this once; `RadioSharingWarning`
there only warns the requested band *might* be shared, it doesn't detect or react to it.
`tools/measure-hotspot-jitter.ps1` is a **manual** diagnostic a person runs by hand — not
automatic remediation.

**Wi-Fi Direct specifically was investigated and not used** — `docs/archive/` history shows a
`Findings-WiFiDirect.md` existed and was deleted (per [03-gap-analysis.md](03-gap-analysis.md)
G8); the current hotspot path uses the Tethering API instead. Citing Wi-Fi Direct as the
mechanism is describing an abandoned approach.

**No RSSI monitoring, no packet-loss-triggered switching, no live channel hopping exist
anywhere.** ~20% covered: band choice exists, nothing else does.

**Not currently tracked as a risk** in `06-risks.md` — closest is R4 (Windows-only host
integration), which is about portability, not RF congestion. This is a real gap in the risk
register if this matrix's scenario (convention-hall RF density) is something you actually intend
to claim resilience to.

---

## 2. Battery drain via sensor polling — mostly not real, one part works backwards

**Claimed:** throttled sensor sampling matched to game tick rate (60Hz), a sleep/wake handshake
over WebSocket, OLED true-black power-saving UI.

**Actual:** `controller/js/sensors.js` reads `devicemotion`/`deviceorientation` at native ~60Hz
with **no throttling at the read layer**. There *is* a 20Hz throttle in `controller/js/app.js:
201-203`, but it throttles the **outbound network send**, not sensor sampling — and per
`docs/Findings-NetworkAndThrowStability.md` finding B1, the backpressure-aware send path
(`sendIfClear` in `net.js:222-227`) exists but is **never called**, so even that throttle isn't
doing what it's designed to. `app.js:791-799` calls `requestWakeLock()` — this **keeps the
screen on**, which is the opposite of battery saving. No dark-mode/OLED UI and no sleep/wake
handshake exist anywhere in `controller/`.

~10% covered, and the one thing that exists (wake lock) actively works against the claim.

**Not tracked** in `06-risks.md` — R2 covers sensor-API *availability* risk (OS restricting
`DeviceMotionEvent`), which is a different concern than battery drain.

---

## 3. Local TLS/cert provisioning — the part that's actually built, but the matrix describes it wrong

**Claimed:** a local Certificate Authority, an "automated QR provisioning script" issuing
short-lived certs natively trusted by mobile secure-origin rules.

**Actual, and this is the most mature system in the project:** there is **no local CA**. Certs
come from a real, publicly-trusted CA (Let's Encrypt) against a domain the project owns
(`MagicDomain.HostName`, e.g. `play.skiprocking.ca`), fetched and cached by
`Assets/Scripts/Net/CertificateProvider.cs:94-193`, renewed automatically via
`.github/workflows/renew-cert.yml`, with a seed cert shipped in `StreamingAssets` for offline
first-run. The host resolves that real hostname to the fixed hotspot IP `192.168.137.1` via a
local DNS responder (`docs/systems/HostingAndCertificates.md`) — that's how it avoids browser
cert warnings, not a "192.168.x.x + local hostname mapping matching browser local-network
rules" as the matrix describes. There genuinely are **two QR codes** (Wi-Fi join, then game URL)
as claimed — that part's accurate.

~75% covered as *a* solution, but the matrix's technical narrative (local CA, short-lived certs)
doesn't match what's built (public CA, ~90-day certs, no local CA at all) — this needs rewriting
to match reality, not more engineering. This is already the subject of extensive, honest
treatment: `06-risks.md` R1 (shrinking cert lifetimes), R6 (published key looks alarming), R7
(CA/domain single point of failure), and gap G5. Use those, not this matrix's version.

---

## 4. Backgrounding / reconnect — mostly real, but the specific claim about players is false

**Claimed:** Unity-side "Instant Reconnect" with a 3-5 second grace period; phone recovers its
Player ID on focus without a rescan.

**Actual:** substantial and mostly working. `controller/js/identity.js` persists a `deviceId` in
`localStorage` as the wire `playerId`, surviving reload. `net.js` does exponential-backoff
reconnect and tolerates a **45-second** rejoin window client-side. `relay/server.js:191-237`
holds a room open and reclaims a returning player by `playerId`. Server-side WS heartbeat exists
(`server.js:804-814`), though the phone client never sends its own app-level PING (documented
gap, finding B7).

**But the specific "instant reconnect, 3-5s grace" claim is wrong for the case the matrix
describes (screen lock / backgrounding drops a player's socket):** per
`docs/Findings-NetworkAndThrowStability.md` finding A2 (CONFIRMED), the relay removes a
disconnected **player** from Unity's roster immediately, with **no grace period** — hosts get
one, players don't — which can hand the active turn to someone else mid-throw before the phone's
own 45s-window reconnect completes. **A2 itself carries no "Status: fixed" annotation in that
doc — it is still open.** (Correction to an earlier draft of this section, which wrongly implied
A2 might be among the fixed findings: only A5, A6, C1, C4, D1 and D2 are marked fixed. A2, A3,
A4, C2 and C3 are not.) There is a proposed fix — "Group 4" in
`docs/plans/Impl-NetworkAndThrowFixes.md:222`, a 20-second `disconnectedTurnGraceSeconds` on the
Unity/TurnManager side — but it is a plan, not shipped code, and its value (20s) doesn't match
the matrix's claimed "3-5 second" grace window either.

~65% covered. The infrastructure for what the matrix claims exists; the specific player-grace-
period behavior it asserts does not, and is a known, already-diagnosed, already-planned-but-
unbuilt bug rather than an unknown risk.

---

## Bottom line

| # | Claim | Reality | Est. done | Already tracked in 06-risks.md? |
|---|---|---|---|---|
| 1 | RF/DFS/channel hopping | One-time band pick only, no RSSI/DFS/hopping | ~20% | No — gap |
| 2 | Sensor/battery throttling | No throttle at sensor layer; wake-lock fights it | ~10% | No — gap |
| 3 | Local CA + short-lived certs | Real public CA, ~90-day certs, no local CA | ~75% (wrong mechanism described) | Yes — R1/R6/R7 |
| 4 | Instant reconnect, 3-5s grace | Reconnect infra real; player grace period is a known bug (A2), not implemented | ~65% | Partially — separate findings doc, not in 06-risks.md |

Recommendation: don't submit this matrix. Items 3 and 4 already have honest, evidence-backed
material to draw from (`06-risks.md`, `Findings-NetworkAndThrowStability.md`). Items 1 and 2
either need real engineering work before claiming them, or need to be dropped/reframed as
future work rather than present capability — reviewers checking claims will find the gap
described above (Wi-Fi Direct was tried and abandoned; wake-lock keeps the screen on).

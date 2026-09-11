# Risks

Write the risk section *before* the reviewer finds the risks. An application with a candid,
specific, mitigated risk register reads as competent; one with no risks reads as naive, and one
with generic risks ("scope creep", "technical challenges") reads as filler.

Each risk below is stated as a reviewer would state it, then answered.

---

## R1 — Certificate lifetimes are shrinking, and the offline window shrinks with them

**Severity: high. Likelihood: certain.**

The offline claim depends on serving a publicly-trusted certificate locally. CA/Browser Forum
maximum lifetimes are contracting — 398 days today, stepping toward roughly 47 days by 2029.
Every reduction shortens how long an installed host can go without seeing the internet, and
tightens the renewal cadence the pipeline must sustain.

**Mitigation.** This is the funded research question, not an unmanaged risk — WP2 exists to
answer it, and it evaluates three independent approaches precisely so the project does not
depend on any one surviving. The current design already degrades to a bounded window rather
than to failure, and hardening that degradation to be *legible* to the player (naming the cause
and the remedy rather than surfacing a networking error) is explicit WP2 scope.

**What is honest to say:** the window narrows over the funded period. The project's answer is
to make renewal opportunistic and cheap enough that any online moment suffices, and to publish
what it learns so other local-first applications are not each solving this alone.

---

## R2 — Platform APIs could restrict sensor access further

**Severity: high. Likelihood: low-moderate.**

The whole approach rests on `DeviceMotionEvent` and `DeviceOrientationEvent` remaining available
to web pages. iOS 13 already added a permission gate. A future OS could restrict motion access
further, require an installed app, or tighten what counts as a secure context.

**Mitigation.** The normalization and gesture layers (WP3) are the durable asset and are
independent of *how* samples arrive; a native shim would replace the acquisition layer without
touching them. The protocol (WP4) likewise does not care what produced a quaternion. Concretely:
the project survives this by losing the no-install property, not by losing the technology.

**What is honest to say:** this risk cannot be eliminated by any design, only contained. Naming
it accurately is better than claiming immunity.

---

## R3 — Bus factor of one

**Severity: high. Likelihood: moderate.**

A single developer, working solo. Illness, burnout or a change in circumstances stops the
project outright. The applicant has also disclosed health conditions that make sustained
solo delivery a genuine planning consideration rather than a hypothetical one.

**Mitigation, and be concrete rather than reassuring:**
- Open licence from week one (WP0), so the work survives the developer regardless of outcome
- A published protocol specification (WP4), so the design is recoverable from documents rather
  than only from source
- Documentation practice already demonstrated at unusual depth — ~7,400 lines of design and
  failure records, with reasoning written into source comments
- Contracted specialist time budgeted where solo capability runs out, rather than assuming
  heroics
- Schedule with WP7 absorbing slippage, and WP5 scoped so a partial year-one outcome is still
  a coherent deliverable

**On disclosure:** whether to name health conditions in an application is entirely the
applicant's choice, and is addressed in [07-funder-map.md](07-funder-map.md). Where it is
disclosed, several funders offer access support funding that directly mitigates this risk —
that is a reason to disclose, not an obligation to.

---

## R4 — Windows-specific host integration may not generalise cleanly

**Severity: moderate. Likelihood: moderate-high.**

Hotspot bring-up is the least portable part of the stack. Windows Mobile Hotspot has an API
this project drives successfully; macOS and Linux equivalents are less uniform, and macOS in
particular has restricted programmatic internet-sharing control over time.

**Mitigation.** WP5 is scoped so that success is a *network-provider abstraction* with at least
two implementations, and the honest year-one outcome may be full LAN-mode parity on macOS and
Linux with hotspot mode remaining Windows-only. State that as the plan, not as a fallback
discovered later.

---

## R5 — Adoption risk: a good plugin nobody uses

**Severity: moderate. Likelihood: moderate.**

Developer tools fail on distribution and documentation far more often than on capability.

**Mitigation.** WP7's acceptance criterion is measured on a stranger — clone to phone-controlling-
something in under fifteen minutes — rather than on the author's judgement. OpenUPM publication,
two sample projects, and an engine-agnostic protocol so the addressable audience is not limited
to Unity. The reference game is itself a demonstration that the plugin survives real use.

---

## R6 — The published certificate key looks worse than it is

**Severity: moderate (reputational). Likelihood: high if unaddressed.**

Publishing a TLS private key to a public repository is defensible here and alarming everywhere
else. A reviewer meeting it without context may stop reading.

**Mitigation.** Publish the threat model before anyone reviews it, and commission the contracted
security review against it (WP2, budgeted). The key authenticates a name that resolves only to a
private address on a network the host itself raised; an attacker who could exploit it is already
associated to that hotspot. Saying this first, in writing, converts the project's scariest-looking
artifact into evidence that the applicant reasons about security explicitly.

---

## R7 — Certificate authority or domain dependency

**Severity: moderate. Likelihood: low.**

The mechanism depends on a single owned domain and on Let's Encrypt's continued issuance
policy. Loss of either — a lapsed domain, a policy change, an account problem — breaks
provisioning for every installed copy simultaneously.

**Mitigation.** WP2's alternatives evaluation is partly a second-source exercise. Practical
steps within the funded period: multi-year domain registration, a documented recovery runbook
for reissuing under a different name, and a seed certificate shipped in the installer so a
first run never depends on a live fetch (this already exists in `CertificateProvider.cs` and
should be stated as an existing mitigation, not a promised one).

---

## R8 — Band-matching fixes the bug that was measured; it is not RF management at scale

**Severity: low for the target use case, moderate if scope expands. Likelihood: low in a
living room, high at a high-density event.**

The host's own Wi-Fi radio band-matches to the phone hotspot at start-up
(`Get-StationBandName`, `hotspot/hotspot-control.ps1`), asking for whichever band the host's
station link is already on instead of always defaulting to 2.4 GHz. That removed the dominant
cause of aim-preview jitter measured on this project's own hardware: mean latency on a
congested school network went from 156.9 ms to 5.0 ms, jitter (stdev) from 194.3 ms to 2.5 ms
(`docs/plans/aim-preview-lag-on-shared-radio.md`). It is a fix for one specific failure mode —
one physical radio time-slicing between two bands — not a general answer to RF-congested
environments. There is no DFS channel selection, no 6 GHz support (Windows'
`TetheringWiFiBand` enum has no 6 GHz member), no RSSI monitoring, and no mid-session channel
switching in response to packet loss. A host with no station Wi-Fi link — the shipping
default — still starts on 2.4 GHz, the most congested band in a room full of other consumer
networks.

**Mitigation.** The living-room audience this game targets does not encounter convention-hall
RF density, and band-matching plus the existing per-socket rate limiting is adequate there —
that is the honest scope to claim today. RSSI-aware channel selection and DFS support are real,
unscoped engineering work for a future high-density deployment (classrooms, tournaments,
conventions), not a solved problem to describe as already handled.

**What is honest to say:** the fix that shipped solves the bug that was actually measured on
this project's own hardware. It has not been tested against, and does not claim to solve,
convention-hall-scale interference.

---

## R9 — Sensor polling has no battery-aware throttling; the one power-related choice made runs the other way

**Severity: low. Likelihood: high over a long session.**

`controller/js/sensors.js` reads `devicemotion`/`deviceorientation` events at whatever rate the
browser delivers them — native, unthrottled, roughly 60 Hz on most devices. The one existing
rate limit, 20 Hz on outbound AIM messages (`controller/js/app.js:201-203`), caps network
traffic, not sensor sampling or wake time, so it does nothing for battery life. Separately,
`requestWakeLock()` (`app.js:791-799`) deliberately keeps the phone's screen on for the whole
session so players aren't unlocking their phone between turns — the opposite of a power-saving
measure, chosen for a real usability reason but real nonetheless.

**Mitigation.** For a typical session length (minutes to tens of minutes) this is unlikely to
matter in practice — screen-on sensor games already run at this duration on every platform
without complaint. If session length grows — a tournament format, extended lobby waits —
worthwhile future work is throttling sensor consumption to the game's actual tick rate rather
than the browser's native delivery rate, and a low-brightness or dark idle state between turns
that doesn't give up the wake lock's usability win.

**What is honest to say:** nothing in the current build claims or attempts battery-aware
operation. The wake lock is a deliberate trade of battery for zero-friction between-turn UX,
not an oversight — but it should never be described as compatible with a power-saving claim.

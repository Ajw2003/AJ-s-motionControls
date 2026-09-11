# Innovation claims

Six claims, each stated as a reviewer needs it: what is new, what proves it, and what the
honest limitation is. **Never state a claim without its limitation.** A reviewer who finds
the limitation themselves discounts everything else you wrote; a reviewer handed it trusts
the rest.

---

## Claim 1 — Offline secure-context provisioning

**The claim.** A consumer PC can raise its own Wi-Fi network with no internet, and serve over
it a browser-trusted HTTPS origin that phones reach by name — no router, no account, no app
store, no certificate warning, no manual configuration by the player.

**Why it is hard, and why nobody has packaged it.** The chain is unforgiving. Phone motion
sensors require a secure context. A secure context requires a certificate. A certificate is
issued for a *name*, so the network needs a resolver. A private address cannot obtain a public
certificate on its own. And an HTTPS page cannot open a plain `ws://` socket, so a page served
securely must have its socket on the same secure origin. Break any link and phone-as-controller
stops working — which is why competing products either ship a native app, require the internet,
or make the player accept a certificate warning.

**How this project closes it.** One process is static server, WebSocket relay, DNS authority
and captive-portal responder simultaneously (`relay/server.js`, `dns.js`, `funnel.js`), served
under a fixed name whose certificate is fetched while online and served locally thereafter
(`Assets/Scripts/Net/CertificateProvider.cs`). The hotspot is raised by
`hotspot/hotspot-control.ps1` from a connection profile with no connectivity at all.

**Evidence.** `docs/Decision-OfflineHosting.md` — measured, reproducible via
`deploy/spike/hotspot-offline-spike.ps1`, including tethering started from a physically
unplugged adapter. The document also records the rejected alternative (Wi-Fi Direct) and
exactly why it loses: it leaves `:53` free and builds no DNS proxy.

The certificate half is independently checkable by a reviewer, which is unusual for a claim
like this: `./deploy/Verify-PublishedCert.ps1` downloads the live certificate from the same
anonymous public URL a player's machine uses, and validates SAN coverage, issuer, expiry, key
pairing and chain shape. No credentials, no build, no Unity — a reviewer can run it from a
fresh clone in about two seconds and watch the claim hold.

**Limitation.** Certificate *provisioning* needs the internet at some point within each
certificate window (~90 days today), and that window is shrinking industry-wide. Play sessions
never need it. See Claim 6, which turns this into the research contribution.

---

## Claim 2 — Cross-platform motion normalization that survives real use

**The claim.** Raw `DeviceMotion` and `DeviceOrientation` disagree between iOS and Android in
ways that produce *wrong behaviour rather than errors*. This project normalizes them into
device-agnostic values — gravity-removed linear acceleration, and orientation as a delta from a
captured calibration reference — so a game engine never sees a platform difference.

**The three non-obvious results**, each of which cost a rewrite to find:

1. **Euler angles are unusable for this application**, not merely inelegant. The natural grip
   pose for aiming a phone at a television is `beta = 90`, which is exactly the Z-X'-Y''
   gimbal singularity. Quaternion-only, applied verbatim, never decomposed.
2. **Absolute heading cannot be reconciled across platforms; deltas can.** Calibrating on the
   gesture-arming tap makes the disagreement irrelevant rather than corrected-for.
3. **Gravity removal must be tiered by artifact, and the cheap tier is quantifiably wrong.**
   Low-pass filter lag leaks up to ~19.6 m/s² of phantom user acceleration during a fast wrist
   rotation — enough to make a hard throw and a soft throw read identically. Hardware
   `event.acceleration` first, exact quaternion subtraction second, filter only as a floor,
   with the tier reported per sample.

**Evidence.** `controller/js/sensors.js`, with the reasoning written into the source rather
than lost; `controller/test-sensors.js` (1,333 lines of pure-math tests runnable with no
device); reported by the developer as working on real hardware across multiple iPhone and
multiple Samsung/Pixel generations, though not yet recorded as checkable evidence —
[../DeviceMatrix.md](../DeviceMatrix.md) is still a template awaiting real entries.

**Limitation.** Tuned for one gesture family (a throw). Generalising it to a documented,
reusable recogniser API is work package WP3, not something already done.

---

## Claim 3 — A latency architecture for motion input over a lossy link

**The claim.** Motion input over a wireless link should be split by *authority*, not by
priority: a high-rate lossy cosmetic channel and a single authoritative event computed from
the buffered gesture.

`AIM` streams at 20 Hz, is sequenced, may be dropped, and is **forbidden from feeding the
simulation** — the rule is written at the top of `controller/js/throw.js`. `THROW` is emitted
once, at release, from the whole 1.5 s buffered swing. Smoothing lives on the host
(`AimPreview.cs`), so the wire stays honest and the display stays smooth.

The payoff: packet loss degrades the preview and never the outcome. A dropped frame cannot
change where the rock goes, because no frame ever could.

**Evidence.** `controller/js/throw.js`, `Assets/Scripts/Net/Messages.cs`, `AimPreview.cs`.

**Limitation.** No clock synchronisation, RTT estimation, or prediction beyond sequencing and
smoothing. Adequate for one-hop local play; explicitly out of scope for internet play.

---

## Claim 4 — Onboarding measured, not guessed

**The claim.** The binding constraint on phone-as-controller is not latency or physics; it is
that some fraction of players never successfully join. This project instruments that directly:
each device is tracked ASSOCIATED → PAGE_OPEN → PLAYING and the stage is broadcast to the host
as `DEVICE_FUNNEL`, so the television can say which player is stuck and where. Captive-portal
probes are answered deliberately — reporting "no internet" until the page is open, so the phone's
OS opens its portal sheet and does the prompting for you.

**Evidence.** `relay/funnel.js`, `relay/test-funnel.js` (19 tests), `relay/test-captive.js`,
`docs/archive/plans/join-failure-reduction.md` (1,013 lines).

**Limitation.** The instrument exists; a published dataset of real drop-off across many
sessions does not. That is WP6.

---

## Claim 5 — Engineering rigour, evidenced by recorded negative results

**The claim.** This project documents what it removed and why, not only what it built. A
WebRTC direct transport was written and deleted (~510 lines and a native dependency for no
measurable gain once the relay moved on-host). Wi-Fi Direct was prototyped, proven to work as a
radio, and rejected on a single decisive row — no DNS proxy, so no name resolution, so no HTTPS
origin. LAN mode was removed rather than left to rot. A confidently-held "Windows cannot do
this offline" conclusion was re-derived and found to be a four-line bug in this project's own
script, and the post-mortem names the process failure (testing on phones before checking the
host) as more expensive than the technical one.

**Evidence.** `docs/archive/Findings-DirectTransportFlapping.md`, `docs/Decision-OfflineHosting.md`
(§"What went wrong in getting here"), `docs/archive/plans/remove-direct-transport.md`,
`docs/archive/Findings-DnsAudit.md`.

**Why it belongs in an application.** Most applicants submit only successes. Funders assessing
whether a solo developer will spend their money well are assessing exactly this.

**Limitation.** Previously the documentation graph had decayed (files deleted, dead links). As
of this re-check (2026-09-11) all four sources above exist, either at their original path or
under `docs/archive/` (paths corrected above) — this limitation is resolved.

---

## Claim 6 — The open research question

**The claim.** *How does a local-first application hold a browser-trusted secure context with
no internet, as certificate lifetimes shrink toward weeks?*

This is genuinely unsolved and getting harder. CA/Browser Forum maximum certificate lifetimes
are contracting — 398 days today, stepping toward roughly 47 days by 2029 — so every design
that depends on a long-lived certificate served locally has a shortening fuse. The alternatives
all cost something: installing a local trust anchor costs user trust and creates real risk;
accepting a warning costs the sensors outright, because `isSecureContext` gates them; native
apps cost the entire no-install premise.

This project holds the only working partial answer in the wild, and it is honest about being
partial: online once per window, offline for everything else.

**Why it is publicly useful beyond games.** The same wall blocks local-first tools generally —
a museum kiosk, a classroom response system, a clinical device on an isolated network, a
field-research rig. Any of them that wants a phone browser to talk to a local host with sensor
or camera access hits this exact chain.

**Evidence.** Working mechanism today; the comparative evaluation and public write-up is WP2.

**Limitation.** This is the *question*, not a solved result. Present it as the thing the grant
funds, and the partial answer as evidence the applicant can make progress on it.

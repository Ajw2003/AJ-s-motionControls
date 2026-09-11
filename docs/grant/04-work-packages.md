# Work packages

Twelve months, one principal developer plus contracted specialist time. Product working name
**HandOff** — the extracted plugin, distinct from RockSkipping the game, which becomes its
reference implementation.

Each package states its deliverable and an **acceptance criterion someone else can check**.
Write acceptance criteria this way in the application itself; funders read "acceptance:
a person who has never seen the repo installs it and plays" very differently from
"acceptance: installer complete".

## Schedule

```
Month      1   2   3   4   5   6   7   8   9  10  11  12
WP0  ████
WP1      ██████████
WP2          ████████████████████
WP3              ████████████████████████
WP4                  ████████████████████
WP5                          ████████████████████
WP6                              ████████████████████
WP7                                      ████████████████
```

WP0 and WP1 are sequential and gate everything. WP2 and WP3 are the research core and run in
parallel. WP7 absorbs slippage.

---

## WP0 — Separation and licence (weeks 1–3)

Extract `relay/`, `controller/` and `Assets/Scripts/Net/` into a standalone package. RockSkipping
becomes its first consumer, which keeps the extraction honest — if the game still plays, the
seam is real.

Ship `LICENSE` (Apache-2.0 recommended: the patent grant is what makes a studio's legal team
comfortable; MIT if a specific funder prefers it).

**Deliverable:** a repository that builds a `.tgz` UPM package.
**Acceptance:** the package installs into an empty Unity project from a local path, and
RockSkipping builds and plays with the package as a dependency rather than as inlined source.

*Closes G3, G4.*

---

## WP1 — Distribution that works for someone who is not you (weeks 2–10)

The gating deliverable. **Do this whether or not you are funded**, because no application
survives without it.

- Compile `deploy/installer.iss` for the first time; produce a Unity build to bundle
- Bundle `node.exe`; vendor `relay/node_modules` so nothing installs at runtime
- Code-sign the installer
- Build CI: tagged commits produce a downloadable signed installer
- Test CI: `relay/` (49 tests), `controller/test-sensors.js`, and Unity EditMode tests run on push
- Confirm an *installed* client picks up a refreshed certificate. The pipeline half of this
  is already proven (see G2) and checkable any time with `deploy/Verify-PublishedCert.ps1`;
  the client half needs a build, which is why it lives here

**Deliverable:** a downloadable installer, and a certificate refresh proven all the way to an
installed client rather than only to the published URL.
**Acceptance:** a person who has never seen the repository installs on a clean Windows VM with
no development tools and plays a match with two phones, unassisted, from a downloaded file.

*Closes G1, G7; completes the client half of G2.*

---

## WP2 — The offline secure-context problem (weeks 6–26)

The research core. Frame the funding ask around this package.

Evaluate, prototype and publish a comparison of:

- **(a) Certificate-in-installer with an update channel.** Measure the real failure window as
  CA lifetimes contract. Establish empirically how long an installed, never-online machine keeps
  working, and what the failure looks like to a player.
- **(b) Renew-on-any-online-moment** (the current design, hardened). Explicit, legible
  degradation when a host stays offline past expiry — the failure must name its cause and its
  remedy, not present as a networking error.
- **(c) Alternatives that dodge the CA entirely.** A locally-installed trust anchor and what it
  genuinely costs in user trust and risk; WebTransport and WebRTC secure-context behaviour;
  and whether `isSecureContext` policy for private-range addresses has a standards path worth
  contributing to. Report honestly if the answer is no.

Also in scope: publish the **threat model for the published certificate key** (G5) and commission
the contracted security review against it.

**Deliverable:** a hardened mechanism, a public technical write-up, and a published threat model.
**Acceptance:** the write-up is specific enough that a developer outside this project can
implement the recommended approach for their own local-first application. For funders like NLnet,
the write-up is the primary deliverable — say so.

*Closes G5; addresses Claim 6.*

---

## WP3 — Sensor and gesture layer as a reusable API (weeks 8–30)

Generalise `sensors.js` beyond "throw".

- A documented normalization layer: calibration, tiered gravity removal, gyro fusion,
  screen-rotation compensation — as a stable public API, not an internal detail
- A gesture-recogniser interface with reference implementations: swing/throw, tilt-steer,
  point-and-click
- Keep `docs/DeviceMatrix.md` current as the maintained verification record (the stale
  `UNVERIFIED (iOS)` markers are already retired) and extend it as new devices are tested
- Publish the expanded throw dataset (`rock-throws.csv` grown well beyond its current 78 rows)

**Deliverable:** a documented sensor/gesture API and an open dataset.
**Acceptance:** a developer implements a *fourth* gesture, not shipped by us, against the public
API alone — no forking of the normalization layer.

*Closes G11; extends Claim 2.*

---

## WP4 — Protocol specification (weeks 12–32)

Write down what is currently implicit in `Assets/Scripts/Net/Messages.cs`: message types, AIM/THROW
channel semantics, the "AIM never feeds the simulation" authority rule, sequencing and drop
behaviour, room codes, reconnect and host-grace semantics, the funnel stages.

Versioned, with a conformance test suite.

**Deliverable:** a versioned protocol specification plus conformance tests.
**Acceptance:** the specification is sufficient for a Godot or web binding to be written against
it without reading the Unity source. This is what makes "engine-agnostic" a claim rather than an
aspiration.

*Supports Claim 3.*

---

## WP5 — Cross-platform host (weeks 20–40)

macOS and Linux. The Node relay is already portable; the work is replacing the PowerShell layer
with a pluggable network-provider interface and implementing hotspot/AP bring-up per platform.

**Scope this honestly in the application.** Hotspot bring-up is deeply platform-specific and
year one may realistically deliver: full LAN-mode parity on macOS and Linux, with hotspot mode
remaining Windows-only and a documented path for the rest. A funder who sees a solved problem
described as solved and an unsolved one described as unsolved trusts both statements.

**Deliverable:** a network-provider abstraction with at least two implementations.
**Acceptance:** the relay, controller and protocol conformance tests pass on macOS and Linux;
LAN-mode play works end to end on both.

*Closes G6.*

---

## WP6 — Accessibility and onboarding (weeks 24–44)

Use the existing funnel telemetry (Claim 4) as the measurement instrument rather than guessing.

- Screen-reader-correct controller page; the phone's own OS accessibility settings should carry
  through, which is a structural advantage of a web controller over a bespoke gamepad
- Adjustable gesture sensitivity — `ThrowTuning` is already parameterised, so this is exposure
  and UI, not new physics
- One-handed and reduced-range-of-motion modes; non-motion fallback controls
- Playtest with disabled players. **Pay participants** — see [05-budget-skeleton.md](05-budget-skeleton.md)
- Publish drop-off results

**Deliverable:** accessibility features, and a published onboarding drop-off study.
**Acceptance:** measured reduction in join-failure rate against a documented baseline, and
completion of a full match by a player using each supported alternative input mode.

*Extends Claim 4.*

---

## WP7 — Documentation, samples, release (weeks 30–48)

Two sample Unity projects. A getting-started path measured in minutes. Repaired documentation
graph (G8, G9 — though do those in week one, not month eight). Tagged 1.0 published to OpenUPM.

**Deliverable:** version 1.0 on OpenUPM with samples and documentation.
**Acceptance:** a developer goes from `git clone` of a sample to a phone controlling something
on screen in under fifteen minutes, measured on someone who has not seen the project.

*Closes G8, G9, G10.*

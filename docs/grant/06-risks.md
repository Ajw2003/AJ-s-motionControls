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
- Documentation practice already demonstrated at unusual depth — ~4,100 lines of design and
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

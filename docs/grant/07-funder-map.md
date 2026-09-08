# Funder map

Where to apply, in what order, and what each one wants.

> **Verify current terms before drafting.** Program names, streams, amounts, eligibility and
> deadlines change every cycle, and some programs pause or close entirely. Everything below is
> a starting shortlist to check, not a citation. Confirm on each funder's own site before
> investing days in a form.

## Eligibility checklist — do this first

Most declines at this stage are administrative, not technical. Before drafting anything:

- [ ] **A licence exists** (`LICENSE`, Apache-2.0) — hard gate for open-source funders
- [ ] **A legal entity exists** — sole proprietorship or incorporation. Most public funders will
      not contract with an individual. A sole proprietorship registration is cheap and fast and
      unblocks the entire Canadian list below.
- [ ] **A demo exists** — WP1. Nothing on this page is worth attempting without it.
- [ ] **Residency and jurisdiction confirmed** for each target
- [ ] **Prior-work provenance is clear** — this project is substantially agent-assisted and ships
      its transcripts in `transcripts/`. Some programs ask about AI use. Answer straightforwardly:
      the design decisions, the measurements, the failure analyses and the judgement calls are
      documented and are the applicant's. Do not conceal it; a funder discovering it later is far
      worse than a funder told plainly.

---

## Tier 1 — apply first

### Epic MegaGrants
**Fit: strongest.** Rolling applications, no equity taken, explicitly aimed at tools that
benefit developers. "Unity plugin plus engine-agnostic protocol, free to developers" is
squarely what the program is for, and the application is short relative to public funders.

*What to send:* [08-narrative-core.md](08-narrative-core.md) condensed, and the WP1 demo video
([10-demo-script.md](10-demo-script.md)). Lead with the problem, not the game.
*Gate:* WP1.

### NLnet / NGI Zero
**Fit: strong on substance.** Local-first, privacy-preserving infrastructure; values open
protocol specifications and published negative results, both of which this project has.
WP2 and WP4 are written to their priorities.

*What to send:* the research framing from [02-innovation-claims.md](02-innovation-claims.md)
Claim 6, plus WP2 and WP4.
*Check first:* call eligibility for applicants outside the EU, which varies by call and is the
one thing that could make this a waste of effort.

---

## Tier 2 — Canadian public funding

The `.ca` domain and the applicant are Canadian, which opens a substantial list. All of these
require an entity and most require matching funds or prior professional credits — check both
before drafting.

### Canada Media Fund — Innovation & Experimentation
Prototype-to-market framing fits well. CMF operates equity-deserving-group priorities and
dedicated envelopes.

### Provincial: Ontario Creates IDM Fund (or the provincial equivalent)
Interactive digital media funding, conditional on being based in that province and typically on
company eligibility.

### Canada Council for the Arts — Digital Now / Explore & Create
Viable if the *game* is framed as the artistic work and the plugin as the infrastructure it
required. Canada Council also funds **access support costs** — money for the supports an artist
needs in order to do the work, assessed separately from and not competing with the project
budget. That is a materially useful provision, and see the note below.

---

## Tier 3 — research and accessibility

### Mitacs Accelerate
Requires an academic partner. A supervisor in HCI would find the sensor-normalization results
(Claim 2) and the onboarding drop-off study (WP6) genuinely publishable — the gimbal-singularity
grip-pose finding and the quantified gravity-filter artifact are conference-paper material as
they stand. Good route if a stipend plus a co-author is attractive; slow if you need money soon,
because it depends on finding a willing supervisor first.

### Accessibility-focused funding (e.g. AbleGamers and similar)
WP6 is the application. The structural argument is strong: a phone as controller inherits the
player's own OS accessibility configuration — text size, screen reader, switch access — which a
bespoke gamepad cannot, and it removes the per-player cost of buying controllers.

---

## On the personal context you raised

You mentioned being 22, queer, and living with ADHD, anxiety and depression, and asked what
that opens up. Factually, and without overpromising:

- **Equity streams exist and are real.** CMF, Canada Council and several provincial agencies
  operate priority streams and dedicated envelopes for equity-deserving groups, which
  in various programs include 2SLGBTQIA+ and disabled applicants. Confirm the current
  definitions and streams on each funder's site — they are revised often.
- **Access support funding is the most directly useful provision.** Canada Council in particular
  funds the supports a disabled artist needs in order to carry out the work, separately from the
  project budget. This does not compete with your project money and is designed to be asked for.
- **Emerging-artist and first-time-applicant streams** are worth checking at every funder on this
  page. Being 22 with a six-week body of serious work is a good position for these specifically,
  where it is a weak one for programs requiring extensive prior credits.
- **These streams still turn on ordinary eligibility.** An entity, a licence, a demo. None of the
  above substitutes for the checklist at the top of this page — which is why WP0 and WP1 come
  first regardless of which door you go through.

**On disclosure.** Self-identification is voluntary and is yours to decide per application. It
is worth knowing that it cuts two ways in practice: it can open streams and access funding, and
some applicants find the identity-forward framing crowds out the technical case. The technical
case here is strong enough to stand alone, so you are not obliged to lead with anything personal.
The one place personal context does clear technical work is R3 in [06-risks.md](06-risks.md) —
where disclosing lets you *fund* the mitigation rather than merely assert it.

---

## Sequencing

1. **Now, unfunded:** WP0 and WP1, plus the week-one documentation repairs (G8, G9). Register
   the entity in parallel — it has a lead time and nothing else needs it to start.
2. **On WP1 landing:** record the demo, then submit to Epic MegaGrants and NLnet. Both are
   rolling or frequent, so they cost little to attempt and their feedback improves the rest.
3. **Next open cycle:** the Canadian programs, which have fixed deadlines and much longer forms.
   Draft against [08-narrative-core.md](08-narrative-core.md) rather than from scratch each time.
4. **In parallel, slower:** approach an HCI supervisor about Mitacs if a research route appeals.

Apply to several. Rejection is the base rate everywhere on this page, is usually about fit and
cycle rather than merit, and reviewer feedback is the cheapest improvement to the next
application you will get.

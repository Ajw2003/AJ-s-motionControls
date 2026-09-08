# Fundability roadmap

The engineering plan to become credible to a reviewer. This is deliberately separate from
[04-work-packages.md](04-work-packages.md): that file is what you propose to do *with* money,
this is what must be true *before* you ask for any.

Sequenced by what unblocks what. Nothing here is optional.

---

## Week 1 — Repairs that cost hours and change first impressions

The cheapest work in this document and the highest ratio of impression to effort. A reviewer's
first action is to open the README.

- [ ] **Fix the broken documentation graph (G8).** `README.md:6` instructs the reader to read
      `docs/PLAN.md` first, and that file was deleted. So were `docs/HostingPlan.md`,
      `docs/Reference-Constants.md`, `docs/Reference-PlayerColours.md`, `docs/SecurityReview.md`
      and `docs/Findings-WiFiDirect.md` — several still linked from the README and from
      `docs/Decision-OfflineHosting.md`. Either restore them from git history or remove the links
      and rewrite what they were carrying. Being told to read a 404 is worse than having no README.
- [ ] **Fix version inconsistencies (G9).** `ProjectSettings/ProjectVersion.txt` says `6000.3.15f1`,
      the README says `6000.3.10f1`. The README notes the project is on the built-in render
      pipeline while `Packages/manifest.json` includes URP.
- [ ] **Correct the iOS undersell (G11).** Done in this change — `docs/DeviceMatrix.md` now
      carries the record and the stale `UNVERIFIED (iOS)` markers point at it. **Fill in the
      matrix with your actual devices**; it currently has marked blanks.
- [ ] **Finish the link cleanup.** `README.md`, `docs/MASTER-PLAN.md`,
      `docs/Decision-OfflineHosting.md` and `docs/OfflineSetup.md` are repaired. Still broken,
      in the older planning docs: `ZeroSetupPlan.md`, `MagicDnsFallback.md`,
      `plans/magic-domain-replacement.md`, `plans/join-failure-reduction.md`,
      `plans/remove-direct-transport.md` and both `seamless-rejoin` docs. Three causes —
      references to the deleted files above; source paths written relative to the repo root
      from inside `docs/`; and stale absolute paths of the form
      `../../../Desktop/GameDev/RockSkipping/...` left over from another machine. Left alone in
      this pass deliberately: these are the historical record of decisions, and rewriting them
      mechanically risks changing what they assert. Fix them by hand, or accept them as archive.
- [ ] **Watch the first scheduled certificate run.** The cron fires Mondays 06:17 UTC. The
      first one after 2026-09-07 is the proof that unattended renewal works — every run before
      that was triggered by hand. Expect it to *skip* (the published certificate has ~86 days
      left, over the 30-day threshold) and to say so; a skip that reports why is the correct
      outcome, a failure is not. Run `./deploy/verify-published-cert.sh` any time to check the
      live certificate independently of the workflow.
- [ ] **Add `LICENSE`.** Apache-2.0. A hard gate for open-source funders and a prerequisite for
      anyone else adopting the work.

## Weeks 1–4 — Administrative, in parallel

Started now because it has lead time and blocks everything in Tier 2 of the funder map.

- [ ] **Register a legal entity.** A sole proprietorship is cheap and quick. Most public funders
      will not contract with an individual, and this silently disqualifies you from programs you
      would otherwise be competitive for.
- [ ] **Multi-year domain renewal** on `skiprocking.ca`. It is a single point of failure for every
      installed copy (R7) and renewal is trivial.
- [ ] **Confirm eligibility** against the checklist in [07-funder-map.md](07-funder-map.md) before
      drafting any form.

---

## Weeks 2–10 — The demo. Everything depends on this.

**This is the gate.** No application on the funder map is worth submitting without it. Right
now nobody but you can install and run this project, and that single fact will sink an otherwise
strong application.

- [ ] **Produce a Unity build.** There is currently no build output anywhere in the repository,
      which is the first blocker `deploy/installer.iss` names.
- [ ] **Bundle `node.exe`** rather than depending on an installed Node runtime.
- [ ] **Vendor `relay/node_modules`** so nothing installs at first run — a machine that is offline
      on first launch must still work.
- [ ] **Compile `installer.iss` for the first time.** It has never been through ISCC.
- [ ] **Code-sign the installer.** An unsigned installer triggers SmartScreen, which will feature
      prominently in your demo video for the wrong reasons.
- [x] **Prove the certificate pipeline (G2).** Done — and it turned out to have already been
      done: two production runs on 2026-09-03 published a real Let's Encrypt certificate, and
      `deploy/verify-published-cert.sh` confirms it from the public URL with no credentials.
      The investigation also caught a live bug that would have stopped renewal entirely (the
      weekly cron defaulted to staging and would have failed every week). Both fixed.
      **Still outstanding:** confirm an installed client picks up a *refreshed* certificate —
      that needs a build, so it is gated on the installer work below.
- [ ] **Clean-VM acceptance test.** A person who has never seen this repository installs from a
      downloaded file on a fresh Windows VM with no development tools and plays a match with two
      phones, unassisted. Watch them do it without helping. Every place they hesitate is a defect.
- [ ] **Record the demo.** [10-demo-script.md](10-demo-script.md).

---

## Weeks 4–8 — Credibility infrastructure, in parallel with the demo work

- [ ] **Test CI.** The tests already exist and are good — 49 relay integration tests, 1,139 lines
      of sensor math tests, 7 Unity EditMode cases. Run them on every push. A project with good
      tests that never run reads exactly like a project with no tests.
- [ ] **Build CI.** Tagged commits produce a downloadable signed installer. This is also how you
      keep the demo reproducible rather than a one-off artifact on your machine.
- [ ] **Publish the certificate-key threat model (G5).** Before anyone reviews the repository, not
      after. Explain that the key authenticates a name resolving only to a private address on a
      network the host itself raised, so an attacker able to use it is already associated to that
      hotspot. This is the single scariest-looking thing in the project and a written threat model
      converts it into evidence of maturity.
- [ ] **Fill in `docs/DeviceMatrix.md` properly.** Specific models, OS versions, and what was
      exercised. This is reusable evidence in every application and it currently exists only in
      your memory, which is the wrong place for it.

---

## Weeks 8–12 — Separability

Not required to *apply*, but required for the proposal to be believable, because the thing being
funded does not yet exist as a separable artifact (G4).

- [ ] **Extract the plugin boundary.** `relay/`, `controller/` and `Assets/Scripts/Net/` become a
      package; RockSkipping becomes its first consumer. If the game still plays with the package
      as a dependency rather than inlined source, the seam is real.
- [ ] **Build a `.tgz` UPM package** and install it into an empty Unity project from a local path.
- [ ] **Write the protocol down**, even roughly. A draft of what is implicit in
      `Assets/Scripts/Net/Messages.cs` makes "engine-agnostic" credible in a way that a promise
      to write one later does not.

---

## Ordering, and what to do if time is short

If you can only do one thing: **weeks 2–10, the demo.** An application with a working demo and
weak prose beats a polished application with nothing to run, every time.

If you can do two: add **week 1**. It costs a day and it is what a reviewer sees first.

The dependency chain is: licence and entity → demo → application. Everything else improves
your odds; those three determine whether you have odds.

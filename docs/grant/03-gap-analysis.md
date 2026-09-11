# Gap analysis

What a reviewer will find if they look properly, ordered by how badly it hurts. Every gap is
mapped to the work package that closes it ([04-work-packages.md](04-work-packages.md)).

The purpose of this file is internal. Do not paste it into an application. Its value is that
nothing in it should be able to surprise you in a reviewer's feedback.

---

## Blocking — fix before applying anywhere

### G1. Nobody but the author can install and run this
`deploy/installer.iss` exists and **has never been compiled**. Its own header says so, and
lists what is outstanding: a Unity build, a bundled `node.exe`, vendored `relay/node_modules`,
and code-signing. `docs/ZeroSetupPlan.md` Track C phases 1, 3 and 4 are not started; phase 5
has never been run.

*Why it is fatal:* almost every program wants a runnable prototype or a video of one. "It works
on my machine" is the single most common reason a strong technical application is declined.

→ **WP1.** Also the precondition for [10-demo-script.md](10-demo-script.md).

### G2. ~~The certificate renewal pipeline has never run for real~~ — RESOLVED, and it hid a live bug
**Resolved 2026-09-07.** The pipeline had in fact run: two `workflow_dispatch` runs on
2026-09-03 issued against Let's Encrypt production and published successfully. The
"UNTESTED: this step has never been run" annotation was stale, exactly like the iOS markers
in G11. `deploy/Verify-PublishedCert.ps1` now proves it from outside — it downloads from the
public URL anonymously and passes.

**But investigating it surfaced a worse, live problem.** The workflow's endpoint defaulted to
Let's Encrypt *staging* on **both** paths, including the weekly cron. A scheduled run would
therefore issue an untrusted staging certificate, and the "refuse to publish staging" gate
would then fail the job. Every weekly run was guaranteed to fail, so **the certificate would
never have auto-renewed** — it would have expired silently while a red X accumulated in the
Actions tab. The offline story's renewal mechanism was broken, just not in the way the
documentation claimed.

Fixed in the same change: cron now runs production; a freshness gate skips issuing while the
published certificate has over 30 days left (protecting Let's Encrypt's five-duplicates-per-week
limit, which the old header comment claimed acme.sh's `--days` flag handled — no `--days` was
ever passed, and no acme.sh state persisted between runs, so every run issued fresh); staging
rehearsals publish to a separate `staging` tag so the publish path is exercisable without
touching the live asset; and the run verifies what it actually published rather than what it
built.

*Lesson worth carrying into the applications:* the repository has now been wrong twice in the
same direction — asserting something was unproven when it was working. Both times the stale
claim was more damaging than the underlying state.

### G3. ~~No licence, no eligible entity~~ — LICENSE resolved, entity registration still open
`LICENSE` (Apache-2.0) now exists at the repo root, added in `3b1aef3` — the hard gate for
open-source funders is closed. Most public funders still require a registered business, sole
proprietorship, or academic sponsor before they will read anything, and that half is unstarted.

*Why it matters:* the entity half is a paperwork gap that silently disqualifies you from programs
you would otherwise win. It costs days, not months, and it blocks everything.

→ **WP0**'s licence step is done; the eligibility checklist in [07-funder-map.md](07-funder-map.md)
still needs the entity question answered.

---

## Serious — will cost you marks

### G4. The plugin does not exist yet as a separable artifact
The relay, the controller app and the Unity networking layer are entangled with the game.
Nothing is redistributable. The thing being proposed is currently a property of a codebase
rather than a package.

*How to handle it:* do not hide this — it is the honest reason to ask for money. But WP0 must
land early enough that the deliverable is credible.

→ **WP0.**

### G5. A TLS private key is deliberately published to a public repository
`renew-cert.yml` publishes `server.pem` and `server.key` to `Ajw2003/rockskipping-cert`.

*The reasoning is actually sound* — the key authenticates a name that only ever resolves to
`192.168.137.1`, a private address on a network the host itself raised, so an attacker gains
the ability to impersonate a server they would already have to be on the host's own hotspot to
reach. But a security reviewer meeting this cold will stop reading.

*How to handle it:* write the threat model **before** anyone reviews it, publish it in the repo,
and budget a contracted security review. That converts the single scariest-looking thing in the
project into evidence of maturity.

→ **WP2**, and a budget line in [05-budget-skeleton.md](05-budget-skeleton.md).

### G6. Windows-only host
PowerShell hotspot control, Inno Setup packaging, ICS pinned to `192.168.137.1`. The Node relay
and the phone client are portable; the host integration is not.

*Why it matters:* "a plugin for game developers" implies at minimum a credible macOS and Linux
roadmap. Many indie developers work on macOS.

→ **WP5.** Scope it honestly — year one may deliver LAN-mode parity on macOS/Linux with hotspot
bring-up remaining Windows-only.

### G7. No build CI and no test CI
`.github/workflows/` contains exactly one workflow, and it is the certificate renewer. The tests
that exist are good and are never run automatically.

*Why it matters:* it signals a project that stops working the moment attention moves elsewhere —
precisely the risk a funder is pricing.

→ **WP1.**

---

## Cosmetic but embarrassing — cheap to fix, fix them now

### G8. ~~The documentation graph is broken~~ — mostly resolved
`README.md` line 6 used to say "Read `docs/PLAN.md` first" and that file was deleted (commits
`0b7c500`, `77a44f1`); as of this re-check `README.md` no longer references it. Of the other
files once listed as deleted, `docs/SecurityReview.md` has since been **re-derived and restored**
(dated 2026-09-11, explicitly rebuilt from the current code rather than recovered from git
history — treat it as new, not as the old file returned). `docs/HostingPlan.md`,
`docs/Reference-Constants.md`, `docs/Reference-PlayerColours.md` and `docs/Findings-WiFiDirect.md`
remain absent; check for any remaining links to them before submitting.

*Why it matters:* it is the first thing a reviewer does. Being told to read a file that 404s is
a worse first impression than having no README at all.

→ Mostly done. Sweep for the four still-missing files' links as a final first-week check.

### G9. Version and status inconsistencies in the README — half resolved
`ProjectSettings/ProjectVersion.txt` and `README.md` now both say `6000.3.15f1` — the version
mismatch is fixed. Still open: `Packages/manifest.json` includes URP
(`com.unity.render-pipelines.universal: 17.3.0`) while the README still notes the project is on
the built-in pipeline — either the package or the README is wrong, and only one commit will tell
you which.

→ First-week task; version half done, pipeline half still open.

### G10. Unity-side test coverage — better than last counted
18 EditMode test cases (`SkipPhysicsTests` 6, `PhoneAttitudeTests` 3, and `DiscContactTests` 9 —
the last of these wasn't in the previous count at all) against 48 relay integration tests and
1,333 lines of sensor math tests. More than double the Unity-side figure previously cited.

*How to handle it:* note that coverage is concentrated exactly on the components being proposed
as the plugin — the relay, the protocol and the sensor pipeline — and that the thin part is the
game, which is not what is being funded. That is a true and favourable framing, and less of an
undersell than the old count made it look.

→ **WP1**/**WP4**, incrementally.

---

## Not a gap — an undersell to correct

### G11. Stale `UNVERIFIED (iOS)` markers understate real cross-device verification — half fixed
Six markers across `controller/js/sensors.js` and `controller/js/app.js` said iOS branches were
"written from spec". Confirmed gone (`grep -rn "UNVERIFIED" controller/js/*.js` returns nothing).
iOS is reported working across multiple iPhone generations and Android across multiple Samsung
and Pixel generations, from the developer's own testing.

*Why it matters more than it looks:* cross-device verification is exactly the evidence that
separates a sensor demo from a plugin other developers can ship. The repository no longer argues
against its own strongest practical claim in source — but the undersell moved rather than
disappeared. **`../DeviceMatrix.md` is not yet the maintained record it's described as being** —
every row is still `TODO`, by the file's own header warning. Every doc that currently cites it as
verification evidence (`01-asset-inventory.md`, `02-innovation-claims.md`, `08-narrative-core.md`,
the grant `README.md`) is citing a template, not a record, until it is filled in.

→ Source markers: fixed. `DeviceMatrix.md`: still a Week-1 task
([09-fundability-roadmap.md](09-fundability-roadmap.md) already tracks filling it in) — do not
cite it as evidence before then.

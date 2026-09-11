# Budget skeleton

Deliberately without numbers. Rates differ enormously between funders, and several publish
mandatory rate guidance you must follow rather than invent. Fill this in per application.

## Structure

| Line | What it covers | Notes for filling it in |
|---|---|---|
| **Principal developer**, 12 months | The bulk of any version of this budget. | Use the funder's own guidance. Canadian arts and media funders accept a self-employed developer/artist fee as a normal line; Epic MegaGrants expects a project cost, not a salary request; Mitacs pays a fixed intern stipend structure you cannot vary. |
| **Device testing** | Access to iPhone/Android hardware across generations, or a modest purchased device set. | Cheap, concrete and very credible — it is visibly the thing that makes a cross-platform claim true. Cite `docs/DeviceMatrix.md` as the existing baseline you are extending. |
| **Code-signing certificate and developer accounts** | Windows code signing; Apple/Google accounts if any native component appears. | Small, unglamorous, itemise it. Reviewers notice when an applicant has thought about shipping rather than only building. |
| **Contracted security review** | External review of the certificate and published-key model (G5), against the threat model produced in WP2. | This line does double duty: it buys real assurance and it pre-empts the single most alarming-looking thing in the repository. |
| **Accessibility playtest participants** | Recruitment and **participant payment** for WP6. | Pay people. Budget it explicitly and say that you are paying them. Unpaid disabled playtesters is a bad look and worse practice, and reviewers on accessibility-adjacent programs will notice either way. |
| **Contracted specialist time** | Short engagements where solo capability runs out — e.g. macOS/Linux network bring-up in WP5. | Naming where you are *not* the right person strengthens rather than weakens an application. |
| **Infrastructure** | Domain renewal, CI minutes, artifact hosting. | Trivial amounts; itemise anyway. |
| **Access support costs** | Supports the applicant needs in order to do the work. | Separate from project costs. Canada Council funds this explicitly and it does not compete with your project budget. See [07-funder-map.md](07-funder-map.md). |
| **Contingency** | ~10%. | Some funders forbid a contingency line; check first and fold it into estimates if so. |

## Guidance that applies to every version

**Match the budget to the work packages, line by line.** A reviewer should be able to read
[04-work-packages.md](04-work-packages.md) beside the budget and see where each figure goes. Budgets
that do not map to deliverables read as guesses.

**Do not pad and do not underbid.** Underbidding is the more common failure for first-time
applicants and it damages you twice: it signals inexperience, and it leaves you unable to
finish, which is the outcome the funder is trying to avoid. Cost the work honestly.

**In-kind contribution is real and should be stated.** Roughly six weeks of full-time work
already exists in this repository — 237 commits, ~15,300 lines of C#, a tested relay, a tested
sensor pipeline, cross-device operation the developer reports from hands-on testing (not yet a
checkable record — see G11 in [03-gap-analysis.md](03-gap-analysis.md)), and an unusually
complete documentation and failure record. Many programs weight prior unfunded effort, and all
of them read it as evidence the applicant finishes things.

**Distinguish what you will do unfunded.** WP0 and WP1 should happen regardless, because they
gate the demo that gates the application. Saying so is a strength: it shows the grant accelerates
and de-risks a project already in motion rather than starting one.

# Grant proposal kit

Everything needed to turn this repository into funding applications, written once and
reused across funders. Nothing here is game design; it is the case for the *plugin* the
game turned out to contain.

## The one-paragraph version

RockSkipping solved a problem that blocks every developer who wants phones as motion
controllers without an app store: browser motion sensors require a secure context, a
secure context requires a certificate, a certificate requires a resolvable public name,
and none of that exists on a network with no internet. This repository contains a working
answer — a host that raises its own Wi-Fi network, answers DNS and captive-portal probes
for it, serves a trusted HTTPS origin over it, and normalizes phone motion sensors across
iOS and Android well enough that a thrown gesture reaches a game engine as one authoritative
quaternion. The proposal is to extract that into a plugin any developer can install.

## How to use this kit

| Read this | When |
|---|---|
| [08-narrative-core.md](08-narrative-core.md) | Always. It is the prose every application quotes. |
| [07-funder-map.md](07-funder-map.md) | Choosing where to apply and checking eligibility. |
| [02-innovation-claims.md](02-innovation-claims.md) | Writing the "what is novel" section. |
| [04-work-packages.md](04-work-packages.md) | Writing the workplan and schedule. |
| [05-budget-skeleton.md](05-budget-skeleton.md) | Writing the budget. |
| [06-risks.md](06-risks.md) | Any form with a risk or mitigation section. |
| [01-asset-inventory.md](01-asset-inventory.md) | Evidence — file paths and line counts to cite. |
| [03-gap-analysis.md](03-gap-analysis.md) | Knowing what a reviewer will attack. |
| [09-fundability-roadmap.md](09-fundability-roadmap.md) | **Before applying anywhere.** What to build first. |
| [10-demo-script.md](10-demo-script.md) | Recording the video. |

## Read this before writing a single sentence

Two claims are easy to overstate and will be checked:

1. **"No Wi-Fi" is wrong. "No internet" is right.** The phone's Wi-Fi radio must be on —
   it joins a hotspot the host PC raises. What is absent is internet, a router, an app
   store and an account. That is still a strong claim. Do not inflate it.
2. **Offline play is indefinite; certificate *provisioning* is not.** The host needs the
   internet at some point within each certificate window (~90 days today) to refresh a
   certificate it then serves locally. Sessions themselves never touch the internet. A
   permanently air-gapped machine eventually stops working. Say so — and see
   [06-risks.md](06-risks.md), where this is the funded research question rather than an
   apology.

And one claim that needs care in the other direction: iOS and Android support **works**
across multiple device generations, from the developer's own hands-on testing — but
[../DeviceMatrix.md](../DeviceMatrix.md), the file meant to make that checkable, is currently a
template with every row still `TODO`. Fill it in before citing device verification as evidence
(tracked in [09-fundability-roadmap.md](09-fundability-roadmap.md)) — citing an empty template
as proof is worse than not mentioning it at all.

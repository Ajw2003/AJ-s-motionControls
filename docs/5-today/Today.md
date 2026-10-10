# Today — 2026-10-09 to 2026-10-10

**Tier 5.** Rewritten at the start of each working session. The session before this one is in
git history (`git log -p docs/5-today/Today.md`).

## What this session was

**Getting a phone onto the PC securely, offline, with nothing installed on the phone.** aj's first
real-phone runs showed the on-screen gamepad reaching Dolphin. They also showed two gaps:

- the page ran over plain HTTP, so iPhone couldn't get motion at all and Android needed a Chrome
  flag;
- the only working connection shared the PC's one Wi-Fi radio, so lag was bad.

This session closed the HTTPS gap (#13) and built everything up to the point where a real phone
has to try it.

## Done

- **Dolphin confirmed on aj's PC**: buttons and sticks over DSU (milestone 4's Dolphin check). See
  [`4-systems/DsuServer.md`](../4-systems/DsuServer.md).
- **Onboarding research.** [`Findings-OnboardingReuse-2026-10-09.md`](../Findings-OnboardingReuse-2026-10-09.md)
  covers what Rock Skipping already solves. Issues #12 (launcher), #14 (QR join page) and #15
  (pre-flight check) came out of it.
- **Certificate research.** [`Findings-CertificateResilience-2026-10-09.md`](../Findings-CertificateResilience-2026-10-09.md)
  covers why a published key can be revoked by anyone, and the options aj's constraints allow:
  two QR codes, no phone install, no new third party, no running cost. It also records the live
  checks behind the design:
  - a revocation-list check against the real certificate;
  - release download counts register about 10 minutes late.
- **#13 decided** ([`6-decisions/Decisions.md`](../6-decisions/Decisions.md), 2026-10-09): a shared,
  hardened certificate for `pad.skiprocking.ca`, watched hourly from the public
  `Ajw2003/rockskipping-cert` repo. Children #17-#20 opened. The deferred fallback is #16.
- **#17 and #18 built, merged and live** (rockskipping-cert#1). Both are proven on GitHub:
  - staging run 37987926842;
  - production run 37988137248, which published a trusted certificate valid to 7 January 2027;
  - the chain continued by itself: run 37994483197 logged `Nothing to issue (reason: ok)`, and
    watchdog 37994485406 started.

  See [`4-systems/CertificateWatch.md`](../4-systems/CertificateWatch.md).
- **#20 built** on `AjsAgent/pad-cert-runtime`:
  - `relay/pad-cert.js` downloads, checks and caches the certificate, with a revocation check in
    plain Node;
  - `relay/start-pad.js` starts the relay over HTTPS on 8443 with the hotspot DNS.

  The relay suite passed (130 tests), and so did a live check against the real certificate and an
  HTTPS smoke test. See [`4-systems/HostingAndCertificates.md`](../4-systems/HostingAndCertificates.md).
- **`tools/test-pad.ps1`** cuts the PC side of the phone test to one command: install, start the
  relay in its own window, wait for it, start the hotspot, print what to do on the phone. Parsed
  clean in PowerShell 7.4.6. It hasn't been run on Windows.
- **Two house-rules bugs filed**, both in Ajw2003/AjsClaudeCodeTools:
  - **#192:** commits on `AjsAgent/` branches still prompt when the repo isn't the session's main
    folder. Builders are paused until it's fixed.
  - **#194:** steps that aren't shell commands skip the step-card check.

## Deliberately not done

- **No launcher (#12) yet.** The test script stands in for it during testing.
- **No QR join page (#14) or nudge handling (#19) yet.** Both come after the phone test shows the
  base works.
- **Issues not closed.** #17, #18 and #20 wait on aj's test.

## What to do next, in order

The full handoff, with the test steps, is in
[`Handoff-2026-10-10-PhoneTest.md`](../Handoff-2026-10-10-PhoneTest.md).

1. **aj runs the phone test** (7 steps). It answers the three open questions:
   - Does the phone stay on an offline hotspot?
   - Does the certificate load with no warning?
   - Does motion reach Dolphin?
2. **Fix whatever the test finds**, then merge `AjsAgent/pad-cert-runtime`.
3. **#12, the launcher.** It turns the test script into the real start-up, with generated Wi-Fi
   credentials.
4. **#14 (QR join page), #19 (nudges), #16 (fallback without a certificate).**

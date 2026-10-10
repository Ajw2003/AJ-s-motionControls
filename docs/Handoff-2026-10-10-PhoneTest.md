# Handoff: test the secure phone controller end to end

**Date:** 2026-10-10. **For:** aj, and whichever session picks this up next.
**Branch with everything:** `AjsAgent/pad-cert-runtime` (not merged to `main` yet).

## Where things stand, in plain terms

- **The phone page can now be secure** (HTTPS) on the offline hotspot, with nothing installed on
  the phone. It uses a real certificate for `pad.skiprocking.ca`, trusted by every phone.
- **That certificate looks after itself.** A free GitHub job in the public
  `Ajw2003/rockskipping-cert` repo checks it about every hour. If it's revoked or nearly expired,
  the job issues a new one with a new key and publishes it. This is proven running: the first real
  certificate was published on 2026-10-09, and the hourly cycle has continued by itself since.
- **The PC side is built.** It downloads the certificate, checks it, keeps a copy for offline
  play, and serves the controller page over HTTPS. It's tested in the sandbox, including against
  the real certificate.
- **Nobody has tried it on your PC with a phone yet.** That's the next job, and it's yours: it
  needs your Windows PC, its Wi-Fi radio and your phone.

## The test: 7 steps

Steps 1-3 and 7 run in Windows PowerShell on your PC. Steps 4-6 are on your phone and in Dolphin.
The folder `C:\Users\aj\Desktop\GameDev\AJ-s-motionControls` is where the earlier Dolphin guide
had you clone the repo. Use yours if it's different.

**Test the secure controller on your phone: 7 steps.** Do them in order. Each step says what you
should see when it worked.

---

### Step 1 of 7 — Download the test branch

UNTESTED: not run on Windows (the agent works in a Linux sandbox with no Windows).

Open **Windows PowerShell**: Start menu, type `PowerShell`, click **Windows PowerShell**. Every
command here works from any folder.

```powershell
git -C "C:\Users\aj\Desktop\GameDev\AJ-s-motionControls" fetch origin AjsAgent/pad-cert-runtime
```

**You should see:** a line ending `AjsAgent/pad-cert-runtime -> FETCH_HEAD`, or no output at
all. Both mean it worked.

*Next: step 2 switches to it.*

---

### Step 2 of 7 — Switch to it

UNTESTED: not run on Windows.

In the same **Windows PowerShell** window:

```powershell
git -C "C:\Users\aj\Desktop\GameDev\AJ-s-motionControls" switch AjsAgent/pad-cert-runtime
```

**You should see:** `Switched to a new branch 'AjsAgent/pad-cert-runtime'` and
`branch 'AjsAgent/pad-cert-runtime' set up to track 'origin/AjsAgent/pad-cert-runtime'.`

*Next: step 3 starts everything on the PC with one command.*

---

### Step 3 of 7 — Start the relay and the hotspot

UNTESTED: not run on Windows. The script parsed with no errors in PowerShell 7.4.6, but a dry run
with the Windows parts faked was refused, and the real Windows parts can't run in the sandbox.
**The PC must be online for this first run**, so it can download the certificate.

In the same **Windows PowerShell** window:

```powershell
powershell -ExecutionPolicy Bypass -File "C:\Users\aj\Desktop\GameDev\AJ-s-motionControls\tools\test-pad.ps1"
```

**You should see:**

- Lines starting `==>` for each stage: Node, packages, clearing old runs, starting the relay,
  waiting, starting the hotspot.
- A **second window opens** with the relay. It should say
  `Using the newly downloaded certificate for pad.skiprocking.ca`, then
  `listening on 0.0.0.0:8443 (TLS)`. Leave it open.
- Back in the first window, `Ready. On the phone:` followed by the Wi-Fi name, the password and
  the address to open.

If **Windows Security** asks about Node.js, tick **Private networks** and click **Allow access**.
If any stage prints an error in red, stop there and send a screenshot of both windows.

*Next: step 4 joins the phone to the hotspot.*

---

### Step 4 of 7 — Join the hotspot on your phone

UNTESTED: not run with a phone. This is where the last offline try failed. **Leave mobile data
on**, so the test shows whether the relay's "is there internet?" answers keep the phone on the
hotspot.

On the phone, open **Settings → Wi-Fi**, tap **AJ Motion** and enter `motion1234`.

**You should see:** the phone connected to **AJ Motion**. "No internet" next to it is fine. If the
phone keeps switching back to mobile data, note that, turn mobile data off and carry on.

*Next: step 5 opens the controller.*

---

### Step 5 of 7 — Open the secure controller page

UNTESTED: not run with a phone.

On the phone, open **Chrome** (or **Safari** on iPhone) and type the address step 3 printed. It
should be:

```text
https://pad.skiprocking.ca:8443/pad.html
```

Then tap **Start controller**, keep **Xbox** and turn the phone sideways.

**You should see:** a **padlock** in the address bar, no security warning and no Chrome flag
needed. On iPhone, one prompt to allow motion: tap **Allow**.

*Next: step 6 checks it in Dolphin.*

---

### Step 6 of 7 — Check it in Dolphin

UNTESTED: not run with a phone.

On the PC, open **Dolphin → Controllers → Configure**: the same DSU setup as before, at
`127.0.0.1:26760`.

**You should see:** buttons and sticks responding, and the **motion bars moving when you tilt
the phone.** That last part is new. Over plain HTTP, motion never reached Dolphin.

*Next: step 7 shuts everything down.*

---

### Step 7 of 7 — Stop everything

UNTESTED: not run on Windows.

In the first **Windows PowerShell** window:

```powershell
powershell -ExecutionPolicy Bypass -File "C:\Users\aj\Desktop\GameDev\AJ-s-motionControls\tools\test-pad.ps1" -Action Stop
```

**You should see:** `Hotspot off.` and `Relay stopped.` The relay's window shows the process
ended, and you can close it.

---

## What to report back

| Question | Where you'll see the answer |
|---|---|
| Did the phone stay on **AJ Motion** with mobile data on? | Step 4 |
| Padlock, no warning? | Step 5 |
| Did iPhone ask for motion, if you tried one? | Step 5 |
| Motion bars moving in Dolphin? | Step 6 |
| How does lag feel compared with the first run? | Step 6 |

A screenshot of anything unexpected, and which step, is enough for the next session to diagnose.

## What comes after the test

1. **Fix whatever the test turns up**, then merge `AjsAgent/pad-cert-runtime` into `main` by pull
   request. It carries #20, the docs and the findings from this session.
2. **#12, the launcher.** It replaces `tools/test-pad.ps1` with the real start-up:
   - generated, remembered Wi-Fi name and password;
   - the same start order (relay DNS bound before the hotspot);
   - Node provisioning.
3. **#14:** a join page with two QR codes (Wi-Fi, then the page), so nobody types an address.
4. **#19:** the watch job counting launcher nudges. **#16:** buttons and sticks keep working if no
   valid certificate is left.
5. **Then the roadmap again:** #6 (virtual Xbox controller), #8 (latency), #7 (WebRTC).

## Open items and decisions waiting on aj

- **Close issues after testing:** #17, #18 and #20 meet their "done when" conditions on the
  agent's side. Only you close them.
- **House-rules guard bug** (Ajw2003/AjsClaudeCodeTools#192): commits on `AjsAgent/` branches in
  other repos still prompt. **Builders stay paused until it's fixed** (aj, 2026-10-09).
- **Steps the step-card check misses** (Ajw2003/AjsClaudeCodeTools#194).
- **Six old commits credited to Claude**, including one on `main`. Rewriting them needs a
  force-push, which is your call.
- **`actions/checkout@v4` warning** in rockskipping-cert's workflows (Node 20 deprecated): a
  one-line bump to `v5` in each, optional.
- **The game code is still in the repo:** an open decision since the fork.

## Where everything is

| What | Where |
|---|---|
| Decision for #13 | [`6-decisions/Decisions.md`](6-decisions/Decisions.md), 2026-10-09 |
| Certificate research and live checks | [`Findings-CertificateResilience-2026-10-09.md`](Findings-CertificateResilience-2026-10-09.md) |
| The hourly certificate job | [`4-systems/CertificateWatch.md`](4-systems/CertificateWatch.md); code in `Ajw2003/rockskipping-cert` |
| PC side (#20) | `relay/pad-cert.js`, `relay/start-pad.js`; [`4-systems/HostingAndCertificates.md`](4-systems/HostingAndCertificates.md) |
| Test script | `tools/test-pad.ps1` |
| Flow diagram for the alternative route B | [`generated/route-b-flow/index.html`](generated/route-b-flow/index.html) (not chosen; kept for the record) |

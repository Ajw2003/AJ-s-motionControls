# Findings: hotspot forwarding error, guest internet, and auto-connect (2026-10-01)

**Prompted by:** a Hotspot-mode start failing with:

> forwarding-still-enabled: the disable task ran but forwarding is still Enabled on Local Area
> Connection* 12 after 10s. Guests are NOT isolated.

Ayden also asked to revisit the earlier discussion about two things:

- keeping guests' phones on the internet while staying secure;
- making the network one phones auto-connect to, since many refuse to auto-join a network with
  no internet.

**Confidence labels:**

- **MEASURED** means seen on this machine or a real phone, with the evidence cited.
- **CONFIRMED** means read in the code.
- **PLAUSIBLE** means the mechanism fits but has not been observed.

## Part 1: why the forwarding check failed

### What the code does

`hotspot/hotspot-control.ps1:327-373` handles the forwarding step:

1. It starts tethering.
2. It fires the scheduled task `RockSkippingDisableForwarding` once.
3. It polls for 10 s to confirm forwarding is `Disabled` on the `192.168.137.*` interface.

`HotspotService.cs:230-241` treats a failed confirmation as fatal. It tears the hotspot down
(correctly: an unconfirmed hotspot is an unisolated one). That is the screen in the report.

The task's action is fixed, and it acts only on whatever interface holds a `192.168.137.*` address
at the moment it runs (`deploy/setup-offline.ps1:153-155`).

### Evidence (MEASURED, this machine, 2026-10-01)

- **The task reported success.** `Get-ScheduledTaskInfo` shows LastRun 23:09:29 with LastResult
  `0x0`.
- **ICS was still busy after the task ran.** The System log shows ICS (`SharedAccess`) still
  bringing its dependencies up at 23:09:27, 23:09:28, 23:09:30 and 23:09:39. Activity both before
  and after the task's single run.
- **The failed state can no longer be inspected.** The hotspot is now off, because the game tore
  it down. No interface holds `192.168.137.*`.

### Root cause (MEASURED with `tools/hotspot/trace-forwarding.ps1`, two runs)

Reading **(b)** is what happens. Both traces showed the same sequence:

1. Forwarding reads Disabled briefly at start-up.
2. ICS enables it about 0.3 s later.
3. The task disables it (around 2.2–2.75 s).
4. **ICS enables it again at 4.4–4.7 s and leaves it on** (still Enabled at 30 s).

The old check passed on the reading in step 3. So **a start reporting "forwarding confirmed
Disabled" left guests unisolated.** The red error only appears when the step-4 re-enable lands
inside the poll.

The two readings considered before the trace:

The task ran exactly once while ICS was still configuring the interface. Either reading fits the
evidence:

- **(a) The address wasn't there yet.** The `192.168.137.1` address was not assigned at 23:09:29.
  The task matched nothing, changed nothing, and still exited 0.
- **(b) ICS turned it back on.** ICS re-asserted forwarding after the task disabled it. The 09-06
  spike found that a disable "sticks". It measured a single settled start, not this start-up
  window.

The check has existed since `bd22729` (09-06), so this is an intermittent race, not a new
regression.

### Fix (done, #70)

**Status:** fixed in `hotspot/hotspot-control.ps1`. It re-runs the task whenever forwarding reads
Enabled, and reports isolation only after 6 s of continuous Disabled (20 s ceiling, inside the
30 s `startTimeout`).

**Traced twice after the fix:**

- the re-enable at about 4.2–4.5 s was caught and reversed within about 0.6 s;
- forwarding then stayed Disabled for the rest of the 35 s trace.

**Costs:**

- a hotspot start takes about 6 s longer;
- forwarding is briefly on for up to about 0.6 s, seconds after the network first appears.

**Still unmeasured:** whether ICS re-enables forwarding later in a session, for example when a
phone joins. That needs a trace with a phone connecting.

The original proposal:

Re-run the task inside the existing 10 s poll whenever forwarding still reads `Enabled`, about
every 1.5 s.

- **It's safe to repeat.** The task takes no parameters and only ever disables forwarding, so
  running it again cannot widen anything.
- **It stays fail-closed.** If forwarding will not stay down within the window, the error and the
  tear-down stay exactly as they are.

**To verify:** start the hotspot several times while sampling forwarding state every 250 ms. That
shows which of (a) or (b) happens, and that the retry holds it down. It needs the hotspot started
on this PC, which is a visible Wi-Fi network, so it waits on Ayden's go-ahead.

## Part 2: guest internet and auto-connect — the tension

### How the current design behaves

Two decisions combine:

1. **Forwarding is off, so guests have no internet through the host.** That's isolation. Guest
   traffic never leaves under the host's IP, and a returning guest's saved Wi-Fi password grants
   nothing beyond the game (`SecurityReview.md` §5).
2. **The relay answers the phones' "do I have internet?" probes as if it did** (`relay/server.js`
   connectivity-probe answers, `dns.js` probe domains, kept by `13c47bf`).
   - **MEASURED on a Samsung:** Android marked the hotspot `+VALIDATED`
     (`relay/samsung-logcat.txt:452-453`).
   - This is what stops "No internet" warnings and lets the phone stay connected and re-join.

**The contradiction.** The original isolation argument relied on Android *not* validating the
network: "Android keeps mobile data as its default route for a Wi-Fi it judges unvalidated, so
guests' real browsing and DNS go over cellular" (`docs/ZeroSetupPlan.md:323`). Decision 2 removed
that.

A validated Wi-Fi becomes the phone's default network for every app. So while joined, a guest's
messages, social apps and browser go to a host that drops them (PLAUSIBLE: the mechanism follows
from Android and iOS default-route behaviour, but has not been measured on a phone). That's the
"no internet on the game Wi-Fi" experience. It's also the likeliest reason a guest switches the
network off, and with it the game.

### The options

| | A. Today: isolated, probes answered | B. Isolated, probes not answered | C. Share the host's internet, block the host's LAN |
|---|---|---|---|
| Phone auto-connects and stays connected | **No, per Ayden's observation**, although Android validates it (Samsung logcat) | **Often not.** Android prompts "no internet", may avoid or drop the network, may not auto-rejoin | Yes, truthfully validated |
| Guest's other apps work while joined | **No** (PLAUSIBLE) | Yes, over mobile data | Yes |
| Guest traffic leaves under the host's IP | No | No | **Yes.** The host is accountable for guest traffic |
| Guests can reach the host's home LAN | No | No | Must be blocked. **Unproven:** Windows Firewall rules may not apply to ICS-forwarded traffic. Needs a spike |
| Works with no internet at the host | Yes | Yes | Falls back to A |
| Work needed | None | Remove probe answers (back to before Phase 4) | Spike the LAN block, then a host setting and a code path |

**There is no option that gives all three:** auto-connect, working guest internet, and no
attribution to the host. A phone only treats a network as good when it can actually reach the
internet through it, and that traffic has to leave under someone's IP.

### Correction (2026-10-01, from Ayden)

Returning phones do **not** auto-rejoin today. Ayden's goal is "one QR code after the first
join": the phone should rejoin the saved hotspot by itself.

**Not the cause:** credentials. The SSID and passphrase are stable across sessions
(`Assets/Scripts/Net/HotspotCredentialsStore.cs`).

**PLAUSIBLE causes:**

- **A phone already on a working Wi-Fi (the home network) does not leave it for another saved
  network** on either OS. Internet sharing (C) would not change that.
- A phone on mobile data may skip a network it remembers as having "no internet". This is where C
  could help.

Which case applies needs Ayden's observation: what the phone was on, and which phones.

### Recommendation

Keep **A** as the default, and add **C** as a host-side opt-in ("Share this PC's internet with
players"). It should be on only when the host has internet, shown on the TV, and off again when
the session ends. The opt-in exists only if the spike proves guests can be blocked from the host's
LAN. If they can't, C is not safe to offer.

Not recommended: B. It trades the guests' internet for the exact join and drop problems Phase 4
was built to remove.

## Decisions needed from Ayden

1. May the Part 1 fix be reproduced and verified by starting the hotspot on this PC?
2. Which of A, B or C (or A plus an opt-in C) for guest internet?

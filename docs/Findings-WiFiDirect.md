# Wi-Fi Direct — can the host run with no network at all?

**Spike:** [`deploy/spike/wifidirect-spike.ps1`](../deploy/spike/wifidirect-spike.ps1)
**Question:** [ZeroSetupPlan.md](ZeroSetupPlan.md) Open Question 2, reopened.
**Status:** **Stage B passed — Wi-Fi Direct starts with no network attached.** Stage C (a real
phone joining) is blocked on the SSID not being findable.

## The headline

**`ZeroSetupPlan.md`'s ceiling is not a Windows limit, it is a Mobile Hotspot limit.** With the
Ethernet cable out and Wi-Fi connected to nothing — `GetInternetConnectionProfile()` returning
null, the exact condition where `hotspot-control.ps1` gives up with `no-connection-profile` — a
Wi-Fi Direct autonomous group owner started anyway, took `192.168.137.1`, and had DHCP serving on
it:

```
   [ok]   no connected adapter, no internet profile - THIS IS THE REAL TEST CONDITION
   Control - what Mobile Hotspot would say under these exact conditions:
     GetInternetConnectionProfile() -> null. NetworkOperatorTetheringManager has
     nothing to tether from; hotspot-control.ps1 would return no-connection-profile.
   ...
   [ok]   publisher status: Started
   [ok]   STARTED WITH NO NETWORK ATTACHED - the thing Mobile Hotspot cannot do
   [ok]   address is 192.168.137.1 - the address LocalServices already pins the certificate to
   [ok]   UDP :67 bound on 192.168.137.1 by svchost (pid 2196)
```

That is the best of the four outcomes tabled below: the address and DHCP both survive with no
uplink, so the cached certificate, `DNS_RESPOND_IP`, the relay bind, and the `192.168.137.0/24`
firewall rule all keep working untouched.

**It took three attempts.** Attempts 1 and 2 aborted; attempt 2 left the radio software-off, which
`-EnableRadio` restored before attempt 3 succeeded. Any production implementation must retry and
must restore the radio between attempts — a single-shot call would have concluded, wrongly, that
this does not work.

**Still open:** no phone has joined yet. The network was not findable by name — see
Stage C below.

## Why this was worth trying

Open Question 2 is answered "no" for Mobile Hotspot, and correctly so:
`NetworkOperatorTetheringManager.CreateFromConnectionProfile()` needs a profile to share, so with
Wi-Fi and Ethernet both down `GetInternetConnectionProfile()` returns null and
`hotspot-control.ps1` returns `no-connection-profile`. That is the API working as designed, not a
bug to route around.

ZeroSetupPlan names one escape hatch — the deprecated `netsh wlan hostednetwork` — and calls it
unproven and unsupported on many modern drivers. **On this machine it is not merely unproven, it
is unavailable:** the Realtek 8852CE reports `Hosted network supported: No`. That escape hatch is
closed here.

Wi-Fi Direct is a third mechanism that ZeroSetupPlan does not consider.
`WiFiDirectAdvertisementPublisher` with `IsAutonomousGroupOwnerEnabled` makes the radio a group
owner outright, and `LegacySettings` layers an ordinary WPA2 SSID on top so a phone joins it as a
normal network, knowing nothing about Wi-Fi Direct. **Nothing in that API takes a connection to
share** — which is precisely the input Mobile Hotspot cannot do without.

The same driver that refuses hosted network reports the capabilities Wi-Fi Direct needs:

| Capability | This machine |
|---|---|
| Hosted network supported | **No** — the ZeroSetupPlan escape hatch is dead here |
| Soft AP | **Not supported** |
| P2P Device Discovery | Supported |
| P2P GO on 5 GHz | Supported |
| P2P Max Mobile AP Clients | **8** — comfortably above the 2–8 player target |

Note the tension worth keeping in mind: `Soft AP: Not supported` while Mobile Hotspot works
anyway. Windows 10+ implements Mobile Hotspot over the Wi-Fi Direct GO path rather than the old
SoftAP path, which is *why* this spike was plausible — the game's existing hotspot mode is
already riding this machinery, just entered through an API that insists on an uplink.

## Stage A — does the mechanism work at all? (network still plugged in)

Realtek 8852CE WiFi 6E, Windows 11 Pro 26100, Wi-Fi adapter Disconnected, Ethernet up.

```
=== STEP 3  Starting a Wi-Fi Direct autonomous group owner (legacy WPA2 SSID)
   [ok]   publisher status: Started

=== STEP 4  Addressing - did it land on 192.168.137.1?
   [ok]   virtual adapter: Local Area Connection* 12 - Microsoft Wi-Fi Direct Virtual Adapter #4
   [ok]   address is 192.168.137.1 - the address LocalServices already pins the certificate to

=== STEP 5  DHCP - is anything prepared to hand a phone a lease?
   SharedAccess (ICS, which is what serves DHCP here): Running
   [ok]   UDP :67 bound on 192.168.137.1 by svchost (pid 2196)
```

**The address is the important line.** Wi-Fi Direct lands on `192.168.137.1` — the exact address
`LocalServices.EffectiveLanAddressOverride` already forces in Hotspot mode, and the address the
cached `192-168-137-1.local-ip.sh` certificate is issued for. If this holds offline, swapping the
tethering call for a Wi-Fi Direct call is a change to *one* component; the certificate, the DNS
responder's `DNS_RESPOND_IP`, the relay bind, and the firewall rule scoped to `192.168.137.0/24`
all keep working untouched.

Stage A proves the mechanism and the addressing. It does **not** answer the actual question,
because an uplink was present — `startedWithNoUplink: false`.

## Stage B, attempt 1 — INVALID, do not cite it

The first offline run returned `"status": "Aborted", "uplinkAtStart": "none"`, which reads as a
clean "Wi-Fi Direct cannot start offline either." **It is not that.** It is a false negative.

Afterwards the Wi-Fi radio was found to be:

```
    Radio status           : Hardware On
                             Software Off
```

A software-off radio aborts `WiFiDirectAdvertisementPublisher` with `RadioNotAvailable`
regardless of the network. Because PowerShell 5.1 cannot subscribe to WinRT events, the script
had no error code to show, so the abort was indistinguishable from a genuine offline limitation.

**Control test that settles it:** re-running Stage A *online*, with the radio still software-off,
also returned `Aborted`. The abort tracks the radio, not the network. The offline question is
therefore still open.

Timeline strongly suggests the radio was turned off by *the spike's own previous run*: two Stage A
runs succeeded, and the radio was off by the time the offline run started. Nothing else touched
it.

### Two changes came out of this

1. **Radio state is now a precondition, checked before STEP 2 asks anyone to unplug a cable.** The
   script reads it through `Windows.Devices.Radios.Radio` (which works unelevated —
   `RequestAccessAsync -> Allowed`) and refuses to run rather than produce another false negative.
   `-EnableRadio` turns it on via `SetStateAsync`, opt-in rather than automatic.
2. **Radio state after the run is now measured and reported** (`radioAfter`), because if a Wi-Fi
   Direct session reliably leaves the radio software-off, that is a production problem
   independent of whether offline hosting works: the host's Wi-Fi would be off after every match,
   and each run would poison the next.

   **Tested, and the hypothesis is disproved.** A Stage A run with `-EnableRadio` returned
   `"radioAfter": "On"` — the session leaves the radio usable. Whatever switched Wi-Fi off
   between the two earlier runs was external to the spike (a Windows or OEM toggle, plausibly
   when Ethernet reconnected), not caused by it. No cleanup obligation falls on production code
   from this. The check stays in, because it costs nothing and it is what caught the false
   negative.

The start is also retried (`-Attempts`, default 3, with `-SettleSeconds` between) so that a
transient failure while the network stack reconfigures after an unplug cannot be mistaken for a
hard limit — and each retry re-checks the radio.

## Stage B, attempt 2 — the real question (pending)

Does it still start, still get `192.168.137.1`, and still get DHCP with **no adapter connected at
all**? Step 5 is the one genuinely in doubt: ICS assigned that address and bound `:67` here while
it had an Ethernet connection to share. Whether it bothers to configure the interface with
nothing to share is not something to reason about — it has to be measured.

Three outcomes and what each costs:

| Outcome | What it means |
|---|---|
| Started, `192.168.137.1`, DHCP bound | **Best case.** Hotspot mode becomes truly offline by replacing one call. Everything downstream is unchanged. |
| Started, `192.168.137.1`, **no DHCP** | Workable. We ship a DHCP responder alongside `relay/dns.js` — same shape as the DNS one, no elevation needed, ~100 lines. |
| Started, but **APIPA** `169.254.x.x` | Expensive. Nothing configured the interface, so we assign the address ourselves *and* serve DHCP, and the certificate's pinned name has to survive that. |
| Aborted | Dead end. Record it and Hotspot mode's "host must stay attached to something" ceiling stands. |

## Correction to the radio finding above

The "each run kills the radio" hypothesis was disproved for *successful* runs and that still
holds (`radioAfter: "On"` every time the advertisement started). But the offline run showed the
other half: **a failed start does turn the radio off.**

```
[warn] attempt 2 of 3: Aborted
[FAIL] the radio is now 'Off' - the failed start turned it off
```

So the obligation on production code is real but narrower than first thought: it is not
"restore the radio after every session", it is **"restore the radio after every failed start,
before retrying"**. Without that, attempt 2's failure guarantees attempt 3's failure, and the
retry loop proves nothing.

## Stage C — a real phone JOINED, over Wi-Fi Direct

```
[ok]   CLIENT JOINED: 192.168.137.157  (mac B2-CE-09-43-91-4D)
       HIT #1  from 192.168.137.157  for http://192.168.137.1/   [by IP - network/firewall only]
```

The phone associated, took a DHCP lease from ICS, and loaded a page served by this machine.
Association, addressing, routing and the firewall are all confirmed working over a Wi-Fi Direct
group owner.

**What fixed the join was the QR, and the reason is worth keeping.** The network was findable all
along — the phone's Wi-Fi list simply does not surface it the way a person expects, so two test
cycles were spent concluding "the network is not there" about a network that was working. The
`WIFI:` payload joins by SSID directly and never consults that list. This is now a project rule in
[`../CLAUDE.md`](../CLAUDE.md): render the QR, never ask anyone to hunt for a network name.

### The remaining gap: DNS

`http://play.local-ip.sh/` failed with `ERR_NAME_NOT_RESOLVED`. Not a Wi-Fi Direct problem —
**the spike never started a DNS responder.** `confirm-server.js` tells the phone to try the name,
and nothing on the host was answering.

Note how close this came to reading as a clean pass: `http://192.168.137.1/` succeeded, and an IP
URL performs no DNS lookup at all. The spike now points SCAN 2 at the *name* when `-Dns` is on,
so the QR itself exercises resolution rather than quietly skipping it.

`-Dns` starts `dns-responder.js` **before** the group owner, which is the ordering
`hotspot-dns-test.ps1` established as load-bearing — ICS takes `:53` when it brings the network
up, and binding first wins. STEP 5c then re-checks who holds the port once the AP is live.

Responder verified independently on 2026-08-20, both names authoritative:

```
query play.local-ip.sh A -> rcode NOERROR, authoritative true, answer 192.168.137.1
query 192-168-137-1.local-ip.sh A -> rcode NOERROR, authoritative true, answer 192.168.137.1
```

Still unproven: that a **phone** resolves through it. The proof is the phone's address appearing
in `dns-responder.log`, not a successful page load — a page can load for other reasons.

## DNS: the phone never asked us

With `-DnsAfter`, everything on the host worked — group owner up on the first attempt, responder
holding `192.168.137.1:53`, `CLIENT JOINED: 192.168.137.157`. The phone joined by QR, then failed
`http://play.local-ip.sh/` with `ERR_NAME_NOT_RESOLVED`.

**`dns-responder.log` recorded zero queries.** Not a wrong answer, not a firewall drop — the phone
never sent us a lookup at all. That exonerates the responder completely and moves the problem onto
the phone's resolver selection.

### Ruled out (tested 2026-08-21)

- **Mobile data off** — still zero queries.
- **Android Private DNS off** — still zero queries.

Both were the leading theories and both are wrong. Whatever is resolving is not choosing cellular
over an unvalidated Wi-Fi, and is not Android's system-wide DoT.

### Leading explanation: the phone is never handed a resolver

Evidence, all from the same run:

- The phone gets an address by DHCP (`192.168.137.157`), so ICS's DHCP server is running, and
  `SharedAccess\Parameters\ScopeAddress` is `192.168.137.1` as expected.
- HTTP **by IP** works. No DNS needed for that, so it proves nothing about resolution.
- **`:53` was free even after the group owner started.** Our responder bound
  `192.168.137.1:53` unopposed. A healthy ICS runs a DNS proxy there; this one does not.
- **Zero queries reach us — including drops.** `dns-responder.js` logs out-of-subnet queries as
  `DROPPED - outside <allow>`, so an empty log means nothing arrived at the socket at all. This
  is not a firewall or allow-list issue. Confirmed separately: no `Block` rules exist for
  `node`, and both spike allow rules are present and enabled.
- The phone loops `Checking the quality of your Internet connection…` → `Connected without
  internet` → repeat. That is what Android does when it cannot resolve its connectivity probe.

One explanation covers all five: ICS advertises a DNS server that nothing is listening on (or
advertises none), so the phone has no usable resolver, never asks us, and can never validate.

**Decisive test (no code):** set a static IP on the phone with DNS `192.168.137.1`. If names then
resolve, the responder and the whole Wi-Fi Direct path are fine and the only defect is what DHCP
hands out.

If that is confirmed, the production fix is bounded and known: **serve our own DHCP** rather than
relying on ICS's, so the DNS option is ours to set. That is the same class of work as
`relay/dns.js` and needs no elevation.

### The validation loop needs solving regardless

Even with DNS fixed, Android will keep failing its connectivity probe offline, because there is no
internet to probe. The standard answer is to satisfy the probe locally: resolve the probe
hostnames to `192.168.137.1` and return **HTTP 204** from `/generate_204` (Android) and the
expected `Success` body from `/hotspot-detect.html` (iOS). That makes the network validate with no
uplink at all — and unlike `-DnsUpstream`, it works offline, which is the entire point.

### Still open

1. **The browser, not the OS.** Chrome has its own DNS-over-HTTPS setting
   (`chrome://settings/security` → "Use secure DNS"), **separate from Android Private DNS and
   unaffected by turning it off**. With it on, Chrome never consults the system resolver, which
   produces exactly what was observed: `ERR_NAME_NOT_RESOLVED` in the browser and zero queries at
   the host. This is the top candidate now precisely because the two OS-level settings were
   already cleared.
2. **DHCP may not be advertising us as the resolver.** ICS normally hands clients its own address
   as DNS, but the host side shows `hotspot interface resolvers: (none set)`, and ICS's own DNS
   proxy was evidently not holding `:53` (our responder bound it without contest). If the phone
   was handed an upstream resolver instead of `192.168.137.1`, no browser setting matters. Windows
   does not expose the DHCP options ICS sends, so this has to be read off the phone: Wi-Fi →
   network → Advanced/Details.
3. **Our NXDOMAIN policy** still makes any validation-driven fallback worse — `-DnsUpstream <ip>`
   forwards other names — but with mobile data off it can no longer be the whole story.

Note the tension (3) creates with the actual goal: **upstream forwarding requires an uplink**, and
the whole point is running with none. If Android's validation behaviour turns out to be the
blocker, offline play cannot fix it by forwarding — it would need the phone to be off mobile data,
or a different approach to naming entirely. That makes this the most important open question in
the design, not a detail.

**Instrumentation added:** the spike now counts queries received and reports them as a step
(`dnsQueries` in the result), instead of that fact living only in a log file. The 2026-08-21 run
looked like a complete success right up until the phone, and the one datum that explained it was
invisible in the spike's own output.

## The orphaned group owner — the real cause of every unexplained `Aborted`

The `-Dns` run failed all three attempts, and the obvious inference was that holding `0.0.0.0:53`
stopped ICS bringing the network up. **That inference was wrong**, and it survived one round of
apparent confirmation: re-running `-Dns` reproduced the failure, which looked like proof and was
not.

The actual cause: **a leftover `Microsoft Wi-Fi Direct Virtual Adapter` was still `Up` with no
spike running.** This driver reports `P2P GO ports count: 1`. An orphan holds that single port, so
every subsequent start returns `Aborted` (`ResourceInUse`) — with no error code available, because
PowerShell cannot subscribe to the WinRT event that carries it. Any change made between runs
looks causal.

The orphan came from a process being **killed** rather than exited, so its cleanup never ran. The
script's own output claimed the opposite — *"it would have died with this process anyway - nothing
persists"* — which is false and actively misleading. Fixed.

Once cleared, **both** DNS orderings start on the first attempt:

| Run | Result |
|---|---|
| `-Dns` (responder holds `0.0.0.0:53` first) | `Started`, `attemptsUsed: 1` |
| `-DnsAfter` (responder binds `192.168.137.1:53` after) | `Started`, `attemptsUsed: 1` |

So `hotspot-dns-test.ps1`'s finding — bind first wins, ICS does not contend — holds for Wi-Fi
Direct too. There is no ordering constraint to design around.

### What this obliges production code to do

1. **Detect a pre-existing Wi-Fi Direct adapter before starting** and refuse, or clear it. A bare
   `Aborted` names no cause, and the single GO port makes this failure permanent until cleared.
2. **Clear it by cycling the Wi-Fi radio** — `Radio.SetStateAsync(Off)` then `On` via
   `Windows.Devices.Radios`. Confirmed to tear the orphan down, **unelevated**. No
   `Disable-NetAdapter`, no admin shell.
3. **Stop the publisher on every failed attempt**, not just successful ones, before retrying.
4. **Survive being killed.** A host process that is force-terminated will orphan the group owner.
   Since cleanup cannot be guaranteed on kill, startup must assume an orphan may exist — which is
   what (1) is for.

## Superseded: the SSID hypothesis (kept, because it was wrong in a useful way)

The group owner was up, addressed and serving DHCP, but the phone could not find
`RockSkip Direct` in its Wi-Fi list.

**The host cannot diagnose this itself.** `netsh wlan show interfaces` reports one WLAN interface
even with a group owner running — the GO does not appear, and a radio cannot scan its own AP. The
phone is the only available instrument.

`LegacySettings.Ssid` reads back as whatever was set, which looked like confirmation and is not:
it echoes the property, it does not read the beacon. Ruled out as evidence.

**Leading hypothesis: the SSID.** The Wi-Fi Direct spec requires a group owner's SSID to begin
with `DIRECT-`. Windows accepted `RockSkip Direct` without complaint and echoed it back, but may
beacon something else entirely. The spike's default SSID is now `DIRECT-RockSkip`, and STEP 6 asks
for every visible network name rather than a yes/no — an unexpected name is the most informative
thing that can come back.

Other candidates if that is not it:

- **Band.** The card is 6 GHz-capable and was associated on 6 GHz channel 101 during one test, but
  `P2P GO on 6 GHz: Not Supported`, so the GO must land on 2.4 or 5 GHz — both normally visible to
  phones. Lower suspicion.
- **Scan caching.** Phones hold scan results far longer than feels reasonable; the network had
  existed for seconds. Cheap to rule out by toggling phone Wi-Fi.

Association is not the result; a DHCP lease and a loaded page are. The spike watches
`Get-NetNeighbor` on the Wi-Fi Direct interface, because a neighbour entry with an IPv4 address
only appears after the phone has actually leased one.

The prior hotspot spike found the firewall was the single thing that failed
([deploy/spike/README.md](../deploy/spike/README.md) #9), so the script checks for the inbound
rule and prints the elevated command rather than running it.

## Known limitation of the spike, not of the mechanism

PowerShell 5.1 refuses to subscribe to WinRT events at all (`Windows PowerShell cannot subscribe
to Windows RT events`), so `WiFiDirectAdvertisementPublisherStatusChangedEventArgs.Error` is
unavailable and the script polls `Status` instead. An abort is visible but unexplained. This is a
limitation of testing through PowerShell — a production implementation in C# gets the error code,
and would need it to distinguish `RadioNotAvailable` from `ResourceInUse`.

That cuts against the decision recorded in
[network-mode-selection.md](plans/network-mode-selection.md) to do tethering in PowerShell rather
than a compiled helper. That decision was right for `NetworkOperatorTetheringManager`, whose
result is a return value. Wi-Fi Direct reports failure through an event, and there is a second
reason a script is a poor fit: **the advertisement lives and dies with the process that owns it**,
so the group owner needs a host process alive for the whole session rather than a one-shot child.
If this ships, it likely revives the `RockSkipHelper.exe` idea from ZeroSetupPlan's runtime
architecture table.

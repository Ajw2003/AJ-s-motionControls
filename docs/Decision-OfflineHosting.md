# Decision: how the host runs a network with no internet

> **Update — see [docs/plans/magic-domain-replacement.md](plans/magic-domain-replacement.md).**
> local-ip.sh (referenced throughout this doc as the source of the `192-168-137-1.local-ip.sh`
> certificate name) is permanently dead. Hotspot mode itself, the tethering mechanism, and
> everything else measured below is unaffected — only the certificate's hostname changed, to
> a fixed domain (`play.skiprocking.ca`) renewed by a GitHub Actions job instead of borrowed
> from a third party. LAN mode, mentioned in passing below, is now deprecated in favour of
> Hotspot-only.

**Date:** 2026-08-21
**Status:** Decided, confirmed offline, and **implemented** in `hotspot/hotspot-control.ps1`.
**Answer: use Mobile Hotspot. Drop Wi-Fi Direct. The blocker was a bug in our code, not a
Windows limitation.**

## The one-line version

`hotspot-control.ps1` asks `GetInternetConnectionProfile()`, which returns null when there is no
internet — by definition. It never asks `GetConnectionProfiles()`, which returns every profile
including ones carrying no connectivity. **Tethering starts fine from those.** The "host must stay
attached to some network" ceiling in [ZeroSetupPlan.md](ZeroSetupPlan.md) Open Question 2 is not
real; it is this function giving up early.

## Evidence

Measured on this machine, 2026-08-21, unelevated.

**Every profile is tetherable, including ones with no connectivity at all:**

```
GetInternetConnectionProfile() : Ethernet 4

  Ethernet 4                         connectivity=InternetAccess   tetherable=True
  TP-Link_C978_6G                    connectivity=None             tetherable=True
  Wilkins                            connectivity=None             tetherable=True
  Bluetooth Network Connection 3     connectivity=None             tetherable=True
```

Note the last three: two remembered Wi-Fi networks that are **not joined**, and a Bluetooth PAN
adapter that is **Disconnected**. All produce a live `NetworkOperatorTetheringManager` with
`MaxClientCount 8`.

**Tethering actually starts from one, and the network it builds is complete:**

```
=== STEP 3  Starting tethering from 'TP-Link_C978_6G' (connectivity=None)
   [ok]   StartTetheringAsync -> Success
   [ok]   started from a profile with NO connectivity

=== STEP 4  Is the network it created actually usable by a phone?
   [ok]   hotspot address: 192.168.137.1
   [ok]   DHCP listening on UDP :67
   [ok]   UDP :53 held on 0.0.0.0 by svchost (pid 2196)
```

Reproduce with [`../deploy/spike/hotspot-offline-spike.ps1`](../deploy/spike/hotspot-offline-spike.ps1)
`-PreferNoConnectivity`, which deliberately selects a zero-connectivity profile so the point is
provable without unplugging anything.

## Why Wi-Fi Direct loses, despite working

Wi-Fi Direct did what it was asked: a group owner starts with **no network attached whatsoever**,
lands on `192.168.137.1`, and a real phone joins it and loads a page. That result stands and is
recorded in [Findings-WiFiDirect.md](Findings-WiFiDirect.md).

It loses on what it does *not* build:

| | Wi-Fi Direct GO | Mobile Hotspot |
|---|---|---|
| Starts with no uplink | Yes | **Yes** (from a no-connectivity profile) |
| Address `192.168.137.1` | Yes | Yes |
| DHCP for clients | Yes (ICS) | Yes (ICS) |
| **DNS proxy on `:53`** | **No — port stays free** | **Yes — `svchost` holds `0.0.0.0:53`** |
| Phone can resolve a name | **No.** Zero queries ever reached the host | Yes — proven end to end, [spike README](../deploy/spike/README.md) #8 |
| Phone validates the network | No — loops `Checking quality…` → `no internet` | Not yet tested offline |

The DNS row decides it. The controller must be served over **HTTPS** (iOS motion sensors need a
secure context), the certificate is issued for a **hostname**, and a hostname needs a resolver. A
network where phones cannot resolve cannot host this game, no matter how well the radio works.

Making Wi-Fi Direct viable would mean building and shipping our own **DHCP server** (to advertise
a resolver at all) on top of the existing DNS responder, plus captive-portal probe answers. That
is a large amount of new networking code to replace something Windows already does correctly on
the other path.

## The change (applied 2026-08-21)

In `hotspot/hotspot-control.ps1`, `Get-TetheringManager` currently gives up when there is no
internet:

```powershell
$profile = [...NetworkInformation...]::GetInternetConnectionProfile()
if ($null -eq $profile) { return $null }
return $mgrType::CreateFromConnectionProfile($profile)
```

It should prefer the internet profile when there is one — that keeps today's behaviour byte for
byte when online — and otherwise fall back to any profile that will host tethering:

```powershell
function Get-TetheringManager {
    $ni = [Windows.Networking.Connectivity.NetworkInformation,Windows.Networking,ContentType=WindowsRuntime]
    $mgrType = [Windows.Networking.NetworkOperators.NetworkOperatorTetheringManager,Windows.Networking,ContentType=WindowsRuntime]

    # Preferred: the internet-bearing profile, so online behaviour is unchanged.
    $profile = $ni::GetInternetConnectionProfile()
    if ($profile) { return $mgrType::CreateFromConnectionProfile($profile) }

    # Offline: any profile will do. Tethering needs *a* profile to hang off, not a working
    # internet connection - measured 2026-08-21, see docs/Decision-OfflineHosting.md.
    foreach ($p in $ni::GetConnectionProfiles()) {
        try { return $mgrType::CreateFromConnectionProfile($p) } catch { }
    }
    return $null
}
```

The `no-connection-profile` failure result stays — it is still correct for a machine with no
profiles at all — but it stops firing merely because the internet is absent.

### Consequences elsewhere

- **`ZeroSetupPlan.md` Open Question 2 must be rewritten.** It currently records "Answered — no,
  confirmed 2026-08-20" and reasons that the API has no workaround. Both are wrong.
- Its suggested escape hatch, `netsh wlan hostednetwork`, is moot — and unavailable on this
  hardware anyway (`Hosted network supported: No`).
- **Open Question 1 is answered yes** in the strongest form: tethering starts not just from a
  connected-but-no-internet profile, but from a profile whose adapter is disconnected entirely.
- `LocalServices`' forced `192.168.137.1` override, the cached `192-168-137-1.local-ip.sh`
  certificate, `relay/dns.js`, and the `192.168.137.0/24` firewall rules are all **unaffected**.
  Nothing downstream changes.
- The DNS responder must still bind `:53` **before** tethering starts — ICS takes `0.0.0.0:53`
  once it is up. `hotspot-dns-test.ps1` already established this and `HotspotService` is already
  built around it.

## First offline attempt — inconclusive, not a failure

The offline run returned `StartTetheringAsync -> Unknown` and looked like a clean refutation. It
is not one, for two reasons found immediately afterwards:

1. **The Wi-Fi radio was off.** Checked right after that run: `wi-fi radio: Off`. A hotspot *is*
   the Wi-Fi radio, so this guarantees failure — and it fails with the uninformative status
   `Unknown`, which is indistinguishable from "Windows cannot do this offline". Switching Wi-Fi
   off is also the most natural way to make a machine offline, so this is the likely path, not an
   exotic one. **Exactly the same confound derailed the Wi-Fi Direct spike**, and the check was
   not carried across to this script.
2. **It tried one profile and stopped.** It selected `Ethernet 4` — the adapter whose cable had
   just been pulled — and never tried the other three, which is the whole point of enumerating
   them.

What that run *did* establish, and this part is solid: **all four profiles still enumerate as
tetherable while fully offline.**

```
tetherable profiles while offline: 4
   Ethernet 4  (None)
   TP-Link_C978_6G  (None)
   Wilkins  (None)
   Bluetooth Network Connection 3  (None)
```

The spike now gates on radio state (with `-EnableRadio` to fix it), tries **every** profile in
turn, and records per-profile results in `attempts`. Verified online with the radio restored:
`TP-Link_C978_6G=Success`, connectivity `None`, DHCP up, ICS DNS proxy on `0.0.0.0:53`.

## Confirmed offline

Run 2026-08-21 with the Ethernet cable out, Wi-Fi radio on and joined to nothing:

```
=== STEP 2  Waiting for the machine to go completely offline
   [ok]   no connected adapter, no internet profile - the real test condition
   tetherable profiles while offline: 4

=== STEP 3  Trying to start tethering from each of 4 profile(s)
   --- 'Ethernet 4'  (connectivity=None)
   [ok]   StartTetheringAsync -> Success  from 'Ethernet 4'
   [ok]   STARTED WITH NO NETWORK ATTACHED

=== STEP 4  Is the network it created actually usable by a phone?
   [ok]   hotspot address: 192.168.137.1
   [ok]   DHCP listening on UDP :67
   [ok]   UDP :53 held on 0.0.0.0 by svchost
```

Tethering starts from a profile whose adapter is **physically unplugged**, on a machine with no
network of any kind, and builds a fully configured network with DHCP and DNS. The decision is
settled: **ZeroSetupPlan Open Question 2 is wrong, and this is not a Windows limitation.**

Verified separately, online, that the DNS half composes correctly — our responder bound `:53`
before tethering and **kept it**:

```
=== STEP 2c  DNS responder - bound BEFORE tethering, the only order that works
   [ok]   responder holds :53 (pid 12856)
   [ok]   StartTetheringAsync -> Success
   [ok]   OUR responder is the sole holder of :53 - ICS did not take it
   [ok]   UDP :53 held on 0.0.0.0 by node (pid 12856)
```

### Two operational facts this surfaced

1. **Tethering survives the process that started it.** A previous run left the hotspot on, which
   kept ICS holding `:53` and silently prevented our responder from binding at all. The spike now
   detects and stops pre-existing tethering first. `HotspotService` needs the same defence: a
   crashed host must not leave a hotspot up, and a later start must not assume `:53` is free.
2. **The Wi-Fi radio must be on and the check must come first.** A software-off radio fails
   tethering with the uninformative status `Unknown`. Both spikes now gate on it.

## Remaining work (not blockers)

Whether any tetherable profile still **enumerates** when the machine is fully offline. The
evidence says yes — the profiles above persist while their adapters are disconnected, and none of
them depend on Ethernet — but "very likely" is not "measured".

The last unproven link is a **phone** resolving `play.local-ip.sh` over an offline hotspot and
loading the page by name. Everything either side of it is proven; this joins the two halves:

```
./deploy/spike/hotspot-offline-spike.ps1 -WaitForOffline -KeepRunning -EnableRadio -Dns -Serve
```

`-Dns` binds the responder before tethering, `-Serve` puts confirm-server on :80, and scan 2 then
carries the **name** rather than the IP. The result to look for is confirm-server printing
`[BY NAME - this is the result]`, corroborated by the query count the spike prints at exit.

Unplug the cable when asked — **and do not switch Wi-Fi off**. Disconnect from the network if it
is joined to one, but leave the radio on; the hotspot needs it. `-EnableRadio` turns it back on if
it does get switched off.

If that passes, offline play is proven end to end and the only code change outstanding is the
four-line `Get-TetheringManager` fix above. It re-enumerates profiles *after* going offline, reports how many
remain tetherable, starts from one, and renders join QRs. If it reports zero tetherable profiles
offline, then Open Question 2 was right for the wrong reason, and Wi-Fi Direct plus a home-grown
DHCP server becomes the fallback — with the spike already written and the radio half proven.

## What is needed to run this in the game

Audited 2026-08-21. **Almost everything is already in place** — the game's Hotspot path was built
around the correct ordering before any of this was measured.

### Already working, verified on this machine

| Piece | State |
|---|---|
| `network-mode.txt` | `Hotspot` |
| `hotspot-control.ps1` profile fallback | Fixed — tethers from any profile |
| `LocalServices` Hotspot env | Sets `DNS_RESPOND_IP` / `DNS_ALLOW` / `DNS_SUFFIX`, forces `192.168.137.1` |
| DNS bind address | `relay/dns.js` defaults to `0.0.0.0`, so it binds before the hotspot address exists |
| **Ordering** | `HotspotService.BeginTethering()` waits on `LocalServices.ProbeDnsBound` *before* tethering — exactly the order the spike proved is required |
| Certificate | Cached, `*.local-ip.sh`, valid to **2026-11-17** (88 days). Covers `192-168-137-1.local-ip.sh` |
| Scheduled task | `RockSkippingDisableForwarding` present, Ready |
| Firewall | `RockSkipping relay` TCP/8443 LocalSubnet; `RockSkipping hotspot DNS` UDP/53 on `192.168.137.0/24` |
| Node + `relay/node_modules` | Present |

Note the certificate expiry recorded elsewhere in the docs (`2026-09-18`) is **stale** — it was
renewed on 2026-08-20.

**So the answer to "what is needed" is: press Play.** Nothing else is required for this machine.

### Two defects that will bite, both observed during testing

Neither blocks a clean first run, and both produce failures that look like something else.

**1. Stale tethering wedges the next session, and restarting does not fix it.**

Tethering survives the process that started it. If a session crashes — or a spike is killed — the
hotspot stays up, ICS keeps `0.0.0.0:53`, and on the next launch the relay's DNS responder cannot
bind. `dnsBound` stays false and `HotspotService` fails with:

> "The local DNS responder never bound. Port 53 may already be in use... **Restart and try
> again.**"

Restarting does not help, because the tethering that is causing it is still running. This
happened twice while testing the spikes.

*Fix:* run `hotspot-control.ps1 -Action Stop` once at startup in Hotspot mode, **before**
`LocalServices` spawns the relay — the relay is what needs `:53`, so the cleanup has to precede
it. The `Stop` action already exists and is safe to call when nothing is tethering.

**2. A software-off Wi-Fi radio reports as `tethering-failed:Unknown`.**

The radio can be off for ordinary reasons — a laptop toggle, an OEM utility disabling Wi-Fi while
wired. `StartTetheringAsync` then returns `Unknown`, and the player sees a failure that names no
cause and suggests no action.

*Fix:* check `Windows.Devices.Radios.Radio` in `hotspot-control.ps1` before starting, return a
distinct `radio-off` error, and optionally turn it on — `RequestAccessAsync` returns `Allowed`
unelevated, and `SetStateAsync` works without a prompt. Both spikes already do this.

### Separate: shipping it to someone else

Unchanged and out of scope for playing on this machine — `ZeroSetupPlan.md` blockers 1–3 (Node is
not bundled, dependencies install at runtime, `relay/` and `controller/` are not copied into the
build). A downloaded build still fails before any of the above matters.

### Housekeeping

`RockSkipping spike HTTP` / `RockSkipping spike DNS` firewall rules were added for the spikes and
are not needed by the game. `RockSkipping relay` and `RockSkipping spike DNS` are each registered
twice.

## The last offline blocker: the HOST cannot resolve its own relay

Found 2026-08-21, in-game, after the tethering fix landed. **The hotspot works offline. The game
still does not**, and the reason is neither the hotspot nor the phones.

From the Unity log of an offline run:

```
[HotspotService]   no internet profile; tethering from 'Ethernet 4' instead
[HotspotService] Tethering is on. Disabling IP forwarding on the hotspot interface
[LocalServices] Serving HTTPS for 192-168-137-1.local-ip.sh on :8443.
[RelayClient] Connect to wss://192-168-137-1.local-ip.sh:8443 failed (Unable to connect to
              the remote server); retrying in 2s
```

Tethering succeeded from a no-connectivity profile — the fix works. The relay started and is
serving. What fails is **Unity connecting to a relay running on the same machine**, because it
connects by *name* and the host has no way to resolve that name offline:

```
host DNS servers : 64.59.135.148, 64.59.128.114     (the ISP's resolvers)
hosts file       : no local-ip.sh entry
Resolve-DnsName 192-168-137-1.local-ip.sh -> 192.168.137.1   (online only, via public DNS)
```

This is exactly why it "only works when connected to Wi-Fi": online, the name resolves publicly;
offline, nothing answers.

**Phones are not affected.** They receive our responder as their resolver over the hotspot, which
is the path already proven end to end. The host is the odd one out: it does not take DHCP from
itself, so it never consults the responder bound on its own `0.0.0.0:53`.

### Recommended fix: a hosts entry, added by the elevated one-time setup

```
192.168.137.1  192-168-137-1.local-ip.sh
```

This is unusually safe, because **`local-ip.sh` names encode their own address**: the public DNS
answer for `192-168-137-1.local-ip.sh` *is* `192.168.137.1`. The entry duplicates the public
answer exactly, so it is correct whether or not the hotspot is running, online or off, and can
never point somewhere wrong. It also keeps TLS validation fully intact — the name still matches
the certificate.

It belongs beside the scheduled task and firewall rules in the installer's elevated step; it is
one line, written once, and needs no maintenance because Hotspot mode's address is fixed.

Scope: this fixes **Hotspot mode only**. LAN mode's address varies per network, so a static entry
cannot cover it — but LAN mode implies a network with working DNS, so it does not need one.

### The alternative, and why not

`RelayClient` could connect to `127.0.0.1:8443` and skip DNS. `LocalServices` already does
something similar for its own health probe, with `AcceptAnyCertificate` scoped tightly to it.

The difference matters: the health probe only ever asked "is the process I spawned alive", while
`RelayClient`'s connection is the game's actual control channel. Relaxing certificate validation
there widens the exception from a liveness check to the real protocol path, to avoid writing one
line in a file. The hosts entry costs an elevated write the installer is already doing.

## What went wrong in getting here

Recorded because the process failure was more expensive than the technical one.

The Wi-Fi Direct investigation ran for hours across many phone tests, chasing DNS ordering, SSID
naming, browser DoH settings and Android validation — while the actual answer was a four-line
function in our own code that had never been questioned. Two things would have caught it sooner:

1. **The "confirmed" finding was never re-derived.** `no-connection-profile` was accepted as
   evidence about Windows, when it was only evidence about `GetInternetConnectionProfile()`.
2. **Testing moved to the phone too early.** Every question answered on the host — is `:53` bound,
   is DHCP up, does a manager construct — is answered in seconds and repeatably. Every question
   pushed to the phone costs minutes and returns ambiguous results. The DNS proxy comparison that
   settled this whole decision is one `Get-NetUDPEndpoint` call.

# Decision: how the host runs a network with no internet

**Date:** 2026-08-21
**Status:** Decided, pending one confirmation run.
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

## The change

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

## The one thing still unconfirmed

Whether any tetherable profile still **enumerates** when the machine is fully offline. The
evidence says yes — the profiles above persist while their adapters are disconnected, and none of
them depend on Ethernet — but "very likely" is not "measured".

One run confirms it, and it is a single action rather than a loop:

```
./deploy/spike/hotspot-offline-spike.ps1 -WaitForOffline -KeepRunning
```

Unplug the cable when asked. It re-enumerates profiles *after* going offline, reports how many
remain tetherable, starts from one, and renders join QRs. If it reports zero tetherable profiles
offline, then Open Question 2 was right for the wrong reason, and Wi-Fi Direct plus a home-grown
DHCP server becomes the fallback — with the spike already written and the radio half proven.

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

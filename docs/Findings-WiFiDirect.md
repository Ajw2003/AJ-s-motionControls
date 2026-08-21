# Wi-Fi Direct — can the host run with no network at all?

**Spike:** [`deploy/spike/wifidirect-spike.ps1`](../deploy/spike/wifidirect-spike.ps1)
**Question:** [ZeroSetupPlan.md](ZeroSetupPlan.md) Open Question 2, reopened.
**Status:** Stage A passed. Stages B and C pending a real offline run.

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

## Stage B — the real question (pending)

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

## Stage C — a real phone (pending)

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

# Findings: what Rock Skipping already does for joining and testing

**Date:** 2026-10-09. **Prompted by:** aj's first real-phone runs on the Windows PC. A phone
couldn't reach the PC on the house Wi-Fi. It connected through a hotspot only while the PC
shared its one radio with the house Wi-Fi (very laggy). On a hotspot with no internet, the phone
joined but couldn't load the page. aj asked for a closer look at what Rock Skipping already
solved, so testing and onboarding stop being hand-assembled.

**Method:** read the Rock Skipping code carried into this repo. Nothing below was run on Windows
for this document. Where Rock Skipping's own docs record that something works on hardware, that is
cited, not re-tested.

## The main finding

Rock Skipping's joining experience is mostly **orchestration in Unity C#**, and this product
doesn't run Unity. The Node and PowerShell pieces it orchestrates are all here and work on their
own. What's missing is a small launcher that does what the Unity classes did, in order.

## Inventory

| Piece | Where | What it does | In this product today |
|---|---|---|---|
| Offline hotspot | `hotspot/hotspot-control.ps1` | Starts Mobile Hotspot with no uplink, tethering from any profile, Bluetooth PAN included. One JSON line out. | Works by hand. aj's run printed `{"ok":true,...}` tethering from "Bluetooth Network Connection". Not wired in (#12) |
| Start order | `Assets/Scripts/Net/HotspotService.cs:142` | Waits for the relay's DNS responder to be bound **before** tethering, so the first phone gets answers | Missing (#12) |
| Relay environment | `Assets/Scripts/Net/LocalServices.cs:386-406` | Starts the relay with `TLS_CERT`/`TLS_KEY`/`PORT`, `DNS_RESPOND_IP=192.168.137.1`, `DNS_ALLOW=192.168.137.0/24`, `DNS_NAME`, optional `SHARE_INTERNET` | Missing. The relay runs bare, so no DNS, no probe answers, no HTTPS (#12, #13) |
| Connectivity-probe answers | `relay/server.js` (`generate_204`, Apple probe page, port 80 listener) | Makes Android and iOS accept a hotspot with no internet, so they route to it instead of mobile data | Code present, off. The likely reason aj's offline run failed (#12) |
| Wi-Fi join QR | `Assets/Scripts/UI/JoinScreen.cs:138` | `WIFI:T:WPA;S:<ssid>;P:<pass>;;` with escaping. Both cameras offer "join network" | Missing (#14) |
| QR images | `relay/server.js:428` (`/qr?data=`) | Renders any short text as a QR, level M for scanning a TV at an angle | Present, unused (#14) |
| Controller-URL QR | `JoinScreen.cs:160` | Second QR: the page to open once joined | Missing; aj typed the address by hand (#14) |
| Hotspot name and password | `Assets/Scripts/Net/HotspotCredentialsStore.cs:100-103` | Random `Rock Skipping XXXX` name and 12-character password, made once and remembered per PC | Missing; the test used a typed `AJ Motion` / `motion1234` (#12) |
| Node runtime | `Assets/Scripts/Net/NodeProvisioner.cs:47` | Finds Node, or downloads a pinned portable copy on Windows | Missing; aj needed Node already installed (#12) |
| HTTPS certificate | `Assets/Scripts/Net/CertificateProvider.cs`, `deploy/fetch-seed-cert.ps1` | Real certificate for `play.skiprocking.ca`, cached so it works offline | Missing (#13) |
| One-time machine setup | `deploy/setup-offline.ps1` | Firewall rules, hosts entry, forwarding task; idempotent | Present, game-named; not part of this product's flow (#12) |
| Pre-flight check | `deploy/verify-stack.ps1` | Answers, in order, every question a failed session raises: Node, certificate, name resolution, setup applied, relay up over TLS, DNS bound and answering | Present, checks the game's setup (#15) |
| Radio-sharing check | `tools/measure-hotspot-jitter.ps1` | Says whether the PC's Wi-Fi radio is split between a network and the hotspot, from ping jitter | Present, unused. It measures exactly aj's laggy run |
| "Why no internet?" check | `relay/diagnose-hotspot.js` | Walks the phone's path link by link against a live session | Present, unused |
| Live guest view | `relay/watch-guests.js` | Shows every connection a hotspot guest opens to the PC | Present, unused |
| Join funnel | `relay/funnel.js` | Counts how far each device got: probe, page, socket | Present, game rooms only |

## What this means for the open issues

- **#12 (launcher)** should absorb: start order, relay environment, probe answers, generated and
  remembered hotspot credentials, Node provisioning, and the one-time setup check.
- **#13 (HTTPS)** reuses the certificate and DNS name as they are.
- **#14 (new):** a join page on the PC with the two QR codes, so nobody types an address.
- **#15 (new):** a pre-flight check for this product, adapted from `verify-stack.ps1`, so a failed
  test says which link broke instead of "it didn't connect".

## What this can't show

- Whether the probe answers are enough on aj's phone. Rock Skipping's records show Android
  behaviour varies by model (`docs/Findings-HotspotForwardingAndInternet-2026-10-01.md`).
- Whether Windows keeps the hotspot at `192.168.137.1` on aj's PC. Rock Skipping relies on it
  (`deploy/setup-offline.ps1`), but aj's address check ran after the hotspot was stopped, so it
  proved nothing either way.

# Offline-hotspot spike — can the PC answer DNS for its own guests?

**The question:** certificates can be cached, so the host only needs to be online once a
quarter for those. DNS cannot be cached — the cache that matters lives on **each phone**, and a
guest joining for the first time has nothing. If the PC can answer `*.local-ip.sh` itself while
acting as the access point, the last reason the host needs to be online at play time disappears
and **offline play becomes possible**.

Context: [../../ZeroSetupPlan.md](../../ZeroSetupPlan.md) Phase 3.6.

---

## Result: the spike succeeded, end to end, on a real phone.

| # | Question | Result |
|---|---|---|
| 1 | Can a process bind UDP `:53` **without admin**? | **YES** — Windows has no privileged-port rule. This was the biggest risk and it is gone: no elevation, no installer involvement. |
| 2 | Can a hand-rolled responder answer correctly? | **YES** — A, AAAA, and non-matching names all behave. |
| 3 | Does it work bound to all interfaces, as a phone would reach it? | **YES** — answered on `0.0.0.0:53`, verified via the LAN address. |
| 4 | Can other names still resolve, so phones keep normal internet? | **YES** — upstream forwarding returns real answers. |
| 5 | Does the Wi-Fi join QR render and scan? | **YES** — a phone joined the hotspot from the QR. |
| 6 | **Does ICS take `:53` when tethering starts, and can we hold it?** | **NO, AND YES.** The responder bound first, tethering started `Success`, and the responder remained the **sole holder** of `:53`. ICS did not contend for it. |
| 7 | Is the hotspot address really `192.168.137.1`? | **YES** — confirmed live while tethering was on. |
| 8 | **Does a real phone actually resolve through the responder?** | **YES** — `http://play.local-ip.sh/` loaded on the phone. That name resolves nowhere on the internet. |
| 9 | Does the firewall allow it out of the box? | **NO** — this was the one thing that failed, and it is why the game ships as an installer. See below. |

Verified on Node v22.19.0, Windows 11 Pro 26100, 2026-08-20.

### The run that settled it

```
[ok]   :53 is free
[ok]   responder holds :53 (pid 14888)
[ok]   band forced to 2.4 GHz (widest phone compatibility)
[ok]   configured SSID 'RockSkip Spike'
       StartTetheringAsync -> Success
[ok]   tethering: On
       :53 on 0.0.0.0 held by node (pid 14888)
[ok]   RESPONDER SURVIVED and is the only holder of :53.
[ok]   hotspot address is 192.168.137.1 (assumption confirmed)
```

Teardown restored tethering to `Off` and the SSID to its original value.

### The firewall finding — and why the game ships as an installer

With the hotspot up and both helpers confirmed listening on `0.0.0.0`, a phone on the hotspot
**timed out** reaching `192.168.137.1:80`. Timed out, not refused: the packets were *dropped*.

The cause is worth recording precisely, because it is invisible and will happen to anyone:

- node's existing inbound firewall rules were **`Private` profile only**
- the hotspot adapter (`Local Area Connection* 12`, `192.168.137.1`) has **no network category**
  — it does not appear in `Get-NetConnectionProfile` at all, so the firewall treats it as
  `Public`
- therefore **the rules did not apply to the one interface that mattered**, and nothing anywhere
  logged an error

Two scoped rules fixed it immediately:

```powershell
New-NetFirewallRule -DisplayName "RockSkip spike - DNS"  -Direction Inbound -Protocol UDP -LocalPort 53 -RemoteAddress 192.168.137.0/24 -Action Allow -Profile Any
New-NetFirewallRule -DisplayName "RockSkip spike - HTTP" -Direction Inbound -Protocol TCP -LocalPort 80 -RemoteAddress 192.168.137.0/24 -Action Allow -Profile Any
```

Remove them when finished:

```powershell
Remove-NetFirewallRule -DisplayName "RockSkip spike - DNS","RockSkip spike - HTTP"
```

**This is what settled the distribution format.** A zip cannot work: the first launch on any
machine that is not the developer's would fail exactly this way — a timeout, with no error to
read. The rules must be added at install time, with `-Profile Any`, and removed on uninstall.
See [../../ZeroSetupPlan.md](../../ZeroSetupPlan.md) Phase 4.

### Confirmed with a real phone — 2026-08-20

The last inferred step is now measured. A phone joined the hotspot and loaded
**`http://play.local-ip.sh/`**, which returned the confirmation page.

**Why that specific name is the whole proof:** `local-ip.sh` publishes DNS only for IP-encoded
names like `192-168-137-1.local-ip.sh`. **`play.local-ip.sh` exists in no public DNS at all.**
There is no resolver on the internet that answers it. The only way the phone could have resolved
it is that this PC answered the query — which also confirms ICS does advertise `192.168.137.1`
to its clients as their DNS server.

That closes the chain: **certificate cached + name resolved locally = the host needs no internet
at play time.**

One caveat on the same run: the raw-IP URL `192.168.137.1` did *not* load, while the name did.
Same address, same port, so this is a browser behaviour rather than a network one — a bare IP
typed into Chrome gets an HTTPS attempt first, and nothing listens on 443. The QR carried an
explicit `http://` scheme and worked. Worth knowing so it is not mistaken for a real fault.

### What "correct" meant for question 2

```
play.local-ip.sh          A     -> 192.168.137.1  NOERROR, authoritative, ttl 60
play.local-ip.sh          AAAA  -> NOERROR, 0 answers          <- not NXDOMAIN
example.com               A     -> NXDOMAIN (no upstream) / forwarded (with upstream)
192-168-137-1.local-ip.sh A     -> 192.168.137.1
```

The AAAA case is the one worth calling out. Returning `NXDOMAIN` for AAAA would mean "this name
does not exist" **for every record type**, and a phone that believes that never goes on to try
A. Answering an empty `NOERROR` is what makes dual-stack clients fall through to IPv4 — the
difference between working and a silent failure on some devices.

---

## Files

| File | What it does |
|---|---|
| `dns-responder.js` | The responder. No dependencies, so it can be folded into the relay process later without adding one. Logs every query — a phone's address in that log is the result the spike is after. |
| `dns-query.js` | Test client. Exists because Windows `nslookup` **silently ignores `-port=`** when a server is named, which looks exactly like the responder being broken. |
| `wifi-qr.js` | Renders the `WIFI:` join QR as terminal ASCII. Reuses `qrcode-generator` from `relay/node_modules`, so it adds nothing to bundle. |
| `hotspot-dns-test.ps1` | The remaining experiment. Disruptive — see below. |

## Running the parts that are safe

```bash
node deploy/spike/dns-responder.js --ip 192.168.137.1 --suffix .local-ip.sh --port 5399 --bind 127.0.0.1
```

```bash
node deploy/spike/dns-query.js play.local-ip.sh A 127.0.0.1 5399
```

```bash
node deploy/spike/wifi-qr.js --ssid "RockSkip TEST" --pass "skipping123"
```

**The QR one is worth doing now**, with no setup at all: point a phone at it. A prompt offering
to join "RockSkip TEST" proves the payload format is understood, even though the network does
not exist. Confirming that on **iOS** in particular removes an assumption the whole two-scan
flow rests on.

## The remaining experiment — disruptive

```bash
pwsh ./deploy/spike/hotspot-dns-test.ps1 -KeepRunning
```

**How disruptive this is depends on your uplink.** With an **Ethernet** uplink and the Wi-Fi
radio free, tethering runs on the Wi-Fi adapter and internet is untouched — this is how the
2026-08-20 run went, and it was a non-event. On a machine whose only adapter is Wi-Fi, tethering
shares that radio and the existing connection can drop. Check first:

```bash
powershell -Command "Get-NetAdapter | Select-Object Name, Status, InterfaceDescription"
```

The script binds `:53` **before** starting tethering, which is the entire experiment: ICS has to
find the port already taken. It then reports who holds it, and restores tethering state and the
original SSID on exit whatever happens.

Three outcomes, and each means something different:

- **Responder survives, sole holder of `:53`** — the good case, and **this is what happened**.
  Offline play is reachable and Phase 3.6 is worth building.
- **Both bound** — ICS took the hotspot address and we hold the rest. Then it depends on which
  address ICS hands phones as their DNS server, and only the phone test settles it.
- **Responder killed** — ICS must be displaced or replaced. That likely needs admin, which
  pushes it into the installer and makes it considerably less attractive.

With `-KeepRunning` it prints the join QR and waits, so a phone can be tested. **Watch the
responder window:** a query arriving from a `192.168.137.x` address is the proof that the phone
asked the PC rather than a router — and it is the one piece still missing.

## Two questions this spike deliberately does not answer

Both need the network genuinely cut, and they decide whether "offline" means *no internet* or
the stricter *no network*:

1. Does `CreateFromConnectionProfile()` succeed from a **connected-but-no-internet** profile?
2. Does tethering start with **no adapter connected at all**?

`GetInternetConnectionProfile()` returning null is itself informative — the script says so and
stops rather than pretending.

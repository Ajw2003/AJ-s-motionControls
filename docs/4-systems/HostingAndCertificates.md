# Hosting and certificates

**Source:** `Assets/Scripts/Net/LocalServices.cs`, `CertificateProvider.cs`, `MagicDomain.cs`,
`HotspotService.cs`; `hotspot/hotspot-control.ps1`, `deploy/setup-offline.ps1`; `relay/dns.js`

## The problem this system exists to solve

Browser motion sensors require a **secure context**. A secure context requires a **certificate**.
A certificate requires a **resolvable public name**. None of that exists on a living-room network
with no internet.

Without this system there is no game — only a demo that works on the developer's machine.

## The chain, end to end

```
Play pressed
   ↓
LocalServices          obtains a certificate, starts relay/server.js as a child process
   ↓                   waits for its own /health probe on loopback
CertificateProvider    cached cert if valid; else download; else the seed shipped in the build
   ↓                   renews inside 14 days of expiry — checked every launch, not scheduled
MagicDomain            says what name the cert covers: play.skiprocking.ca (overridable)
   ↓
relay/dns.js           binds 0.0.0.0:53 and answers that name with 192.168.137.1
   ↓                   must hold :53 BEFORE ICS starts, or ICS takes it
HotspotService         waits on LocalServices.ProbeDnsBound, then tethers via
   ↓                   hotspot/hotspot-control.ps1
Windows Mobile Hotspot raises the network; ICS hands out DHCP; address is always 192.168.137.1
   ↓
hosts entry            so the HOST can resolve its own relay — it takes no DHCP from itself
   ↓
phone scans QR #1 (Wi-Fi payload) → QR #2 (game URL) → HTTPS by name → sensors unlock
```

## Why each piece is the way it is

### The address is forced, not detected

`192.168.137.1` is what Windows ICS always assigns a hotspot. It is hardcoded in three places that
must agree — `LocalServices.HotspotHostAddress`, `HotspotService.HotspotAddress`, and
`setup-offline.ps1`'s `$RelayHostName` — and it is forced rather than discovered because a
detected address that came back wrong would produce a certificate mismatch nobody could diagnose
at a party.

### The certificate is single-name, on a domain we own

`local-ip.sh` — a public service that parsed an IP out of a hostname and published a wildcard
certificate with its private key given away openly — **died at the DNS layer**. Not degraded:
`NS local-ip.sh` and `SOA local-ip.sh` both time out, so nothing resolves. `local-ip.co`,
`traefik.me`, `sslip.io` and `nip.io` were each checked and rejected as replacements.

The replacement, shipped 2026-09-03: a single-name certificate for `play.skiprocking.ca`, renewed
unattended by `.github/workflows/renew-cert.yml` and published as a GitHub release asset on a
small public repo that holds nothing but the certificate. That repo is separate from the game repo
because the game repo is private. The `current` tag is fixed forever and the workflow re-uploads
over it.

`MagicDomain` is the single repointable source of truth, overridable per-machine by a
`magic-domain.txt` beside the executable — one hostname per line, then paired cert/key URLs.

**LAN mode is deprecated.** It was the only mode that needed public wildcard DNS at all, and
removing it is what removed the need. Hotspot is the only mode `StartScreen` launches; LAN is
selectable only by hand-editing `network-mode.txt`, and its certificate no longer validates,
because a single-name cert cannot cover LAN's varying address.

### Renewal is structural, not scheduled

Every launch checks. A certificate inside 14 days of expiry is renewed — far enough ahead that a
party is never the moment it happens. A missing certificate is not fatal either: a **seed
certificate** shipped inside the build covers a first launch that cannot reach the download URL.

### DNS has two halves, and they fail independently

The **guest** resolves `MagicDomain.HostName` via `relay/dns.js` on the hotspot subnet. The
**host** resolves it via a hosts-file entry, because the host does not take DHCP from itself.
`HotspotService` gates on both, separately, because either can fail on its own — and one of them
failing silently was the entire content of a DNS audit that started with "the DNS on the Unity
side seems to be broken". It was not. The host could not resolve the name its own relay is served
under, because the script that installs the hosts entry was being skipped, and nothing anywhere
checked.

### `relay/dns.js` is narrow on purpose

It is gated to the hotspot's own subnet via an `allow` CIDR, precisely *because* it answers with a
single fixed IP and only ever runs while a LAN with no other resolver exists. It **drops** packets
from outside that subnet rather than refusing them — any reply to a possibly-forged source is the
amplification behaviour being prevented. An open recursive resolver was a real incident on this
project, not a hypothetical.

### Machine setup runs itself

`deploy/setup-offline.ps1` registers the one-time, per-machine, elevated prerequisites: a
scheduled task that disables IP forwarding so guests are isolated (only when sharing is off; see
"Sharing internet (default)" below), firewall rules for the relay
and the DNS responder, and the hosts entry. `HotspotService.EnsureOfflineSetup` runs it elevated,
itself, the first time this machine ever starts a hotspot. **One UAC prompt** — Windows requires
that and there is no way around it — and every session after that skips straight past.

The script is idempotent above all, because it gets run repeatedly on machines in every possible
half-configured state. `Register-ScheduledTask -Force` is safe; `New-NetFirewallRule` and
`Add-Content` are **not**, and both happily create duplicates. Its first real run found a
duplicate relay rule, three obsolete spike rules, and two copies of the hosts entry — all created
by hand-running the individual commands during the investigation. That is the case idempotency
exists for, and it was not hypothetical.

```bash
powershell -File "C:/Users/aj/Desktop/GameDev/RockSkipping/deploy/setup-offline.ps1" -DryRun
```

`-DryRun` deliberately needs no elevation, because it writes nothing — which makes it the right
way to inspect a machine before changing it.

### Guest isolation is verified, not assumed

Isolation now applies only when "Share internet" is off. With it on (the default, 2026-10-02)
the forwarding check is skipped; see "Sharing internet (default)" below.

`hotspot-control.ps1` used to call `Start-ScheduledTask` and report success — proving only that a
task *launched*. It now reads the forwarding flag back. `HotspotService` treats that warning as
fatal, so an unisolated hotspot refuses to start rather than pretending.

**A single Disabled reading is not proof.** ICS turns forwarding back on about 2 s after the task
first disables it (traced twice, `tools/hotspot/trace-forwarding.ps1`). So the check re-runs the
task whenever forwarding reads Enabled, and reports isolation only after 6 s of continuous
Disabled, with a 20 s ceiling (#70). Whether ICS re-enables forwarding later in a session, for
example when a phone joins, is not yet measured.

### Sharing internet (default)

The lobby setting "Share internet" (`MatchSettingsStore.shareInternet`, default on) makes
`LocalServices` start the relay with `SHARE_INTERNET=1` and `HotspotService` run
`hotspot/hotspot-control.ps1 -ShareInternet`, which skips the forwarding-disable task and reports
`shared`. Turning it off takes effect on the next hotspot start. The relay picks the host's first
usable IPv4 resolver (`dns.getServers()`) as upstream and forwards other names to it. The phones'
connectivity-probe names are forwarded too, and answered locally only if upstream is silent for
1.5 s; the game name is always answered locally. The hotspot-subnet allow guard is unchanged.
Guests can reach the home router and LAN; the host accepted that
([`Decisions.md`](../6-decisions/Decisions.md), 2026-10-02). If sharing was asked for but not achieved,
`HotspotService` logs a non-fatal warning.

## The QR rule — binding

> **Always render a QR code for network or URL joining. Never ask someone to type an address or
> hunt for one.**

This was a standing project-wide rule in a document that has since been deleted. The rule stands.
Two QRs: the Wi-Fi payload, then the game URL. A room code exists as a spoken fallback and uses an
alphabet with no `I` and no `O`, because it gets read aloud off a television and typed on a phone.

## Invariants

- **`192.168.137.1` in all three places, or the certificate does not match.**
- **The DNS responder holds `:53` before tethering starts.** Otherwise ICS takes it.
- **Both halves of name resolution are checked separately.**
- **`MagicDomain` is the only place the hostname is written.** Not a second literal anywhere.
- **The host address is validated, never trusted blindly.**

## Traps

**Stale tethering wedges the next launch, and restarting does not clear it.** Tethering outlives
the process that started it. A crashed Editor leaves the hotspot up, ICS keeps `0.0.0.0:53`, and
the next launch cannot bind the DNS responder — then reports *"The local DNS responder never
bound… Restart and try again"*, advice that cannot work because the cause is still running.
Observed twice.

**Hosting while the host is associated to Wi-Fi splits one radio across two bands, and the phones
feel it — mitigated, not eliminated, since 2026-09-09 (`796854a`).** This adapter class reports
`Soft AP : Not supported`, so Mobile Hotspot runs as a Wi-Fi Direct GO on the *same physical
radio* as the station link. `hotspot-control.ps1`'s `Get-StationBandName` now asks the access
point for whichever band the host's own station link is already on — 5 GHz instead of always
2.4 GHz — so the radio serves both on the same channel instead of time-slicing between two. That
took the school-network measurement from `mean 156.9 / stdev 194.3 ms` to `mean 5.0 / stdev
2.5 ms` (`../plans/aim-preview-lag-on-shared-radio.md`).

This is a **one-time band match at hotspot start-up, not ongoing RF management.** Windows exposes
no API to read back the channel actually chosen, so `HotspotService.RadioSharingWarning` reports
only whether the requested band was honored ("may" feel laggy), never confirms it. There is no
DFS channel forcing, no 6 GHz support (the `TetheringWiFiBand` enum has no 6 GHz member), no RSSI
monitoring, and no mid-session channel switching in response to congestion — `Get-StationBandName`
also still defaults an *unassociated* host to 2.4 GHz for phone compatibility and room range, which
costs real jitter in a congested building with many other 2.4 GHz networks (see the "Open question"
in the plan doc, and `../grant/Findings-RiskMatrixAudit.md` item 1 for the gap this leaves against
convention-hall-scale RF density). `tools/measure-hotspot-jitter.ps1` remains the way to check a
given machine rather than assume the fix applies.

**A software-off Wi-Fi radio reports as `tethering-failed:Unknown`,** which reads exactly like
"Windows cannot do this offline". This confounded two separate investigations. The radio must be
**on but need not be associated**.

**Windows PowerShell 5.1 reads a BOM-less UTF-8 `.ps1` as ANSI,** so an em dash in a string is a
parse error. Keep new `.ps1` files ASCII-only. Several existing ones contain em dashes and work
only because they sit in comments.

**`$ErrorActionPreference = 'Stop'` plus a native command is a trap.** PowerShell turns stderr
into a throwing ErrorRecord, so a harmless notice can kill a script mid-run.

**Certificate expiry is silent until it is not.** The cache is used correctly and renewal does not
need the host online, but a machine that is *always* offline will eventually run one party past
expiry with no warning. Surfacing days-remaining on the join screen is still unbuilt.

## Verification

```bash
powershell -File "C:/Users/aj/Desktop/GameDev/RockSkipping/deploy/spike/hotspot-offline-spike.ps1" -PreferNoConnectivity -EnableRadio -Dns
```

Runs **online** and still proves the offline mechanism, by deliberately tethering from a
zero-connectivity profile. No cable pulling, no phone. Reports profile selection, tethering status,
address, DHCP, and who holds `:53`.

Also: `deploy/Verify-PublishedCert.ps1` (independent check of the published certificate),
`deploy/verify-stack.ps1`, and `node relay/diagnose-hotspot.js` against a live game.

## Further reading

[`../Decision-OfflineHosting.md`](../6-decisions/Decision-OfflineHosting.md) (why Hotspot beat Wi-Fi Direct),
[`../OfflineSetup.md`](../OfflineSetup.md) (what a machine needs),
[`../plans/magic-domain-replacement.md`](../plans/magic-domain-replacement.md) (the certificate
design), [`../../deploy/README.md`](../../deploy/README.md) (the runbook).

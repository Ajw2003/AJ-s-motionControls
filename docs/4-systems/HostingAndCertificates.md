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

## Notes moved from source comments

The author's reasoning, moved verbatim out of the code (issue #84/#85). Each note cites the code it describes; the code carries a `doc-ref` pointer to its note.

### `Assets/Scripts/Net/CertificateProvider.cs` — Obtains the TLS material the relay needs, with no human involved.
<!-- ref:3e74 -->

Was `Assets/Scripts/Net/CertificateProvider.cs:12-44`.

Obtains the TLS material the relay needs, with no human involved.


Phone motion sensors require a secure context, a secure context requires a publicly
trusted certificate, and a certificate requires a name — for a machine on a private
address that used to be a circle with no obvious exit. local-ip.sh used to break it by
publishing a real wildcard for *.local-ip.sh together with DNS that mapped IP-encoded
labels back to the address they encoded. It is permanently dead (verified at the DNS
layer — see docs/plans/magic-domain-replacement.md), and there is no drop-in
replacement, so this now downloads a single-name certificate for a domain we own
instead (MagicDomain.HostName, renewed by .github/workflows/renew-cert.yml). LAN mode,
the only mode that ever needed an address-varying wildcard, has been removed — Hotspot
is the only mode now, and its address is always the fixed 192.168.137.1
(LocalServices.HotspotHostAddress) — one name is enough.

Its private key is published deliberately. That is what makes this possible and also
what caps what it may ever be used for: the certificate proves nothing about who is
serving, it only unlocks the sensor APIs. Nothing sensitive may travel over it.
SecurityReview.md section 1 is the long version.

This replaces deploy/local-https.ps1, which did the same work but required somebody to
remember to run it. The failure that motivated the change: the script's own chain check
calls a file deleted in 1cd7647, so re-running it after the cached certificate expires
fails in a way that looks nothing like "your certificate expired".

Renewal is structural rather than scheduled. Every launch checks, and a 90-day
certificate cannot lapse between two launches of a game nobody has opened in three
months, because opening it is the check. On a launch where the cache is stale AND the
configured source(s) are unreachable, TrySeedCache() falls back to a certificate shipped
in StreamingAssets rather than failing outright — see its remarks for what that does and
does not cover.

### `Assets/Scripts/Net/CertificateProvider.cs` — Confirms the address handed in is a well-formed IPv4 address.
<!-- ref:12b7 -->

Was `Assets/Scripts/Net/CertificateProvider.cs:209-217`.

Confirms the address handed in is a well-formed IPv4 address. This used to be the
override branch of a larger method (ResolveLanAddress) that could also detect an
address by sweeping network interfaces — the auto-detection body is gone now that
Hotspot is the only mode and its address (LocalServices.HotspotHostAddress) is
always forced rather than discovered. What remains is the one check that was ever
reachable from that path: a malformed configured address fails at the phone in a
way indistinguishable from a broken certificate, so it is caught here instead.

### `Assets/Scripts/Net/CertificateProvider.cs` — Confirms the SAN really covers the configured hostname, which catches a truncated or swapped download that...
<!-- ref:2f35 -->

Was `Assets/Scripts/Net/CertificateProvider.cs:306-318`.

Confirms the SAN really covers the configured hostname, which catches a truncated
or swapped download that would otherwise only surface as a name mismatch on
somebody's phone.


Named CoversWildcard for history — with local-ip.sh's wildcard gone, the
certificate <see cref="MagicDomain"/> configures is single-name
(docs/plans/magic-domain-replacement.md Phase 1 explains why a wildcard is no
longer needed at all), so this now checks for an exact match rather than a
leading <c>*.</c> label. Not renamed, to keep this diff to the behaviour change
rather than a name that would otherwise need updating at every call site too.

### `Assets/Scripts/Net/CertificateProvider.cs` — Fetches both files, writing each only once it has arrived complete.
<!-- ref:0584 -->

Was `Assets/Scripts/Net/CertificateProvider.cs:334-347`.

Fetches both files, writing each only once it has arrived complete.


Downloading straight over the cache would turn a dropped connection into a
corrupted cache — and the fallback path exists precisely for hosts whose
connection is unreliable, so that is not a rare combination.

MagicDomain.CertUrls/KeyUrls are lists, tried in order as a pair — index 0's cert
with index 0's key, and so on — rather than a single hardcoded URL. One entry
today, same as local-ip.sh ever had; a mirror later (a second CDN, a direct R2
object URL as a fallback for a custom-domain outage) costs no code, only another
line in magic-domain.txt or the compiled default list.

### `Assets/Scripts/Net/CertificateProvider.cs` — Copies the certificate bundled in StreamingAssets into the cache, so a brand-new install still reaches Read...
<!-- ref:8019 -->

Was `Assets/Scripts/Net/CertificateProvider.cs:394-411`.

Copies the certificate bundled in StreamingAssets into the cache, so a brand-new
install still reaches Ready on a day none of MagicDomain.CertUrls is reachable —
today's local-ip.sh outage, next time as a CDN hiccup instead of a shipped-domain
outage. There is no real certificate to seed with yet (the renewal pipeline has
never run), so on a build with nothing placed at
StreamingAssets/server.pem/server.key this is a harmless no-op and Ensure() falls
through to the same fatal Result it always returned — this never fabricates a
certificate, only relocates one that was deliberately placed ahead of time.


Plain File I/O rather than UnityWebRequest against
<c>Application.streamingAssetsPath</c>: this project ships Windows Standalone only
(every other elevation/registry/ICS assumption in this codebase already commits to
that), where StreamingAssets is an ordinary uncompressed folder next to the
executable — the platforms where that assumption breaks (Android's compressed
APK, WebGL) are not targets here.

### `Assets/Scripts/Net/ChildProcessGroup.cs` — Binds spawned child processes to the lifetime of this one, so a development relay cannot outlive the game.
<!-- ref:348f -->

Was `Assets/Scripts/Net/ChildProcessGroup.cs:8-26`.

Binds spawned child processes to the lifetime of this one, so a development relay
cannot outlive the game.


Killing a process on Windows does NOT kill its children. Without this, every
crashed editor session, every "stop responding and force quit", and every domain
reload at the wrong moment leaves an orphaned node.exe holding port 3000. The next
launch then finds the port taken and fails in a way that looks like a bug in the
game.

A job object with KILL_ON_JOB_CLOSE is the OS-level fix: when the last handle to
the job closes — including when the process holding it dies for any reason,
gracefully or not — Windows terminates every process in the job. That covers the
crash case, which an OnApplicationQuit hook cannot.

LocalServices still kills its children explicitly on the way out. This is the
backstop for when there is no way out.

### `Assets/Scripts/Net/HotspotCredentialsStore.cs` — Reads and writes the host's Wi-Fi hotspot credentials as a plain JSON file in this Windows user's app-data...
<!-- ref:69e1 -->

Was `Assets/Scripts/Net/HotspotCredentialsStore.cs:8-30`.

Reads and writes the host's Wi-Fi hotspot credentials as a plain JSON file in this
Windows user's app-data folder, so a returning phone can remember this network instead of
being handed a fresh SSID and passphrase — and therefore a fresh QR to scan — every launch.


The file lives in <c>Application.persistentDataPath</c>
(<c>%USERPROFILE%\AppData\LocalLow\&lt;company&gt;\&lt;product&gt;</c>), not beside the
executable like <see cref="Managers.MatchSettingsStore"/> (#81). The installer puts the
game in Program Files, which the game cannot write to, so a save beside the exe failed and
every launch minted a new network that no phone had saved. A new build folder did the same.
App data survives both, and the Editor and every build on one PC share it. A plain file,
not PlayerPrefs, so a host can still open it by hand to read the passphrase off a stuck TV.

A file left beside the executable by an older version is copied over once, never moved,
so phones that saved that network keep it and the tools that read it there still work.

Security note: a guest who once played keeps access to this hotspot forever, since the
credentials no longer change. With Share internet on (the default since #72) a
returning guest gets this PC's internet and LAN, which the host accepted by leaving it
on; with it off, forwarding stays disabled and they reach only the game's relay and
the other phones on it.

### `Assets/Scripts/Net/HotspotService.cs` — Drives Windows Mobile Hotspot for Hotspot mode via hotspot/hotspot-control.ps1 — a short-lived child proces...
<!-- ref:3614 -->

Was `Assets/Scripts/Net/HotspotService.cs:25-41`.

Drives Windows Mobile Hotspot for Hotspot mode via hotspot/hotspot-control.ps1 — a
short-lived child process, since Unity/Mono cannot call the WinRT tethering APIs
directly. See docs/plans/network-mode-selection.md.


Waits for LocalServices to reach Ready, and specifically for its DNS responder to have
actually finished binding :53 (LocalServices.ProbeDnsBound — Ready alone only proves the
HTTP listener is up), before starting tethering. The responder must hold :53 before ICS
starts, or phones on the hotspot cannot resolve the game's address at all.

Name resolution has two halves and this class gates on both, because they fail
independently and look nothing alike. The responder above is how a PHONE resolves
MagicDomain.HostName; VerifyHostNameResolves is how the HOST does, via the hosts entry
deploy/setup-offline.ps1 writes. Phones working while the host does not is the normal
shape of this being broken.

### `Assets/Scripts/Net/HotspotService.cs` — Not a failure, so this is checked after the fatal warning above rather than beside it.
<!-- ref:0fd3 -->

Was `Assets/Scripts/Net/HotspotService.cs:260-268`.

Not a failure, so this is checked after the fatal warning above rather than
beside it. bandMatched is false when the host's station link is on a band the
access point cannot follow it onto (6 GHz), or when the adapter refused the
band outright. Either way one radio is serving two channels, it time-slices,
and the phones feel it long before anyone thinks to blame Wi-Fi.

Note this reports what was ASKED for. The driver places the access point, and
Windows exposes no way to read back the channel it chose - so the wording says
"may", and tools/measure-hotspot-jitter.ps1 is how you find out for certain.

### `Assets/Scripts/Net/HotspotService.cs` — Registers the one-time, elevated, per-machine prerequisites Hotspot mode needs — the same three things depl...
<!-- ref:1910 -->

Was `Assets/Scripts/Net/HotspotService.cs:281-295`.

Registers the one-time, elevated, per-machine prerequisites Hotspot mode needs —
the same three things deploy/setup-offline.ps1 has always set up by hand (the
forwarding-disable scheduled task, firewall rules, and the host's own hosts entry)
— automatically, the first time this machine ever reaches this point, instead of
requiring someone to find and run that script themselves first.


Windows itself still requires one UAC prompt — there is no way around that for an
operation this privileged, and there should not be — but it now happens in place,
triggered by pressing Start, rather than being a separate manual step nobody is
told about until hotspot-control.ps1 fails four steps later with a cryptic
scheduled-task error. Every later Play session on this machine skips straight past
this: the cheap unelevated -DryRun below reports nothing left to change.

### `Assets/Scripts/Net/HotspotService.cs` — Ask the script itself whether this machine is set up, rather than re-implementing the question here.
<!-- ref:7791 -->

Was `Assets/Scripts/Net/HotspotService.cs:307-317`.

Ask the script itself whether this machine is set up, rather than re-implementing
the question here. -DryRun writes nothing and needs no elevation, and answers via
its exit code: 0 already correct, 3 setup needed, anything else a real problem.

This used to ask a cheaper proxy instead — "does the RockSkippingDisableForwarding
scheduled task exist?" — and that proxy is what broke Hotspot mode on every machine
set up before the magic-domain replacement. The task was registered in the
local-ip.sh era and is still there, so setup was skipped, so the hosts entry for
MagicDomain.HostName was never added, so the host resolved its own relay via public
DNS to a parked address and retried against it forever. A proxy that stops implying
what it used to imply fails silently, which is the worst way for this to fail.

### `Assets/Scripts/Net/HotspotService.cs` — Confirms this machine resolves <see cref="MagicDomain.HostName"/> to the hotspot address, which is what the...
<!-- ref:4a38 -->

Was `Assets/Scripts/Net/HotspotService.cs:438-456`.

Confirms this machine resolves <see cref="MagicDomain.HostName"/> to the hotspot
address, which is what the hosts entry setup-offline.ps1 writes is for.


The single most misleading failure in this design, and the reason it is checked
explicitly rather than left to fail downstream. The host never consults the DNS
responder on its own :53 — it takes no DHCP from itself — so it asks a public
resolver instead, and play.skiprocking.ca is a real registered name whose public
record is the registrar's parking page. A missing hosts entry therefore does not
look like "unknown host". It looks like the relay is up but rejecting the
connection, because Unity is talking to a stranger's web server on the internet.
RelayClient retries that forever without ever saying anything is wrong.

Run after EnsureOfflineSetup, so the entry it writes is already in place, and
before tethering, so a wrong answer is reported instead of a hotspot nobody can
use. Deliberately not gated on the address existing yet — the hosts file is
consulted whether or not the hotspot adapter is up.

### `Assets/Scripts/Net/LocalServices.cs` — Runs the relay on this machine.
<!-- ref:80a3 -->

Was `Assets/Scripts/Net/LocalServices.cs:27-44`.

Runs the relay on this machine. This is how the game is hosted — not a dev convenience.


ON BY DEFAULT, and there is nothing to fall back to: the hosted Fly.io relay this used
to be an alternative to was deliberately torn down. Bootstrap's startLocalServices
creates this, and RelayClient takes its address from SecureSocketUrl rather than
needing a relay-url.txt — that file is now only the manual escape hatch for pointing a
build at a relay somebody started by hand.

HTTPS is not optional in practice. Phone motion-sensor APIs only unlock on a secure
origin, so the certificate step (CertificateProvider, MagicDomain) runs before the
relay and hands it TLS material through the environment.

This class used to also spawn a cloudflared tunnel and scrape the assigned hostname
out of its stderr, because a quick tunnel offers no machine-readable channel for it.
A fixed service address removed the need for both.

### `Assets/Scripts/Net/LocalServices.cs` — Where the relay actually lives, derived from the same relay-url.txt RelayClient reads so the two can never...
<!-- ref:9fca -->

Was `Assets/Scripts/Net/LocalServices.cs:113-125`.

Where the relay actually lives, derived from the same relay-url.txt RelayClient
reads so the two can never disagree about it.


This used to be hardcoded to http://127.0.0.1:3000, which quietly broke the reuse
path below for any relay not on that address: a relay started by hand on another
port looked absent, so this spawned a second one that nothing ever connected to.

This is the address shown to a human (log messages, the Fail() hint) and handed to
a phone (SecureSocketUrl, JoinUrlFor) — it is NOT what LocalServices probes its own
process with. See ProbeHealthUrl for why those had to split.

### `Assets/Scripts/Net/LocalServices.cs` — The address ProbeHealth/ProbeDnsBound actually connect to — loopback, once a certificate is in play, never...
<!-- ref:bf91 -->

Was `Assets/Scripts/Net/LocalServices.cs:130-149`.

The address ProbeHealth/ProbeDnsBound actually connect to — loopback, once a
certificate is in play, never the public hostname in HealthUrl.


In Hotspot mode the certificate's address (192.168.137.1) is not reachable on
this host until Hotspot tethering has actually started — which happens only after
this class reaches Ready. Probing the public hostname here deadlocks forever:
LocalServices waits on its own health check to reach Ready, HotspotService waits
on Ready before starting tethering, and the address the health check needs does
not exist until tethering has started. Loopback sidesteps the whole cycle — it is
always reachable regardless of hotspot state, because this is really just asking
"is the process I just spawned still alive," not a network question at all.

A phone never sees this URL — SecureSocketUrl/JoinUrlFor still hand out the real
hostname, validated normally by every phone that connects to it. Loopback HTTPS
needs its own certificate handling since the cert's SAN never includes
"127.0.0.1"; see AcceptAnyCertificate for why that is scoped tightly enough to be
safe here and nowhere else.

### `Assets/Scripts/Net/LocalServices.cs` — The one address a hotspot host is ever reachable at.
<!-- ref:250a -->

Was `Assets/Scripts/Net/LocalServices.cs:222-230`.

The one address a hotspot host is ever reachable at. Forced rather than detected —
the hotspot's own AP adapter has no default gateway of its own (it hands out this
same address as a gateway to its clients but has none configured on itself), so any
attempt to detect it by sweeping network interfaces would fail or guess wrong every
time. With Hotspot the only mode, this is simply a constant rather than something
to compute per launch — see CertificateProvider.ValidateHostAddress, which is all
that is left to check once nothing needs discovering.

### `Assets/Scripts/Net/LocalServices.cs` — Probes /health and reports whether the DNS responder has actually finished binding — a plain health-endpoin...
<!-- ref:eeb6 -->

Was `Assets/Scripts/Net/LocalServices.cs:402-412`.

Probes /health and reports whether the DNS responder has actually finished
binding — a plain health-endpoint success only proves the HTTP listener is up.


relay/server.js initiates the UDP :53 bind inside its httpServer.listen()
callback but does not await it before that callback returns, so /health can
answer before the bind has resolved one way or the other. HotspotService needs
the stronger guarantee before starting tethering, since the responder must win a
race against ICS for :53 — this turns that inferred timing into a confirmed one.

### `Assets/Scripts/Net/MagicDomain.cs` — Single source of truth for the hostname the certificate covers and where to download it from, replacing the...
<!-- ref:6fda -->

Was `Assets/Scripts/Net/MagicDomain.cs:8-29`.

Single source of truth for the hostname the certificate covers and where to download
it from, replacing the constant that used to be duplicated across
<see cref="CertificateProvider"/>, <c>relay/server.js</c>'s <c>DNS_SUFFIX</c> default,
and <c>setup-offline.ps1</c>'s <c>$RelayHostName</c> — drift
docs/Reference-Constants.md already flagged before any of those three had a reason to
disagree with each other.


See docs/plans/magic-domain-replacement.md for why this exists at all: local-ip.sh (the
service that used to publish both the wildcard DNS and the wildcard certificate this
domain replaces) is permanently dead, and there is no drop-in replacement. The fix moves
the certificate to a domain we own — <c>play.skiprocking.ca</c> — renewed by an
unattended GitHub Actions job (.github/workflows/renew-cert.yml) rather than borrowed
from a third party.

Windows ICS always assigns the hotspot address 192.168.137.1, and Hotspot is now the
only mode there is (LAN mode was removed entirely), so unlike local-ip.sh's wildcard
there is now exactly one address that is ever relevant and exactly one name is ever
needed to cover it — a single-name certificate, not a wildcard, and
CertificateProvider.CoversWildcard's exact-name check reflects that.

### `Assets/Scripts/Net/MagicDomain.cs` — Where the renewed certificate lands.
<!-- ref:6147 -->

Was `Assets/Scripts/Net/MagicDomain.cs:42-66`.

Where the renewed certificate lands. A list, not a single URL, so a second mirror
costs no code — Download() in CertificateProvider tries each in order and only the
first success matters.


A GitHub release asset on a small public repo that holds nothing but the
certificate, published by .github/workflows/renew-cert.yml. Cloudflare R2 was the
first choice and was rejected: enabling R2 requires a payment method on file even
within its free tier, and this whole design exists to have no recurring cost.

It is a separate repo from the game because the game repo is private, and a
release asset on a private repo needs an Authorization header to fetch — the
download below is an anonymous UnityWebRequest from a player's machine.

The <c>current</c> tag is fixed forever and the workflow re-uploads over it with
--clobber, so this URL is stable across renewals. Changing the tag would strand
every already-shipped build, which is why it is not a thing anyone should tune.

Live and verified: the renewal workflow has published a production certificate here
since 2026-09-03. deploy/Verify-PublishedCert.ps1 downloads from these exact URLs
anonymously and checks everything CertificateProvider.Inspect() checks plus the key
pairing and chain shape it does not — run it any time to confirm the URLs still
serve something usable, without needing Unity, a build, or any credential.

### `Assets/Scripts/Net/NodeProvisioner.cs` — Finds a usable Node.js runtime, downloading a portable copy when this machine has none — the thing that was...
<!-- ref:7972 -->

Was `Assets/Scripts/Net/NodeProvisioner.cs:10-26`.

Finds a usable Node.js runtime, downloading a portable copy when this machine has
none — the thing that was missing for a clean install to ever reach the npm stage.


Before this, a machine with no Node on PATH failed at "npm install" with an exit
code that named npm, not Node — the actual missing piece was never installed, only
asked for. This downloads the official Windows zip distribution (no installer, no
admin rights, no PATH edit) into <see cref="Application.persistentDataPath"/> and
hands back its node.exe, which InstallDependencies then uses directly to run npm's
own CLI script — so npm never has to be found on PATH either.

Windows-only, matching every other elevation/registry/ICS assumption already baked
into this project's networking stack (see CertificateProvider's remarks on
StreamingAssets for the same call). A non-Windows editor falls back to a PATH-only
lookup and the same "install it yourself" failure this replaces.

### `Assets/Scripts/Net/ProcessUtil.cs` — Child-process plumbing shared by the long-running relay (drained continuously via Update, expected to outli...
<!-- ref:9fef -->

Was `Assets/Scripts/Net/ProcessUtil.cs:11-22`.

Child-process plumbing shared by the long-running relay (drained continuously via
Update, expected to outlive the session) and the short-lived hotspot-control script
(runs once, prints one line, exits) alike.


Extracted from LocalServices, which used to own this outright. Both shapes need the
same three things — a job-object-adopted child so a crash cannot orphan it, both
std streams drained so a chatty child never blocks on a full pipe buffer, and the
PATH-plus-known-install-locations executable search — so one copy is safer than two
that can drift out of sync.

### `Assets/Scripts/Net/ProcessUtil.cs` — Starts a process with the given environment overrides layered on top of this process's own environment, and...
<!-- ref:844e -->

Was `Assets/Scripts/Net/ProcessUtil.cs:25-38`.

Starts a process with the given environment overrides layered on top of this
process's own environment, and adopted into <paramref name="group"/> so an unclean
shutdown cannot orphan it.

<param name="stdout">Every stdout line, in order.</param>
<param name="stderr">
Every stderr line, in order. Defaults to <paramref name="stdout"/> — the relay's
existing behavior, where a consumer just echoes whatever arrives and interleaving
doesn't matter. Pass a separate queue when a caller needs to read stdout alone
after exit (e.g. to parse one line of structured output) — both streams are always
drained regardless, since an unread pipe blocks the child, but they don't have to
land in the same place.
</param>

### `Assets/Scripts/Net/RoomCodeStore.cs` — Remembers the room code across launches, so a returning phone's stored/remembered join URL still points som...
<!-- ref:588e -->

Was `Assets/Scripts/Net/RoomCodeStore.cs:7-23`.

Remembers the room code across launches, so a returning phone's stored/remembered join
URL still points somewhere real after the host restarts, instead of landing on the join
form because the room code changed underneath it.


Deliberately not PlayerPrefs, for the same reason <see cref="Managers.MatchSettingsStore"/>
isn't: on Windows that writes into the registry, which is invisible and isn't cleared by
deleting the game folder. A plain one-line file beside the executable follows the same
convention already used for <c>relay-url.txt</c> and <c>relay-env.txt</c>.

This only seeds <c>preferredCode</c> on <see cref="RelayClient"/>'s first CREATE_ROOM —
the relay (<c>handleCreateRoom</c>) still decides whether the code is actually free and
may hand back a different one, which <see cref="RelayClient"/> then saves here via
<see cref="Save"/>. So this file records "the code we'll ask for next time", not a
guarantee.

### `deploy/setup-offline.ps1` — Why exit 3 means "not set up yet"
<!-- ref:c36f -->

Was `deploy/setup-offline.ps1:456-464`.

Exit 3 = "nothing is wrong, but this machine is not set up yet". HotspotService.cs runs this
script with -DryRun (which needs no elevation) purely to ask that question before deciding
whether to raise a UAC prompt, so it needs an answer it can read from an exit code.

It used to ask a cheaper proxy question instead - "does the scheduled task exist?" - and that
proxy went stale the moment this script grew steps the task does not imply. On every machine
set up before 2026-09-03 the task was present while the play.skiprocking.ca hosts entry was
not, so setup was skipped, the host could not resolve its own relay, and the game sat retrying
forever. One question, answered by the script that defines the answer.

### `deploy/spike/dns-responder.js` — Mirroring output to a file, synchronously
<!-- ref:25f5 -->

Was `deploy/spike/dns-responder.js:37-44`.

Mirror everything to a file as well as the console. The console lives in a window the
person running this may not be watching - and during the 2026-08-20 run it was a separate
window nobody could read back, which made the single most important output of the spike
something that had to be relayed by hand.
Appended synchronously on purpose. A buffered stream loses whatever it is holding when the
process is killed rather than Ctrl+C'd, which is how a test rig usually dies - and losing
the last few lines loses exactly the ones that matter. The volume here is a handful of DNS
queries, so the cost of a sync write is irrelevant.

### `deploy/spike/hotspot-dns-test.ps1` — Upstream forwarding and the 0.0.0.0 bind
<!-- ref:1715 -->

Was `deploy/spike/hotspot-dns-test.ps1:133-144`.

192.168.137.1 is the conventional Windows ICS host address; confirmed below.

Upstream forwarding matters more than it looks. Without it every other name returns
NXDOMAIN, the phone decides the network has no internet, and Android in particular
may quietly fall back to mobile data - which fails the test for a reason that has
nothing to do with what is being tested. Forwarding keeps the phone connected and
still discriminates, because play.local-ip.sh resolves nowhere but here.
Bound to 0.0.0.0 out of necessity, not preference: 192.168.137.1 does not exist until
the hotspot is up, and we must hold :53 BEFORE that to win the race against ICS. The
source guard (--allow) is therefore the real protection here - without it, listening
everywhere while forwarding upstream is an open resolver and an amplification
reflector. See ../../docs/SecurityReview.md section 4.

### `deploy/verify-stack.ps1` — Accepting any certificate on a loopback probe
<!-- ref:335c -->

Was `deploy/verify-stack.ps1:283-291`.

The certificate's SAN never covers 127.0.0.1, and probing loopback is a liveness check on a
process this script just started rather than a trust decision - the same reasoning, and the
same narrow scope, as LocalServices.AcceptAnyCertificate.

A compiled delegate rather than the usual `= { $true }` scriptblock. .NET invokes the
validation callback on a TLS worker thread, which under -NonInteractive has no runspace to
run PowerShell in, so the scriptblock form fails with "There is no Runspace available to run
scripts in this thread" - surfaced as a bare "the underlying connection was closed", which
reads exactly like the relay refusing the connection.

### `hotspot/hotspot-control.ps1` — Verify the isolation fact, not the task launch
<!-- ref:da53 -->

Was `hotspot/hotspot-control.ps1:371-385`.

ICS re-enables forwarding on every tethering start, so this must run fresh each
session - the scheduled task exists precisely to do that without a UAC prompt per
party (docs/ZeroSetupPlan.md Phase 4.2 / 5). A missing/failed task means the hotspot
is up but NOT isolated: HotspotService treats a non-null warning here as a failure,
not a soft caveat, and stops tethering rather than leave that state running.

VERIFY THE FACT, NOT THE LAUNCH. This used to report success when Start-ScheduledTask
returned, which proves only that a task was started - not that forwarding changed, and
not that it stayed changed. It reported isolation on a machine that measurably had
none, for as long as the task existed, which is the worst way for this to fail. The
state is read back below instead, and it needs no elevation to read.

Polled rather than read once, because this is a race: the task runs asynchronously and
ICS may re-assert forwarding after it. If the flag will not stay down within the
window, that is the honest answer and the caller should act on it.

### `hotspot/hotspot-control.ps1` — Tethering needs a connection profile, not internet
<!-- ref:83ad -->

Was `hotspot/hotspot-control.ps1:226-233`.

Tethering needs *a* connection profile to hang off. It does not need that profile to carry
internet, or even to be connected - measured 2026-08-21, see docs/6-decisions/Decision-OfflineHosting.md.

This used to ask only GetInternetConnectionProfile(), which returns null whenever there is no
internet, and reported that as no-connection-profile. That single call is the entire reason
Hotspot mode appeared not to work offline, and why ZeroSetupPlan.md recorded Open Question 2
as "answered - no". Confirmed with the Ethernet cable out and Wi-Fi joined to nothing:
tethering starts, takes 192.168.137.1, and serves DHCP and DNS.

### `hotspot/hotspot-control.ps1` — Matching the AP band to the station band
<!-- ref:9c91 -->

Was `hotspot/hotspot-control.ps1:78-93`.

Which TetheringWiFiBand the access point should ask for, given what this radio is
already doing. Returns a band name, plus the human-readable station band for logging.

WHY THIS EXISTS. Adapters in this class report "Soft AP : Not supported", so Windows
runs Mobile Hotspot as a Wi-Fi Direct GO on the SAME PHYSICAL RADIO as the station
link. Ask for a band the station is not on and the radio has to time-slice between
two channels: packets are queued rather than dropped, and the 20Hz AIM stream reaches
the game in bursts. That is felt directly as a jerky, delayed aim preview.

Measured 2026-09-09, host on 5 GHz ch44, AP pinned to 2.4 GHz, 60 pings to a joined
phone: mean 156.9ms, stdev 194.3ms, max 1161ms, 2% loss. Disconnecting the station
link and changing nothing else: mean 26.6ms, stdev 52.9ms, max 289ms, 0% loss.
See docs/plans/aim-preview-lag-on-shared-radio.md.

Matching the band is what lets the GO share the station's channel instead of
alternating with it. It is a request, not a guarantee - the driver places the GO.

### `relay/dns.js` — Responder options
<!-- ref:7fec -->

Was `relay/dns.js:200-228`.

@param {object}   options
@param {string}   options.ip        address to answer with
@param {string}  [options.suffix]   answer anything ending in this
@param {string}  [options.name]     or answer exactly this name
@param {string[]} [options.names]   or answer exactly any of these names (e.g. the game's
                                    hostname plus the connectivity probe domains —
                                    see docs/plans/magic-domain-replacement.md Phase 4)
@param {string[]} [options.fallbackNames] names forwarded upstream first, answered locally
                                    (like `names`) only if the upstream stays silent for
                                    `fallbackMs` or errors. For sharing mode, where the probe
                                    domains must resolve for real if the internet is up.
                                    With no `upstream` they behave exactly like `names`.
@param {string[]} [options.blockedNames] names always answered NXDOMAIN, never forwarded —
                                    iCloud Private Relay's opt-out signal (#79)
@param {number}  [options.fallbackMs] how long to wait for upstream before answering a
                                    fallback name locally (default 1500)
@param {number}  [options.upstreamPort] upstream's UDP port (default 53; tests override)
@param {string}  [options.allow]    CIDR permitted to query. REQUIRED in practice.
@param {number}  [options.rate]     max queries per second per source
@param {string}  [options.upstream] forward non-matching names here; omitted means NXDOMAIN
@param {function} [options.onQuery] called with the source address of every query that
                                    passes the allow-list and rate limit, whatever name it
                                    asked for. This is the earliest evidence a phone is on
                                    the hotspot at all — both platforms probe for internet
                                    access within about a second of associating, unprompted —
                                    so it is what the join funnel keys ASSOCIATED off.
                                    See funnel.js.

### `relay/dns.js` — Why the DNS responder exists and where it must not run
<!-- ref:680e -->

Was `relay/dns.js:3-28`.

A deliberately small authoritative DNS responder, for hotspot mode only.

WHY THIS EXISTS
Certificates can be cached, so a host only needs the internet once a quarter for those.
DNS cannot be cached, because the cache that matters lives on *each phone* — a guest
joining for the first time has nothing. Answering locally is the only way round that, and
it is what makes playing with no internet possible at all.

When the PC is the access point it is already its clients' DNS server, so it can answer
authoritatively with no upstream. Proven with a real phone resolving `play.local-ip.sh`,
a name no public resolver answers.

WHERE IT MUST NOT RUN
LAN mode. There, phones use the router's DNS, which is none of our business — and running
a resolver a phone did not ask for would be both useless and rude. Env-gated accordingly.

SECURITY - READ BEFORE CHANGING ANY DEFAULT
An earlier cut of this bound every interface and forwarded for anyone, which is the
textbook definition of an open recursive resolver: free for any host on any attached
network, and a DNS amplification reflector for spoofed-source DDoS.

The source guard is therefore LOAD-BEARING, not defence in depth. It cannot be replaced by
binding narrowly, because the hotspot address does not exist until the hotspot is running
and :53 must be held *before* that to win the race with ICS. See SecurityReview.md §4.

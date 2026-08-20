# Zero-setup hosting — download, run, play

**Goal:** a player downloads the game, runs it, and hosts a party. No commands, no installs, no
certificate management, no configuration. Setup — including certificate renewal — is automatic.

**Status: proposed. Nothing below is built.** This plan supersedes the hosting sections of
[HostingPlan.md](HostingPlan.md) if adopted; until then that document plus
[deploy/README.md](deploy/README.md) describe what actually runs.

---

## The headline: the certificate was never the hard part

The certificate is the problem this plan was asked to solve, and it is the *easiest* of the
four things standing between here and "download and play". `local-ip.sh` publishes its
certificate and key openly over HTTPS, so renewal is a file download on a timer — no ACME, no
credential, no DNS challenge. That is perhaps 200 lines of C#.

The real blockers are that **a downloaded build cannot currently start a relay at all**:

| # | Blocker | Where it bites | Evidence |
|---|---|---|---|
| 1 | **Node.js is not bundled** | Hard failure with "Install Node 20 or newer from nodejs.org and relaunch" | [LocalServices.cs:175](Assets/Scripts/Net/LocalServices.cs:175) |
| 2 | **Dependencies install at runtime** | First run shells out to `npm install`, needing npm and internet, with a 180s timeout | [LocalServices.cs:219](Assets/Scripts/Net/LocalServices.cs:219) |
| 3 | **`relay/` and `controller/` are not in the build** | `ResolveRelayDirectory` looks for `<exe folder>/relay/server.js`; both directories live outside `Assets/`, so Unity never copies them | [LocalServices.cs:420](Assets/Scripts/Net/LocalServices.cs:420) |
| 4 | **The certificate is manual and expires** | Only `deploy/local-https.ps1` obtains it; the current copy expires **2026-09-18** | [local-https.ps1:283](deploy/local-https.ps1:283) |

A build handed to somebody else today fails at #3 before it ever reaches #1. The reason this is
not visible is that the developer's machine has Node, a populated `node_modules`, both
directories in place from the checkout, and a certificate left over from a run on 2026-08-13.

---

## Two things this plan cannot deliver, and one it can only defer

Stating these first, because a plan that quietly omits them is worse than no plan.

### 1. Guests on mobile data can never join — this is arithmetic, not engineering

The relay is reached at a private address (`10.0.0.213`). Private addresses are unroutable from
the internet by definition. No certificate, bundling or automation changes that. **Everyone
plays in the same room, or they do not play.**

This is the one requirement the deleted Fly.io deployment met and local hosting cannot. If
remote guests ever matter more than self-containment, the answer is a hosted relay and this
entire plan is the wrong shape — see *The fork* below.

**But "the same network" is a weaker constraint than it first appears**, and field testing has
softened it twice. Phones joined from either side of a two-router home setup (ISP router plus a
second in AP mode with its own SSID), so a flat LAN is not required. And the PC can *create* a
network for guests who cannot join the venue's — a PC-hosted hotspot carried a phone that could
not get onto a school network, while the PC stayed on it and bridged.

So the requirement is not "everybody on the same network" but **the host on a network, and the
guests in the room**. Note what did *not* soften: the host still needs connectivity, because the
name has to resolve and the certificate has to be fetched. Phase 3.5 makes this a product
feature instead of a trick the developer happens to know.

### 2. The firewall needs an installer — decided, and now demonstrated

**Decision: the game ships as an installer, not a zip.** This is settled rather than proposed,
and it is the one place the plan deliberately spends a UAC prompt.

Something listens now, so Windows blocks it, and that cannot be fixed from inside a running
process without administrator rights. The 2026-08-20 spike turned this from a predicted cost
into a measured one:

> With the hotspot running and the responder and web server both confirmed listening on
> `0.0.0.0`, a phone on the hotspot **timed out** against `192.168.137.1:80`. Not "refused" —
> *dropped*. Adding two inbound rules made the same request succeed immediately.

The detail that matters for the installer: node's existing inbound rules were **`Private`
profile only**, and the hotspot adapter (`Local Area Connection* 12`) carries **no network
category at all** — it does not appear in `Get-NetConnectionProfile`, so the firewall treats it
as `Public`. **A rule scoped to `Private` silently does nothing on the interface that matters.**

So the installer must add rules with `-Profile Any`, scoped to the hotspot subnet so nothing is
exposed to the home network or the internet:

```powershell
New-NetFirewallRule -DisplayName "..." -Direction Inbound -Protocol UDP -LocalPort 53 -RemoteAddress 192.168.137.0/24 -Action Allow -Profile Any
New-NetFirewallRule -DisplayName "..." -Direction Inbound -Protocol TCP -LocalPort 443 -RemoteAddress 192.168.137.0/24 -Action Allow -Profile Any
```

This trades "unzip and run" for "install and run" — one prompt at install time, of the kind
every player already expects from a game, in exchange for zero prompts at every party
thereafter. **A zip cannot deliver the stated goal**, because the first launch on any machine
would fail exactly the way the spike did, with a timeout and no explanation.

### 3. iOS still needs one in-page tap

Safari mandates a user gesture before `DeviceMotionEvent` — it is browser policy, not ours. It
is already step 4 of the join flow, it stays in the browser, and it costs one tap.

---

## The fork, stated once so it is not re-litigated later

| | **Local relay** (this plan) | **Hosted relay** (what was deleted) |
|---|---|---|
| Host setup | Install the game | Install the game |
| Certificate | Downloaded and renewed automatically | Platform's problem entirely |
| Guests on cellular | **Impossible** | Fine |
| Firewall prompt | Once, at install | None — outbound only |
| Third parties in the path | `local-ip.sh` (DNS + certificate) | The hosting platform |
| Running cost | £0 | ~$5/month |
| Single point of failure | Per-host, plus `local-ip.sh` | One, shared by everyone |
| Work to get there | **Phases 1–4 below** | Redeploy; the code still supports it |

"Self-contained" points at the left column, so that is what this plan builds. Worth being clear
that the right column is closer to the literal goal ("download and play", with no firewall
prompt and no network restriction) and is a fraction of the work — `RelayClient` still connects
outbound to whatever URL it is given. **Cost was never the reason the pivot happened**, so if
the LAN-only constraint bites at a real party, reversing is cheap.

---

## Phase 1 — put a working relay inside the build

*The load-bearing phase. Nothing else matters until a downloaded build can start a relay.*

**1.1 Vendor the dependencies.** `relay/node_modules` is **796 KB** — just `ws` and
`qrcode-generator`. Commit it, or copy it at build time. This deletes the `npm install` path
([LocalServices.cs:216-243](Assets/Scripts/Net/LocalServices.cs:216)) along with its
timeout, its `cmd.exe` shim handling, and its failure mode.

**1.2 Bundle the Node runtime.** Ship `node.exe` (~50 MB) beside the relay. Replace
`ResolveExecutable("node", …)` with a bundled-first lookup that falls back to PATH for the
editor loop. **Recommended over a C# rewrite** — see *Alternative* below.

**1.3 Get both directories into the build.** Add a post-build step
(`IPostprocessBuildWithReport`) copying `relay/` and `controller/` next to the executable, which
is exactly where `ResolveRelayDirectory` already looks — so that function needs no change.
`StreamingAssets` is the alternative and is worse here: it would need `Application.dataPath`
rewiring and buys nothing on Windows, where it is a plain directory anyway.

**1.4 Verify the thing that is currently untested.** Build, copy to a machine with **no Node
installed**, and run it. This is the check that would have caught all of blockers 1–3, and
nothing in the repo performs it today.

> **Alternative considered: port the relay to C# and drop Node entirely.** Genuinely attractive
> — one process, no bundling, ~50 MB smaller. Rejected as the *first* move because `server.js`
> is 575 lines of deliberately hardened code: server-side room codes, `JSON.parse` in try/catch,
> room cleanup on host disconnect, player caps, heartbeats, per-socket rate limiting, the
> reconnect grace period. A rewrite re-opens every one of those, and the 23-test suite that
> guards them (`relay/npm test`) does not come along. It also means hand-rolling WebSocket
> framing on `TcpListener`+`SslStream`, because `HttpListener` HTTPS needs `netsh http
> add sslcert` — an admin operation, which is precisely what this plan is trying to avoid.
> Revisit once the bundled version is working and the process management has proven annoying.

## Phase 2 — automatic certificates

*Do this second. It is the smallest phase and it is worthless before Phase 1.*

Move certificate handling out of `deploy/local-https.ps1` and into the game, as a
`CertificateProvider` running before `LocalServices`:

1. **Detect the LAN IP**, refusing to guess between multiple gatewayed interfaces — port the
   logic already proven in `Resolve-LanAddress`.
2. **Derive the name** — `10.0.0.213` → `10-0-0-213.local-ip.sh`.
3. **Check the cached certificate.** Read `notAfter` and re-download if it is missing, expired,
   or **within 14 days** of expiry. PEM → strip armour → base64-decode → `new
   X509Certificate2(der)` works on Unity's Mono; `CreateFromPemFile` is .NET 5+ and is not
   available here.
4. **Download** `server.pem` / `server.key` from `local-ip.sh` when needed. Cache under
   `Application.persistentDataPath`, **never** the install directory.
5. **Verify the SAN covers `*.local-ip.sh`** before trusting it — the existing script already
   does this and it catches a swapped or truncated download.
6. **Hand the paths to the relay** as `TLS_CERT` / `TLS_KEY`. The relay's env-gated TLS branch
   already consumes exactly this, so `server.js` needs no change at all.

**Renewal is then structural rather than scheduled:** every launch checks, and a 90-day
certificate cannot expire between two launches of a game nobody has opened in three months —
because opening it *is* the check.

**Failure handling, in order:** a valid cached certificate is used even if `local-ip.sh` is
unreachable (up to 90 days of grace). Only a stale cache *and* an unreachable service is fatal,
and that message should name the real cause rather than surfacing a TLS error.

**Then delete `deploy/local-https.ps1`.** This is what retires the `test-deployed.js` bug —
fixing that script is wasted effort if the script itself is the thing being removed. Keep only
`-SetupOnly`'s behaviour, as an editor menu item for the desktop dev loop.

## Phase 3 — make the game's own defaults correct

**3.1 Remove the dead compiled-in address.** `relayUrl` still defaults to
`wss://rockskipping-relay.fly.dev` ([RelayClient.cs:47](Assets/Scripts/Net/RelayClient.cs:47)),
which has been dead since the Fly teardown. It should default to the URL Phase 2 derives.
`relay-url.txt` stays as a developer override — it is genuinely useful and costs a player
nothing.

**3.2 Turn `Start Local Services` on by default.** It is `launchOnStart`-gated and off, from
when a normal host ran no relay. Under this plan every host runs one.

**3.3 Handle a changed LAN IP.** DHCP moving the machine currently produces a stale
`relay-url.txt` pointing at nothing. Recompute at every launch and treat the file as a cache,
not as configuration.

**3.4 Pick a free port** rather than assuming 8443, and derive the URL from what was actually
bound.

**3.5 Automatic hotspot + Wi-Fi QR — the "guests join the PC, not the venue" flow.** The
highest-value item in Phase 3, and it came out of field testing rather than design. Hosting a
mobile hotspot *from the PC* makes the PC the access point for the phones, removing AP
isolation, guest-network segmentation, and any need for **guests** to hold network credentials.

Confirmed on a school network, and the direction matters: **the PC was connected and stayed
connected** — it was the *phone* that could not join, as is normal on enterprise Wi-Fi where
each user needs their own account. The PC bridged it. This is the ordinary party case too, where
the host is on the network and the guests are not.

The whole flow can be automated. **This is verified against the API on the dev machine, not
assumed:**

```
TetheringState : Off          <- startable via StartTetheringAsync()
MaxClientCount : 8            <- exactly the game's 2-8 player design cap
Config members : AuthenticationKind, Band, Passphrase, Ssid
Passphrase     : readable and settable
```

`NetworkOperatorTetheringManager` (WinRT, `Windows.Networking.NetworkOperators`) exposes
everything required, and **the query above ran unelevated** — no admin, no UAC.

The flow:

1. **Configure the AP** — `ConfigureAccessPointAsync()` with a generated SSID (e.g. the room
   code) and a fresh random passphrase per session.
2. **Force `Band` to 2.4 GHz.** Compatibility, not speed: some phones cannot see 5/6 GHz APs,
   and 2.4 GHz carries further across a room. The dev machine's current uplink is a 6 GHz
   network, so an inherited default would be exactly the wrong choice.
3. **Start tethering**, then read `Ssid` and `Passphrase` back.
4. **Render a Wi-Fi QR** in the standard payload format both Android and iOS cameras understand:
   `WIFI:T:WPA;S:<ssid>;P:<passphrase>;;`
5. **Then show the existing join QR.** Re-derive the relay URL first — see the fixed-address
   note below.

> **This is two scans, not one, and that is unavoidable.** A `WIFI:` payload joins a network; a
> URL payload opens a page. They are different payload types and no single QR does both. The
> sequence — scan to join, scan to play — is still far better than reading a passphrase aloud.
>
> *Optional refinement, not the path:* both OSes probe a known URL after joining a network to
> detect captive portals, and intercepting that probe can pop the controller open automatically.
> Captive-portal mini-browsers are heavily restricted, though, and motion sensors are exactly
> the kind of thing they break. Treat auto-open as a bonus that must degrade to "open in
> browser", never as the mechanism.

**A bonus worth designing around: in hotspot mode the address stops moving.** The PC is the AP,
so it holds a fixed, predictable address (Windows ICS conventionally `192.168.137.1`). That
makes `192-168-137-1.local-ip.sh` **stable across sessions and venues** — the same URL, the same
cached certificate, the same join QR every time, with no IP detection and none of 3.3's
re-derivation problems. Confirm the actual address once tethering is live, then treat hotspot
mode as the *deterministic* path and DHCP-assigned LAN mode as the variable one.

**Three constraints found while verifying, all of which shape the design:**

1. **`CreateFromConnectionProfile()` requires a connection profile**, so the PC must be
   connected to *something* to tether at all. This looks like it settles the open question from
   the previous section in the disappointing direction: **hotspot mode is a hostile-network fix,
   not an offline one**, and it is therefore *not* a substitute for HostingPlan §6's unbuilt
   tier-3 fallback. Worth one deliberate test before accepting that.
2. **A single Wi-Fi adapter having to both uplink and tether is the fragile case.** The dev
   machine can do either: it has a Realtek 8852CE Wi-Fi 6E adapter *and* a Realtek 2.5GbE
   Ethernet controller. When the 2026-08-20 spike ran, Ethernet was the uplink with Wi-Fi free,
   and starting the hotspot was a non-event. Wi-Fi-only hosts share one radio between both jobs;
   it works (the school session proves it) but driver behaviour varies. **The game should detect
   which case it is in and warn before starting a hotspot that may drop the host's own uplink.**
3. **Unity cannot call WinRT directly** (Mono / .NET Standard 2.1). This needs a small bundled
   helper — either a `net8.0-windows10.0.19041` executable the game launches, or the same
   PowerShell interop used to verify the above. A helper executable is cleaner and fits Phase 1's
   bundling work; it is also the natural place to put hotspot teardown on exit.

**Sequencing note:** this is worth building *before* Phase 4's installer, because a hotspot
listener changes which firewall profile matters — a rule scoped only to the network the
developer tested on is precisely the failure LOCAL-HTTPS-SPIKE Step 3 warns about.

**3.6 Local DNS responder — the piece that unlocks offline play. ✅ SPIKED, PASSED.** The only
item here that removes a standing limitation rather than automating an existing one — and the
mechanism is now proven rather than proposed.

Caching solves the certificate but **cannot** solve name resolution, and the reason is worth
being exact about: the cache that matters lives on **each phone**, not on the PC. Every guest's
device resolves the name itself, phone DNS caches are short-TTL, and a phone joining for the
first time has nothing cached. No PC-side cache helps.

What does work: **in hotspot mode the PC is the DNS server for its own clients**, so it can
answer the query authoritatively with no upstream at all — the same mechanism captive portals
use, pointed at a useful end. Combined with the cached certificate from Phase 2, that removes
the last reason the host needs to be online at play time.

Two things fall out of it:

- **`local-ip.sh` shrinks from "DNS + certificate" to "certificate only"**, which retires half
  of risk 1 — the remaining dependency is a file already cached for up to 90 days.
- **The name no longer has to encode the IP.** The wildcard covers one label, so
  `play.local-ip.sh` is exactly as valid as `192-168-137-1.local-ip.sh`, and when we are doing
  the resolving it can point anywhere. That means a constant URL, a constant join QR, and no IP
  detection — strictly better than 3.5's fixed-address bonus, which still depended on Windows
  picking the address we expect.

**The spike is built and it passed.** See [deploy/spike/](deploy/spike/) for the code and full
results. The concern was that `SharedAccess` (ICS) would claim `:53` on the hotspot interface
and evict the responder. It does not:

| Question | Result |
|---|---|
| Bind UDP `:53` **without admin** | **YES** — Windows has no privileged-port rule, so this never touches the installer |
| Responder answers A / AAAA / non-matching correctly | **YES** |
| Upstream forwarding, so phones keep normal internet | **YES** |
| **ICS evicts the responder when tethering starts** | **NO** — tethering started `Success` and the responder stayed the *sole* holder of `:53` |
| Hotspot address is really `192.168.137.1` | **YES**, confirmed live |

**Still inferred, not measured:** whether ICS advertises `192.168.137.1` to phones as their DNS
server. It conventionally advertises its own address as both gateway and DNS, which routes phone
queries into the responder — but if it instead hands out upstream resolvers, phones bypass us and
offline play fails quietly, with the responder running and simply never asked. One phone on the
hotspot settles it: look for a `192.168.137.x` address in the query log.

**Two questions to answer manually, since testing them means cutting the machine's network:**

1. Does `CreateFromConnectionProfile()` succeed from a **connected-but-no-internet** profile?
2. Does tethering start with **no adapter connected at all**?

Together they decide whether "offline" here means *no internet* or the stricter *no network*.
Until they are answered, treat offline play as plausible rather than planned.

**Scope limit:** this applies to hotspot mode only. On a normal LAN the phones use the router's
DNS, which we do not control, so IP-encoded names and upstream resolution remain necessary
there. Two modes, deliberately.

## Phase 4 — the installer

**The distribution format is decided: an installer, not a zip.** See *The firewall needs an
installer* above for the measurement that settled it. A zip is not a lighter-weight alternative
here; it is a build that fails on first launch for everyone who is not the developer.

Wrap the build (Inno Setup or MSIX) so install time absorbs:

- **The firewall rules** — `-Profile Any`, never `Private` alone. The hotspot adapter has no
  network category, so the firewall treats it as `Public`, and a `Private`-scoped rule is
  silently inert on the one interface that matters. This is not the theoretical profile mismatch
  [LOCAL-HTTPS-SPIKE.md](deploy/LOCAL-HTTPS-SPIKE.md) Step 3 warned about — it is that warning
  actually happening, observed as a timeout with no error message anywhere.
- Scope those rules to the hotspot subnet (`192.168.137.0/24`) and to the specific ports rather
  than allowing `node.exe` wholesale. Same effect for the game, far smaller blast radius, and it
  keeps the LAN and the internet out of scope entirely.
- Placing `relay/`, `controller/` and `node.exe`.
- A Start Menu entry.
- **An uninstaller that removes the firewall rules.** Leaving them behind after the game is gone
  is exactly the kind of residue that makes people distrust hobby software.

After this, first run performs zero prompts.

**Sequencing:** still last. An installer that ships a build which cannot start a relay only
hides the failure behind a progress bar — but note the spike has now shown the reverse risk too,
that a correct build can look broken purely because nothing granted it a firewall exception.
Phases 1-3 should be verified with rules added by hand before the installer automates them.

---

## What "done" looks like

Not "it works on this machine" — that is what hid four blockers:

1. A **clean Windows machine with no Node.js**, from the installer, no repo checkout.
2. Launch. **No console, no prompt, no configuration.**
3. Two phones on the house Wi-Fi scan the QR and play a full match with motion controls.
4. Delete the certificate cache, relaunch, confirm it re-downloads silently.
5. Move the machine to a different network so its IP changes, relaunch, confirm it adapts.
6. Set the clock past the certificate's expiry, relaunch, confirm renewal rather than a TLS error.
7. **Hotspot mode:** with the PC on a network the *phones* cannot join, switch to a PC-hosted
   hotspot from inside the game and confirm phones join and play.
8. **Hotspot mode with the PC fully offline** — the open question from Phase 3.5. Whatever this
   returns, record it; it decides whether offline play is possible at all.

Steps 4–8 are the ones that matter: they are the failures that will otherwise surface months
later, at a party, with no terminal open.

---

## Risks

1. **`local-ip.sh` becomes a hard runtime dependency.** It supplies both DNS and the
   certificate, and nobody here operates it. If it disappears, no new certificate can be
   obtained anywhere. *Mitigation:* cached certificates give up to 90 days of grace, and the
   provider should be an interface with `traefik.me` / `local-ip.co` as alternates rather than a
   hardcoded URL. **Phase 3.6 halves this** — a local DNS responder removes the dependency on
   their *resolution*, leaving only the certificate download, which is already cached.
2. **The certificate's private key is published on purpose, so it may be revoked.** CA policy
   requires revocation within 24h of a key-compromise disclosure. Mobile browsers largely do not
   hard-fail on revocation today, which is the only reason this works at all. *Mitigation:* the
   multi-provider interface above; treat it as a dependency that can vanish, not as
   infrastructure.
3. **Router DNS rebinding protection** strips private answers from public names. **Downgraded —
   this has not been observed in practice.** A home setup with an ISP router plus a second in AP
   mode carried a 5-player iOS/Android session with phones joining from either network. It
   remains possible on other hardware. *Mitigation:* detect it at the resolution check and say so
   plainly — it is indistinguishable from a broken certificate otherwise — then fall back to
   hotspot mode (risk 4).
4. **AP isolation and locked-down networks.** ~~Cannot be detected from the PC alone.~~
   **Largely solved, by a workaround that turned out to be strong:** hosting a mobile hotspot
   *from the PC* makes the PC the access point, which removes AP isolation, guest-network
   segmentation and any need for *guests* to hold network credentials at once. Confirmed on a
   school network the **phone** could not join, with the PC connected throughout and acting as
   the bridge. *Action:* this should be a **first-class in-game feature**, not
   folklore in a runbook — see Phase 3.5. *Mitigation meanwhile:* a raw-IP fallback URL on the
   TV, so a name-mismatch warning (network fine) is distinguishable from a timeout (isolation).
5. **Bundling `node.exe` adds ~50 MB and an antivirus surface** — an unsigned binary spawning a
   listening child process is a plausible false positive. *Mitigation:* sign the installer; the
   C# port removes this risk class entirely and is the reason to revisit it.

---

## Sequencing

Phases 1 → 2 → 3 can each be verified independently, and Phase 1 is where the risk is
concentrated. **Phase 4 should not start until 1–3 pass on a clean machine**, since an installer
that ships a build which cannot start a relay only hides the failure behind a progress bar.

Nothing here needs the certificate work first. If the temptation is to start with Phase 2
because that is what prompted the plan, resist it: the certificate is a one-file download, and a
build that cannot run a relay will not be able to use it.

# Zero-setup hosting — download, run, play

**Goal:** a player downloads the game, runs it, and hosts a party. No commands, no installs, no
certificate management, no configuration. Setup — including certificate renewal — is automatic.

**Status: designed and de-risked; not yet implemented.** Every mechanism this plan depends on has
been proven by a working spike ([deploy/spike/](deploy/spike/)), including two that were expected
to fail. **No production code has been written.** The game today still cannot be handed to anyone
else — see *The four blockers*.

Companion documents: [SecurityReview.md](SecurityReview.md) for what this design asks of a
player, and [HostingPlan.md](HostingPlan.md) for the history of how the architecture got here.

---

## What the spikes settled

Run 2026-08-20. Consolidated because these results are the reason the plan has the shape it does,
and each one was capable of killing a phase.

| Question | Result | Consequence |
|---|---|---|
| Can the game drive Windows tethering? | **Yes, unelevated** — `NetworkOperatorTetheringManager`, `MaxClientCount` 8 | Hotspot mode is viable; no UAC to start it |
| Can we bind UDP `:53` without admin? | **Yes** — Windows has no privileged-port rule | Local DNS never touches the installer |
| Does ICS seize `:53` when tethering starts? | **No** — bind first and it leaves you alone | Local DNS survives alongside the hotspot |
| Does a real phone resolve through us? | **Yes** — `play.local-ip.sh` loaded, a name in no public DNS | **Offline play is possible** |
| Can the hotspot carry the game but not the internet? | **Yes** — ICS uses plain IP forwarding; disabling it sticks | Guests' traffic never reaches the host |
| Does the firewall allow any of this by default? | **No** — silent packet drops, no error anywhere | **The game must ship as an installer** |
| Is the hotspot address `192.168.137.1`? | **Yes**, confirmed live | Fixed address and fixed URL in hotspot mode |

Two results changed the plan's direction rather than confirming it: **offline play went from
written-off to proven**, and **the distribution format went from open question to decided**.

## The runtime architecture

The spikes settled what actually runs, which the first draft of this plan left vague:

| Component | Job | Why it exists separately |
|---|---|---|
| **Unity (C#)** | Game, orchestration, UI | — |
| **`relay/server.js`** (Node) | Controller page, WebSocket, **and DNS** | DNS folds in here rather than becoming a fifth process — the responder has no dependencies |
| **`RockSkipHelper.exe`** (.NET) | Tethering lifecycle, triggering the forwarding toggle | Unity cannot call WinRT (Mono / .NET Standard 2.1) |
| **Scheduled task** | The elevated IP-forwarding toggle | The only way to avoid a UAC prompt every session |
| **Installer** | Firewall rules, task registration, file placement | Nothing works on a clean machine without it |

## The four blockers

The certificate is the problem this plan was asked to solve, and it is the *easiest* of the four
things in the way. `local-ip.sh` publishes its certificate and key openly, so renewal is a file
download on a timer — no ACME, no credential, no DNS challenge.

The real blockers are that **a downloaded build cannot currently start a relay at all**:

| # | Blocker | Where it bites | Evidence |
|---|---|---|---|
| 1 | **Node.js is not bundled** | Hard failure: "Install Node 20 or newer from nodejs.org and relaunch" | [LocalServices.cs:175](Assets/Scripts/Net/LocalServices.cs:175) |
| 2 | **Dependencies install at runtime** | First run shells out to `npm install`, needing npm and internet, 180s timeout | [LocalServices.cs:219](Assets/Scripts/Net/LocalServices.cs:219) |
| 3 | **`relay/` and `controller/` are not in the build** | Looks for `<exe folder>/relay/server.js`; both live outside `Assets/`, so Unity never copies them | [LocalServices.cs:420](Assets/Scripts/Net/LocalServices.cs:420) |
| 4 | **The certificate is manual and expires** | Only `deploy/local-https.ps1` obtains it; the current copy expires **2026-09-18** | [local-https.ps1:283](deploy/local-https.ps1:283) |

A build handed to somebody else today fails at #3 before it ever reaches #1. This is invisible
because the developer's machine has Node, a populated `node_modules`, both directories from the
checkout, and a certificate left over from a run on 2026-08-13.

---

## Constraints — what this design cannot do

Stating these plainly, because a plan that quietly omits them is worse than no plan.

### Guests on mobile data can never join

The relay lives at a private address, and private addresses are unroutable from the internet.
No certificate, bundling or automation changes that. This is the one requirement the deleted
Fly.io deployment met and local hosting cannot.

**But "the same network" is weaker than it sounds**, and field testing softened it twice. Phones
joined from either side of a two-router home setup (ISP router plus a second in AP mode with its
own SSID), so a flat LAN is not required. And the PC can *create* a network for guests who
cannot join the venue's. So the real requirement is **the host on a network, and the guests in
the room** — which for a couch game is close to no constraint at all.

### iOS still needs one in-page tap

Safari mandates a user gesture before `DeviceMotionEvent`. Browser policy, not ours. It is
already step 4 of the join flow, it stays in the browser, and it costs one tap.

### The host can still see *some* things about guests

Traffic isolation (Phase 4.2) means guests' browsing never reaches the host. **DNS lookups and
local-subnet chatter still do.** The defensible claim is *"the hotspot does not carry your
internet traffic, and nothing you do is attributable to the host"* — **not** *"the host can see
nothing."* [SecurityReview.md](SecurityReview.md) §2 has the precise boundary.

### What is *no longer* a constraint

Earlier drafts of this document said the host must be online at play time, because the
certificate had to be fetched and the name resolved. **Both are now solved** — certificates cache
for up to 90 days (Phase 2) and names resolve locally (Phase 4.3). What remains open is only
whether Windows will *start* tethering with no network attached at all.

---

## The fork, stated once so it is not re-litigated

| | **Local relay** (this plan) | **Hosted relay** (what was deleted) |
|---|---|---|
| Host setup | Install the game | Install the game |
| Certificate | Downloaded and renewed automatically | Platform's problem entirely |
| Guests on cellular | **Impossible** | Fine |
| Firewall prompt | Once, at install | None — outbound only |
| Third parties in the path | `local-ip.sh` (certificate only, after Phase 4.3) | The hosting platform |
| Running cost | £0 | ~$5/month |
| Single point of failure | Per-host | One, shared by everyone |
| Work to get there | **Phases 1–5 below** | Redeploy; the code still supports it |

"Self-contained" points at the left column, so that is what this plan builds. Worth being clear
that the right column is closer to the literal goal and is a fraction of the work — `RelayClient`
still connects outbound to whatever URL it is given. **Cost was never the reason the pivot
happened**, so if the LAN-only constraint bites at a real party, reversing is cheap.

---

# Phase 1 — put a working relay inside the build

*The load-bearing phase. Nothing else matters until a downloaded build can start a relay, and
this is the only phase no spike has de-risked.*

**1.1 Vendor the dependencies.** `relay/node_modules` is **796 KB** — just `ws` and
`qrcode-generator`. Commit it, or copy it at build time. Deletes the `npm install` path
([LocalServices.cs:216-243](Assets/Scripts/Net/LocalServices.cs:216)) with its timeout, its
`cmd.exe` shim handling, and its failure mode.

**1.2 Bundle the Node runtime.** Ship `node.exe` (~50 MB) beside the relay. Replace
`ResolveExecutable("node", …)` with a bundled-first lookup that still falls back to PATH for the
editor loop.

**1.3 Get both directories into the build.** A post-build step (`IPostprocessBuildWithReport`)
copying `relay/` and `controller/` next to the executable — exactly where `ResolveRelayDirectory`
already looks, so that function needs no change. `StreamingAssets` is worse here: it needs
`Application.dataPath` rewiring and buys nothing on Windows.

**1.4 Verify the thing that is currently untested.** Build, copy to a machine with **no Node
installed**, and run it. This is the check that would have caught blockers 1–3, and nothing in
the repo performs it today.

> **Alternative considered: port the relay to C# and drop Node entirely.** Attractive — one
> process, no bundling, ~50 MB smaller. Rejected as the *first* move because `server.js` is 575
> lines of deliberately hardened code: server-side room codes, `JSON.parse` in try/catch, room
> cleanup on host disconnect, player caps, heartbeats, per-socket rate limiting, the reconnect
> grace period. A rewrite reopens every one of those, and the 23-test suite guarding them does
> not come along. It also means hand-rolling WebSocket framing on `TcpListener`+`SslStream`,
> because `HttpListener` HTTPS needs `netsh http add sslcert` — an admin operation, which is
> precisely what this plan is avoiding. Revisit once bundling proves annoying.

# Phase 2 — automatic certificates 🟡 IN PROGRESS

**Built:** [CertificateProvider.cs](Assets/Scripts/Net/CertificateProvider.cs) — detection,
caching, expiry check, download, SAN verification — wired into `LocalServices` ahead of the
relay, which now receives `TLS_CERT`/`TLS_KEY` from it. `relay-env.txt` still overrides, so the
developer escape hatch survives. Phase 4.3's DNS responder landed at the same time as
[relay/dns.js](relay/dns.js), env-gated behind `DNS_RESPOND_IP`, with 26 tests in
[relay/test-dns.js](relay/test-dns.js).

**Not done:** confirming the C# path end to end in the editor, and deleting
`deploy/local-https.ps1` once it is. The script is marked deprecated meanwhile.

> **The deadline moved, and the reason is worth recording.** This section previously said the
> cached certificate expires 2026-09-18. That was true of *that copy* — `%TEMP%` has since been
> cleared, which took it, and a fresh download now returns a certificate valid to
> **2026-11-17**. So the calendar pressure is off, but the incident is a live demonstration of
> why the cache cannot live in `%TEMP%`: `Application.persistentDataPath` is not swept by
> Storage Sense, which is precisely why the new provider uses it.

Move certificate handling out of `deploy/local-https.ps1` into a `CertificateProvider` that runs
before `LocalServices`:

1. **Detect the LAN IP**, refusing to guess between multiple gatewayed interfaces — port the
   logic already proven in `Resolve-LanAddress`.
2. **Derive the name** — `10.0.0.213` → `10-0-0-213.local-ip.sh`.
3. **Check the cached certificate.** Read `notAfter`; re-download if missing, expired, or within
   **14 days** of expiry. PEM → strip armour → base64-decode → `new X509Certificate2(der)` works
   on Unity's Mono; `CreateFromPemFile` is .NET 5+ and unavailable.
4. **Download** `server.pem` / `server.key` when needed. Cache under
   `Application.persistentDataPath`, **never** the install directory.
5. **Verify the SAN covers `*.local-ip.sh`** before trusting it — catches a truncated or swapped
   download.
6. **Hand the paths to the relay** as `TLS_CERT` / `TLS_KEY`. The relay's env-gated TLS branch
   already consumes exactly this, so `server.js` needs no change.

**Renewal becomes structural rather than scheduled:** every launch checks, and a 90-day
certificate cannot expire between two launches — because opening the game *is* the check.

**Failure handling, in order:** a valid cached certificate is used even if `local-ip.sh` is
unreachable (up to 90 days of grace). Only a stale cache *and* an unreachable service is fatal,
and the message should name that cause rather than surfacing a TLS error.

**Then delete `deploy/local-https.ps1`.** This is what retires the `test-deployed.js` bug —
fixing a script you are about to remove is wasted effort. Keep `-SetupOnly`'s behaviour as an
editor menu item for the desktop dev loop.

# Phase 3 — make the game's own defaults correct

*Small mechanical fixes. Each is independent and none is risky.*

**3.1 Remove the dead compiled-in address.** `relayUrl` still defaults to
`wss://rockskipping-relay.fly.dev` ([RelayClient.cs:47](Assets/Scripts/Net/RelayClient.cs:47)),
dead since the Fly teardown. Default to the URL Phase 2 derives. `relay-url.txt` stays as a
developer override.

**3.2 Turn `Start Local Services` on by default.** It is `launchOnStart`-gated and off, from when
a normal host ran no relay. Under this plan every host runs one.

**3.3 Handle a changed LAN IP.** DHCP moving the machine currently leaves a stale `relay-url.txt`
pointing at nothing. Recompute every launch; treat the file as a cache, not configuration.

**3.4 Pick a free port** rather than assuming 8443, and derive the URL from what was actually
bound.

# Phase 4 — hotspot mode

*The largest phase, and **optional**. The LAN path already works — proven at 5 players across two
routers. Hotspot earns its keep only for guests who cannot join the venue's network. If the
shortest path to something distributable is what matters, skip to Phase 5 and come back.*

Every mechanism here is spike-proven. What remains is production implementation.

## 4.1 Automatic hotspot + Wi-Fi QR

Hosting a mobile hotspot *from the PC* makes the PC the access point for the phones, removing AP
isolation, guest-network segmentation, and any need for **guests** to hold network credentials.

Confirmed on a school network, and the direction matters: **the PC was connected and stayed
connected** — it was the *phone* that could not join, as is normal on enterprise Wi-Fi. The PC
bridged it. That is the ordinary party case too, where the host is on the network and the guests
are not.

Verified against the API, not assumed:

```
TetheringState : Off          <- startable via StartTetheringAsync()
MaxClientCount : 8            <- exactly the game's 2-8 player design cap
Config members : AuthenticationKind, Band, Passphrase, Ssid
Passphrase     : readable and settable
```

The flow:

1. **Configure the AP** — `ConfigureAccessPointAsync()` with a generated SSID and a
   cryptographically random passphrase, **fresh per session** and never derived from the room
   code.
2. **Force `Band` to 2.4 GHz.** Compatibility, not speed: some phones cannot see 5/6 GHz APs, and
   2.4 GHz carries further across a room. The dev machine's uplink is a 6 GHz network, so an
   inherited default would be exactly wrong.
3. **Start tethering**, then read `Ssid` and `Passphrase` back.
4. **Render a Wi-Fi QR** — `WIFI:T:WPA;S:<ssid>;P:<passphrase>;;`, which both Android and iOS
   cameras understand.
5. **Then show the join QR.** Re-derive the relay URL first.

> **Two scans, not one, and that is unavoidable.** A `WIFI:` payload joins a network; a URL
> payload opens a page. Different payload types; no single QR does both. Scan to join, scan to
> play — still far better than reading a passphrase aloud.
>
> *Optional refinement, not the path:* both OSes probe a known URL after joining to detect
> captive portals, and intercepting that can pop the controller open automatically. Captive-portal
> mini-browsers are heavily restricted and motion sensors are exactly what they break. Treat
> auto-open as a bonus that must degrade to "open in browser", never as the mechanism.

**In hotspot mode the address stops moving.** The PC is the AP, so it holds `192.168.137.1`
(confirmed live). Combined with 4.3's local DNS, that means a constant URL and a constant join QR
across sessions and venues, with no IP detection and none of 3.3's re-derivation problems.

**Two implementation constraints:**

- **A single Wi-Fi adapter having to both uplink and tether is the fragile case.** The dev machine
  has both a Wi-Fi 6E adapter and 2.5GbE Ethernet; when the spike ran, Ethernet was the uplink
  with Wi-Fi free, and starting the hotspot was a non-event. Wi-Fi-only hosts share one radio;
  it works but driver behaviour varies. **Detect which case the host is in and warn before
  starting a hotspot that may drop their own uplink.**
- **`CreateFromConnectionProfile()` requires a connection profile**, so the PC must be attached to
  *something* to tether at all.

## 4.2 Carry the game, not the internet ✅ spike passed

When the PC is the access point it can see guests' traffic metadata — not HTTPS *contents*, but
more than it has any need for. The game requires phones to reach *this PC*; it never required
giving them internet.

ICS turns on plain IP forwarding when tethering starts (`Get-NetNat` is empty, so there is no
separate NAT engine to fight). Turning it back off with `Set-NetIPInterface -Forwarding Disabled`
**sticks — ICS does not revert it.** Confirmed from a phone: the game page loaded, ordinary
websites did not.

Because **Android keeps mobile data as its default route** for a Wi-Fi it judges unvalidated,
guests' real browsing and DNS go over cellular and never touch the host at all.

> **This is where Phase 5's scheduled task comes from.** The forwarding toggle needs administrator
> rights, and ICS re-enables forwarding on *every* tethering start — a per-session runtime action,
> not a one-off at install. A UAC prompt per party would defeat the whole plan.

## 4.3 Local DNS responder ✅ spike passed

Caching solves the certificate but **cannot** solve name resolution: the cache that matters lives
on **each phone**, and a guest joining for the first time has nothing cached. No PC-side cache
helps.

What does work: in hotspot mode the PC **is** the DNS server for its clients, so it can answer
authoritatively with no upstream at all. Confirmed with a real phone loading
`http://play.local-ip.sh/` — a name **no public resolver can answer**, so the answer could only
have come from the PC.

**The chain closes: certificate cached + name resolved locally = no internet needed at play
time.**

Two consequences worth designing around:

- **`local-ip.sh` shrinks from "DNS + certificate" to "certificate only"**, halving that
  dependency (risk 1).
- **The name no longer has to encode the IP.** The wildcard covers one label, so
  `play.local-ip.sh` is as valid as `192-168-137-1.local-ip.sh`, and when we do the resolving it
  can point anywhere. Constant URL, constant QR, no IP detection.

**Ship the hardened version, not the naive one.** The spike's first cut was an open recursive
resolver — a DNS amplification reflector. The shipped code must keep the source guard (drop, do
not refuse), the per-source rate limit, and the safe-by-default bind.
[SecurityReview.md](SecurityReview.md) §4 has the reasoning; do not reimplement from memory.

**Scope limit:** hotspot mode only. On a normal LAN phones use the router's DNS, which we do not
control, so the responder must not run there at all. Two modes, deliberately.

# Phase 5 — the installer

**The distribution format is decided: an installer, not a zip.** A zip is not a lighter
alternative; it is a build that fails on first launch for everyone who is not the developer,
with a timeout and no error to read.

Wrap the build (Inno Setup or MSIX) so install time absorbs:

- **The firewall rules** — `-Profile Any`, never `Private` alone. An adapter with no assigned
  network category — the hotspot adapter, and just as often an ordinary Wi-Fi adapter Windows
  never got around to categorising — is treated as `Public`, and a `Private`-scoped rule is
  **silently inert on the one interface that matters**. This is not a theoretical profile
  mismatch; it has now been observed twice — once on the hotspot adapter, once on a Wi-Fi
  adapter in plain LAN mode — as dropped packets with nothing logged anywhere. See
  `Findings-PhoneUnreachable.md` for the LAN-mode occurrence.
- Scope those rules to `LocalSubnet`, not the fixed `192.168.137.0/24` hotspot range — a rule
  scoped only to the hotspot subnet leaves ordinary LAN play broken by this identical failure.
  `LocalSubnet` covers both modes while still keeping the rule off the internet. Scope to
  specific ports rather than allowing `node.exe` wholesale either way.
- **A scheduled task with highest privileges** that disables IP forwarding on the hotspot
  interface, so the game can call it per session without a UAC prompt (Phase 4.2). **No
  parameters** — hardcode the interface match, or it becomes a local privilege-escalation vector.
- Placing `relay/`, `controller/`, `node.exe` and `RockSkipHelper.exe`.
- A Start Menu entry.
- **Code signing.** An unsigned binary that requests elevation, opens firewall holes and spawns a
  listening child process is indistinguishable from malware to antivirus heuristics and to a
  careful user alike.
- **An uninstaller that removes the firewall rules *and* the scheduled task.** A stray privileged
  task is the worse of the two.

After this, first run performs zero prompts.

---

## Where to start

**Phase 2 first, then Phase 1.** This inverts the numbering and the reason is the calendar: the
cached certificate expires **2026-09-18**, and when it does the current dev loop breaks and
`local-https.ps1` fails on its dead `test-deployed.js` call. Phase 2 also retires that script
entirely, so the bug dies with it.

Fold **4.3's DNS responder into `relay/server.js`** at the same time — same file, same env-gated
pattern as TLS, and it wants tests in the existing suite rather than shipping as spike code.

Then:

| Order | Work | Why here |
|---|---|---|
| 1 | Phase 2 + fold in 4.3 | Deadline-driven; retires `local-https.ps1` and its bug |
| 2 | **Phase 1** | The only unde-risked phase, and it gates all distribution |
| 3 | Phase 3 | Small, independent, safe to interleave |
| 4 | Phase 4.1 / 4.2 (`RockSkipHelper.exe`) | Riskiest integration, but every call underneath is proven |
| 5 | Phase 5 | Last — but verify Phases 1–4 with hand-added firewall rules first |

**Phase 5 last, with a caveat the spike taught:** an installer shipping a build that cannot start
a relay only hides the failure behind a progress bar — but the reverse is now also known, that a
*correct* build looks broken when nothing granted it a firewall exception. Add the rules by hand
while developing so the two failure modes stay distinguishable.

## What "done" looks like

Not "it works on this machine" — that is what hid all four blockers:

1. A **clean Windows machine with no Node.js**, from the installer, no repo checkout.
2. Launch. **No console, no prompt, no configuration.**
3. Two phones on the house Wi-Fi scan the QR and play a full match with motion controls.
4. Delete the certificate cache, relaunch, confirm it re-downloads silently.
5. Move the machine to a different network so its IP changes, relaunch, confirm it adapts.
6. Set the clock past the certificate's expiry, relaunch, confirm renewal rather than a TLS error.
7. **Hotspot mode:** with the PC on a network the *phones* cannot join, switch to hotspot from
   inside the game and confirm phones join and play.
8. **Hotspot isolation:** confirm the game works *and* ordinary websites do not, from the phone.
   Turn mobile data off first, or Android's cellular fallback makes a success look like a failure.
9. **Hotspot with the PC fully offline** — still unanswered. Whatever it returns, record it.

Steps 4–9 are the ones that matter: they are the failures that otherwise surface months later, at
a party, with no terminal open.

## Open questions

Both need the machine's network genuinely cut, and together they decide whether "offline" means
*no internet* or the stricter *no network*:

1. Does `CreateFromConnectionProfile()` succeed from a **connected-but-no-internet** profile?
2. Does tethering start with **no adapter connected at all**?

Neither blocks Phases 1–3. Until answered, treat fully-offline play as plausible rather than
planned.

## Risks

1. **`local-ip.sh` is a runtime dependency nobody here operates.** If it disappears, no new
   certificate can be obtained. *Mitigation:* cached certificates give up to 90 days of grace;
   make the provider an interface with `traefik.me` / `local-ip.co` as alternates rather than a
   hardcoded URL. **Phase 4.3 halves this** by removing the dependency on their *resolution*.
2. **The certificate's private key is published on purpose, so it may be revoked.** CA policy
   requires revocation within 24h of a key-compromise disclosure; mobile browsers largely do not
   hard-fail on revocation today, which is the only reason this works. *Mitigation:* the
   multi-provider interface above. Treat it as a dependency that can vanish.
3. **Router DNS rebinding protection.** **Downgraded — not observed in practice.** A two-router
   home setup carried a 5-player iOS/Android session with phones joining from either network.
   *Mitigation:* detect at the resolution check and say so plainly, then fall back to hotspot mode.
4. **AP isolation and locked-down networks.** **Largely solved** by hotspot mode (Phase 4.1),
   confirmed on a school network the phone could not join.
5. **Bundling `node.exe` adds ~50 MB and an antivirus surface.** *Mitigation:* code-sign; the C#
   port removes this risk class entirely and is the reason to revisit it.
6. **The security fixes live in spike code, not production.** The DNS source guard in particular
   is load-bearing rather than defence-in-depth, because the narrow bind is unavailable at
   startup. *Mitigation:* port it deliberately, with tests — see
   [SecurityReview.md](SecurityReview.md).

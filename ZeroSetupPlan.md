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
plays from the same network, or they do not play.**

This is the one requirement the deleted Fly.io deployment met and local hosting cannot. If
remote guests ever matter more than self-containment, the answer is a hosted relay and this
entire plan is the wrong shape — see *The fork* below.

### 2. The Windows firewall prompt needs an installer to absorb it

Something listens now, so Windows prompts on first run. It cannot be suppressed from inside a
running process without administrator rights. The honest fix is to **ship an installer** and add
the firewall rule during install, where a single UAC prompt is what every player already expects
from installing a game. Running the game then prompts for nothing.

This trades "unzip and run" for "install and run". If that trade is unacceptable, the prompt
stays and the goal is met everywhere except that one dialog.

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

## Phase 4 — the installer

Wrap the build (Inno Setup or MSIX) so that install time absorbs:

- the firewall rule, on **both** Private and Public profiles — the profile mismatch described in
  [LOCAL-HTTPS-SPIKE.md](deploy/LOCAL-HTTPS-SPIKE.md) Step 3 is why a rule added during
  development can appear to work and then prompt at a party
- placing `relay/`, `controller/` and `node.exe`
- a Start Menu entry

After this, first run performs zero prompts.

---

## What "done" looks like

Not "it works on this machine" — that is what hid four blockers:

1. A **clean Windows machine with no Node.js**, from the installer, no repo checkout.
2. Launch. **No console, no prompt, no configuration.**
3. Two phones on the house Wi-Fi scan the QR and play a full match with motion controls.
4. Delete the certificate cache, relaunch, confirm it re-downloads silently.
5. Move the machine to a different network so its IP changes, relaunch, confirm it adapts.
6. Set the clock past the certificate's expiry, relaunch, confirm renewal rather than a TLS error.

Steps 4–6 are the ones that matter: they are the failures that will otherwise surface months
later, at a party, with no terminal open.

---

## Risks

1. **`local-ip.sh` becomes a hard runtime dependency.** It supplies both DNS and the
   certificate, and nobody here operates it. If it disappears, no new certificate can be
   obtained anywhere. *Mitigation:* cached certificates give up to 90 days of grace, and the
   provider should be an interface with `traefik.me` / `local-ip.co` as alternates rather than a
   hardcoded URL.
2. **The certificate's private key is published on purpose, so it may be revoked.** CA policy
   requires revocation within 24h of a key-compromise disclosure. Mobile browsers largely do not
   hard-fail on revocation today, which is the only reason this works at all. *Mitigation:* the
   multi-provider interface above; treat it as a dependency that can vanish, not as
   infrastructure.
3. **Router DNS rebinding protection** strips private answers from public names and will break
   this on some home networks, with no workaround from inside the game. *Mitigation:* detect it
   at the resolution check and say so plainly — it is indistinguishable from a broken
   certificate otherwise.
4. **AP isolation** stops LAN clients reaching each other and cannot be detected from the PC
   alone. *Mitigation:* a raw-IP fallback URL on the TV, so a name-mismatch warning (network
   fine) is distinguishable from a timeout (isolation).
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

# Local HTTPS — serving the controller page from the host machine

> **Status: adopted. This is no longer a spike — it is how the game runs.** The Fly.io
> deployment it was an alternative to has been torn down and its files deleted, so the local
> path is now the only path. The operational runbook is [README.md](README.md); this document
> is kept for the reasoning, the measurements, and the costs the decision accepted.
>
> **It was adopted with several of the questions below still open, not because they were
> answered.** That is a deliberate trade for a project that is played on one network by people
> in one room, and it is not a claim that the objections were wrong. The section at the bottom
> is still accurate and still worth reading before anyone assumes this scales.

**The core premise holds.** A page served from *this* machine over HTTPS, on a publicly-trusted
certificate for a name that resolves to a LAN IP, gives an Android phone a secure context and
**motion sensors fire normally**. Measured 2026-08-12 on a Galaxy S25, and since exercised in a
5-player session across iOS and Android.

One cost still cannot be measured on a machine that has already been developed on, and it can
only be measured once ever per machine. **Read Step 3 before you run this at a party.**

Written against [HostingPlan.md](../HostingPlan.md) §2, which had rejected a version of this on
paper. §2's economics are unchanged by adoption — what changed is that the design they argued
for no longer exists.

---

## Why anyone would want this

*Written while the hosted service still existed; the argument is what won.*

The service was a single point of failure for every party (HostingPlan §10, §12 risk 3), and
the §6 tier-3 fallback that was supposed to cover an outage was never built. If the page came
from the host machine, a party would survive the service being down with motion controls
intact. It now does — there is no service left to be down.

The proposal that prompted this claimed cloud tunnels add latency that "ruins" the game. **That
premise does not apply here** — `AIM`/`THROW` already ride a direct DataChannel (HostingPlan
§5), so nothing latency-sensitive touches the service today. Resilience is the only real prize.

## What you need in front of you

- The PC and an Android phone **on the same network**, or the phone hosting a hotspot the PC
  joins. Hotspot works fine and removes AP isolation as a confound, since the phone is then the
  router.
- No domain and no certificate of your own. The rig borrows a published one.

## Step 1 — Run it

```bash
pwsh ./deploy/local-https.ps1
```

Windows PowerShell works too — `powershell -File .\deploy\local-https.ps1`, or right-click →
*Run with PowerShell*. `pwsh` is only the spelling that is also right on macOS and Linux.

That is the whole setup. It detects the LAN IP, borrows the certificate, writes the config,
starts the relay, proves the chain, and holds. **Ctrl+C puts everything back.**

| Flag | For |
|---|---|
| `-Ip 10.0.0.213` | The machine has more than one plausible interface, so detection refuses to guess |
| `-Port 9443` | Something already owns 8443 |
| `-SetupOnly` | Verify, then exit leaving the config in place so Unity starts the relay itself |

It stops before the phone is involved if anything is wrong, and each check maps to a failure
that would otherwise be indistinguishable at the phone:

| Check | What a failure means |
|---|---|
| One interface with a default gateway | Ambiguity is an **error, never a guess** — a wrong IP fails exactly like a bad certificate |
| Certificate not expired, SAN covers `*.local-ip.sh` | The borrowed cert lapsed; on the phone this reads as a config mistake |
| `<name>.local-ip.sh` resolves to that IP | **DNS rebinding protection** on the router, stripping private answers from public names |
| `/health` over `https://` | The chain does not validate from this machine |
| `test-deployed.js` over `wss://` | The upgrade or routing is broken under strict validation. **This check no longer runs** — the file was deleted in `1cd7647` and the script still calls it, so it aborts here instead of passing. An existing setup keeps working without it; see [README.md](README.md) |

**What it never does is touch the firewall** — see Step 3, that measurement is one-shot.

The one thing no script can rule out is AP isolation — some routers stop clients on the same
Wi-Fi from reaching each other at all. If the phone cannot load the page, open
`https://<pc-ip>:8443/health` by **raw IP**. A *certificate name mismatch* means the network is
fine and only DNS was at fault. A *timeout* means isolation, and the run proved nothing.

## Step 2 — Play, join, throw

Press Play in `Bootstrap`, scan the QR. Then read, in this order:

| Where | What it tells you |
|---|---|
| **F1, the `Aim` line** | The fastest answer, and it needs no throw. `20 Hz` with `aim`/`attack` moving as you tilt means sensors are live. `no aim frames yet — grip on the phone` while you are gripping means they are not. |
| **F1, per-throw `peak`** | `peak 0.0` is the tell for a sensorless device — `computeSimulatedThrowDescriptor` sends `peakAccel: 0` ([throw.js:104](../controller/js/throw.js:104)). |
| **Unity Console** | The full line: `[Throw] JOE: … (peak 51.5 m/s², 90 samples)`. Sample count reaches the Console only, never the F1 overlay. |

## Step 3 — The firewall, and why it is one-shot

**Read the prompt before you dismiss it.** Note the wording and which profiles it offers.

A development machine cannot measure this honestly, which is the trap: `node.exe` accumulates
allow rules from ordinary `npm start` runs. Worse, those rules are usually **Public**-profile
while a home network is categorised **Private**, so the prompt appears at a party and never in
testing. Once you allow it, the rule persists and that machine can never produce the
measurement again.

This is the cost HostingPlan §8's deliberately-empty table exists to protect, so the number
matters more than it looks. **It is also why the script only ever reports firewall state and
never changes it** — a convenience rule added on your behalf would spend the measurement
without anyone noticing. It prints the rules it finds, their profiles, and this network's
category, so you know in advance whether a prompt is coming.

## Step 4 — Put it back

Ctrl+C. The script stops the relay, deletes the borrowed key, and restores or removes
`relay-url.txt` and `relay-env.txt`.

Worth knowing anyway, since `-SetupOnly` skips it and a hard kill can too:

- `server.pem` / `server.key` live in `%TEMP%\rockskip-local-https`, **never in the repo**.
  A published private key must never reach a build or a commit. The container image that used
  to make this acute is gone, but `.gitignore` has no `*.pem` rule by default, so a key left in
  `relay/` is still one `git add` away from being published permanently.
- A stale `relay-url.txt` leaves the editor pointing at a relay that is not running.
- The TLS branch in `server.js` is inert with the env vars unset and can stay.

---

## Doing it by hand

The script automates the four steps below; they are the fallback when it cannot run, and the
explanation of what it is actually doing.

**Borrow the certificate.** `local-ip.sh` publishes a real Let's Encrypt wildcard for
`*.local-ip.sh` and resolves IP-encoded names, so `10-144-225-71.local-ip.sh` → `10.144.225.71`.

```bash
curl -o server.pem https://local-ip.sh/server.pem && curl -o server.key https://local-ip.sh/server.key
```

**Its private key is published on purpose. This is a test rig and can never be a production
path** — anyone can impersonate anything served under it. Download to a scratch directory, not
into the repo, and delete it afterwards.

Two properties to respect:

- The SAN is `*.local-ip.sh` and covers **one** label only. `10-144-225-71.local-ip.sh` works;
  `x.y.local-ip.sh` does not.
- It is a wildcard, so **it covers any IP**. Moving networks needs no new certificate.

**Start the relay with TLS.** [relay/server.js](../relay/server.js) has an env-gated TLS branch:
set `TLS_CERT` and `TLS_KEY` and it builds an `https` listener instead of `http`.
`WebSocketServer` attaches to either, so `wss://` comes free. **With the variables unset the
path is unchanged** — which is what keeps the plain-HTTP desktop loop working unmodified. This
branch used to be inert in production, where Fly terminated TLS; it is now the normal path.

```bash
cd relay && PORT=8443 TLS_CERT=/path/to/server.pem TLS_KEY=/path/to/server.key node server.js
```

It binds `0.0.0.0`, so a new interface — plugging in, or joining a hotspot — is picked up live
without a restart.

**Point Unity at it.** Find the PC's LAN IP, convert dots to dashes, and write one line to
`relay-url.txt` at the repo root (gitignored):

```
wss://10-144-225-71.local-ip.sh:8443
```

`RelayClient` reads that file ([RelayClient.cs:163](../Assets/Scripts/Net/RelayClient.cs:163))
and derives the page origin from it
([DeriveOrigin, RelayClient.cs:188](../Assets/Scripts/Net/RelayClient.cs:188)), so the join URL
and the QR follow. On the phone side `deriveSocketUrl`
([net.js:27](../controller/js/net.js:27)) uses `location.host`, which carries the `:8443`.

**Optionally let Unity start it.** A `relay-env.txt` beside `relay-url.txt`, one `KEY=VALUE` per
line, is passed to the relay child by `LocalServices` — which is how `-SetupOnly` works:

```
PORT=8443
TLS_CERT=C:\Users\you\AppData\Local\Temp\rockskip-local-https\server.pem
TLS_KEY=C:\Users\you\AppData\Local\Temp\rockskip-local-https\server.key
```

`LocalServices` reuses a relay that is already answering rather than starting a second one, so
running the script and pressing Play do not fight: the script owns the relay and Unity borrows
it.

---

## Results — 2026-08-12

Galaxy S25 hosting a hotspot, PC joined at `10.144.225.71`, Android Chrome.

```
[RelayClient] Connected to wss://10-144-225-71.local-ip.sh:8443
[RelayClient] Room code: NLPT
[Throw] JOE: pow 1.00 tim 0.00 aim  -3° atk  -3° spin  17 (peak 51.5 m/s², 90 samples)
[Throw] JOE: pow 1.00 tim 0.00 aim  11° atk   4° spin  23 (peak 53.8 m/s², 90 samples)
[Throw] JOE: pow 0.89 tim 0.20 aim   8° atk -12° spin  36 (peak 31.9 m/s², 90 samples)
[Throw] JOE: pow 0.65 tim 0.53 aim -28° atk -25° spin 236 (peak 24.9 m/s², 90 samples)
```

| Question | Result |
|---|---|
| Phone trusts the certificate | **PASS** — no interstitial. |
| Secure context | **PASS**, proven by consequence: sensors cannot fire without it. |
| **Motion sensors fire** | **PASS, decisively.** 90 samples per throw, peak 24.9–53.8 m/s². The simulated path sends `sampleCount: 0, peakAccel: 0`, so these are real gestures. |
| Chain validates over a LAN IP | **PASS** — `curl` full verification, `ssl_verify_result 0`. |
| `wss://` under strict validation | **PASS** — open in 23ms, no `rejectUnauthorized` override. This is the path Unity's `ClientWebSocket` takes; a browser being happy would not have proven it. |
| DNS: public name → private IP | **PASS** on two resolvers — a corporate AD resolver and a carrier path via the hotspot. Neither stripped the private answer. **A home router is still untested.** |
| Page load | Local **9–11ms**. Fly warm **240–270ms**. Fly **cold 5.47s**, landing on the first guest of a session. The Fly figures are historical — that deployment is gone — and this row is the clearest thing local hosting won. |
| Firewall cost | **Not measurable on the dev machine.** See Step 3. |
| Guest on cellular | **Confirmed impossible.** A private IP is unroutable from the internet. Such a guest could play under the old hosted design and cannot now. This is the price that was paid. |

### The finding that was not on screen

```
[PeerLink] JOE: no direct connection after 8s (pc=Connecting) — staying on the relay.
```

WebRTC never established; every aim frame went over the relay. It still felt perfect — because
**the relay was on the same machine**, so the fallback was also local. This run cannot
distinguish direct from relay. Against the deployed relay the same fallback is a round trip to
Toronto.

Cause undiagnosed. Candidates: the mDNS candidate obfuscation HostingPlan §5 flags as untested,
or something about hotspot routing. It ran on hotspot topology, which is not the target.
**Independent of the certificate question and worth its own look on normal Wi-Fi.**

## What this settles, and what adoption simply accepted

**Settled:** the technical premise. A locally-served HTTPS page unlocks motion sensors, and the
whole chain works with no per-host domain, no configuration, and no Unity changes.

**Settled since, by use rather than by argument:**

- **Home-router rebinding.** Originally untested — the 2026-08-12 run only had a corporate AD
  resolver and a carrier path via a hotspot. The setup has since run on an ordinary home
  network (`10.0.0.x`) through a multi-player session, so at least one consumer router does not
  strip the private answer. **This is one router, not a general result** — a router that does
  strip it will fail at Step 1's resolution check, which is exactly where you want to find out.

**Not settled. These were accepted as costs, not resolved:**

- **Cellular guests.** Confirmed impossible, and now a live limitation rather than a
  hypothetical one: everybody plays from the same network. This is the single biggest thing
  the project gave up by leaving hosted infrastructure.
- **The firewall prompt**, against HostingPlan §8's setup table — which local hosting can no
  longer keep empty, because something listens now.
- **Key distribution.** A shared wildcard means shipping one private key inside every
  distributed binary — extractable, not secret, and CA rules force revocation within 24h of
  disclosure, breaking every install at once. A per-host CSR fixes that and reintroduces Let's
  Encrypt's 50-certificates-per-registered-domain-per-week cap. **This is the one that would
  block distribution**, and it is unaddressed. It does not bite while the game is run by the
  person who built it, on their own machine, from a checkout — which is the only situation this
  design currently has to survive.

Adoption was a decision to live with that list, and none of it stopped being true.

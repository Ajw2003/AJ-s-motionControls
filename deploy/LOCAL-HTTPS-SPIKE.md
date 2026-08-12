# Local-HTTPS spike — serving the controller page from the host machine

**Result so far: the core premise holds.** A page served from *this* machine over HTTPS, on a
publicly-trusted certificate for a name that resolves to a LAN IP, gives an Android phone a
secure context and **motion sensors fire normally**. Measured 2026-08-12 on a Galaxy S25.

Two costs remain unmeasured, both needing a home network, and one of them can only be measured
once ever per machine. **Read Step 5 before you run this at home.**

This tests an alternative to [HostingPlan.md](../HostingPlan.md) §2, which rejected a version
of it on paper. Nothing here changes that document — the economics it rejects the idea on are
untouched by this result. See *What this does and does not settle* at the bottom.

---

## Why anyone would want this

The service is a single point of failure for every party (HostingPlan §10, §12 risk 3), and the
§6 tier-3 fallback that is supposed to cover an outage is unbuilt. If the page came from the
host machine, a party would survive the service being down with motion controls intact.

The proposal that prompted this claimed cloud tunnels add latency that "ruins" the game. **That
premise does not apply here** — `AIM`/`THROW` already ride a direct DataChannel (HostingPlan
§5), so nothing latency-sensitive touches the service today. Resilience is the only real prize.

## What you need in front of you

- The PC and an Android phone **on the same network**, or the phone hosting a hotspot the PC
  joins. Hotspot works fine and removes AP isolation as a confound, since the phone is then the
  router.
- No domain and no certificate of your own. The rig borrows a published one.

## Step 0 — Pre-flight

Same reason as [SPIKE-TEST.md](SPIKE-TEST.md) §0: some routers isolate clients from each other,
and a phone that cannot reach the PC at all fails identically to a certificate problem.

If the page later fails to load on the phone, open `https://<pc-ip>:8443/health` by **raw IP**.
A *certificate name mismatch* means the network is fine and only DNS was at fault. A *timeout*
means isolation, and the run proved nothing.

## Step 1 — Borrow a certificate

`local-ip.sh` publishes a real Let's Encrypt wildcard for `*.local-ip.sh` and resolves
IP-encoded names, so `10-144-225-71.local-ip.sh` → `10.144.225.71`.

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

## Step 2 — Start the relay with TLS

[relay/server.js](../relay/server.js) grows an env-gated TLS branch: set `TLS_CERT` and
`TLS_KEY` and it builds an `https` listener instead of `http`. `WebSocketServer` attaches to
either, so `wss://` comes free.

**With the variables unset, the path is unchanged** — this is inert in production, where Fly
terminates TLS and the relay never sees a certificate (HostingPlan §3, §7).

```bash
cd relay && PORT=8443 TLS_CERT=/path/to/server.pem TLS_KEY=/path/to/server.key node server.js
```

It binds `0.0.0.0`, so a new interface — plugging in, or joining a hotspot — is picked up live
without a restart.

## Step 3 — Point Unity at it

Find the PC's LAN IP, convert dots to dashes, and write one line to `relay-url.txt` at the repo
root (gitignored):

```
wss://10-144-225-71.local-ip.sh:8443
```

Nothing else changes. `RelayClient` reads that file
([RelayClient.cs:163](../Assets/Scripts/Net/RelayClient.cs:163)) and derives the page origin
from it ([DeriveOrigin, RelayClient.cs:188](../Assets/Scripts/Net/RelayClient.cs:188)), so the
join URL and the QR follow. On the phone side `deriveSocketUrl`
([net.js:27](../controller/js/net.js:27)) uses `location.host`, which carries the `:8443`.

**The spike needs no Unity code at all.**

## Step 4 — Play, join, throw

Press Play in `Bootstrap`, scan the QR. Then read, in this order:

| Where | What it tells you |
|---|---|
| **F1, the `Aim` line** | The fastest answer, and it needs no throw. `20 Hz` with `aim`/`attack` moving as you tilt means sensors are live. `no aim frames yet — grip on the phone` while you are gripping means they are not. |
| **F1, per-throw `peak`** | `peak 0.0` is the tell for a sensorless device — `computeSimulatedThrowDescriptor` sends `peakAccel: 0` ([throw.js:104](../controller/js/throw.js:104)). |
| **Unity Console** | The full line: `[Throw] JOE: … (peak 51.5 m/s², 90 samples)`. Sample count reaches the Console only, never the F1 overlay. |

## Step 5 — The firewall, and why it is one-shot

**Read the prompt before you dismiss it.** Note the wording and which profiles it offers.

A development machine cannot measure this honestly, which is the trap: `node.exe` accumulates
allow rules from ordinary `npm start` runs. Worse, those rules are usually **Public**-profile
while a home network is categorised **Private**, so the prompt appears at a party and never in
testing. Once you allow it, the rule persists and that machine can never produce the
measurement again.

This is the cost HostingPlan §8's deliberately-empty table exists to protect, so the number
matters more than it looks.

## Step 6 — Put it back

- Delete `server.pem` / `server.key`. **A published private key must never reach a build.**
- Delete `relay-url.txt`, or the editor keeps pointing at a relay that is not running.
- The TLS branch in `server.js` is inert with the env vars unset and can stay.

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
| Page load | Local **9–11ms**. Fly warm **240–270ms**. Fly **cold 5.47s** — `auto_stop_machines = 'suspend'` in [fly.toml](../fly.toml), landing on the first guest of a session. |
| Firewall cost | **Not measurable on the dev machine.** See Step 5. |
| Guest on cellular | **Confirmed impossible.** A private IP is unroutable from the internet. Such a guest plays fine today and could not under this design. |

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

## What this does and does not settle

**Settled:** the technical premise. A locally-served HTTPS page unlocks motion sensors, and the
whole chain works with no per-host domain, no configuration, and no Unity changes.

**Not settled, and these are what HostingPlan §2 actually rejects the idea on:**

- **Key distribution.** A shared wildcard means shipping one private key inside every
  distributed binary — extractable, not secret, and CA rules force revocation within 24h of
  disclosure, breaking every install at once. A per-host CSR fixes that and reintroduces Let's
  Encrypt's 50-certificates-per-registered-domain-per-week cap.
- **The firewall prompt**, against §8's empty setup table.
- **Cellular guests**, who can play today and could not under this design.
- **Home-router rebinding**, still untested.

A passing spike is a licence to weigh those, not to skip them.

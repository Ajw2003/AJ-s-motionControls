# Hosting runbook — run the relay on this machine

Gets the controller page served from **this PC** over HTTPS, so a phone on the same network
gets a secure context and its motion sensors unlock. Design and reasoning:
[HostingPlan.md](../HostingPlan.md) §2.

> **This replaces the Fly.io deployment.** The hosted relay is gone: `fly.toml`, `Dockerfile`,
> `.dockerignore`, `deploy/Caddyfile`, `deploy/rockskip-relay.service` and
> `relay/test-deployed.js` were all deleted in `1cd7647`, and the Fly app was torn down. The
> VPS route that used to sit at the bottom of this file is abandoned with it. Nothing in the
> game connects to a hosted service any more.

---

## The one command

```bash
pwsh ./deploy/local-https.ps1
```

Windows PowerShell works too — `powershell -File .\deploy\local-https.ps1`, or right-click →
*Run with PowerShell*. `pwsh` is only the spelling that is also right on macOS and Linux.

That is the whole setup. In order, it:

1. detects this machine's LAN IP (`Resolve-LanAddress`)
2. converts it to the IP-encoded name `local-ip.sh` publishes a wildcard certificate for —
   `10.0.0.213` becomes `10-0-0-213.local-ip.sh`
3. downloads that certificate and key to `%TEMP%\rockskip-local-https`, **never into the repo**
4. checks the name actually resolves to that IP from this machine
5. writes `relay-url.txt` (`wss://<name>:8443`) and `relay-env.txt` (`PORT`, `TLS_CERT`,
   `TLS_KEY`) at the repo root — both gitignored
6. starts the relay with TLS and proves `/health` and the `wss://` upgrade both work

**Ctrl+C puts everything back** — stops the relay, deletes the borrowed key, restores or
removes both config files.

> ⚠️ **Step 6 is broken — but latent, so read this before concluding the game is broken.**
> `Test-Chain` shells out to `node test-deployed.js` (`local-https.ps1:465`), but
> `relay/test-deployed.js` was deleted in `1cd7647` along with the Fly config. Node exits 1,
> so the script calls `Fail` and stops — *after* `Start-Relay` has already run. The same dead
> reference survives in `relay/package.json` as the `test:deployed` script.
>
> **Why the game still plays today anyway.** Nothing in the play path goes through this
> script once the setup exists. A run on 2026-08-13 — hours before the deletion — left behind
> `relay-url.txt`, `relay-env.txt` and the certificate in `%TEMP%`, all gitignored or outside
> the repo, so no checkout disturbs them. Unity's `LocalServices` reads `relay-env.txt` and
> starts the relay itself on Play. The script is only needed to *establish* that state.
>
> **When it will start mattering**, i.e. when the persisted state stops being valid:
>
> | Trigger | Effect |
> |---|---|
> | The borrowed certificate expires (**Sep 18 2026** on the current copy) | TLS fails; a re-run is required, and the re-run aborts |
> | DHCP moves the PC off its current IP | `relay-url.txt` names the wrong host |
> | `%TEMP%` is cleared — Storage Sense does this unprompted | Certificate and key are gone |
> | A new machine, or a teammate's first setup | Nothing to inherit |
>
> **Workaround until it is fixed:** the relay is already up and serving when the script bails,
> so a run is still usable — or start it yourself with the values `-SetupOnly` writes (see
> [RECONNECT-TEST.md](RECONNECT-TEST.md) Test B for the exact command). The fix is either to
> restore a smoke test under that name or to drop the call.

| Flag | For |
|---|---|
| `-Ip 10.0.0.213` | The machine has more than one plausible interface, so detection refuses to guess |
| `-Port 9443` | Something already owns 8443 |
| `-SetupOnly` | Verify, then exit leaving the config in place so Unity's `LocalServices` starts the relay itself |

Then press Play in `Bootstrap` and scan the QR. `RelayClient` reads `relay-url.txt` and
derives the page origin from it, so the join URL and the QR follow automatically.

## Why a borrowed certificate rather than a plain LAN address

Phone motion sensors require a secure context, and an HTTPS page cannot open a plain `ws://`
socket — so `http://10.0.0.213:3000` fails twice over. A publicly-trusted certificate needs a
public name, and `local-ip.sh` publishes exactly that: a real Let's Encrypt wildcard for
`*.local-ip.sh`, plus DNS that maps IP-encoded labels back to the address they encode. That
gives a private LAN address a name the phone's trust store already accepts, with no domain and
no certificate of our own.

Two properties of that certificate to respect:

- The SAN is `*.local-ip.sh` and covers **one** label only. `10-0-0-213.local-ip.sh` works;
  `x.y.local-ip.sh` does not.
- **Its private key is published on purpose.** Anyone can impersonate anything served under
  it. This is a test-and-play rig on your own network and can never be a distribution path —
  which is why the script keeps the key in `%TEMP%` and deletes it on exit.

## What this costs, and who cannot play

**A guest on mobile data cannot join.** The name resolves to a private address, which is
unroutable from the internet. Everyone plays from the same network, or nobody plays.

**A firewall prompt is likely the first time**, because unlike the old outbound-only design
something now listens on this machine. The script deliberately reports firewall state and
never changes it — see [LOCAL-HTTPS-SPIKE.md](LOCAL-HTTPS-SPIKE.md) Step 3 for why that
measurement is one-shot per machine and worth not spending by accident.

**Some routers will break it.** DNS rebinding protection strips private-IP answers from public
names, and AP isolation stops two clients on the same Wi-Fi reaching each other at all. Step 4
of the script catches the first; nothing can catch the second except trying.

## When something goes wrong

| Symptom | Almost always | Check |
|---|---|---|
| Script stops at interface detection | Several interfaces with a default gateway; guessing wrong fails exactly like a bad certificate | Re-run with `-Ip <the right one>` |
| `Could not reach local-ip.sh` | TLS 1.2 not enabled in Windows PowerShell, or the service is down | The script sets the protocol itself; a cached copy in `%TEMP%` is used if present |
| Name does not resolve to the IP | **Router DNS rebinding protection** stripping the private answer | Try a phone hotspot; if that works, it is the router |
| `/health` fails over `https://` | Certificate expired, or the wrong IP was chosen | Re-run — the script re-downloads and re-checks the SAN |
| Phone cannot load the page, PC can | AP isolation, or the firewall | Open `https://<pc-ip>:8443/health` by **raw IP** on the phone: a *certificate name mismatch* means the network is fine and only DNS was at fault; a *timeout* means isolation |
| Page loads, sensors dead | Not a secure context | In the phone's browser console: `window.isSecureContext` |
| Unity connects nowhere | `relay-url.txt` stale, or the relay is not running | The file should hold `wss://<dashed-ip>.local-ip.sh:8443`, and the IP must be current |
| Everything worked yesterday, not today | **The LAN IP changed** — DHCP moved the machine | Re-run the script; it rewrites the name and the config |

## Day-to-day

| Task | How |
|---|---|
| Start hosting | `pwsh ./deploy/local-https.ps1` |
| Stop and clean up | Ctrl+C in that window |
| Let Unity own the relay | `-SetupOnly`, then press Play |
| Watch relay output | The script's own window, or Unity's Console when `LocalServices` owns it |
| Relay integration tests | `cd relay && npm test` |

`LocalServices` reuses a relay that is already answering rather than starting a second one, so
running the script and pressing Play do not fight: the script owns the relay and Unity borrows
it.

## The domain

`skiprocking.ca` is registered and resolves, but it currently points at **registrar parking**
(`play.skiprocking.ca` is a CNAME to `pixie.porkbun.com`) — it is not wired to this machine or
to anything else, and **no code or config in this repo references it**. It was bought as a
cosmetic vanity name for the Fly deployment that no longer exists. Pointing it at a host is a
possible future step, not part of how the game runs today.

---

## Operating notes

- **Abuse surface:** anyone who can reach the relay can send `CREATE_ROOM`; there is
  per-socket rate limiting but none per IP. On a home LAN this is a non-issue, and it is the
  main thing that would need revisiting if the relay were ever exposed publicly again.
- **Nothing survives a restart.** Rooms live in a per-process `Map`, so restarting the relay
  drops every room — the host reclaims its code and phones rejoin on their own. That path is
  tested in [RECONNECT-TEST.md](RECONNECT-TEST.md).

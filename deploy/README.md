# Phase 1 runbook — deploy the relay

Gets `wss://play.skiprocking.ca` serving the controller page and relaying messages, so hosts
install the game and play with no setup of their own. Design and reasoning:
[HostingPlan.md](../HostingPlan.md) §4.

Two routes. **Fly.io is the one to use** — the config lives in the repo and there is no server
to administer. The VPS route at the bottom is the migration path if Fly's pricing, cold starts
or terms ever stop suiting you; it produces an identical result and nothing in the game code
knows which one is running.

> **DNS is not a blocker.** `skiprocking.ca` was still NXDOMAIN at time of writing — registry
> delegation had not landed, and `.ca` registrations can pend on CIRA's Canadian Presence
> Requirements check. It only gates steps 4–5. Every app gets `https://<app>.fly.dev` with a
> working certificate, so steps 1–3 give a fully testable system today, phone sensors included.
>
> ```
> nslookup -type=NS skiprocking.ca
> ```
>
> Porkbun nameservers in the answer means steps 4–5 are unblocked.

---

# Route A — Fly.io

## 1. Install flyctl and sign in

```
winget install --id Fly.Flyctl
```

```
fly auth signup
```

A payment card is required even on the smallest plan. With `min_machines_running = 0` the
machine suspends between parties, so the bill tracks time actually played rather than the
calendar.

## 2. Create the app

**From the repo root**, not `relay/`. `--copy-config` keeps [fly.toml](../fly.toml) as written
instead of regenerating it, and `--no-deploy` stops it launching before the certificate exists.

```
fly launch --no-deploy --copy-config
```

App names are globally unique across Fly, so `rockskipping-relay` may be taken — accept
whatever name it suggests and let it rewrite `app =`. Nothing depends on the app name; the
game only ever sees `play.skiprocking.ca`.

Confirm the region is `yyz` when prompted. Latency to this machine is added to every aim frame
in Phase 1.

## 3. Deploy — before touching DNS

Deploy comes first, and the reason matters: **Fly allocates no IP addresses until the first
deploy** (`fly ips list` on a launched-but-undeployed app returns an empty table), and every
app gets `https://<app>.fly.dev` with a working certificate for free.

So the whole of Phase 1 is testable on `rockskipping-relay.fly.dev` — including phone motion
sensors, which only care that the origin is secure, not what it is called. **The custom domain
is a cosmetic finishing step, not a prerequisite.** Do not wait on `.ca` delegation.

```
fly deploy
```

Docker is not installed locally, so this uses Fly's remote builder — no local Docker needed.
Expect a few minutes the first time; later deploys reuse cached layers.

What it does, in order: uploads the build context (allowlisted to `relay/` and `controller/`
by [.dockerignore](../.dockerignore)), builds [Dockerfile](../Dockerfile) remotely, pushes the
image, **allocates a shared IPv4 and a dedicated IPv6**, and starts one machine in `yyz`.

Watch for these in the output:

- `--> Building image done` — the image built. A failure here is almost always `npm ci`
  disagreeing with `relay/package-lock.json`.
- `Creating a v6 address` / `shared ipv4` — address allocation, which is what step 4 needs.
- `1 desired, 1 placed, 1 healthy` — the machine is up.

If it fails, `fly logs` shows the container's own output; the relay logs
`Rock Skipping relay listening on :8080` when it has started cleanly.

### Scale to exactly one machine — not optional

`fly deploy` creates **two** machines by default for high availability. That is wrong for this
app and will fail intermittently in a way that is very hard to diagnose at a party.

The relay keeps rooms in a per-process `Map` (`const rooms = new Map()` in
[server.js](../relay/server.js)). With two backends, a host can create room `WCHY` on machine A
while a phone's join lands on machine B, which has never heard of it — the phone gets
`ROOM_NOT_FOUND` for a code that is plainly on the TV. Whether it happens depends on how Fly
routes each connection, so it is intermittent, and guests arriving from eight different
networks are far more likely to spread across backends than a single test client is.

```
fly scale count 1
```

Two machines are not buying anything here regardless: a restart drops every room either way,
so there is no availability to lose. On a fresh app, `fly deploy --ha=false` avoids creating
the second machine in the first place.

```
fly machines list
```

Confirm exactly one row before continuing.

### Verify before going further

```
curl -s https://rockskipping-relay.fly.dev/health
```

Expect `{"ok":true,"rooms":0}`. This proves the machine is up, `PORT` reached the process, and
Fly's proxy is routing.

```
curl -sI https://rockskipping-relay.fly.dev/ | head -1
```

Expect `HTTP/2 200`. This is the check that matters most, because it is the one that catches
the build-context mistake: it proves `controller/index.html` is *inside the image*. A `404`
here means the Dockerfile built without `controller/`, and the symptom in the wild would be
sockets connecting fine while every phone shows a blank page.

Then the WebSocket, which is the part curl cannot reach — and the part everything depends on:

```
cd relay && npm run test:deployed
```

Nine checks against the live deployment: upgrade through the proxy, room creation, join,
player→host routing with server-side identity stamping, host→player targeting, malformed-input
survival, and the multi-backend probe described above. Point it at any host with
`node test-deployed.js wss://play.skiprocking.ca`.

**Then open `https://rockskipping-relay.fly.dev/` on your phone over mobile data.** Padlock,
join screen, and in the browser console `window.isSecureContext === true`. At that point Phase 1
works end to end and everything below is naming.

## 4. Point DNS at it

Now the app has addresses. Use a **CNAME**, not A/AAAA:

| Type | Host | Answer | TTL |
|---|---|---|---|
| CNAME | `play` | `rockskipping-relay.fly.dev` | 600 |

A CNAME tracks Fly's addresses automatically, covers IPv6 without a second record — which
matters, because guests arrive on mobile networks that are increasingly IPv6-only — and
survives Fly reassigning an IP. A/AAAA records would work and would need maintaining. This is
legal here only because `play` is a subdomain; a CNAME at the apex would not be.

No API key is needed for any of this — that requirement disappeared with the DNS-01 design. If
you enabled API Access on the domain earlier, turn it back off.

**This step is blocked until `skiprocking.ca` delegates** (still NXDOMAIN at time of writing).
Nothing else is.

```
nslookup play.skiprocking.ca
```

Expect an answer that chains through `rockskipping-relay.fly.dev` to Fly's addresses.

## 5. Issue the certificate

Only once step 4 resolves — Fly validates by looking DNS up, so doing this early just produces
a certificate stuck in `Awaiting configuration`.

```
fly certs add play.skiprocking.ca
```

Read the output rather than assuming: Fly prints exactly which DNS records it wants. With the
CNAME already in place it can usually validate over HTTP and needs nothing more, but if it asks
for an `_acme-challenge.play` CNAME, add that too and re-check.

```
fly certs show play.skiprocking.ca
```

Wait for the certificate to report as issued and `Ready` before relying on it. Renewal from
then on is Fly's problem — no certbot, no cron entry, and no ACME code anywhere in the relay.

## 6. Final verification

The same three checks as step 3, now against the real name:

```
curl -s https://play.skiprocking.ca/health
```

```
curl -sI https://play.skiprocking.ca/ | head -1
```

**And the one that counts: open `https://play.skiprocking.ca/` on your phone over mobile data.**
Mobile data rather than Wi-Fi is deliberate — it proves the path works from outside your
network, which is the only thing a guest's phone will ever do.

That single page load exercises DNS delegation, the CNAME, certificate validity, Fly's proxy,
and static serving from inside the image, together. If it loads with a padlock and the join
screen appears, Phase 1's infrastructure is done and only the Part C code changes remain.

## When something goes wrong

| Symptom | Almost always | Check |
|---|---|---|
| Build fails at `npm ci` | `relay/package-lock.json` out of sync with `package.json` | Run `npm install` locally, commit the lock file |
| Build uploads for ages | `.dockerignore` not being applied — the Unity `Library/` is entering the context | Confirm you ran `fly deploy` from the **repo root** |
| `/health` times out | Machine not started, or `PORT` mismatch | `fly status`, then `fly logs` for `listening on :8080` |
| `/health` works, `/` returns 404 | The image has no `controller/` | Rebuild from the repo root; check the `COPY controller` line ran |
| Certificate stuck `Awaiting configuration` | DNS not resolving yet, or the CNAME is wrong | `nslookup play.skiprocking.ca`, then `fly certs show` for what Fly wants |
| Phone shows a certificate warning | Certificate not `Ready` yet | `fly certs show play.skiprocking.ca` |
| `ROOM_NOT_FOUND` for a code that is on the TV | More than one machine — rooms are per-process | `fly machines list`; fix with `fly scale count 1` |
| Sensors dead, page loads fine | Not a secure context | In the phone's browser console: `window.isSecureContext` |
| First connection of a session is slow | Machine suspended between parties — expected | `fly status` shows `suspended`; see `min_machines_running` in fly.toml |

## Day-to-day

| Task | Command |
|---|---|
| Redeploy after a change | `fly deploy` |
| Watch logs | `fly logs` |
| Machine state | `fly status` |
| Certificate expiry | `fly certs show play.skiprocking.ca` |

Rooms are ephemeral and clients reconnect on their own, so a redeploy mid-party costs a few
seconds of reconnection rather than the match.

**flyctl may not be on PATH** in a shell that was already open when it installed. It lives at
`%USERPROFILE%\.fly\bin\flyctl.exe`; open a new terminal, or call it by full path.

---

# Part C — the code changes

**Done.** Compiles clean on 6000.3.10f1, and `npm test` stays green at 23/23.

1. **`relay/server.js`** — binds `process.env.BIND_HOST || '0.0.0.0'`.
   **An earlier draft of this file said to hardcode `127.0.0.1`, which was wrong and would
   have broken the Fly deploy**: Fly's proxy reaches the process from outside the VM, so
   loopback would refuse every connection and fail the health check. Loopback is right only
   behind a same-host proxy, which is why the VPS route's systemd unit sets `BIND_HOST`
   explicitly and Fly does not.
2. **`RelayClient.cs`** — `relayUrl` defaults to `wss://rockskipping-relay.fly.dev`, overridable
   by a one-line `relay-url.txt` beside the executable (HostingPlan §12.6). It now also owns
   `PublicOrigin` and `JoinUrlFor()`, **derived from the socket URL** rather than configured
   separately — which is what makes it structurally impossible for the page and the socket to
   drift onto different hosts.
3. **`LocalServices.cs`** — the `cloudflared` spawn, the `TunnelUrlPattern` regex, the
   stderr-scraping `Update()` loop, `PublicUrl` and the `OpeningTunnel` state are all gone. What
   remains is a development-only local relay, **off by default** in `Bootstrap`.
4. **`JoinScreen.cs`** — driven by the relay connection rather than by `LocalServices`, which is
   now optional. Keying the screen off a local process would have left every non-developer
   machine stuck on "starting up" forever. Progress steps are now Connected / Room code.

### Pointing the game at a local relay for development

Two things, and both are needed — either alone does nothing useful:

1. Tick **Start Local Services** on the `Bootstrap` component.
2. Create `relay-url.txt` next to the project folder (or beside the built executable):

```
ws://localhost:3000
```

It is gitignored, so it stays a per-machine choice.

---

# Route B — VPS (migration path)

Only if you leave Fly. Produces the same result: TLS-terminating proxy in front of the relay on
loopback. Ubuntu 24.04, smallest instance, region nearest where you play.

Files here are ready to copy and need no editing: [Caddyfile](Caddyfile) and
[rockskip-relay.service](rockskip-relay.service).

**Base setup** — Node 22 matching local dev, since Ubuntu's repos ship Node 18, below the
`>=20` in `relay/package.json`:

```
curl -fsSL https://deb.nodesource.com/setup_22.x | bash - && apt-get install -y nodejs
```

Caddy, from its own repository. It obtains and renews the certificate over ACME HTTP-01 with no
configuration beyond the three-line Caddyfile, and proxies WebSocket upgrades transparently:

```
apt install -y debian-keyring debian-archive-keyring apt-transport-https curl && curl -1sLf 'https://dl.cloudsmith.io/public/caddy/stable/gpg.key' | gpg --dearmor -o /usr/share/keyrings/caddy-stable-archive-keyring.gpg && curl -1sLf 'https://dl.cloudsmith.io/public/caddy/stable/debian.deb.txt' | tee /etc/apt/sources.list.d/caddy-stable.list && apt update && apt install -y caddy
```

Service account and firewall — 80 and 443 only, since the relay binds loopback:

```
useradd --system --create-home --home-dir /opt/rockskip --shell /usr/sbin/nologin rockskip && ufw allow OpenSSH && ufw allow 80/tcp && ufw allow 443/tcp && ufw --force enable
```

**Upload**, from your PC in the repo root. Both directories are required — `server.js` resolves
`STATIC_DIR` as `../controller`, so the server layout has to mirror the repo:

```
scp -r relay controller deploy root@YOUR_VPS_IP:/opt/rockskip/
```

**Install**, on the VPS:

```
cd /opt/rockskip/relay && npm ci --omit=dev && chown -R rockskip:rockskip /opt/rockskip
```

```
cp /opt/rockskip/deploy/rockskip-relay.service /etc/systemd/system/ && cp /opt/rockskip/deploy/Caddyfile /etc/caddy/Caddyfile && systemctl daemon-reload && systemctl enable --now rockskip-relay && systemctl reload caddy
```

DNS for this route is a single A record at `play` pointing to the VPS IPv4 — no AAAA unless the
instance has IPv6.

**Verify** with the same checks as Route A step 6, plus one more: `curl` the relay's port from
off the box and confirm it is refused, proving the loopback binding from Part C item 1 landed.

**Logs:** `journalctl -u rockskip-relay -f`, and `/var/log/caddy/rockskip.log` for requests.

---

## Operating notes

- **Abuse surface:** anyone can send `CREATE_ROOM`; there is per-socket rate limiting but none
  per IP. Fine for a hobby game, worth revisiting if the address is ever shared publicly.
- **This service is a single point of failure for every host.** That is the accepted cost of
  zero host setup — [HostingPlan.md](../HostingPlan.md) §6 has the fallback ladder that softens
  it.

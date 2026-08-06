# Phase 1 runbook — deploy the relay

Gets `wss://play.skiprocking.ca` serving the controller page and relaying messages, so hosts
install the game and play with no setup of their own. Design and reasoning:
[HostingPlan.md](../HostingPlan.md) §4.

Two routes. **Fly.io is the one to use** — the config lives in the repo and there is no server
to administer. The VPS route at the bottom is the migration path if Fly's pricing, cold starts
or terms ever stop suiting you; it produces an identical result and nothing in the game code
knows which one is running.

> **Blocked until DNS resolves.** `skiprocking.ca` was still NXDOMAIN at time of writing —
> registry delegation had not landed. `.ca` registrations can also pend on CIRA's Canadian
> Presence Requirements check. Certificate issuance cannot happen for a name that does not
> resolve, on either route.
>
> ```
> nslookup -type=NS skiprocking.ca
> ```
>
> Porkbun nameservers in the answer means you are clear to continue.

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

## 3. Point DNS at it

Get the app's addresses:

```
fly ips list
```

Then in Porkbun → `skiprocking.ca` → DNS, add both records so the name works on IPv6-only
mobile networks as well as IPv4:

| Type | Host | Answer | TTL |
|---|---|---|---|
| A | `play` | the `v4` address from `fly ips list` | 600 |
| AAAA | `play` | the `v6` address | 600 |

No API key is needed for any of this — that requirement disappeared with the DNS-01 design. If
you enabled API Access on the domain earlier, turn it back off.

Wait for it to resolve:

```
nslookup play.skiprocking.ca
```

## 4. Issue the certificate

```
fly certs add play.skiprocking.ca
```

Fly validates ownership against the DNS records from step 3, issues the certificate, and
renews it from then on. There is no certbot, no cron entry, and no ACME code in the relay.

```
fly certs show play.skiprocking.ca
```

Wait for it to report the certificate as issued before deploying.

## 5. Deploy

```
fly deploy
```

The build runs from the repo root so the image contains both `relay/` and `controller/` — see
[Dockerfile](../Dockerfile). `.dockerignore` uses an allowlist, so the Unity project (`Library/`
alone is gigabytes) never enters the build context.

## 6. Verify

```
curl -s https://play.skiprocking.ca/health
```

Expect `{"ok":true,"rooms":0}`. If this hangs, `fly logs` will say why.

```
curl -sI https://play.skiprocking.ca/ | head -1
```

Expect `HTTP/2 200` — that is the controller page, which proves `controller/` made it into the
image.

Last check, and the one that matters: **open `https://play.skiprocking.ca/` on your phone over
mobile data.** The join screen should load with a valid padlock. That single page load exercises
DNS, the certificate, the proxy, and the static serving together.

## Day-to-day

| Task | Command |
|---|---|
| Redeploy after a change | `fly deploy` |
| Watch logs | `fly logs` |
| Machine state | `fly status` |
| Certificate expiry | `fly certs show play.skiprocking.ca` |

Rooms are ephemeral and clients reconnect on their own, so a redeploy mid-party costs a few
seconds of reconnection rather than the match.

---

# Part C — the code changes

**Not yet done, and Phase 1 is not finished without them.** Four changes, none large:

1. **`relay/server.js`** — `httpServer.listen(PORT)` → `httpServer.listen(PORT, '127.0.0.1')`.
   On Fly this is good hygiene; on the VPS route it is what keeps the plain-HTTP relay off the
   public interface entirely.
2. **`Assets/Scripts/Net/RelayClient.cs`** — `relayUrl` default becomes
   `wss://play.skiprocking.ca`, read from a file beside the executable so a hostname change
   never needs a rebuild (HostingPlan §12.6). Local dev overrides it to `ws://localhost:3000`.
3. **`Assets/Scripts/Net/LocalServices.cs`** — delete the `cloudflared` spawn, the
   `TunnelUrlPattern` regex and the stderr-scraping `Update()` loop. Keep the local-relay spawn
   behind a flag; it is still the right dev loop.
4. **`Assets/Scripts/UI/JoinScreen.cs`** — the join URL becomes the service address instead of
   `LocalServices.PublicUrl`, and the QR fetch moves from `http://127.0.0.1:3000/qr` to the
   service. The progress list loses its "Public tunnel" step.

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

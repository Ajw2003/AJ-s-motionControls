# Replacing local-ip.sh: Hotspot-only, zero servers

## Context

`local-ip.sh` is gone. Not degraded — gone at the DNS layer: `NS local-ip.sh` and `SOA
local-ip.sh` both time out against 8.8.8.8, so its authoritative nameservers are offline and
nothing resolves. `https://local-ip.sh/server.pem` fails at name resolution, not at HTTP.

That takes the game down, because `CertificateProvider.Ensure` treats "no cached cert + service
unreachable" as fatal ([CertificateProvider.cs:130](../../Assets/Scripts/Net/CertificateProvider.cs:130)).
A fresh install cannot get a certificate, cannot open a secure context, and cannot read phone
motion sensors — the entire premise.

**There is no drop-in replacement left.** Verified directly:

| Service | DNS | Published cert + key |
|---|---|---|
| `local-ip.sh` | **dead** (NS timeout) | dead |
| `local-ip.co` | **dead** (NXDOMAIN) | dead |
| `traefik.me` | resolves | **404 on every cert path** (`fullchain.pem`, `privkey.pem`, `cert.pem`, `chain.pem`) |
| `sslip.io` | resolves | never — states so explicitly |
| `nip.io` | resolves | never — states so explicitly |

The mitigation already written into [ZeroSetupPlan.md:451](../ZeroSetupPlan.md:451) — "make the
provider an interface with `traefik.me` / `local-ip.co` as alternates" — is dead on arrival.

### The decision that shrinks this

**LAN mode is being deprecated.** It bought nothing: it already required the phone to be on the
host's network, and Hotspot achieves the same thing with the host as the AP instead of the
router. Dropping it removes the only requirement that forced always-on public infrastructure —
public wildcard DNS existed *solely* so a house router's resolver could map an IP-encoded
hostname back to a DHCP-assigned address. In Hotspot mode [relay/dns.js](../../relay/dns.js) already
answers locally, offline, on the host itself.

Three consequences follow, and they are large:

1. **No public DNS server. Ever.** No VPS, no Fly machine, no UDP :53, no NS delegation, no glue
   records. The `magic-dns-server.js` in [MagicDnsFallback.md](../archive/MagicDnsFallback.md) is not
   needed at all.
2. **No wildcard certificate.** Windows ICS always puts the hotspot on `192.168.137.1`
   ([setup-offline.ps1:57](../../deploy/setup-offline.ps1:57), forced at
   [LocalServices.cs:246](../../Assets/Scripts/Net/LocalServices.cs:246)). The address is fixed, so one
   **single-name** cert for a fixed hostname replaces the wildcard.
3. **The IP-encoding machinery becomes dead weight.** `ToHostName`'s dash encoding, and the whole
   of `ResolveLanAddress` — gateway enumeration, the `VirtualAdapter` regex, the TEST-NET-3 route
   probe tie-break — exist to discover a varying address. Nothing varies now.

`dns.js` already supports this shape: `createResponder` takes **`name`** ("answer exactly this
name") alongside `suffix` ([dns.js:204](../../relay/dns.js:204)), and `test-dns.js` already exercises
the fixed-name path.

### Being straight about "zero infrastructure"

This gets to **zero servers and $0/month**, which is the goal in practice. It cannot get to
literally zero, and it is worth naming the floor rather than discovering it later:

- A publicly-trusted certificate requires a public CA issuing for a name you control. Let's
  Encrypt's 2025 IP-address certs are globally-routable IPs only, explicitly excluding RFC1918,
  so `192.168.137.1` cannot be certified directly.
- Certificate lifetimes are shrinking industry-wide; there is no "issue once" option. Something
  must renew, roughly quarterly.
- That renewal needs the domain's DNS API credentials, which must never ship in the client —
  the design already rejected in `HostingPlan.md` §1 (deleted in `0b7c500`; in git history).

So the irreducible floor is: **a domain you own + an unattended renewal job + a static file.**
The renewal job is a scheduled GitHub Actions workflow — free, no server, no OS, no patching, no
intervention. That is the closest thing to zero that also produces a certificate a guest's
unmodified phone will trust.

---

## Architecture

```
CERTIFICATE — serverless, free, unattended
  GitHub Actions (cron, weekly)
    └─ acme.sh --dns dns_porkbun --issue -d play.skiprocking.ca
         └─ publish server.pem + server.key  →  static object (CDN)
              ├─ fetched at launch by CertificateProvider (existing code path)
              └─ baked into the installer as a seed, so a fresh install
                 works even when that URL is unreachable

DNS — already local, already written
  relay/dns.js on the host, gated to 192.168.137.0/24
    ├─ play.skiprocking.ca            → 192.168.137.1   (createResponder `name`)
    └─ captive-portal probe domains   → 192.168.137.1   (new; see Phase 4)

NOTHING ELSE RUNS ANYWHERE.
```

Every address in the system is now a constant: `192.168.137.1`, `play.skiprocking.ca`, port
8443. The join URL is the same string on every install forever. That alone closes the drift gap
recorded at `Findings-PhoneUnreachable.md:115` (deleted; in git history), where
`relay-url.txt` could outrank the cert-derived URL and silently disagree with it.

### One security constraint to record

The private key is published deliberately — that is what makes the whole approach work, and
`SecurityReview.md` §1 (deleted; in git history) already reasoned it through. With a single-name
cert the blast radius is only `play.skiprocking.ca`; nobody can impersonate `skiprocking.ca`
itself. The one real coupling: whoever holds the key can set cookies scoped to
`.skiprocking.ca` that the apex site would receive. Harmless while the apex is a static page
with no auth, but it should be written down as a constraint on what the apex may ever become —
or sidestepped by putting the controller on its own registrable domain.

---

## Implementation

### Phase 0 — Land this document in the repo

First action on approval: copy this plan to `docs/plans/magic-domain-replacement.md` so it is
tracked and outlives the session, and add "Superseded — see
docs/plans/magic-domain-replacement.md" callouts to
[docs/MagicDnsFallback.md](../archive/MagicDnsFallback.md) (its VPS design is now moot) and
[ZeroSetupPlan.md](../ZeroSetupPlan.md) Risk 1. This follows the convention `Assets/CLAUDE.md`
records for this repo: docs drift fast and say so.

### Phase 1 — Certificate pipeline (serverless, free)

**New: `.github/workflows/renew-cert.yml`** — weekly schedule plus `workflow_dispatch`.

- `acme.sh --issue --dns dns_porkbun -d play.skiprocking.ca`. The
  [`dns_porkbun.sh`](https://github.com/acmesh-official/acme.sh/blob/master/dnsapi/dns_porkbun.sh)
  hook is maintained upstream and needs `PORKBUN_API_KEY` / `PORKBUN_SECRET_API_KEY` as repo
  secrets; API access must also be toggled on per-domain in Porkbun's panel.
- DNS-01 rather than HTTP-01 deliberately: `play.skiprocking.ca` needs **no public A record at
  all**, and HTTP-01 would require a public listener the design no longer has.
- Publishes `fullchain.cer` → `server.pem` and the key → `server.key`. **Exact filenames and the
  fullchain form matter** — `CertificateProvider.Inspect()` hand-decodes the PEM and checks the
  SAN OID `2.5.29.17` ([CertificateProvider.cs:384](../../Assets/Scripts/Net/CertificateProvider.cs:384)),
  so matching the existing shape means no parsing changes.
- Prove the workflow against Let's Encrypt **staging** first, then switch to production.
- Publish to Cloudflare R2 (free tier, custom domain) rather than the repo: the key is
  deliberately public, but committing it puts it in git history permanently and will trip
  GitHub push protection.

**`CoversWildcard` becomes an exact-name check.** With a single-name cert there is no `*.` to
match; the SAN check should assert the configured hostname is present.

### Phase 2 — Repointable config, seed cert, no fatal path

The phase that stops any of this from ever being crippling again.

**New: `Assets/Scripts/Net/MagicDomain.cs`** — one source of truth for hostname and cert URLs,
replacing the constant duplicated across `CertificateProvider.Suffix`, the `DNS_SUFFIX` default at
[relay/server.js:408](../../relay/server.js:408), and `$RelayHostName` at
[setup-offline.ps1:69](../../deploy/setup-offline.ps1:69) — drift that
`Reference-Constants.md:427` (deleted; in git history) already flagged.

- Optional override file `magic-domain.txt`, sited and loaded exactly like
  `NetworkModeStore.Load` (`NetworkMode.cs:30`, deleted with LAN mode; in git history) — never throws, falls back to
  the compiled default. Repointing the domain becomes a text edit, not a Unity rebuild.
- `LocalServices` already forwards `CertificateProvider.Suffix` to the relay child
  ([LocalServices.cs:455](../../Assets/Scripts/Net/LocalServices.cs:455)); point that at `MagicDomain`
  so `server.js`'s value becomes a genuine fallback rather than a second source of truth.
- `setup-offline.ps1` reads the same file instead of its literal.

**Seed certificate shipped in the build.** `server.pem`/`server.key` in `StreamingAssets`, plus a
branch in `Ensure`: when the cache is unusable *and* the download failed, copy the seed into the
cache rather than returning the fatal Result at
[CertificateProvider.cs:130](../../Assets/Scripts/Net/CertificateProvider.cs:130). A brand-new install
then works on a day the CDN is unreachable — exactly today's failure. Existing 90-day/14-day
renewal logic is untouched and takes over on the next successful launch.

**Cert URL list rather than a single URL.** `Download()` tries each in order; one entry today, a
mirror later costs no code.

### Phase 3 — Deprecate LAN

> **Superseded 2026-09-06:** the "deprecate, don't delete" step below is now completed as a
> deletion. `docs/plans/remove-direct-transport.md` removed `NetworkMode`/`NetworkModeStore`,
> `ResolveLanAddress`, `ToHostName` and the LAN hard-stop in `LocalServices` outright — Hotspot
> is the only mode there is now, and `network-mode.txt` is inert on any machine that still has
> one from an older install. See `docs/Findings-DirectTransportFlapping.md` for the reasoning
> that motivated finishing the removal rather than continuing to hold it open.

Deprecate, not delete — keep it reachable for a release in case Hotspot hits a machine it cannot
work on.

- `StartScreen` stops offering the picker and always launches Hotspot; `NetworkModeStore.Load`
  flips its fallback to `NetworkMode.Hotspot`. LAN stays selectable only by hand-editing
  `network-mode.txt`.
- Mark `NetworkMode.Lan` `[Obsolete]` with a pointer to this doc, and mark `ResolveLanAddress` /
  `ToHostName`'s encoding as LAN-only and slated for removal.
- Do **not** delete them this pass — that is a follow-up once Hotspot is proven on real hardware.
- Update `docs/plans/network-mode-selection.md`, `docs/Decision-OfflineHosting.md`,
  `docs/OfflineSetup.md`, `README.md`.

### Phase 4 — Make the Hotspot path actually robust

The user-visible half of this work. Hotspot is now the only path, so its rough edges are the
product.

**Captive-portal detection is the big one.** [setup-offline.ps1:10](../../deploy/setup-offline.ps1:10)
deliberately disables IP forwarding each session so guests are isolated — which means the hotspot
genuinely has no internet, so Android's probe to `connectivitycheck.gstatic.com` and iOS's to
`captive.apple.com` both fail. The phone then flags "No internet", may keep cellular as the
default route, and can silently drop off the network mid-game. This is the most likely cause of
"it just stopped working" reports.

Fix by answering the probes rather than by re-enabling forwarding, which would undo the
isolation decision:

- `dns.js` answers the probe domains with `192.168.137.1`, alongside `play.skiprocking.ca`. Safe
  by construction: the responder is already gated to the hotspot CIDR
  ([dns.js:205](../../relay/dns.js:205)) and already rate-limited, so this is scoped to the host's own
  AP exactly as every commercial captive portal does it.
- `server.js` serves `/generate_204` → HTTP 204 and Apple's `/hotspot-detect.html` → the literal
  `Success` body. Slots into the existing `url.pathname` table next to `/health` and `/qr`
  ([server.js:424](../../relay/server.js:424)).
- These probes are **cleartext HTTP on port 80**, and the relay listens on 8443 — so this needs a
  plain HTTP listener on :80 and a matching firewall rule in `setup-offline.ps1`.
- Alternative worth a deliberate decision, not a silent one: leave ICS forwarding enabled so the
  hotspot has real internet and the problem disappears. That trades away the guest isolation in
  [Decision-OfflineHosting.md](../Decision-OfflineHosting.md). Recommended: keep isolation,
  answer the probes.

**Everything else fixed and constant.** With the hostname, IP and port all constants, the join
URL QR is identical on every install — it can be rendered before the hotspot finishes coming up
instead of waiting on it. Two QRs, Wi-Fi payload then URL, per
the QR rule in [systems/HostingAndCertificates.md](../systems/HostingAndCertificates.md).

**Offline chain validation.** Let's Encrypt certs no longer carry OCSP URLs, so a phone with no
internet validates without a soft-fail stall. Confirm on real hardware in step 6.

### Phase 5 — Installer

What turns this into "download one thing". Inno Setup (single script, no toolchain) over WiX.

- Bundles the Unity build, a Node runtime + `relay/` (removes the "install Node" prerequisite),
  and the seed cert.
- Runs the elevated one-time work now in [setup-offline.ps1](../../deploy/setup-offline.ps1) — firewall
  rules for TCP 8443, TCP 80, UDP 53, the IP-forwarding scheduled task, the hosts entry — **at
  install time**, where elevation already exists.

  > **Superseded — the "run by hand" gap this bullet described is closed.**
  > `HotspotService.EnsureOfflineSetup` (`Assets/Scripts/Net/HotspotService.cs`) now runs
  > `setup-offline.ps1` itself, elevated, the first time a machine ever reaches Hotspot mode —
  > triggered by pressing Start, not a separate command someone has to already know to run. It
  > checks unelevated whether the forwarding-disable scheduled task already exists (the common
  > case after the first run) and only launches the elevated script when it doesn't. Windows
  > still shows exactly one UAC prompt — that boundary cannot be removed, and should not be —
  > but there is no longer a manual step before it. `network-mode-selection.md`'s "run by hand"
  > line is stale for the same reason. This installer bullet is still worth doing eventually
  > (running it at install time removes even that one UAC prompt from party night), but it is
  > no longer the blocker "single largest remaining source of manual intervention" made it
  > sound like.
- Nothing else: `LocalServices` already spawns the relay and fetches the cert on launch.

### Cleanups folded in

- Delete `deploy/local-https.ps1` — broken since `1cd7647` removed the `test-deployed.js` it
  shells out to, superseded by `CertificateProvider`, yet still the command documented in
  `README.md`, `Assets/CLAUDE.md:43` and `deploy/README.md`.
- Fix [README.md:42](../../README.md:42): claims `RelayClient.relayUrl` compiles in
  `wss://rockskipping-relay.fly.dev`; the actual value is `ws://localhost:3000`.

---

## Verification

1. **Cert pipeline** — run `renew-cert.yml` manually against LE staging, confirm two objects
   land. Switch to production, then
   `curl -sS https://<cert-url>/server.pem | openssl x509 -noout -text` and confirm the SAN is
   `play.skiprocking.ca` with `NotAfter` ~90 days out.
2. **Unity parses it** — `Inspect()` is the strict reader (hand-rolled PEM base64, SAN OID
   `2.5.29.17`). Launch and confirm `Certificate valid until <date>` rather than a parse failure.
3. **No fatal path** — delete the `local-https` cache under `persistentDataPath`, block network
   access, launch. Must reach `Ready` off the seed instead of "Could not get a certificate."
4. **Repointability** — write a bogus hostname into `magic-domain.txt`, confirm the derived
   hostname and the relay child's env both change with no rebuild; delete it, confirm the default
   returns.
5. **Relay tests** — `cd relay && npm test`. `test-dns.js` hardcodes `.local-ip.sh` in ~9 places
   and needs updating; add cases for the probe domains and for a probe query arriving from
   outside the hotspot CIDR (must still be dropped).
6. **End-to-end on real hardware** — Start → Hotspot, phone scans both QRs, sensors work with the
   PC's internet unplugged. Specifically watch that the phone does **not** show "No internet" and
   does not drift back to cellular over a full match. This is the test
   [network-mode-selection.md:77](../archive/plans/network-mode-selection.md:77) records as still
   outstanding, plus the captive-portal behaviour.
7. **Installer** — clean Windows VM, no Node installed. Install, launch, confirm a phone joins
   with zero manual steps.

### On handover

I can run and verify steps 2–5 here. Porkbun API key creation, the R2 bucket and the GitHub
secrets need accounts I do not hold; those will be handed over as `UNTESTED:` steps and marked as
such. Steps 6 and 7 need a real phone and a clean VM and are yours. Everything I can execute on
this machine, I will execute before writing it down.

## Cost

| Item | Monthly |
|---|---|
| `skiprocking.ca` | already owned |
| GitHub Actions renewal + static hosting (free tiers) | **$0** |
| Servers | **none** |

## Open question

Phase 4's captive-portal fix needs a plain HTTP listener on port 80 on the host. That is a second
firewall rule and a second bind, and on some machines port 80 is already taken (IIS, Docker
Desktop, Skype's legacy binding). Worth deciding whether the relay should fail loudly or degrade
quietly to "no probe answering, phone shows No internet but the game still works" when :80 is
unavailable. Recommended: degrade quietly, log loudly — the game works either way, and a failed
launch is worse than a cosmetic warning on the phone.

# Hosting runbook — the relay runs on this machine automatically

Gets the controller page served from **this PC** over HTTPS, so a phone gets a secure
context and its motion sensors unlock. Design and reasoning:
[docs/systems/HostingAndCertificates.md](../docs/systems/HostingAndCertificates.md). Certificate design:
[docs/plans/magic-domain-replacement.md](../docs/plans/magic-domain-replacement.md).

> **This replaces the Fly.io deployment.** The hosted relay is gone: `fly.toml`, `Dockerfile`,
> `.dockerignore`, `deploy/Caddyfile`, `deploy/rockskip-relay.service` and
> `relay/test-deployed.js` were all deleted in `1cd7647`, and the Fly app was torn down.
> Nothing in the game connects to a hosted service any more.
>
> **`deploy/local-https.ps1` is also gone**, deleted rather than kept broken. It automated
> exactly what `Assets/Scripts/Net/CertificateProvider.cs` and `Assets/Scripts/Net/
> LocalServices.cs` now do at launch with nobody having to remember to run anything — see
> those files' own remarks for the certificate/relay lifecycle. Its last verification step
> called `relay/test-deployed.js`, itself deleted in `1cd7647`, so the script had been unable
> to complete a clean run since that commit.

---

## The one step: press Play

There is no longer a setup script to run before Play. `LocalServices` (on `Bootstrap`,
`Start Local Services` ticked by default) does the whole thing on launch:

1. `CertificateProvider` obtains a certificate for `MagicDomain.HostName`
   (`play.skiprocking.ca` by default — see `Assets/Scripts/Net/MagicDomain.cs`), from the
   cache, a fresh download, or the seed bundled in `StreamingAssets` if both fail.
2. It starts the relay (`relay/server.js`) over TLS with that certificate.
3. In Hotspot mode, `HotspotService` brings up a Wi-Fi hotspot on `192.168.137.1` once the
   relay and its DNS responder (`relay/dns.js`) are ready.
4. `JoinScreen` renders the Wi-Fi and game QR codes once everything is up.

**One thing still needs doing once per machine, elevated:**

```powershell
./deploy/setup-offline.ps1            # apply — needs Administrator
./deploy/setup-offline.ps1 -DryRun    # inspect first — needs no elevation
```

This registers the firewall rules (relay TCP 8443, hotspot DNS UDP 53, captive-portal probe
TCP 80), the scheduled task that disables IP forwarding each session so guests stay isolated,
and a hosts entry so the *host* can resolve its own relay while offline. See
[docs/OfflineSetup.md](../docs/OfflineSetup.md) for the full reasoning behind each item.

`HotspotService` runs this for you, elevated, the first time this machine starts a hotspot — so
in practice you only run it by hand to `-DryRun` it or to `-Uninstall` a test machine. Its exit
codes are a contract, not decoration: **0** already correct, **1** something genuinely wrong,
**2** needs Administrator, **3** (`-DryRun` only) not set up yet.

## Checking the stack without Unity

```powershell
./deploy/verify-stack.ps1              # needs no elevation
./deploy/verify-stack.ps1 -KeepRunning # leave the relay up to point a phone at it
```

Starts the relay with the exact environment `LocalServices` gives it and then *does* each
thing rather than inspecting config: fetches the certificate, checks `/health` really reports
`dns.bound`, and sends four real DNS queries to the running responder.

**The check worth knowing about is "This PC resolving play.skiprocking.ca".** There are two
independent halves to name resolution here and they fail nothing alike — a phone resolves the
game through `relay/dns.js`, but the *host* takes no DHCP from itself and so never asks its own
responder. It asks a public resolver, and `play.skiprocking.ca` is a registered name whose
public record is the registrar's parking page. Without the hosts entry above, Unity therefore
connects to a stranger's web server on the internet and retries there forever — which looks
like anything except a DNS problem. See
[docs/Findings-DnsAudit.md](../docs/archive/Findings-DnsAudit.md).

## Why HTTPS at all, rather than plain `ws://`

Phone motion sensors require a secure context, and an HTTPS page cannot open a plain `ws://`
socket — so a bare `http://192.168.137.1:3000` fails twice over. `CertificateProvider`
downloads a publicly-trusted certificate for a domain we own
(`play.skiprocking.ca`, renewed weekly by `.github/workflows/renew-cert.yml`) so the fixed
hotspot address gets a name the phone's trust store already accepts, with no manual
certificate wrangling.

Two properties of that certificate worth knowing:

- It is **single-name**, not a wildcard — it covers exactly `play.skiprocking.ca` (or
  whatever `magic-domain.txt` overrides it to), not a whole subdomain space. Windows ICS
  always assigns the hotspot `192.168.137.1`, so one fixed name is all Hotspot mode ever
  needs.
- **Its private key is published on purpose**, same as local-ip.sh's was — see
  `docs/plans/magic-domain-replacement.md`'s security note for the constraint that comes
  with that.

## LAN mode has been removed

LAN mode used to be selectable by hand-editing `network-mode.txt` to `Lan`, but its
certificate had already stopped validating — the replacement certificate is single-name for a
fixed hostname, which a LAN address's varying name can never match — and
[docs/plans/remove-direct-transport.md](../docs/archive/plans/remove-direct-transport.md) removed the
dead code path outright rather than leaving it deprecated. Hotspot is the only mode there is
now; `network-mode.txt` is inert if one is still lying around from an older install. See
[docs/plans/magic-domain-replacement.md](../docs/plans/magic-domain-replacement.md) Phase 3
for why the certificate stopped working in the first place. Everything below that mentions
"the venue's network" or a LAN address describes that removed path, kept here as history.

## What this costs, and who cannot play

**A guest on mobile data cannot join.** Hotspot mode's address is private and unroutable from
the internet; everyone plays on the PC's own hotspot, or nobody plays.

**A firewall prompt is likely the first time**, because something now listens on this
machine. `setup-offline.ps1` reports firewall state and creates the rules it needs; it does
not silently widen anything beyond that.

## When something goes wrong

| Symptom | Almost always | Check |
|---|---|---|
| `[LocalServices] Could not get a certificate.` on a fresh install | No network, no cache, and no seed certificate in `StreamingAssets` | Connect this machine to the internet once, or ship a seed cert — see `CertificateProvider.TrySeedCache` |
| `/health` fails over `https://` | Certificate expired and the renewal workflow hasn't published a fresh one, or a stale cache | Check `.github/workflows/renew-cert.yml`'s last run |
| Phone shows "No internet" but the game works | Expected on an isolated hotspot unless the captive-portal probes are answered — see `relay/dns.js`/`relay/server.js`'s `/generate_204` and `/hotspot-detect.html` | Confirm the plain HTTP `:80` listener bound; it degrades quietly and logs loudly if `:80` is already taken (IIS, Docker Desktop, etc.) |
| Phone pops a sign-in / portal page before anyone has opened the game | Deliberate — `serveCaptiveProbe` reports a portal until that device has loaded the controller, so the phone's OS opens the nudge page itself. See `docs/plans/join-failure-reduction.md` P2 | It must stop once the device is playing. If a phone still flags "No internet" mid-match, check `relay/funnel.js` reached `PAGE_OPEN` for it |
| Phone opened the game over plain `http://` and no sensors work | Fixed — `:80` redirects everything that is not a probe to the HTTPS origin | If it recurs, check `:80` is served by `handleProbeRequest`, not `handleRequest`: the latter ends in `serveStatic` and would serve the controller insecurely |
| Phone cannot load the page, PC can | The isolation `setup-offline.ps1`'s scheduled task enforces is working as intended for guest-to-guest traffic; check the guest can still reach the host itself | Open `https://192.168.137.1:8443/health` by raw IP on the phone |
| Unity connects nowhere | `relay-url.txt` stale (leftover from local development), or the relay is not running | Delete `relay-url.txt` to let `LocalServices` resolve its own origin, or check the relay process is alive |
| The host itself can't resolve its own relay while offline | The host does not take DHCP from itself, so it never consults its own DNS responder | Confirm `setup-offline.ps1`'s hosts entry is present: `MagicDomain.HostName  192.168.137.1` |

## Day-to-day

| Task | How |
|---|---|
| Play | Press Play in `Bootstrap` |
| One-time machine setup | `./deploy/setup-offline.ps1` (Administrator) |
| Watch relay output | Unity's Console |
| Relay integration tests | `cd relay && npm test` |

## The domain

`skiprocking.ca` is registered on Porkbun. `play.skiprocking.ca` is the hostname the
certificate covers and the DNS responder answers in Hotspot mode — see
[docs/plans/magic-domain-replacement.md](../docs/plans/magic-domain-replacement.md) for the
full design, including why it needs no public A record at all.

---

## Operating notes

- **Abuse surface:** anyone who can reach the relay can send `CREATE_ROOM`; there is
  per-socket rate limiting but none per IP. On an isolated hotspot this is a non-issue.
- **Nothing survives a restart.** Rooms live in a per-process `Map`, so restarting the relay
  drops every room — the host reclaims its code and phones rejoin on their own. That path is
  tested in [RECONNECT-TEST.md](RECONNECT-TEST.md).

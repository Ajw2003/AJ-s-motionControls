# A self-operated fallback for `local-ip.sh`

**Status: design sketch, not built.** Answers the question "can we recreate `local-ip.sh`'s
logic ourselves, on our own Porkbun domain, as a backup if it disappears?" — yes, and it slots
directly into the risk [ZeroSetupPlan.md](ZeroSetupPlan.md) already logged rather than opening a
new one.

---

## What `local-ip.sh` actually provides

Two independent things, bundled under one name:

1. **A wildcard authoritative nameserver** that parses an IP out of a hostname label and answers
   with it — `10-0-0-213.local-ip.sh` → `A 10.0.0.213`. No zone file, no per-name records; the
   name *is* the record.
2. **A published wildcard certificate.** A real Let's Encrypt cert for `*.local-ip.sh`, with the
   **private key given away openly**, so any machine can terminate TLS for that domain without
   ever talking to a CA itself. `CertificateProvider.cs` just downloads `server.pem`/`server.key`
   ready-made ([ZeroSetupPlan.md:182](ZeroSetupPlan.md:182)).

Both are recreatable on a domain you control. Neither requires a player's PC to do anything new.

## Why this is not the design that was rejected before

[HostingPlan.md §2](HostingPlan.md:37) rejected two adjacent ideas, and it's worth being precise
about why neither applies here:

- **"Self-hosted, own domain" (Design 2, §1):** rejected because the DNS API credential would
  ship *inside every player's binary* — account-wide control, extractable, unrotatable without
  breaking every install. **This design keeps the Porkbun credential on one server you alone
  operate.** Players never see it, exactly as they never see it today (`local-ip.sh`'s operator
  holds an equivalent credential and no one considers that a problem for the person hosting a
  game on their LAN).
- **"Self-hosted authoritative DNS" ([§2 alternatives table](HostingPlan.md:109)):** rejected
  because it needs port 53 forwarded *and the PC permanently online* — if the host's machine is
  off, the domain stops resolving. **That objection is about each player's gaming PC being the
  nameserver.** Here the nameserver is a small always-on VPS you run, structurally identical to
  how `local-ip.sh` itself is hosted. A player's PC is never involved in DNS at all, same as
  today.

So this isn't reopening either rejected design — it's building the thing `local-ip.sh` *is*,
under your own name, as a second instance of the same pattern.

## Architecture

```
Porkbun (registrar + DNS host for the parent zone)
  └─ NS delegation: ip.yourdomain.com → ns1.yourdomain.com (glue: A record to VPS IP)

Your VPS (~$5/mo, always on)
  ├─ magic-dns-server.js   — UDP :53, authoritative for ip.yourdomain.com only
  │                          parses "10-0-0-213.ip.yourdomain.com" → replies 10.0.0.213
  ├─ acme renewal (cron)   — acme.sh/certbot + Porkbun DNS-01 plugin, wildcard cert for
  │                          *.ip.yourdomain.com, renews well before 90-day expiry
  └─ static HTTPS endpoint — serves the fresh cert.pem/key.pem, exactly what
                             CertificateProvider.cs already expects from local-ip.sh
```

`CertificateProvider.cs` already has the shape for this: [ZeroSetupPlan.md's Risk
1](ZeroSetupPlan.md:422) proposes "make the provider an interface with `traefik.me` /
`local-ip.co` as alternates rather than a hardcoded URL." This VPS becomes one more entry in that
list — tried as a fallback if `local-ip.sh` is unreachable, or promoted to primary later if you'd
rather depend on infrastructure you control.

## The DNS server is not a rewrite of `dns.js` — it's a different threat model

[relay/dns.js](relay/dns.js) is deliberately narrow: it's gated to a hotspot's own subnet
(`allow` CIDR, [dns.js:225](relay/dns.js:225)) precisely *because* it answers with a single fixed
IP and only ever runs while a LAN with no other resolver exists. That guard is what makes it safe
— see the "open recursive resolver" incident already documented in
[SecurityReview.md:250](SecurityReview.md:250).

A public-facing version can't use a subnet allow-list — it must be reachable from anywhere. It
has to be safe by construction instead:

- **Never forwards.** `dns.js`'s `upstream` option ([dns.js:219](relay/dns.js:219)) must not
  exist in this build at all — a public authoritative server that only ever answers its own zone
  or refuses cannot be used as an amplification reflector, because there's no code path that
  sends a query anywhere on a client's behalf.
- **Parses instead of matching one fixed IP.** The one real logic change from `dns.js`:
  `matches()` currently checks `endsWith(suffix)`; this needs to also decode the label's leading
  part into an IP (reject anything that doesn't parse as four dot/dash-separated octets — refuse,
  don't guess).
- **Per-source rate limit stays**, same rationale as today, tuned for internet-scale noise rather
  than a party of eight.
- **REFUSED for out-of-zone queries, not silence** — unlike the hotspot responder (which drops
  packets from outside its subnet because *any* reply to a possibly-forged source is the
  amplification behavior being prevented), this server has no subnet to distrust; a real client
  outside the zone still deserves a real REFUSED rather than a timeout. The forged-source
  amplification risk here is fully closed by "never forwards" and by keeping answers small
  (a single A record, never larger than the query).

Worth reusing from `dns.js`: `decodeQuery`, `buildReply`, `makeRateLimiter`, and the packet-level
tests in `test-dns.js` — the wire parsing is where mistakes are expensive and it's already
hardened. Worth **not** reusing: `makeSubnetTest`/`allow`, `upstream`, and the "drop, don't
refuse" posture, because they're answers to a question ("who's inside my hotspot") this server
doesn't get to ask.

## Certificate renewal specifics

- Wildcard certs require DNS-01 (no HTTP-01 fallback for `*.foo`), so the ACME client needs to
  create/delete a `_acme-challenge.ip.yourdomain.com` TXT record via the Porkbun API each
  renewal. `acme.sh` has a maintained Porkbun DNS hook (`dns_porkbun`); Certbot has a
  community plugin. Either works from a cron job on the VPS.
- Publish the result the same way `local-ip.sh` does — a plain HTTPS GET for `server.pem` /
  `server.key` — so `CertificateProvider.cs`'s existing SAN-check-and-cache logic
  ([ZeroSetupPlan.md:178](ZeroSetupPlan.md:178)) needs no new code, only a second URL to try.
- The same tradeoff `local-ip.sh` accepts applies here too: publishing the private key openly
  means anyone can terminate TLS for `*.ip.yourdomain.com`. That's fine under the same reasoning
  already recorded in [ZeroSetupPlan.md Risk 2](ZeroSetupPlan.md:426) — it's scoped to a name
  that only ever encodes a private LAN address, not a security boundary — but it's a decision to
  make consciously, not inherit by accident.

## What this costs

- One VPS, ~$5/month, already-owned domain (Porkbun) delegating one subdomain to it.
- A few hours to write `magic-dns-server.js` (mostly reuse), wire up the ACME cron job, and stand
  up the static cert-serving endpoint.
- Near-zero ongoing maintenance — the renewal cadence mirrors what's already designed for
  `local-ip.sh` (checked every launch, renewed with 14 days to spare, [ZeroSetupPlan.md
  §2.3](ZeroSetupPlan.md:182)).
- One new secret to hold: the Porkbun API token, live only on the VPS, never shipped in a build.

## Open question before building

Confirm the domain: `skiprocking.ca` is registered and currently parked
([HostingPlan.md:27](HostingPlan.md:27)) — presumably the one meant here, but worth confirming
it's the Porkbun account in question and that repurposing it (from "dead vanity name" to "load-
bearing infrastructure") is intended before a subdomain gets delegated away from it.

## Where this fits in the existing plan

Slot into [ZeroSetupPlan.md Risk 1](ZeroSetupPlan.md:422)'s mitigation, upgrading "an interface
with alternates" from a design note to an actual second provider. Does not block or get blocked
by Phases 1–5 — it's an independent addition to `CertificateProvider.cs`'s provider list, and the
DNS half only matters once Phase 4.3 (hotspot-mode local DNS) or a public reachability need
exists; until then it's purely certificate-fallback insurance.

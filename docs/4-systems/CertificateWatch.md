# Certificate watch

**Source:** `Ajw2003/rockskipping-cert`, files `.github/workflows/pad-cert-watch.yml`,
`.github/workflows/pad-cert-watchdog.yml` and `ci/*.sh` (merged in rockskipping-cert#1). Built
for #17 and #18 under #13. Decision: [`6-decisions/Decisions.md`](../6-decisions/Decisions.md),
2026-10-09.

## What it owns

Keeping one shared, publicly trusted certificate for the controller page valid, with no server.
The page needs HTTPS before phones give it motion sensors. Getting the certificate onto a PC and
serving it is #20. Nudges from launchers are #19.

## How it works

```
pad-cert-watch.yml (dispatch only) ──▶ check ──▶ issue? ──▶ publish pad-cert.json ──▶ next run ~60 min later
pad-cert-watchdog.yml (5-min wake) ──▶ one long loop ──▶ restarts the chain if quiet > 70 min
```

- **Check:** downloads `pad-cert.json` from the `pad-current` release, unpacks it
  (`ci/unbundle_cert.sh`), and runs `ci/check_cert.sh`. That checks the certificate against the
  revocation list it names, with the list's signature verified, then checks expiry.
- **Decide** (the `decide` step):
  - **revoked:** reissue on the next spare name (`ci/pick_name.sh`). The names rotate
    `pad` → `pad2` → `pad3`, because Let's Encrypt allows only 5 certificates per exact name per
    week.
  - **expiring** (under 30 days), **invalid** or **missing:** reissue on the same name.
  - **ok:** do nothing.
  - **unreadable** (the revocation list couldn't be fetched): warn and don't reissue, so Let's
    Encrypt's own outages can't burn the weekly limit.
- **Issue:** acme.sh, with DNS-01 through Porkbun (the `PORKBUN_API_KEY` and
  `PORKBUN_SECRET_API_KEY` secrets on that repo) and `ec-256`. No state survives between runs, so
  **every issue has a new key.**
- **Pack and publish:** `ci/bundle_cert.sh` refuses anything that fails one of these checks:
  - the chain isn't full;
  - the certificate doesn't cover the name;
  - the key doesn't match;
  - the issuer doesn't match the endpoint that was asked for.

  It then writes one `pad-cert.json` holding the name, serial, expiry, chain and key, uploaded to
  the release with `--clobber`. One file means a launcher can never pair a new certificate with an
  old key.
- **Keep going:** the `next` job (`ci/chain_next.sh`, from CannaScraper) waits until 60 minutes
  after the run began, dispatches the next run, and makes sure a watchdog run is active. It also
  re-enables the watchdog every run, because GitHub turns off a schedule after 60 days with no
  repo activity.
- **Staging:** a manual `environment: staging` run publishes to `pad-staging`, issues from Let's
  Encrypt's test authority, and never starts the chain.

## Proven (2026-10-09)

- **Staging run 37987926842:** every step succeeded and `next` was skipped, as designed.
  `pad-staging/pad-cert.json` held a certificate for `pad.skiprocking.ca` from
  `(STAGING) Baloney Bulgur YE2`, with a matching key and a 4-certificate chain.
- **Production run 37988137248:** the `watch` job succeeded. `pad-current/pad-cert.json` holds
  `pad.skiprocking.ca`:
  - serial `05F461B571E57854F1915BFA803B865C2BFB`, issuer Let's Encrypt `YE2`, valid until
    7 January 2027;
  - `openssl verify` against the system trust store printed `OK`;
  - `check_cert.sh` printed `ok: not revoked`;
  - the key matches.
- **Not yet proven:** the chain dispatching its own next run, and the watchdog starting. The first
  `next` job dispatches about 60 minutes after 20:36 UTC.

## Invariants

- **A key is never reused.** Fresh acme.sh state on every run.
- **One issue per run, at most.** A Let's Encrypt refusal fails the run and is retried on the next
  one, never in a loop.
- **The live tag only ever holds a production certificate, and the staging tag only a staging
  one.** `bundle_cert.sh` enforces it.
- **Launchers download one file.** The release asset name `pad-cert.json` and the tag
  `pad-current` are part of the contract with #20. Changing either breaks every shipped
  launcher.

## Traps

- **The key is public by design.** Anyone can revoke the certificate. This system exists to make
  that a short outage, not to prevent it. See the decision record.
- **Rock Skipping's certificate is separate.** `play.skiprocking.ca` on the `current` release,
  renewed weekly by RockSkipping's own `renew-cert.yml`. Nothing here touches it.
- **Both renewals use Porkbun keys.** Rotating or deleting a Porkbun key pair can break either
  one.
- **Stopping it:** disable both workflows in the repo's Actions tab, then cancel any run in
  progress. A disabled workflow doesn't stop a run already going.

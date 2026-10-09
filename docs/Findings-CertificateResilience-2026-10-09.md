# Findings: secure pages for the phone without an always-on service or running costs

**Date:** 2026-10-09. **Prompted by:** aj, on #13: keep the certificate from being revoked or
breaking, with no always-on service, no ongoing cost, and easy for any user.

**Status:** research only. Nothing here is built or decided, and #13 is unchanged.

## The constraint that drives everything

Phones only give motion sensors to secure (HTTPS) pages. Rock Skipping gets HTTPS offline by
publishing one certificate's private key for `play.skiprocking.ca` to every install
(`docs/plans/magic-domain-replacement.md`). Under ACME, the protocol Let's Encrypt uses, a
revocation request may be signed with the certificate's own private key, not only the account key
([Ansible ACME revoke docs](https://docs.ansible.com/ansible/latest/collections/community/crypto/acme_certificate_revoke_module.html)).
So **anyone holding a published key can revoke that certificate**, at any time, with a standard
tool. Rotating keys, adding more certificate authorities or renewing faster only buys time: every
new key is published too. The only real guard is a private key that never leaves the user's PC.

## Three options

### A. Each PC is its own certificate authority (recommended default)

On first run the PC creates its own small certificate authority, which stays on the PC. It issues
the controller page's certificate for the hotspot address (`192.168.137.1`). Each phone installs
that authority **once**, from a QR code on the join page (#14):

- **iPhone:** install the profile, then turn on full trust under Settings → General → About →
  Certificate Trust Settings
  ([SSL Store](https://www.thesslstore.com/blog/trust-manually-installed-root-certificates-in-ios/)).
  About a minute, once per phone.
- **Android:** install the certificate in Settings. Chrome on Android uses the platform's trust
  store, user certificates included, by Google's stated design
  ([LWN](https://lwn.net/Articles/678170/)), but one report disagrees
  ([Sophos](https://community.sophos.com/utm-firewall/f/web-protection-web-filtering-application-visibility-control/96531/chrome-android-oreo-unable-to-google-even-with-utm-ca-cert-installed)).
  **Must be tested on aj's phone.**

| Guards against | How |
|---|---|
| Revocation by anyone | Nothing to revoke: no public certificate authority involved |
| Expiry while offline | We choose the lifetime; Apple's 398-day limit applies to public authorities, not user-installed ones (*to confirm on device*) |
| Certificate-authority policy | None applies |
| Ongoing cost or service | None. Works fully offline forever, no domain needed |

**Cost to the user:** a one-time install per phone. That suits this product: it's *your* phone,
used every time, unlike Rock Skipping's party guests who each join once. A consumer product already
works this way: Start9's home server has users install its root on iOS
([Start9 docs](https://docs.start9.com/0.3.5.x/device-guides/ios/ca-ios.html)).

**Risk to manage:** a phone trusting this authority would trust anything it signs. The authority's
key must stay on the PC, and should be limited to the hotspot address (X.509 name constraints;
*whether iOS and Android Chrome honour these must be checked*).

### B. Each PC gets its own public certificate through a free DNS provider (optional, for zero phone setup)

The PC registers its own free name with a non-profit dynamic-DNS service such as deSEC
(`<name>.dedyn.io`, [deSEC docs](https://desec.readthedocs.io/en/latest/dyndns/configure.html)).
It proves ownership through a DNS record, and gets a normal Let's Encrypt certificate whose
**private key never leaves the PC**. On the hotspot, the relay's own DNS answers that name with
`192.168.137.1`, as Rock Skipping does today.

| Guards against | How |
|---|---|
| Revocation by anyone | Key is private, so only the PC can revoke it |
| Shared failure | Each install has its own certificate; one problem hits one PC |
| Ongoing cost or service | None of ours: the PC renews itself whenever it's online; deSEC is free |

**Costs and open questions:** a one-time free deSEC account and pasted token. A dependency on a
free third party. The certificate expires after about 90 days if the PC is never online, in which
case fall back to A. Whether `dedyn.io` names share Let's Encrypt's weekly limit is **unconfirmed**:
deSEC once reported the limit lifted
([Let's Encrypt forum](https://community.letsencrypt.org/t/dynamic-dns-provider-desec-io-can-be-used-with-lets-encryt/17931)).
Check the Public Suffix List before relying on it.

### C. Keep the shared published key (testing only)

Free and already working, but anyone can revoke it (above), and it shares failures with Rock
Skipping. Fine for aj's testing now. Not for release.

## Recommendation

**A as the default, B as an option, C only until A exists.** A meets every constraint: no
service, no cost, no domain, works offline indefinitely. Its one cost, a one-minute install per
phone, fits a product used with your own phone. B removes even that for users willing to make a
free account. Neither publishes a key, so neither can be revoked by a stranger.

## Revisited under aj's phone rule: two QR codes, nothing else

aj, later the same day: the phone gets two QR codes and nothing else, with no app, no download and
no other setup. If an installed certificate can't be avoided, a downloadable app would be better,
since aj sees the install as more friction than an app.

That rules out A on the phone, and only **B** meets the rule (flow for the PC and each phone, step by step: `generated/route-b-flow/index.html`). The phone sees a normal, trusted
certificate, so scanning the two QR codes is all it does. The setup moves to the PC instead:

- **One free sign-in on the PC, once.** Either provider works with an account the user may already
  have:
  - **DuckDNS:** sign in with an existing account, copy a token, and set the TXT record with one
    HTTPS request
    ([Posh-ACME plugin](https://www.powershellgallery.com/packages/Posh-ACME/4.9.0/Content/Plugins%5CDuckDNS.ps1)).
  - **deSEC:** a captcha plus an emailed activation link
    ([deSEC quickstart](https://desec.readthedocs.io/en/latest/quickstart.html)).

  The launcher (#12) can open the page and take the token, but the person has to do the sign-in.
  Removing that step would need a service run by us, which aj ruled out.
- **The PC must go online now and then to renew.** Certificates last 90 days today, 64 days from
  February 2027 and 45 days from February 2028
  ([Let's Encrypt](https://letsencrypt.org/docs/cert-lifetimes/)). If one lapses, the controller
  page fails until the PC is next online.

The fallbacks are worse:

- **Accepting a warning ("proceed anyway").** This needs no install, but iOS Safari may refuse
  untrusted certificates outright
  ([Kurento docs](https://doc-kurento.readthedocs.io/en/latest/_sources/knowledge/selfsigned_certs.rst.txt)).
  It is also unknown whether either browser still hands out motion data after the warning is
  bypassed. *Needs testing; not relied on.*
- **An app.** A native app needs no HTTPS. But iPhone distribution means Apple's paid developer
  programme, a yearly fee, which breaks the no-running-cost rule. Android can be free to sideload,
  but that is still a download.

## Revisited again: no new third party, so harden the shared certificate

aj, later the same day: no DuckDNS, and no other new third party. The infrastructure already in
use stays: the `skiprocking.ca` domain at Porkbun, Let's Encrypt, and GitHub (the weekly
`.github/workflows/renew-cert.yml` job publishes to the `Ajw2003/rockskipping-cert` release).

**What can't be done:**

- **A published key can't be protected from revocation.** Under the Baseline Requirements a CA
  must revoke within 24 hours of evidence that a key was disclosed
  ([CA/B Forum ballot SC6](https://lists.cabforum.org/pipermail/public/2018-August/030334.html)).
  Let's Encrypt also accepts a revocation signed with the certificate's own key, and then blocks
  that key ([Let's Encrypt](https://community.letsencrypt.org/t/2024-03-28-cp-cps-deviation-regarding-keycompromise-key-blocking/215579)).
  Publishing the key also breaks Let's Encrypt's subscriber agreement, which is a risk to the
  account itself.
- **Per-install keys under our own domain need a request channel.** The PC would have to send its
  certificate request to the job that holds the Porkbun credentials. Without a service, nothing
  can carry that request.

**What revocation actually breaks offline (reported; not tested on our phones):**

- A phone on the offline hotspot can't fetch revocation data at all.
- iPhone Safari reportedly doesn't enforce ordinary revocation
  ([Let's Encrypt forum](https://community.letsencrypt.org/t/are-revoked-certificates-detected-in-safari-and-chrome/42677),
  [Apple forums](https://developer.apple.com/forums/thread/808875)).
- Chrome's built-in revocation list holds only a small curated subset
  ([LWN](https://lwn.net/Articles/1033809)).

So a revoked certificate may keep working on phones. The real cost lands on new downloads and
renewals.

**The bigger hidden risk is a browser's built-in key blocklist.** It works offline. Chromium once
blocked sslip.io's published key this way
([blocklist README](https://cobalt.googlesource.com/cobalt.git/+/25902c6cb1ab9cfd8ae2ee6b0fb52953f73deda5/net/data/ssl/blocklist/README.md)).
A blocked key fails everywhere until the certificate is reissued with a new one.

**Hardening, using only what's already in place ($0):**

| # | Change | Guards against |
|---|---|---|
| 1 | A name of its own for this product (e.g. `pad.skiprocking.ca`) with its own key | One product's revocation or blocking taking down the other |
| 2 | A new key on every issue. **Already true**: no acme.sh state persists between runs (`renew-cert.yml:169-171`) | A blocklisted or revoked key outliving one certificate |
| 3 | Spare names, each with its own key, issued in the same run. The launcher uses the first good one, and the QR code carries whichever name is in use, so users never see the switch | One revocation stopping play |
| 4 | A daily check in the workflow that reissues as soon as a published certificate is revoked | A revocation lasting until the weekly run |
| 5 | Ship the key inside the launcher download, not as a file named `server.key` on a public release | Automated scanners finding and reporting it. It doesn't stop a determined person |
| 6 | If no valid certificate is left, the page falls back to plain HTTP: buttons and sticks keep working, motion turns off with a message | Total failure. The worst case becomes "no motion", not "no controller" |

**What's left exposed:** someone determined can revoke every spare each time they're published.
That denies motion until the next issue; nothing is stolen. Anyone holding the key could also
impersonate the page to phones on the same hotspot.

**Test that settles how much this matters:** issue a throwaway certificate on a test name with the
existing pipeline, revoke it, then load it on an Android phone and an iPhone, offline on the hotspot
and online a day later.

## Proposal: an hourly revocation watch, with the key downloaded at runtime

aj's idea: a scheduled job, hourly or daily, that checks the published certificate hasn't been
revoked and reissues it at once if it has. The key isn't shipped in any download; the launcher
fetches it at runtime. First setup needs internet; play after that doesn't.

**The check works today (run 2026-10-09 against the live Rock Skipping certificate):**

- Each certificate names its own revocation list (CRL). For `play.skiprocking.ca`, serial
  `0662714B…4961`, that's `http://ye1.c.lencr.org/98.crl`.
- The list was 60 KB with 1,536 entries, last updated 28 minutes before the check (next scheduled
  update 9 days later).
- `openssl verify -crl_check` against the issuing YE1 certificate printed `c1.pem: OK`, meaning
  not revoked. It also checks the list's signature.

The whole check is three downloads and one command, so an hourly run is cheap.

**What the job does each run:**

1. Download the published certificate, its revocation list, and the issuer.
2. If it's revoked, or close to expiry: issue a new certificate with a **new key**, rotating to
   the next spare name, and publish it.
3. Otherwise stop. Almost every run ends here.

**Limits to design around:**

- **Let's Encrypt issues at most 5 certificates per exact name per 7 days, with no overrides**
  ([rate limits](https://letsencrypt.org/docs/rate-limits/)). Rotating across three names gives
  15 a week.
- **The whole domain is capped at 50 a week**, shared with Rock Skipping. Someone who revoked every
  new certificate could exhaust that. Phones probably wouldn't notice offline (see above), but new
  setups would.
- **GitHub turns off schedules in a public repo after 60 days with no activity**
  ([GitHub docs](https://docs.github.com/en/actions/how-tos/manage-workflow-runs/disable-and-enable-workflows)).
  With 90-day certificates renewed at 30 days left, a quiet repo sits right on that edge. The job
  needs its own keep-alive, such as a monthly heartbeat commit.
- **Cost:** none in a public repo. A private repo bills each run as a whole minute, so an hourly
  job uses about 720 of the 2,000 free minutes a month.

**What the launcher does:** on every start with internet, download the current certificate and
key, run the same revocation check, and cache them. Offline, use the cache until it expires.

**Not shipping the key doesn't make it private.** It's still a public download. It only keeps it
out of the installer and out of the git history, and makes it a little harder for automated
scanners to find. Everything in the previous section about who can revoke it still holds.

## To check before building

1. Android Chrome trusts a user-installed authority for a page at `https://192.168.137.1` (aj's phone).
2. iOS accepts a long-lived certificate from a user-installed authority, and honours name constraints.
3. iOS Safari grants motion permission on that page once the authority is trusted.
4. For B: deSEC's API allows the `_acme-challenge` record, and `dedyn.io` is on the Public Suffix List.
5. For B under the two-QR rule: DuckDNS's single shared TXT record works for one name per PC.
6. The warning fallback: on each phone, does a page whose certificate warning was bypassed still
   report `isSecureContext` and receive `devicemotion`?

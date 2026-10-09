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

## To check before building

1. Android Chrome trusts a user-installed authority for a page at `https://192.168.137.1` (aj's phone).
2. iOS accepts a long-lived certificate from a user-installed authority, and honours name constraints.
3. iOS Safari grants motion permission on that page once the authority is trusted.
4. For B: deSEC's API allows the `_acme-challenge` record, and `dedyn.io` is on the Public Suffix List.

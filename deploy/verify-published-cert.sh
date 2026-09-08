#!/usr/bin/env bash
#
# Verify the certificate published by .github/workflows/renew-cert.yml, by downloading it
# from the same public URL a player's machine uses and checking everything that has to be
# true for a phone to load the controller page without a warning.
#
# This is the end-to-end check. It deliberately does not look at any local build output,
# any workflow state, or anything inside GitHub Actions — it fetches the published artifact
# anonymously, exactly as CertificateProvider.Download() does, and validates it the way
# CertificateProvider.Inspect() and fetch-seed-cert.ps1 do. If this passes, the pipeline
# worked; if it fails, it names which link broke.
#
# Run it any time, from anywhere, with no credentials:
#
#     ./deploy/verify-published-cert.sh
#
# The workflow runs it too, after publishing, so a broken publish fails in CI rather than
# on somebody's phone at a party.
#
# Options:
#   --tag TAG          release tag to check (default: current)
#   --domain NAME      hostname the SAN must cover (default: play.skiprocking.ca)
#   --repo OWNER/REPO  certificate repo (default: Ajw2003/rockskipping-cert)
#   --allow-staging    accept a Let's Encrypt STAGING issuer. Only for verifying a staging
#                      rehearsal; a staging certificate is trusted by no device.
#   --min-days N       fail if fewer than N days remain (default: 1). Packaging a seed wants
#                      a much higher bar — fetch-seed-cert.ps1 uses 30.
#   --keep             keep the downloaded files and print where they are.

set -euo pipefail

TAG=current
DOMAIN=play.skiprocking.ca
REPO=Ajw2003/rockskipping-cert
ALLOW_STAGING=0
MIN_DAYS=1
KEEP=0

while [ $# -gt 0 ]; do
  case "$1" in
    --tag) TAG="$2"; shift 2;;
    --domain) DOMAIN="$2"; shift 2;;
    --repo) REPO="$2"; shift 2;;
    --allow-staging) ALLOW_STAGING=1; shift;;
    --min-days) MIN_DAYS="$2"; shift 2;;
    --keep) KEEP=1; shift;;
    -h|--help) sed -n '2,30p' "$0" | sed 's/^# \{0,1\}//'; exit 0;;
    *) echo "unknown option: $1" >&2; exit 2;;
  esac
done

BASE="https://github.com/$REPO/releases/download/$TAG"
WORK=$(mktemp -d)
if [ "$KEEP" -eq 0 ]; then trap 'rm -rf "$WORK"' EXIT; fi

fail=0
step() { printf '\n=== %s\n' "$1"; }
pass() { printf '   [pass]  %s\n' "$1"; }
bad()  { printf '   [FAIL]  %s\n' "$1"; fail=1; }
info() { printf '           %s\n' "$1"; }

step "Downloading from the public URL ($TAG)"
info "$BASE/server.pem"
# --fail so an HTTP 404 is an error rather than an HTML error page written to server.pem,
# which would otherwise fail later as a confusing parse error instead of "not published".
if ! curl -fsSL "$BASE/server.pem" -o "$WORK/server.pem"; then
  bad "server.pem could not be downloaded. The release asset does not exist, or the tag is wrong."
  info "Nothing has been published under tag '$TAG' in $REPO, or the repo is private."
  exit 1
fi
if ! curl -fsSL "$BASE/server.key" -o "$WORK/server.key"; then
  bad "server.key could not be downloaded, though server.pem was."
  info "A half-published release is worse than none: the game downloads both or neither."
  exit 1
fi
pass "both assets downloaded ($(wc -c < "$WORK/server.pem") and $(wc -c < "$WORK/server.key") bytes)"

step "Certificate parses"
if ! openssl x509 -in "$WORK/server.pem" -noout >/dev/null 2>&1; then
  bad "server.pem is not a PEM certificate."
  info "First line: $(head -1 "$WORK/server.pem")"
  exit 1
fi
pass "parses as PEM"

SUBJECT=$(openssl x509 -in "$WORK/server.pem" -noout -subject)
ISSUER=$(openssl x509 -in "$WORK/server.pem" -noout -issuer)
NOT_AFTER=$(openssl x509 -in "$WORK/server.pem" -noout -enddate | cut -d= -f2)
info "$SUBJECT"
info "$ISSUER"

step "Subject Alternative Name covers $DOMAIN"
# CertificateProvider.CoversWildcard reads SAN (OID 2.5.29.17) specifically and ignores the
# subject CN entirely, so a certificate whose CN is right and whose SAN is wrong passes a
# casual eyeball and fails in the game.
SAN=$(openssl x509 -in "$WORK/server.pem" -noout -ext subjectAltName 2>/dev/null | tail -n +2 | tr -d ' ' || true)
if [ -z "$SAN" ]; then
  bad "no Subject Alternative Name extension at all. CertificateProvider requires one."
else
  info "SAN: $SAN"
  if echo "$SAN" | tr ',' '\n' | grep -qx "DNS:$DOMAIN"; then
    pass "SAN contains DNS:$DOMAIN"
  else
    bad "SAN does not cover $DOMAIN. Every phone would show a certificate warning."
  fi
fi

step "Issuer is a real CA"
if echo "$ISSUER" | grep -qi 'staging\|pretend\|bogus'; then
  if [ "$ALLOW_STAGING" -eq 1 ]; then
    pass "STAGING issuer, accepted because --allow-staging was passed"
    info "This certificate is trusted by no device. Rehearsal only."
  else
    bad "issued by Let's Encrypt STAGING. No device trusts it."
    info "Re-run renew-cert.yml with environment=production."
  fi
else
  pass "not a staging issuer"
fi

step "Validity window"
NOW=$(date -u +%s)
END=$(date -u -d "$NOT_AFTER" +%s 2>/dev/null || date -u -j -f '%b %e %T %Y %Z' "$NOT_AFTER" +%s)
DAYS=$(( (END - NOW) / 86400 ))
info "expires $NOT_AFTER"
if [ "$DAYS" -lt 0 ]; then
  bad "already expired ($((-DAYS)) days ago)"
elif [ "$DAYS" -lt "$MIN_DAYS" ]; then
  bad "$DAYS days remaining, under the --min-days $MIN_DAYS threshold"
else
  pass "$DAYS days remaining"
fi
# CertificateProvider renews within 14 days of expiry, so anything under that will trigger a
# download on the next launch anyway - worth saying out loud rather than only failing hard.
if [ "$DAYS" -ge 0 ] && [ "$DAYS" -le 14 ]; then
  info "note: under CertificateProvider.RenewWithinDays (14), so clients will try to re-download."
fi

step "Private key matches the certificate"
# Inspect() never opens the key - it only checks the file exists - so a mismatched pair
# passes every check on the Unity side and fails inside Node when the relay starts TLS,
# which surfaces as the relay dying at launch rather than as a certificate problem.
CERT_PUB=$(openssl x509 -in "$WORK/server.pem" -noout -pubkey 2>/dev/null || true)
KEY_PUB=$(openssl pkey -in "$WORK/server.key" -pubout 2>/dev/null || true)
if [ -z "$KEY_PUB" ]; then
  bad "server.key is not a readable private key."
elif [ "$CERT_PUB" = "$KEY_PUB" ]; then
  pass "key and certificate share a public key"
else
  bad "server.key does NOT match server.pem. The relay would fail to start TLS."
fi

step "Chain shape"
# --fullchain-file should give leaf + intermediate(s). A bare leaf still validates on
# desktops that cache intermediates and fails on a phone that does not, which is the worst
# possible way to find out.
COUNT=$(grep -c 'BEGIN CERTIFICATE' "$WORK/server.pem" || true)
info "$COUNT certificate(s) in server.pem"
if [ "$COUNT" -ge 2 ]; then
  pass "fullchain (leaf + intermediate)"
else
  bad "only $COUNT certificate — this is a bare leaf, not a fullchain."
  info "Phones do not cache intermediates the way desktops do; expect intermittent failures."
fi

if [ "$KEEP" -eq 1 ]; then printf '\nkept: %s\n' "$WORK"; fi

printf '\n'
if [ "$fail" -eq 0 ]; then
  printf 'PASS — the published certificate at %s is usable.\n' "$BASE"
  exit 0
fi
printf 'FAIL — the published certificate is not usable. See the [FAIL] lines above.\n'
exit 1

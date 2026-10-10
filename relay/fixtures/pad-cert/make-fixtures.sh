#!/bin/sh
# Regenerates the committed test fixtures for test-pad-cert.js. Needs openssl; the tests
# themselves do not (so they run on Windows). CA private keys are thrown away on purpose.
# In Git Bash on Windows, run it as:
#   TMPDIR=$(cygpath -m "$LOCALAPPDATA/Temp") MSYS2_ARG_CONV_EXCL="/CN=" sh make-fixtures.sh
set -eu
here=$(cd "$(dirname "$0")" && pwd)
work=$(mktemp -d)
trap 'rm -rf "$work"' EXIT
DAYS=36500
CRL_URL=http://crl.test/ca.crl
export LEAF_NAME=unset # openssl req reads every config section, so the variable must exist

ec_key() { openssl genpkey -algorithm EC -pkeyopt ec_paramgen_curve:P-256 -out "$1" 2>/dev/null; }
rsa_key() { openssl genpkey -algorithm RSA -pkeyopt rsa_keygen_bits:2048 -out "$1" 2>/dev/null; }

# make_ca <dir> <ec|rsa>: a CA whose database lives in $work/<dir>
make_ca() {
  d=$work/$1
  mkdir -p "$d"
  : > "$d/index.txt"
  echo 1000 > "$d/serial"
  echo 01 > "$d/crlnumber"
  cat > "$d/ca.cnf" <<EOF
[ca]
default_ca = CA_default
[CA_default]
database = $d/index.txt
new_certs_dir = $d
serial = $d/serial
crlnumber = $d/crlnumber
default_md = sha256
policy = policy_any
unique_subject = no
private_key = $d/key.pem
certificate = $d/cert.pem
[policy_any]
commonName = supplied
[req]
distinguished_name = dn
prompt = no
[dn]
CN = Test CA
[v3_ca]
basicConstraints = critical,CA:true
keyUsage = critical,keyCertSign,cRLSign
subjectKeyIdentifier = hash
[v3_leaf]
basicConstraints = CA:false
subjectAltName = DNS:\$ENV::LEAF_NAME
crlDistributionPoints = URI:$CRL_URL
EOF
  if [ "$2" = rsa ]; then rsa_key "$d/key.pem"; else ec_key "$d/key.pem"; fi
  openssl req -new -x509 -key "$d/key.pem" -config "$d/ca.cnf" -extensions v3_ca \
    -days $DAYS -sha256 -out "$d/cert.pem"
}

# issue <ca dir> <leaf key> <name> <out pem> [extra openssl ca args]
issue() {
  ca=$1; key=$2; name=$3; out=$4; shift 4
  openssl req -new -key "$key" -subj "/CN=$name" -out "$work/leaf.csr"
  LEAF_NAME=$name openssl ca -batch -config "$work/$ca/ca.cnf" -extensions v3_leaf \
    -days $DAYS -in "$work/leaf.csr" -out "$out" -notext "$@" 2>/dev/null
}

make_ca ca ec
make_ca other ec
make_ca rsa rsa
ec_key "$work/leaf-key.pem"
ec_key "$work/key-other.pem"
rsa_key "$work/rsa-key.pem"

issue ca "$work/leaf-key.pem" pad.skiprocking.ca "$work/leaf.pem"
issue ca "$work/leaf-key.pem" pad.skiprocking.ca "$work/leaf-revoked.pem"
issue ca "$work/leaf-key.pem" pad.skiprocking.ca "$work/leaf-expired.pem" \
  -startdate 20200101000000Z -enddate 20200201000000Z
issue ca "$work/leaf-key.pem" wrong.example.com "$work/leaf-wrongname.pem"
issue rsa "$work/rsa-key.pem" pad.skiprocking.ca "$work/rsa-leaf.pem"
issue rsa "$work/rsa-key.pem" pad.skiprocking.ca "$work/rsa-leaf-revoked.pem"

# An empty CRL first, then revoke and publish the real one.
openssl ca -config "$work/ca/ca.cnf" -gencrl -crldays $DAYS -out "$work/crl-empty.pem" 2>/dev/null
openssl ca -config "$work/ca/ca.cnf" -revoke "$work/leaf-revoked.pem" 2>/dev/null
openssl ca -config "$work/ca/ca.cnf" -gencrl -crldays $DAYS -out "$work/crl-revoked.pem" 2>/dev/null
openssl crl -in "$work/crl-revoked.pem" -outform DER -out "$work/crl-revoked.der"

# A forged CRL: the other CA "revokes" the good leaf's serial, but cannot sign as ca.
serial=$(openssl x509 -in "$work/leaf.pem" -noout -serial | sed 's/^serial=//')
echo "$serial" > "$work/other/serial"
issue other "$work/leaf-key.pem" pad.skiprocking.ca "$work/forged.pem"
openssl ca -config "$work/other/ca.cnf" -revoke "$work/forged.pem" 2>/dev/null
openssl ca -config "$work/other/ca.cnf" -gencrl -crldays $DAYS -out "$work/crl-other-ca.pem" 2>/dev/null

openssl ca -config "$work/rsa/ca.cnf" -revoke "$work/rsa-leaf-revoked.pem" 2>/dev/null
openssl ca -config "$work/rsa/ca.cnf" -gencrl -crldays $DAYS -out "$work/rsa-crl.pem" 2>/dev/null
openssl crl -in "$work/rsa-crl.pem" -outform DER -out "$work/rsa-crl.der"

cp "$work/ca/cert.pem" "$here/ca.pem"
cp "$work/other/cert.pem" "$here/other-ca.pem"
cp "$work/rsa/cert.pem" "$here/rsa-ca.pem"
for f in leaf leaf-revoked leaf-expired leaf-wrongname rsa-leaf rsa-leaf-revoked \
  leaf-key key-other rsa-key crl-empty crl-other-ca; do
  cp "$work/$f.pem" "$here/$f.pem"
done
cp "$work/crl-revoked.der" "$here/crl-revoked.der"
cp "$work/rsa-crl.der" "$here/rsa-crl.der"
echo "Fixtures written to $here"

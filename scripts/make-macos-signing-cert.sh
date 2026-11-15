#!/usr/bin/env bash
# Creates the self-signed code-signing certificate the Release workflow uses to sign GitDash.app on macOS, and (optionally)
# stores it as repository secrets.
#
# Why: with the ad-hoc signature every release looks like a different app to macOS, so the Keychain asks for your password
# after each update. Signing every release with the SAME certificate gives the app a stable identity, so "Always Allow"
# sticks. Keep the generated .p12 and its password safe: a new certificate means one more prompt after the next update.
#
# Usage:  scripts/make-macos-signing-cert.sh            # creates the files and prints what to do next
#         scripts/make-macos-signing-cert.sh --upload   # also sets the secrets with the GitHub CLI (gh auth login first)
set -euo pipefail

name="GitDash Self-Signed"
dir="${GITDASH_CERT_DIR:-$HOME/.gitdash-signing}"
mkdir -p "$dir"
chmod 700 "$dir"

if [ -f "$dir/gitdash-signing.p12" ]; then
  echo "A certificate already exists in $dir; keeping it (delete the folder to make a new one)."
else
  password="$(openssl rand -base64 24 | tr -d '/+=' | cut -c1-24)"
  cat > "$dir/openssl.cnf" <<CNF
[req]
distinguished_name = dn
x509_extensions = ext
prompt = no
[dn]
CN = $name
O = Solitontech
[ext]
basicConstraints = critical,CA:false
keyUsage = critical,digitalSignature
extendedKeyUsage = critical,codeSigning
CNF
  openssl req -x509 -newkey rsa:2048 -nodes -days 3650 -sha256 \
    -keyout "$dir/key.pem" -out "$dir/cert.pem" -config "$dir/openssl.cnf"
  # OpenSSL 3 needs -legacy so that the macOS `security import` can read the file; LibreSSL (the system one) has no such flag.
  openssl pkcs12 -export -legacy -inkey "$dir/key.pem" -in "$dir/cert.pem" -name "$name" \
    -out "$dir/gitdash-signing.p12" -passout "pass:$password" 2>/dev/null \
    || openssl pkcs12 -export -inkey "$dir/key.pem" -in "$dir/cert.pem" -name "$name" \
         -out "$dir/gitdash-signing.p12" -passout "pass:$password"
  printf '%s' "$password" > "$dir/password.txt"
  rm -f "$dir/key.pem" "$dir/openssl.cnf"
  chmod 600 "$dir"/*
  echo "Created $dir/gitdash-signing.p12 (valid for 10 years)."
fi

base64 < "$dir/gitdash-signing.p12" | tr -d '\n' > "$dir/gitdash-signing.p12.base64"

if [ "${1:-}" = "--upload" ]; then
  gh secret set MACOS_CERT_P12 < "$dir/gitdash-signing.p12.base64"
  gh secret set MACOS_CERT_PASSWORD < "$dir/password.txt"
  echo "Repository secrets MACOS_CERT_P12 and MACOS_CERT_PASSWORD are set. The next release is signed with this certificate."
else
  cat <<MSG

Next: add two repository secrets (Settings > Secrets and variables > Actions), or run this script again with --upload:
  MACOS_CERT_P12       = contents of $dir/gitdash-signing.p12.base64
  MACOS_CERT_PASSWORD  = contents of $dir/password.txt
Back the folder up somewhere private; do not commit it.
MSG
fi

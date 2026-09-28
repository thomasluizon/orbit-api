#!/usr/bin/env bash
set -euo pipefail

if [[ $# -lt 1 || $# -gt 4 ]]; then
  printf 'Usage: %s <cloudflare-nameserver> [apex-target] [app-target] [www-target]\n' "$0" >&2
  exit 2
fi

cloudflare_nameserver=$1
apex_target=${2:-orbit-landing-aaa7.onrender.com}
app_target=${3:-orbit-web-3qmv.onrender.com}
www_target=${4:-orbit-landing-aaa7.onrender.com}
status=0

normalize_answers() {
  sed -E 's/"[[:space:]]+"//g; s/^"//; s/"$//' | sort
}

render_addresses=$(dig "$apex_target" A +noall +answer +time=2 +tries=1 | awk '$4 == "A" {print $5}' | sort)
cloudflare_addresses=$(dig "@$cloudflare_nameserver" useorbit.org A +noall +answer +time=2 +tries=1 | awk '$4 == "A" {print $5}' | sort)

if [[ -z "$render_addresses" || "$render_addresses" != "$cloudflare_addresses" ]]; then
  printf 'Mismatch: useorbit.org flattened A\nRender target: %s\nCloudflare: %s\n' "$render_addresses" "$cloudflare_addresses" >&2
  status=1
fi

for record in "app.useorbit.org:$app_target" "www.useorbit.org:$www_target"; do
  name=${record%%:*}
  expected_target=${record#*:}
  cloudflare_target=$(dig "@$cloudflare_nameserver" "$name" CNAME +short +time=2 +tries=1)

  if [[ "$cloudflare_target" != "${expected_target%.}." ]]; then
    printf 'Mismatch: %s CNAME\nExpected: %s\nCloudflare: %s\n' "$name" "$expected_target" "$cloudflare_target" >&2
    status=1
  fi
done

while read -r name type; do
  spaceship_answers=$(dig @launch1.spaceship.net "$name" "$type" +short +time=2 +tries=1 | normalize_answers)
  cloudflare_answers=$(dig "@$cloudflare_nameserver" "$name" "$type" +short +time=2 +tries=1 | normalize_answers)

  if [[ "$spaceship_answers" != "$cloudflare_answers" ]]; then
    printf 'Mismatch: %s %s\nSpaceship: %s\nCloudflare: %s\n' "$name" "$type" "$spaceship_answers" "$cloudflare_answers" >&2
    status=1
  fi
done <<'RECORDS'
api.useorbit.org CNAME
useorbit.org MX
send.send.useorbit.org MX
send.updates.useorbit.org MX
_dmarc.useorbit.org TXT
useorbit.org TXT
google._domainkey.useorbit.org TXT
resend._domainkey.send.useorbit.org TXT
resend._domainkey.updates.useorbit.org TXT
send.send.useorbit.org TXT
send.updates.useorbit.org TXT
RECORDS

if [[ $status -eq 0 ]]; then
  printf 'The landing and web records match Render and the remaining DNS answers match the Spaceship zone.\n'
fi

exit "$status"

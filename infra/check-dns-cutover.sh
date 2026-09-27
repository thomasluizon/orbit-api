#!/usr/bin/env bash
set -euo pipefail

if [[ $# -ne 1 ]]; then
  printf 'Usage: %s <cloudflare-nameserver>\n' "$0" >&2
  exit 2
fi

cloudflare_nameserver=$1
status=0

normalize_answers() {
  sed -E 's/"[[:space:]]+"//g; s/^"//; s/"$//' | sort
}

while read -r name type; do
  spaceship_answers=$(dig @launch1.spaceship.net "$name" "$type" +short +time=2 +tries=1 | normalize_answers)
  cloudflare_answers=$(dig "@$cloudflare_nameserver" "$name" "$type" +short +time=2 +tries=1 | normalize_answers)

  if [[ "$spaceship_answers" != "$cloudflare_answers" ]]; then
    printf 'Mismatch: %s %s\nSpaceship: %s\nCloudflare: %s\n' "$name" "$type" "$spaceship_answers" "$cloudflare_answers" >&2
    status=1
  fi
done <<'RECORDS'
useorbit.org A
api.useorbit.org CNAME
app.useorbit.org CNAME
www.useorbit.org CNAME
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
  printf 'All DNS answers match the Spaceship zone.\n'
fi

exit "$status"

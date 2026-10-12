#!/usr/bin/env bash
set -euo pipefail

action="$1"
: "${RENDER_API_KEY:?}"
: "${STAGING_POSTGRES_ID:?}"
database_url="https://api.render.com/v1/postgres/${STAGING_POSTGRES_ID}"

read_allow_list() {
  curl --fail-with-body --silent --show-error --max-time 30 \
    --header "Authorization: Bearer ${RENDER_API_KEY}" "$database_url" \
    | jq -ce --arg id "$STAGING_POSTGRES_ID" \
      'select(.id == $id) | .ipAllowList | select(type == "array") | sort_by(.cidrBlock, .description)'
}

remove_temporary_access() {
  local current desired
  current="$(read_allow_list)"
  desired="$(jq -ce 'map(select(.description != "github-actions-staging-reseed"))' <<< "$current")"
  if [[ "$current" != "$desired" ]]; then
    set_allow_list "$desired"
  fi
  echo "Kept staging database operator entries: $desired"
}

set_allow_list() {
  local desired="$1"
  local payload attempt
  payload="$(jq -cn --argjson addresses "$desired" '{ipAllowList: $addresses}')"
  curl --fail-with-body --silent --show-error --max-time 30 \
    --request PATCH \
    --header "Authorization: Bearer ${RENDER_API_KEY}" \
    --header 'Content-Type: application/json' \
    --data "$payload" "$database_url" > /dev/null
  for ((attempt=1; attempt<=12; attempt++)); do
    if [[ "$(read_allow_list)" == "$desired" ]]; then
      echo "Staging database allow list matches the requested addresses"
      return 0
    fi
    sleep 5
  done
  echo 'Staging database allow list did not converge' >&2
  return 1
}

read_current_ipv4() {
  local address
  address="$(curl --fail --silent --show-error --max-time 15 -4 https://api.ipify.org)" || return 1
  python3 -c 'import ipaddress, sys; ipaddress.IPv4Address(sys.argv[1])' "$address" || return 1
  echo "$address"
}

case "$action" in
  open)
    current="$(read_allow_list)"
    operator_list="$(jq -ce 'map(select(.description != "github-actions-staging-reseed"))' <<< "$current")"
    echo "Kept staging database operator entries: $operator_list"
    runner_ip="$(read_current_ipv4)"
    runner_cidr="${runner_ip}/32"
    temporary_list="$(jq -cn --argjson operators "$operator_list" --arg cidr "$runner_cidr" '
      $operators + (if any($operators[]; .cidrBlock == $cidr) then []
        else [{cidrBlock: $cidr, description: "github-actions-staging-reseed"}] end)
      | sort_by(.cidrBlock, .description)')"
    if [[ "$temporary_list" != "$operator_list" ]]; then
      : "${STAGING_ACCESS_MARKER:?}"
      touch "$STAGING_ACCESS_MARKER"
    fi
    if [[ "$temporary_list" != "$current" ]]; then
      set_allow_list "$temporary_list"
    fi
    echo "Temporary runner access: $runner_cidr"
    ;;
  restore)
    : "${STAGING_ACCESS_MARKER:?}"
    remove_temporary_access
    rm -f "$STAGING_ACCESS_MARKER"
    echo "Restored staging database allow list: $(read_allow_list)"
    ;;
  reconcile)
    remove_temporary_access
    echo "Reconciled staging database allow list: $(read_allow_list)"
    ;;
  operator-mac)
    operator_ip="$(read_current_ipv4)"
    current="$(read_allow_list)"
    desired="$(jq -ce --arg cidr "${operator_ip}/32" '
      map(select(.description != "operator mac")) + [{cidrBlock: $cidr, description: "operator mac"}]
      | sort_by(.cidrBlock, .description)' <<< "$current")"
    if [[ "$desired" != "$current" ]]; then
      set_allow_list "$desired"
    fi
    echo "Staging database operator mac access: ${operator_ip}/32"
    ;;
  *)
    echo "Unsupported allow list action: $action" >&2
    exit 1
    ;;
esac

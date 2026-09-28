#!/usr/bin/env bash
set -euo pipefail

action="$1"
: "${RENDER_API_KEY:?}"
: "${STAGING_POSTGRES_ID:?}"
: "${TF_VAR_staging_postgres_ip_allow_list:?}"

operator_list="$(jq -ce '
  select(type == "array" and all(.[];
    (.cidr_block | type == "string" and length > 0) and
    (.description | type == "string" and . != "github-actions-staging-reseed")))
  | map({cidrBlock: .cidr_block, description}) | sort_by(.cidrBlock, .description)
' <<< "$TF_VAR_staging_postgres_ip_allow_list")"
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
  if [[ "$desired" != "$operator_list" ]]; then
    echo 'Staging database operator allow list differs from the configured addresses' >&2
    return 1
  fi
}

set_allow_list() {
  local desired="$1"
  local payload
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

case "$action" in
  open)
    current="$(read_allow_list)"
    if [[ "$current" != "$operator_list" ]]; then
      echo 'Staging database allow list differs from the operator addresses' >&2
      exit 1
    fi
    runner_ip="$(curl --fail --silent --show-error --max-time 15 -4 https://api.ipify.org)"
    python3 -c 'import ipaddress, sys; ipaddress.IPv4Address(sys.argv[1])' "$runner_ip"
    runner_cidr="${runner_ip}/32"
    temporary_list="$(jq -cn --argjson operators "$operator_list" --arg cidr "$runner_cidr" '
      $operators + (if any($operators[]; .cidrBlock == $cidr) then []
        else [{cidrBlock: $cidr, description: "github-actions-staging-reseed"}] end)
      | sort_by(.cidrBlock, .description)')"
    if [[ "$temporary_list" != "$operator_list" ]]; then
      : "${STAGING_ACCESS_MARKER:?}"
      touch "$STAGING_ACCESS_MARKER"
      set_allow_list "$temporary_list"
    fi
    echo "Temporary runner access: $runner_cidr"
    ;;
  restore)
    : "${STAGING_ACCESS_MARKER:?}"
    if [[ -f "$STAGING_ACCESS_MARKER" ]]; then
      remove_temporary_access
      rm "$STAGING_ACCESS_MARKER"
      echo "Restored staging database allow list: $(read_allow_list)"
    else
      echo 'No temporary runner access was opened'
    fi
    ;;
  reconcile)
    remove_temporary_access
    echo "Reconciled staging database allow list: $(read_allow_list)"
    ;;
  *)
    echo "Unsupported allow list action: $action" >&2
    exit 1
    ;;
esac

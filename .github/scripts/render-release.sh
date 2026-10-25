#!/usr/bin/env bash

render_request() (
  set -euo pipefail
  local method=$1 url=$2 response_file http_status attempt
  shift 2
  response_file=$(mktemp)
  trap 'rm -f "$response_file"' EXIT
  for attempt in {1..4}; do
    # https://curl.se/docs/manpage.html#--retry-all-errors requires omitting --fail to avoid retrying permanent HTTP errors.
    http_status=$(curl --silent --show-error --retry 3 --retry-all-errors \
      --retry-delay 2 --retry-max-time 60 --connect-timeout 10 --max-time 30 \
      --output "$response_file" --write-out '%{http_code}' \
      --request "$method" "$url" \
      --header "Authorization: Bearer $RENDER_API_KEY" "$@") || return "$?"
    case "$http_status" in
      2??) cat "$response_file"; return 0 ;;
      5??)
        case "$http_status" in
          500|502|503|504) break ;;
        esac
        if [ "$attempt" -lt 4 ]; then
          sleep 2
          continue
        fi ;;
    esac
    break
  done
  echo "::error::Render $method $url answered HTTP $http_status" >&2
  return 22
)

render_find_deploy() {
  local step_started=$1 response match count cursor='' page
  local -a query
  for page in {1..100}; do
    query=(--get --data-urlencode 'limit=100')
    if [ -n "$cursor" ]; then query+=(--data-urlencode "cursor=$cursor"); fi
    response=$(render_request GET "https://api.render.com/v1/services/$RENDER_SERVICE_ID/deploys" "${query[@]}") || return "$?"
    match=$(jq -r --arg sha "$COMMIT_SHA" --argjson started "$step_started" '
      def created_epoch:
        (capture("^(?<seconds>[^.]+)(?:\\.(?<fraction>[0-9]+))?Z$") // error("Invalid Render deploy creation time"))
        | (.seconds + "Z" | fromdateiso8601) + (("0." + (.fraction // "0")) | tonumber);
      if type != "array" then error("Expected a Render deploy list") else . end
      | [.[] | .deploy
        | if (.id | type) != "string" or (.id | length) == 0
            or (.commit.id | type) != "string" or (.createdAt | type) != "string"
          then error("Missing Render deploy identity, commit or creation time") else . end
        | (.createdAt | created_epoch) as $created
        | select(.commit.id == $sha and $created > $started) | .id]
      | if length > 1 then error("Multiple Render deploys match this release") else .[0] // empty end
    ' <<< "$response") || return "$?"
    if [ -n "$match" ]; then
      printf '%s\n' "$match"
      return 0
    fi
    count=$(jq -er 'length' <<< "$response") || return "$?"
    if [ "$count" -lt 100 ]; then return 0; fi
    cursor=$(jq -er '.[-1].cursor | select(type == "string" and length > 0)' <<< "$response") || return "$?"
  done
  echo "::error::Render deploy reconciliation exceeded its limit of $page pages" >&2
  return 1
}

render_start_deploy() {
  local payload=$1 response_file=$2 step_started=$3 http_status curl_status deploy_id attempt
  for attempt in {1..4}; do
    curl_status=0
    http_status=$(curl --fail-with-body --silent --show-error --connect-timeout 10 --max-time 30 \
      --output "$response_file" --write-out '%{http_code}' \
      --request POST "https://api.render.com/v1/services/$RENDER_SERVICE_ID/deploys" \
      --header "Authorization: Bearer $RENDER_API_KEY" \
      --header 'Content-Type: application/json' --data "$payload") || curl_status=$?
    if [ "$curl_status" -eq 0 ]; then
      case "$http_status" in
        201|202) printf '%s\n' "$http_status"; return 0 ;;
      esac
    fi
    case "$http_status" in
      408|429|5??|000) ;;
      2??) if [ "$curl_status" -eq 0 ]; then return 22; fi ;;
      *) echo "::error::Render deploy request answered HTTP $http_status" >&2; return 22 ;;
    esac
    echo "::warning::Render deploy request was interrupted or answered HTTP $http_status; checking recent deploys before retrying" >&2
    deploy_id=$(render_find_deploy "$step_started") || return "$?"
    if [ -n "$deploy_id" ]; then
      jq -cn --arg id "$deploy_id" '{id: $id}' > "$response_file"
      printf '201\n'
      return 0
    fi
    if [ "$attempt" -lt 4 ]; then sleep 2; fi
  done
  echo '::error::Render deploy request exhausted its retry limit' >&2
  return 1
}

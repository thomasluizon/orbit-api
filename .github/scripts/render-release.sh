#!/usr/bin/env bash

render_request() (
  set -euo pipefail
  local method=$1 url=$2 response_file http_status attempt
  shift 2
  response_file=$(mktemp)
  trap 'rm -f "$response_file"' EXIT
  for attempt in {1..4}; do
    # https://curl.se/docs/manpage.html#--retry-all-errors requires omitting --fail to avoid retrying permanent HTTP errors.
    # https://github.com/curl/curl/blob/curl-8_7_1/src/tool_operate.c bounds Retry-After by --retry-max-time.
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

render_list_deploys() (
  set -euo pipefail
  local response count cursor='' entries_file
  local -a query
  entries_file=$(mktemp)
  trap 'rm -f "$entries_file"' EXIT
  while true; do
    query=(--get --data-urlencode 'limit=100')
    if [ -n "$cursor" ]; then query+=(--data-urlencode "cursor=$cursor"); fi
    response=$(render_request GET "https://api.render.com/v1/services/$RENDER_SERVICE_ID/deploys" "${query[@]}") || return "$?"
    response=$(jq -ce '
      if type != "array" then error("Expected a Render deploy list") else . end
      | map(if (.deploy.id | type) != "string" or (.deploy.id | length) == 0
          then error("Missing Render deploy identity") else . end)
    ' <<< "$response") || return "$?"
    count=$(jq -er 'length' <<< "$response") || return "$?"
    if [ "$count" -lt 100 ]; then
      printf '%s\n' "$response" >> "$entries_file"
      jq -cs 'add' < "$entries_file"
      return "$?"
    fi
    cursor=$(jq -er '.[-1].cursor | select(type == "string" and length > 0)' <<< "$response") || return "$?"
    jq -s --arg cursor "$cursor" '
      if any(.[]; .[-1].cursor == $cursor)
      then error("Render deploy listing repeated its cursor") else empty end
    ' < "$entries_file" || return "$?"
    printf '%s\n' "$response" >> "$entries_file"
  done
)

render_find_deploy() (
  set -euo pipefail
  local step_started=$1 existing_ids_file=$2 response_file
  response_file=$(mktemp)
  trap 'rm -f "$response_file"' EXIT
  render_list_deploys > "$response_file" || return "$?"
  jq -r --arg sha "$COMMIT_SHA" --argjson started "$step_started" --slurpfile existing "$existing_ids_file" '
      def created_epoch:
        (capture("^(?<seconds>[^.]+)(?:\\.(?<fraction>[0-9]+))?Z$") // error("Invalid Render deploy creation time"))
        | (.seconds + "Z" | fromdateiso8601) + (("0." + (.fraction // "0")) | tonumber);
      if type != "array" then error("Expected a Render deploy list") else . end
      | [.[] | .deploy
        | if (.id | type) != "string" or (.id | length) == 0
            or (.commit.id | type) != "string" or (.createdAt | type) != "string"
          then error("Missing Render deploy identity, commit or creation time") else . end
        | (.createdAt | created_epoch) as $created
        | select(.commit.id == $sha and $created > $started)
        | .id | select(. as $id | $existing[0] | index($id) | not)] | unique
      | if length > 1 then error("Multiple Render deploys match this release") else .[0] // empty end
    ' < "$response_file"
)

render_post_retry_delay() {
  jq -Rrs '
    [splits("\\r?\\n") | select(test("^retry-after:"; "i"))
      | sub("^[^:]*:[ \\t]*"; "") | sub("[ \\t]+$"; "")] | last // ""
    | if test("^[0-9]+$") then tonumber else 2 end
    | if . > 60 then error("Render Retry-After exceeds the 60-second retry wait limit; stopping without retrying early") else . end
  ' < "$1"
}

render_start_deploy() (
  set -euo pipefail
  local deploy_payload=$1 response_file=$2 step_started=$3 http_status curl_status deploy_id attempt existing_ids_file headers_file delay directory
  directory=$(mktemp -d)
  trap 'rm -rf "$directory"' EXIT
  existing_ids_file="$directory/existing-ids.json"
  headers_file="$directory/headers"
  render_list_deploys | jq -ce '[.[].deploy.id]' > "$existing_ids_file" || return "$?"
  for attempt in {1..4}; do
    curl_status=0
    http_status=$(curl --fail-with-body --silent --show-error --connect-timeout 10 --max-time 30 \
      --output "$response_file" --dump-header "$headers_file" --write-out '%{http_code}' \
      --request POST "https://api.render.com/v1/services/$RENDER_SERVICE_ID/deploys" \
      --header "Authorization: Bearer $RENDER_API_KEY" \
      --header 'Content-Type: application/json' --data "$deploy_payload") || curl_status=$?
    if [ "$curl_status" -eq 0 ]; then
      case "$http_status" in
        201|202) printf '%s\n' "$http_status"; return 0 ;;
      esac
    fi
    case "$http_status:$curl_status" in
      429:*|000:6|000:7)
        if [ "$attempt" -eq 4 ]; then
          echo "::error::Render deploy of $COMMIT_SHA exhausted four attempts without acceptance" >&2
          return 1
        fi
        delay=2
        if [ "$http_status" = 429 ]; then delay=$(render_post_retry_delay "$headers_file") || return "$?"; fi
        sleep "$delay"
        continue ;;
      5??:*|000:*) ;;
      2??:*) if [ "$curl_status" -eq 0 ]; then return 22; fi ;;
      *) echo "::error::Render deploy request answered HTTP $http_status" >&2; return 22 ;;
    esac
    break
  done
  # https://render.com/docs/deploys#deploy-pipeline documents overlapping deploy cancellation; a negative snapshot cannot authorize a second POST.
  echo "::warning::Render deploy outcome is ambiguous for $COMMIT_SHA (HTTP $http_status, curl $curl_status); reconciling without another POST" >&2
  for attempt in {1..4}; do
    deploy_id=$(render_find_deploy "$step_started" "$existing_ids_file") || return "$?"
    if [ -n "$deploy_id" ]; then
      jq -cn --arg id "$deploy_id" '{id: $id}' > "$response_file"
      printf '201\n'
      return 0
    fi
    if [ "$attempt" -lt 4 ]; then sleep 2; fi
  done
  echo "::error::Cannot recover the Render deploy identity for commit $COMMIT_SHA; check service $RENDER_SERVICE_ID deploys before dispatching again" >&2
  return 1
)

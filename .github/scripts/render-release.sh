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

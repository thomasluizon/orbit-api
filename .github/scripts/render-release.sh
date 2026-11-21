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

verify_image_source() (
  set -euo pipefail
  if [ "$(git -C release-source rev-parse HEAD)" != "$COMMIT_SHA" ]; then
    echo '::error::Image checkout does not match the selected release SHA'
    exit 1
  fi
  if ! git -C release-source merge-base --is-ancestor "$COMMIT_SHA" "refs/remotes/origin/$RELEASE_BRANCH"; then
    echo "::error::The selected release SHA is not reachable from $RELEASE_BRANCH"
    exit 1
  fi
)

render_deploy() (
  set -euo pipefail
  service_name=$1
  image_url="ghcr.io/thomasluizon/orbit-api@$IMAGE_DIGEST"
  payload=$(jq -cn --arg image "$image_url" '{imageUrl: $image}')
  response_file=$(mktemp)
  trap 'rm -f "$response_file"' EXIT
  if ! http_status=$(curl --silent --show-error --connect-timeout 10 --max-time 30 \
    --output "$response_file" --write-out '%{http_code}' \
    --request POST "https://api.render.com/v1/services/$RENDER_SERVICE_ID/deploys" \
    --header "Authorization: Bearer $RENDER_API_KEY" \
    --header 'Content-Type: application/json' --data "$payload"); then
    echo "::error::Render deploy acceptance is unknown for $image_url; check service deploys before rerunning"
    exit 1
  fi
  case "$http_status" in
    201) ;;
    202)
      echo '::error::Render queued the deploy without a deploy ID; rerun once the service is idle'
      exit 1 ;;
    400)
      echo "::error::Render rejected imageUrl. If $service_name is not image-backed yet, the coordinator must switch its source to the published GHCR image through the Render API, refresh Terraform state, then rerun"
      exit 1 ;;
    *)
      echo "::error::Render answered HTTP $http_status; check service deploys before rerunning"
      exit 1 ;;
  esac
  if ! deploy_id=$(jq -er --arg image "$image_url" \
    'select(.image.ref == $image) | .id | select(type == "string" and length > 0)' < "$response_file"); then
    echo '::error::Render HTTP 201 did not identify a deploy of the requested image; check service deploys before rerunning'
    exit 1
  fi
  log_url="https://dashboard.render.com/web/$RENDER_SERVICE_ID/deploys/$deploy_id"
  echo "log_url=$log_url" >> "$GITHUB_OUTPUT"
  echo "Render deploy: $log_url"

  for attempt in {1..60}; do
    response=$(render_request GET "https://api.render.com/v1/services/$RENDER_SERVICE_ID/deploys/$deploy_id")
    status=$(jq -er '.status' <<< "$response")
    echo "Render deploy status: $status (poll $attempt)"
    case "$status" in
      live) exit 0 ;;
      build_failed|pre_deploy_failed|update_failed|canceled|deactivated)
        echo "::error::Render deploy $deploy_id ended with $status. Logs: $log_url"
        exit 1 ;;
      created|queued|build_in_progress|pre_deploy_in_progress|update_in_progress)
        sleep 15 ;;
      *)
        echo "::error::Render deploy $deploy_id returned unknown status $status. Logs: $log_url"
        exit 1 ;;
    esac
  done
  echo "::error::Render deploy $deploy_id timed out. Logs: $log_url"
  exit 1
)

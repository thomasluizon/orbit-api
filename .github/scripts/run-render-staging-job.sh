#!/usr/bin/env bash
set -euo pipefail

command_name="$1"
case "$command_name" in
  migrate-staging|seed-staging) ;;
  *) echo "Unsupported staging command: $command_name" >&2; exit 1 ;;
esac

: "${RENDER_API_KEY:?}"
: "${TF_VAR_staging_api_service_id:?}"
: "${STAGING_INTERNAL_HOST:?}"
: "${SEED_OWNER_EMAIL:?}"

start_command="$(jq -nr \
  --arg host "$STAGING_INTERNAL_HOST" \
  --arg email "$SEED_OWNER_EMAIL" \
  --arg command "$command_name" \
  '["env", "ASPNETCORE_ENVIRONMENT=Staging", "Seed__ExpectedHost=" + $host,
    "Seed__OwnerEmail=" + $email, "dotnet", "Orbit.Api.dll", $command]
  | map(@sh) | join(" ")')"
payload="$(jq -nc --arg command "$start_command" '{startCommand: $command}')"
job="$(curl --fail-with-body --silent --show-error --max-time 30 \
  --request POST \
  --header "Authorization: Bearer ${RENDER_API_KEY}" \
  --header 'Content-Type: application/json' \
  --data "$payload" \
  "https://api.render.com/v1/services/${TF_VAR_staging_api_service_id}/jobs")"
job_id="$(jq -er --arg service "$TF_VAR_staging_api_service_id" \
  'select(.serviceId == $service) | .id | select(type == "string" and startswith("job-"))' <<< "$job")"
echo "Render staging job: $job_id ($command_name)"

for ((attempt=1; attempt<=90; attempt++)); do
  response="$(curl --fail-with-body --silent --show-error --max-time 30 \
    --header "Authorization: Bearer ${RENDER_API_KEY}" \
    "https://api.render.com/v1/services/${TF_VAR_staging_api_service_id}/jobs/${job_id}")"
  status="$(jq -er --arg id "$job_id" \
    'select(.id == $id) | .status | select(type == "string")' <<< "$response")"
  echo "Render staging job $job_id: $status"
  case "$status" in
    succeeded) exit 0 ;;
    failed|canceled) exit 1 ;;
    pending|running) sleep 10 ;;
    *) echo "Unexpected Render job status: $status" >&2; exit 1 ;;
  esac
done

echo "Render staging job $job_id timed out" >&2
exit 1

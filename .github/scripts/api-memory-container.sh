#!/usr/bin/env bash
# Start the API image in a 512 MiB, half-CPU container like Render Starter, on a migrated
# database over TLS, drive the recorded HTTP load, and fail when anonymous plus shmem memory
# reaches the warning line. See infra/memory-budget.md.
set -euo pipefail

image=${1:?usage: api-memory-container.sh <image>}
warning_bytes=314572800
platform=${ORBIT_MEMORY_PLATFORM:-linux/amd64}
settle_seconds=${ORBIT_MEMORY_SETTLE_SECONDS:-60}
script_dir=$(cd "$(dirname "$0")" && pwd)
network=orbit-memory-guard
database=orbit-memory-guard-postgres
api=orbit-memory-guard-api
connection="Host=$database;Port=5432;Database=orbit;Username=orbit;Password=orbit;SSL Mode=Require;Trust Server Certificate=true"
smoke_email=memory-guard@example.test
smoke_code=246810
env_file=$(mktemp)

cleanup() {
  docker rm -f "$api" "$database" >/dev/null 2>&1 || true
  docker network rm "$network" >/dev/null 2>&1 || true
  rm -f "$env_file"
}
trap cleanup EXIT

cat > "$env_file" <<EOF
ASPNETCORE_ENVIRONMENT=Production
ASPNETCORE_URLS=http://+:8080
ConnectionStrings__DefaultConnection=$connection
ConnectionStrings__SessionConnection=$connection
Jwt__SecretKey=memory-guard-placeholder-signing-key-0123456789
Encryption__Key=AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA=
Google__AllowedRedirectUris__0=https://app.useorbit.org/auth/callback
AI__ApiKey=memory-guard-placeholder
Stripe__MonthlyPriceIdUsd=memory-guard-placeholder
Stripe__YearlyPriceIdUsd=memory-guard-placeholder
Stripe__MonthlyPriceIdBrl=memory-guard-placeholder
Stripe__YearlyPriceIdBrl=memory-guard-placeholder
GooglePlay__PackageName=org.useorbit.memoryguard
GooglePlay__ServiceAccountJson={}
GooglePlay__ProductId=memory-guard-placeholder
GooglePlay__MonthlyBasePlanId=memory-guard-placeholder
GooglePlay__YearlyBasePlanId=memory-guard-placeholder
GooglePlay__ReferralOfferId=memory-guard-placeholder
GooglePlay__RtdnAudience=memory-guard-placeholder
GooglePlay__RtdnServiceAccountEmail=memory-guard@example.test
Storage__S3__Bucket=memory-guard-placeholder
Storage__S3__PublicBaseUrl=https://api.useorbit.org
Storage__S3__Region=us-east-2
Storage__S3__AccessKeyId=memory-guard-placeholder
Storage__S3__SecretAccessKey=memory-guard-placeholder
Ses__AccessKeyId=memory-guard-placeholder
Ses__SecretAccessKey=memory-guard-placeholder
Ses__Region=us-east-2
Ses__FromEmail=memory-guard@example.test
Ses__SupportEmail=memory-guard@example.test
Ses__MarketingFromEmail=memory-guard@example.test
Ses__TransactionalConfigurationSet=memory-guard-placeholder
Ses__MarketingConfigurationSet=memory-guard-placeholder
Ses__TopicArn=arn:aws:sns:us-east-2:000000000000:memory-guard
AI__BaseUrl=http://orbit-memory-guard-ai.invalid/v1
SMOKE_TEST_EMAIL=$smoke_email
SMOKE_TEST_CODE=$smoke_code
EOF

read_memory() {
  echo "== $1"
  docker exec "$api" sh -c '
    grep -E "^(VmRSS|RssAnon|RssFile|RssShmem|Threads)" /proc/1/status
    grep -E "^(Rss|Anonymous|Private_Clean|Private_Dirty)" /proc/1/smaps_rollup
    echo "cgroup memory.current $(cat /sys/fs/cgroup/memory.current)"
    grep -E "^(anon|file|shmem) " /sys/fs/cgroup/memory.stat'
}

check_memory() {
  [ "$(docker inspect -f '{{.State.Running}}' "$api")" = true ] || {
    docker logs "$api" 2>&1 | tail -40
    echo "::error::API container stopped (OOMKilled=$(docker inspect -f '{{.State.OOMKilled}}' "$api"))"
    exit 1
  }
  read_memory "$1"
  local status anon_kb shmem_kb unreclaimable_bytes
  status=$(docker exec "$api" cat /proc/1/status)
  anon_kb=$(awk '/^RssAnon:/ {print $2}' <<<"$status")
  shmem_kb=$(awk '/^RssShmem:/ {print $2}' <<<"$status")
  unreclaimable_bytes=$(( (anon_kb + shmem_kb) * 1024 ))
  echo "API anonymous plus shmem $unreclaimable_bytes bytes; warning line $warning_bytes bytes"
  if [ "$unreclaimable_bytes" -ge "$warning_bytes" ]; then
    echo "::error::API anonymous plus shmem memory $unreclaimable_bytes bytes reached the $warning_bytes byte warning line ($1) in a 512 MiB container"
    exit 1
  fi
}

docker image inspect -f '{{range .Config.Env}}{{println .}}{{end}}' "$image" | grep -qx 'MALLOC_ARENA_MAX=2' || {
  echo "::error::The API image must set MALLOC_ARENA_MAX=2; without it the glibc arenas grow with uptime (infra/memory-budget.md)"
  exit 1
}

docker network create "$network" >/dev/null
docker run -d --name "$database" --network "$network" \
  -e POSTGRES_USER=orbit -e POSTGRES_PASSWORD=orbit -e POSTGRES_DB=orbit postgres:17 \
  -c ssl=on -c ssl_cert_file=/etc/ssl/certs/ssl-cert-snakeoil.pem -c ssl_key_file=/etc/ssl/private/ssl-cert-snakeoil.key >/dev/null
until docker exec "$database" pg_isready -U orbit -d orbit >/dev/null 2>&1; do sleep 1; done

wait_for_hangfire() {
  for _ in $(seq 1 180); do
    docker logs "$1" 2>&1 | grep -q "all the dispatchers started" && return
    [ "$(docker inspect -f '{{.State.Running}}' "$1")" = true ] || return
    sleep 1
  done
}

# Production starts with no pending migration, so a separate process applies them first.
if [ "$platform" = linux/amd64 ]; then
  docker run --rm --network "$network" --platform "$platform" --entrypoint /app/efbundle \
    "$image" --connection "$connection" >/dev/null
else
  # The image's efbundle targets linux-x64, so a first API start applies the migrations on another platform.
  docker run -d --name "$api" --network "$network" --platform "$platform" \
    --env-file "$env_file" -e Database__MigrateOnStartup=true "$image" >/dev/null
  wait_for_hangfire "$api"
  docker rm -f "$api" >/dev/null
fi

docker run -d --name "$api" --network "$network" --platform "$platform" \
  --memory=512m --memory-swap=512m --cpus=0.5 --env-file "$env_file" "$image" >/dev/null
wait_for_hangfire "$api"
check_memory startup
sleep "$settle_seconds"
check_memory "idle after ${settle_seconds}s"

docker run --rm --network "$network" -v "$script_dir:/scripts:ro" \
  -e API_BASE_URL="http://$api:8080" -e SMOKE_TEST_EMAIL="$smoke_email" -e SMOKE_TEST_CODE="$smoke_code" \
  node:22-alpine node /scripts/api-memory-load.mjs
check_memory "after the recorded load"
sleep "$settle_seconds"
check_memory "after the recorded load and ${settle_seconds}s"

[ "$(docker inspect -f '{{.State.OOMKilled}}' "$api")" = false ] || { echo "::error::API container was OOM-killed"; exit 1; }

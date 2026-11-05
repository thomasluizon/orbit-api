#!/usr/bin/env bash
# Start the API image in a 512 MiB container on a migrated database and fail when
# the process working set reaches the memory warning line. See infra/memory-budget.md.
set -euo pipefail

image=${1:?usage: api-memory-container.sh <image>}
warning_bytes=429496729
settle_seconds=${ORBIT_MEMORY_SETTLE_SECONDS:-60}
network=orbit-memory-guard
database=orbit-memory-guard-postgres
api=orbit-memory-guard-api
connection="Host=$database;Port=5432;Database=orbit;Username=orbit;Password=orbit"
env_file=$(mktemp)

cleanup() {
  docker rm -f "$api" "$database" >/dev/null 2>&1 || true
  docker network rm "$network" >/dev/null 2>&1 || true
  rm -f "$env_file"
}
trap cleanup EXIT

# Placeholder values only satisfy startup validation; the guard makes no external call.
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
EOF

read_memory() {
  echo "== $1"
  docker exec "$api" sh -c '
    grep -E "^(VmRSS|RssAnon|RssFile|RssShmem|Threads)" /proc/1/status
    grep -E "^(Rss|Anonymous|Private_Clean|Private_Dirty)" /proc/1/smaps_rollup
    echo "cgroup memory.current $(cat /sys/fs/cgroup/memory.current)"
    grep -E "^(anon|file|shmem) " /sys/fs/cgroup/memory.stat'
}

docker network create "$network" >/dev/null
docker run -d --name "$database" --network "$network" \
  -e POSTGRES_USER=orbit -e POSTGRES_PASSWORD=orbit -e POSTGRES_DB=orbit postgres:17 >/dev/null
until docker exec "$database" pg_isready -U orbit -d orbit >/dev/null 2>&1; do sleep 1; done

# Production starts with no pending migration, so the separate bundle process applies them first.
docker run --rm --network "$network" --platform linux/amd64 --entrypoint /app/efbundle \
  "$image" --connection "$connection" >/dev/null

docker run -d --name "$api" --network "$network" --platform linux/amd64 \
  --memory=512m --memory-swap=512m --env-file "$env_file" "$image" >/dev/null
for _ in $(seq 1 180); do
  docker logs "$api" 2>&1 | grep -q "all the dispatchers started" && break
  [ "$(docker inspect -f '{{.State.Running}}' "$api")" = true ] || break
  sleep 1
done
[ "$(docker inspect -f '{{.State.Running}}' "$api")" = true ] || {
  docker logs "$api" 2>&1 | tail -40
  echo "::error::API container stopped during startup (OOMKilled=$(docker inspect -f '{{.State.OOMKilled}}' "$api"))"
  exit 1
}
read_memory startup
sleep "$settle_seconds"
read_memory "after ${settle_seconds}s"

[ "$(docker inspect -f '{{.State.OOMKilled}}' "$api")" = false ] || { echo "::error::API container was OOM-killed"; exit 1; }
rss_bytes=$(( $(docker exec "$api" awk '/^VmRSS/ {print $2}' /proc/1/status) * 1024 ))
echo "API working set $rss_bytes bytes; warning line $warning_bytes bytes"
if [ "$rss_bytes" -ge "$warning_bytes" ]; then
  echo "::error::API working set $rss_bytes bytes reached the $warning_bytes byte warning line in a 512 MiB container"
  exit 1
fi

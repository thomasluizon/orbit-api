#!/usr/bin/env bash
# Soak the API image in a 512 MiB, half-CPU container: the recorded HTTP load once, then one soak client round a
# minute. An OpenAI-shaped fake over TLS answers moderation and Responses calls, so Astra turns load the tokenizer.
# Every 5 minutes it reads /proc/1/status and adds up /proc/1/smaps by mapping kind. At the end, dotnet-gcdump
# forces a full GC and reports the heap. See infra/memory-budget.md.
set -euo pipefail

image=${1:?usage: api-memory-soak.sh <image> [minutes] [output directory under a Docker-shared path] [extra env file]}
minutes=${2:-60}
out=${3:-$PWD/api-memory-soak}
mkdir -p "$out"
out=$(cd "$out" && pwd)
extra_env=${4:-}
platform=${ORBIT_MEMORY_PLATFORM:-linux/amd64}
script_dir=$(cd "$(dirname "$0")" && pwd)
prefix=${ORBIT_MEMORY_SOAK_PREFIX:-orbit-memory-soak}
network=$prefix; database=$prefix-postgres; api=$prefix-api; sink=$prefix-sink; counters=$prefix-counters
connection="Host=$database;Port=5432;Database=orbit;Username=orbit;Password=orbit;SSL Mode=Require;Trust Server Certificate=true"
sink_dir=$out/sink; state_dir=$out/state; env_file=$out/env
mkdir -p "$sink_dir" "$state_dir"; chmod 777 "$out" "$state_dir"

sed -n '/^cat > "\$env_file" <<EOF$/,/^EOF$/p' "$script_dir/api-memory-container.sh" | sed '1d;$d' \
  | sed "s|\$connection|$connection|g; s|\$smoke_email|memory-guard@example.test|; s|\$smoke_code|246810|" \
  | sed "s|^AI__BaseUrl=.*|AI__BaseUrl=https://sink:8443/v1|" > "$env_file"
cat >> "$env_file" <<EOF
Sentry__Dsn=https://0123456789abcdef0123456789abcdef@sink:8443/1
PostHog__ApiKey=phc_memory_soak_placeholder
PostHog__HostUrl=https://sink:8443
SSL_CERT_FILE=/sink/bundle.crt
EOF

cleanup() {
  docker rm -f "$api" "$database" "$sink" "$counters" >/dev/null 2>&1 || true
  docker network rm "$network" >/dev/null 2>&1 || true
  rm -rf "$sink_dir"
}
trap cleanup EXIT
cleanup
mkdir -p "$sink_dir"
openssl req -x509 -newkey rsa:2048 -nodes -keyout "$sink_dir/sink.key" -out "$sink_dir/sink.crt" -days 2 \
  -subj "/CN=sink" -addext "subjectAltName=DNS:sink" 2>/dev/null
chmod 644 "$sink_dir/sink.key"
cp "$sink_dir/sink.crt" "$sink_dir/bundle.crt"

docker build -q -t orbit-memory-diag - >/dev/null <<'EOF'
FROM mcr.microsoft.com/dotnet/sdk:10.0
RUN dotnet tool install --tool-path /diag dotnet-counters --version 10.0.750501 \
    && dotnet tool install --tool-path /diag dotnet-gcdump --version 10.0.750501
EOF

docker network create "$network" >/dev/null
docker run -d --name "$database" --network "$network" -e POSTGRES_USER=orbit -e POSTGRES_PASSWORD=orbit -e POSTGRES_DB=orbit postgres:17 \
  -c ssl=on -c ssl_cert_file=/etc/ssl/certs/ssl-cert-snakeoil.pem -c ssl_key_file=/etc/ssl/private/ssl-cert-snakeoil.key >/dev/null
until docker exec "$database" pg_isready -U orbit -d orbit >/dev/null 2>&1; do sleep 1; done
docker run -d --name "$sink" --network "$network" --network-alias sink -v "$sink_dir:/sink:ro" -v "$script_dir:/scripts:ro" \
  node:22-alpine node /scripts/api-memory-fake-openai.mjs >/dev/null

wait_for_hangfire() {
  local container=$1
  for _ in $(seq 1 300); do
    [[ $(docker logs "$container" 2>&1 | grep -c "all the dispatchers started") -gt 0 ]] && return
    [[ $(docker inspect -f '{{.State.Running}}' "$container") == true ]] || return
    sleep 1
  done
}

# A first start applies the migrations, so the measured start finds a migrated database like production.
docker run -d --name "$api" --network "$network" --platform "$platform" -v "$sink_dir:/sink:ro" \
  --env-file "$env_file" -e Database__MigrateOnStartup=true "$image" >/dev/null
wait_for_hangfire "$api"
docker rm -f "$api" >/dev/null
docker run -d --name "$api" --network "$network" --platform "$platform" -v "$sink_dir:/sink:ro" -v "$out:/out" \
  --memory=512m --memory-swap=512m --cpus=0.5 --env-file "$env_file" ${extra_env:+--env-file "$extra_env"} "$image" >/dev/null
wait_for_hangfire "$api"
start=$(date +%s)
docker run -d --name "$counters" --pid=container:"$api" --cap-add SYS_PTRACE -e TMPDIR=/proc/1/root/tmp -v "$out:/out" \
  --entrypoint /diag/dotnet-counters orbit-memory-diag collect -p 1 --refresh-interval 60 --format csv -o /out/counters.csv \
  --counters System.Runtime >/dev/null

sample() {
  local label=$1
  docker exec "$api" sh -c '
    s=$(grep -E "^(RssAnon|RssShmem|RssFile|Threads)" /proc/1/status | tr -s " \t" " " | tr "\n" " ")
    k=$(awk "function kind(p,perm){ if(p==\"\")return \"anonymous\"; if(p==\"[heap]\")return \"heap\"; if(p ~ /^\\[stack/)return \"stack\"; if(p ~ /^\\[/)return \"kernel\"; if(p ~ /^\\/memfd:doublemapper/)return (perm ~ /x/ ? \"dm-code\" : \"dm-data\"); if(p ~ /^\\/memfd:/)return \"shm\"; if(p ~ /\\.dll\$/)return \"dll\"; if(p ~ /\\.so/)return \"so\"; return \"file\"} /^[0-9a-f]+-[0-9a-f]+ /{p=\"\"; for(i=6;i<=NF;i++){p=p (i>6?\" \":\"\") \$i}; c=kind(p,\$2); n[c]++} /^Rss:/{r[c]+=\$2} END{for(c in n) printf \"%s=%d/%d \", c, r[c], n[c]}" /proc/1/smaps)
    echo "$s| $k"' | sed "s/^/$(( $(date +%s) - start ))s $label /" | tee -a "$out/samples.txt"
}

sample startup
docker run --rm --network container:"$api" -v "$script_dir:/scripts:ro" -e API_BASE_URL=http://localhost:8080 \
  -e SMOKE_TEST_EMAIL=memory-guard@example.test -e SMOKE_TEST_CODE=246810 node:22-alpine node /scripts/api-memory-load.mjs > "$out/load.txt"
sample after-load
end=$(( start + minutes * 60 ))
next_sample=$(( $(date +%s) + 300 ))
while [[ $(date +%s) -lt $end ]]; do
  [[ $(docker inspect -f '{{.State.Running}}' "$api") == true ]] || {
    echo "API stopped OOMKilled=$(docker inspect -f '{{.State.OOMKilled}}' "$api")" | tee -a "$out/samples.txt"
    docker logs "$api" > "$out/api.log" 2>&1
    exit 1
  }
  docker run --rm --network container:"$api" -v "$script_dir:/scripts:ro" -v "$state_dir:/state" \
    -e SMOKE_TEST_EMAIL=memory-guard@example.test -e SMOKE_TEST_CODE=246810 node:22-alpine node /scripts/api-memory-soak-client.mjs \
    >> "$out/client.txt" 2>&1 || true
  if [[ $(date +%s) -ge $next_sample ]]; then sample soak; next_sample=$(( next_sample + 300 )); fi
  sleep 45
done

sample before-full-gc
docker rm -f "$counters" >/dev/null
docker run --rm --pid=container:"$api" --cap-add SYS_PTRACE -e TMPDIR=/proc/1/root/tmp -v "$out:/out" \
  --entrypoint /diag/dotnet-gcdump orbit-memory-diag collect -p 1 -o /out/soak.gcdump > "$out/gcdump.txt" 2>&1
docker run --rm -v "$out:/out" --entrypoint /diag/dotnet-gcdump orbit-memory-diag report /out/soak.gcdump > "$out/gcdump-report.txt"
sleep 5
sample after-full-gc
docker logs "$api" > "$out/api.log" 2>&1
echo "Soak output in $out"

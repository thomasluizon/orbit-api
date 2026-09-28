#!/usr/bin/env bash
set -euo pipefail

repo_root="$(cd "$(dirname "$0")/../.." && pwd)"
script="${SCRIPT_UNDER_TEST:-$repo_root/.github/scripts/staging-postgres-access.sh}"
test_dir="$(mktemp -d)"
trap 'rm -rf "$test_dir"' EXIT

cat > "$test_dir/curl" <<'MOCK'
#!/usr/bin/env bash
set -euo pipefail

url="${@: -1}"
if [[ "$url" == 'https://api.ipify.org' ]]; then
  echo '203.0.113.10'
  exit 0
fi

payload=''
method=GET
while (($#)); do
  case "$1" in
    --request) method="$2"; shift 2 ;;
    --data) payload="$2"; shift 2 ;;
    *) shift ;;
  esac
done

if [[ "$method" == PATCH ]]; then
  jq -cn --arg id 'dpg-test' --argjson list "$(jq -c '.ipAllowList' <<< "$payload")" \
    '{id: $id, ipAllowList: $list}' > "$MOCK_STATE"
  echo PATCH >> "$MOCK_PATCHES"
  if [[ -f "$MOCK_FAIL_AFTER_PATCH" ]]; then
    touch "$MOCK_FAIL_VERIFICATION"
    rm "$MOCK_FAIL_AFTER_PATCH"
  fi
else
  if [[ -f "$MOCK_FAIL_VERIFICATION" ]]; then
    exit 22
  fi
  cat "$MOCK_STATE"
fi
MOCK
chmod +x "$test_dir/curl"
cat > "$test_dir/sleep" <<'MOCK'
#!/usr/bin/env bash
exit 0
MOCK
chmod +x "$test_dir/sleep"

export PATH="$test_dir:$PATH"
export MOCK_STATE="$test_dir/state.json"
export MOCK_PATCHES="$test_dir/patches"
export MOCK_FAIL_AFTER_PATCH="$test_dir/fail-after-patch"
export MOCK_FAIL_VERIFICATION="$test_dir/fail-verification"
export RENDER_API_KEY=test
export STAGING_POSTGRES_ID=dpg-test
export TF_VAR_staging_postgres_ip_allow_list='[{"cidr_block":"192.0.2.5/32","description":"operator"}]'
export STAGING_ACCESS_MARKER="$test_dir/access-marker"

set_state() {
  jq -cn --argjson list "$1" '{id: "dpg-test", ipAllowList: $list}' > "$MOCK_STATE"
  : > "$MOCK_PATCHES"
  rm -f "$STAGING_ACCESS_MARKER" "$MOCK_FAIL_AFTER_PATCH" "$MOCK_FAIL_VERIFICATION"
}

assert_list() {
  actual="$(jq -c '.ipAllowList | sort_by(.cidrBlock, .description)' "$MOCK_STATE")"
  expected="$(jq -c 'sort_by(.cidrBlock, .description)' <<< "$1")"
  if [[ "$actual" != "$expected" ]]; then
    echo "Expected allow list $expected, got $actual" >&2
    return 1
  fi
}

operator='[{"cidrBlock":"192.0.2.5/32","description":"operator"}]'
drift='[{"cidrBlock":"192.0.2.5/32","description":"operator"},{"cidrBlock":"198.51.100.8/32","description":"other operator"}]'
temporary='[{"cidrBlock":"192.0.2.5/32","description":"operator"},{"cidrBlock":"203.0.113.10/32","description":"github-actions-staging-reseed"}]'

set_state "$drift"
if bash "$script" open > /dev/null 2>&1; then
  echo 'Drifted open unexpectedly succeeded' >&2
  exit 1
fi
bash "$script" restore > /dev/null
assert_list "$drift"
[[ ! -s "$MOCK_PATCHES" ]]
[[ ! -e "$STAGING_ACCESS_MARKER" ]]

set_state "$operator"
touch "$MOCK_FAIL_AFTER_PATCH"
if bash "$script" open > /dev/null 2>&1; then
  echo 'Open unexpectedly passed after verification failed' >&2
  exit 1
fi
[[ -e "$STAGING_ACCESS_MARKER" ]]
assert_list "$temporary"
rm "$MOCK_FAIL_VERIFICATION"
bash "$script" restore > /dev/null
assert_list "$operator"
[[ ! -e "$STAGING_ACCESS_MARKER" ]]

set_state "$operator"
bash "$script" open > /dev/null
assert_list "$temporary"
bash "$script" reconcile > /dev/null
assert_list "$operator"

set_state "$temporary"
jq -cn --argjson list "$(jq -cn --argjson first "$temporary" --argjson extra "$drift" '$first + [$extra[1]]')" \
  '{id: "dpg-test", ipAllowList: $list}' > "$MOCK_STATE"
if bash "$script" reconcile > /dev/null 2>&1; then
  echo 'Reconciliation unexpectedly accepted operator drift' >&2
  exit 1
fi
assert_list "$drift"

assert_staging_queue() {
  local workflow="$1"
  local group="$2"
  local concurrency
  concurrency="$(awk '/^concurrency:/{found=1;next} found && /^[^[:space:]]/{exit} found {print}' "$workflow")"
  if ! grep -Fxq "  group: $group" <<< "$concurrency" ||
    ! grep -Fxq '  cancel-in-progress: false' <<< "$concurrency" ||
    ! grep -Fxq '  queue: max' <<< "$concurrency"; then
    echo "Staging workflow can replace a queued reseed: $workflow" >&2
    return 1
  fi
}

assert_staging_queue "$repo_root/.github/workflows/staging-reseed.yml" 'api-release-staging'
assert_staging_queue "$repo_root/.github/workflows/staging-postgres-access-reconcile.yml" 'api-release-staging'
assert_staging_queue "$repo_root/.github/workflows/release.yml" 'api-release-${{ inputs.environment }}'

staging_release="$(sed -n '/^  staging:/,$p' "$repo_root/.github/workflows/release.yml")"
if ! grep -Fq 'environment: staging' <<< "$staging_release" ||
  ! grep -Fq 'RENDER_API_KEY: ${{ secrets.RENDER_API_KEY }}' <<< "$staging_release" ||
  ! grep -Fq '/deploys/$deploy_id' <<< "$staging_release" ||
  ! grep -Fq 'live) exit 0' <<< "$staging_release" ||
  ! grep -Fq 'returned no deploy ID' <<< "$staging_release" ||
  grep -Fq 'createdAfter' <<< "$staging_release" ||
  grep -Fq 'RENDER_STAGING_DEPLOY_HOOK_URL' <<< "$staging_release"; then
  echo 'Staging release must wait for its Render deploy before recording success' >&2
  exit 1
fi

echo 'Staging access drift, partial open, restoration, and timeout reconciliation passed'

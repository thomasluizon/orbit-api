#!/usr/bin/env bash
# Usage: changed-inputs.sh <group>...
#
# Writes "<group>=true" or "<group>=false" to GITHUB_OUTPUT for each group. A group is true when
# the pull request changes a file that the group's steps read. A job skips only the steps whose
# inputs did not change, so each job still reports its context.
#
# Every event other than pull_request sets every group to true, so a push to main or a dispatch
# runs every step that the event allows. A pull request is compared through its merge commit
# (HEAD) with the base tip (HEAD^1), so the checkout needs fetch-depth 2 or more. Renames count
# both paths.
set -euo pipefail

pattern() {
  case "$1" in
    # Unit Tests: the C# test projects read src/, their fixtures under tests/ and evals/,
    # infra/configuration.tf, the memory guard script and the
    # migration contracts. LegacyNameGuardTests reads every path in the repository, so a path
    # that carries the retired product name is an input as well.
    tests) echo '^(src|tests|bench|infra|evals)/|^(Orbit\.slnx|Directory\.Build\.(props|targets)|global\.json|CodeMetricsConfig\.txt|coverlet\.runsettings)$|^\.config/dotnet-tools\.json$|^\.github/(migration-(contracts|locks)\.json|scripts/(api-memory-container\.sh|check_coverage\.py|openapi_artifacts\.py|changed-inputs\.sh)|workflows/test\.yml)$|Astra|ASTRA|(^|[^A-Za-z])astra' ;;
    # OpenAPI Breaking-Change Gate steps of Guards.
    openapi) echo '^src/Orbit\.Api/(openapi[^/]*\.json|OpenApi/artifacts\.json)$|^(\.oasdiff\.yaml|\.github/oasdiff-levels\.txt)$|^\.github/(scripts/(openapi_artifacts\.py|changed-inputs\.sh)|workflows/guards\.yml)$' ;;
    # Dependency Review step of Guards: a project file, a lock file or a workflow.
    deps) echo '(^|/)[^/]+\.(csproj|slnx)$|(^|/)Directory\.(Packages|Build)\.(props|targets)$|(^|/)(packages\.lock\.json|package(-lock)?\.json)$|^(global\.json|\.config/dotnet-tools\.json)$|^\.github/(workflows/|dependabot\.yml$|scripts/changed-inputs\.sh$)' ;;
    # Render infrastructure steps of Guards.
    infra) echo '^infra/|^\.github/workflows/(guards|release|staging-reseed|staging-postgres-access-reconcile)\.yml$|^\.github/scripts/(render-release\.sh|staging-reseed[^/]*|[^/]*postgres-access[^/]*|changed-inputs\.sh)$' ;;
    *)
      echo "changed-inputs.sh: unknown input group: $1" >&2
      exit 2
      ;;
  esac
}

output=${GITHUB_OUTPUT:?GITHUB_OUTPUT is required}
if [ "$#" -eq 0 ]; then
  echo 'usage: changed-inputs.sh <group>...' >&2
  exit 2
fi

if [ "${GITHUB_EVENT_NAME:?GITHUB_EVENT_NAME is required}" != pull_request ]; then
  for group in "$@"; do
    pattern "$group" > /dev/null
    echo "$group=true" | tee -a "$output"
  done
  exit 0
fi

changed=$(git diff --name-only --no-renames HEAD^1 HEAD)
for group in "$@"; do
  regex=$(pattern "$group")
  if grep -qE "$regex" <<< "$changed"; then value=true; else value=false; fi
  echo "$group=$value" | tee -a "$output"
done

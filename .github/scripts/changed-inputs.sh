#!/usr/bin/env bash
# Usage: changed-inputs.sh <group>...
#
# Writes "<group>=true" or "<group>=false" to GITHUB_OUTPUT for each group. A group is true when
# the pull request changes a file that the group's steps read. Each job reports its required
# context whatever the diff holds, and skips only the steps whose inputs did not change.
#
# Every event other than pull_request sets every group to true: release admission reads the push
# results at the release SHA, and the weekly CodeQL run analyzes everything. A pull request is
# compared through its merge commit (HEAD) with the base tip (HEAD^1), so the checkout needs
# fetch-depth 2 or more. Renames count both paths.
set -euo pipefail

pattern() {
  case "$1" in
    # Unit Tests: the C# test projects read src/, their fixtures under tests/ and evals/,
    # infra/configuration.tf, the memory guard script, the suppression allowlist and the
    # migration contracts. LegacyNameGuardTests reads every path in the repository, so a path
    # that carries the retired product name is an input as well.
    tests) echo '^(src|tests|bench|infra|evals)/|^(Orbit\.slnx|Directory\.Build\.props|global\.json|CodeMetricsConfig\.txt|coverlet\.runsettings)$|^\.config/dotnet-tools\.json$|^tools/suppression-allowlist\.json$|^\.github/(migration-(contracts|locks)\.json|scripts/(api-memory-container\.sh|check_coverage\.py|openapi_artifacts\.py|changed-inputs\.sh)|workflows/test\.yml)$|Astra|ASTRA|(^|[^A-Za-z])astra' ;;
    # Docker migration bundle: the image copies only src/; Orbit.Memory.Tests links files from
    # tests/Shared and tests/Orbit.Infrastructure.Tests.
    docker) echo '^(src|tests/(Shared|Orbit\.Memory\.Tests|Orbit\.Infrastructure\.Tests))/|^(Dockerfile|\.dockerignore|Directory\.Build\.props|global\.json|CodeMetricsConfig\.txt)$|^\.github/(scripts/(api-memory-[^/]+|changed-inputs\.sh)|workflows/test\.yml)$' ;;
    openapi) echo '^src/Orbit\.Api/(openapi[^/]*\.json|OpenApi/artifacts\.json)$|^(\.oasdiff\.yaml|\.github/oasdiff-levels\.txt)$|^\.github/(scripts/(openapi_artifacts\.py|changed-inputs\.sh)|workflows/test\.yml)$' ;;
    # CodeQL legs, named after the matrix language.
    csharp) echo '^src/|^(Directory\.Build\.props|global\.json)$|^\.github/(scripts/changed-inputs\.sh|workflows/codeql\.yml)$' ;;
    actions) echo '^\.github/(workflows|actions)/|^\.github/scripts/changed-inputs\.sh$' ;;
    javascript-typescript) echo '\.(c|m)?(j|t)sx?$|^\.github/(scripts/changed-inputs\.sh|workflows/codeql\.yml)$' ;;
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

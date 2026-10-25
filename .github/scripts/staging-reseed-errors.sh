#!/usr/bin/env bash
set -Eeuo pipefail

staging_reseed_error_reported=false

staging_reseed_report_failure() {
  local status="$1" command="$2" source="$3" line="$4" source_line=''
  staging_reseed_error_reported=true
  if [[ -f "$source" ]]; then
    source_line="$(sed -n "${line}p" "$source")"
  fi
  printf '::error::%s failed (exit %s) at %s:%s: %s\n' \
    "${STAGING_RESEED_STEP:-Staging reseed}" "$status" "$source" "$line" "$command" >&2
  if [[ -n "$source_line" && "$source_line" != "$command" ]]; then
    printf 'Source: %s\n' "$source_line" >&2
  fi
}

staging_reseed_report_exit() {
  local status="$1"
  if ((status != 0)) && [[ "$staging_reseed_error_reported" != true ]]; then
    staging_reseed_report_failure "$@"
  fi
}

trap 'staging_reseed_report_failure "$?" "$BASH_COMMAND" "${BASH_SOURCE[0]:-$0}" "$LINENO"' ERR
trap 'staging_reseed_report_exit "$?" "$BASH_COMMAND" "${BASH_SOURCE[0]:-$0}" "$LINENO"' EXIT

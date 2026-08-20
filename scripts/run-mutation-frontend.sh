#!/usr/bin/env bash
# Run the frontend StrykerJS clients/auth pilot.
set -euo pipefail

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
cd "${ROOT}/frontend"

write_baseline=false
force=false
while [[ $# -gt 0 ]]; do
  case "$1" in
    --write-baseline)
      write_baseline=true
      shift
      ;;
    --force)
      force=true
      shift
      ;;
    *)
      echo "unknown argument: $1" >&2
      exit 2
      ;;
  esac
done

args=(run)
if [[ "${force}" == true ]]; then
  args+=(--force)
fi
npm exec stryker -- "${args[@]}"

report="reports/mutation/mutation.json"
if [[ ! -f "${report}" ]]; then
  echo "error: StrykerJS produced no ${report}" >&2
  exit 1
fi
mkdir -p "${ROOT}/quality/mutation-reports"
cp "${report}" "${ROOT}/quality/mutation-reports/frontend-latest.json"

if [[ "${write_baseline}" == true ]]; then
  python3 "${ROOT}/scripts/check-mutation-baseline.py" \
    --report "${ROOT}/quality/mutation-reports/frontend-latest.json" \
    --baseline "${ROOT}/quality/mutation-baseline.json" \
    --write-baseline
fi

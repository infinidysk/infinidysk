#!/usr/bin/env bash
# Run the backend Stryker pilot and optionally update quality/mutation-baseline.json.
set -euo pipefail

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
cd "${ROOT}"
dotnet tool restore

mutate_args=()
write_baseline=false
since=""
while [[ $# -gt 0 ]]; do
  case "$1" in
    --write-baseline)
      write_baseline=true
      shift
      ;;
    --since)
      since="${2:?}"
      shift 2
      ;;
    --mutate)
      mutate_args+=("$2")
      shift 2
      ;;
    *)
      echo "unknown argument: $1" >&2
      exit 2
      ;;
  esac
done

cd tests/NzbWebDAV.Tests
rm -rf StrykerOutput
config="stryker-config.json"
if [[ ${#mutate_args[@]} -gt 0 ]]; then
  printf '%s\n' "${mutate_args[@]}" > /tmp/mutate-globs.txt
  python3 "${ROOT}/scripts/write-stryker-mutate-config.py" \
    --base stryker-config.json \
    --output stryker-config.ci.json \
    --globs-file /tmp/mutate-globs.txt
  config="stryker-config.ci.json"
fi
cmd=(dotnet stryker --verbosity info --config-file "${config}")
if [[ -n "${since}" ]]; then
  cmd+=(--since:"${since}")
fi
"${cmd[@]}"

report="$(find StrykerOutput -name mutation-report.json | sort | tail -n 1)"
if [[ -z "${report}" ]]; then
  echo "error: Stryker produced no mutation-report.json" >&2
  exit 1
fi
mkdir -p "${ROOT}/quality/mutation-reports"
cp "${report}" "${ROOT}/quality/mutation-reports/backend-latest.json"

if [[ "${write_baseline}" == true ]]; then
  python3 "${ROOT}/scripts/check-mutation-baseline.py" \
    --report "${ROOT}/quality/mutation-reports/backend-latest.json" \
    --baseline "${ROOT}/quality/mutation-baseline.json" \
    --write-baseline
fi

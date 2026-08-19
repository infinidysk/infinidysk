#!/usr/bin/env bash
# Fixture checks for scripts/check-performance-baseline.py
set -euo pipefail

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
GATE="${ROOT}/scripts/check-performance-baseline.py"
FIXTURES="${ROOT}/scripts/fixtures"
BASELINE="${FIXTURES}/performance-gate-baseline.json"

run_gate() {
  local expected="$1"
  shift
  local output=""
  local actual=0
  set +e
  output="$(python3 "${GATE}" "$@" 2>&1)"
  actual=$?
  set -e
  if [[ "${actual}" -ne "${expected}" ]]; then
    echo "expected exit ${expected}, got ${actual} for: $*" >&2
    echo "${output}" >&2
    exit 1
  fi
  printf '%s\n' "${output}"
}

output="$(run_gate 0 \
  --baseline "${BASELINE}" \
  --candidates \
    "${FIXTURES}/performance-gate-clean-a.json" \
    "${FIXTURES}/performance-gate-clean-b.json" \
    "${FIXTURES}/performance-gate-clean-c.json")"
if grep -q '^error:' <<<"${output}"; then
  echo "clean candidates unexpectedly printed errors" >&2
  exit 1
fi

output="$(run_gate 2 \
  --baseline "${BASELINE}" \
  --candidates "${FIXTURES}/performance-gate-count-regression.json")"
if ! grep -q 'transportRequests expected 2 but was 3' <<<"${output}"; then
  echo "count regression did not name the field" >&2
  echo "${output}" >&2
  exit 1
fi
if ! grep -q 're-baseline' <<<"${output}"; then
  echo "count regression did not print re-baseline instruction" >&2
  exit 1
fi

run_gate 2 \
  --baseline "${BASELINE}" \
  --candidates "${FIXTURES}/performance-gate-count-regression.json" \
  --deterministic-only >/dev/null

output="$(run_gate 1 \
  --baseline "${BASELINE}" \
  --candidates "${FIXTURES}/performance-gate-envelope-breach.json")"
if ! grep -q 'firstByteMs' <<<"${output}"; then
  echo "envelope breach did not name firstByteMs" >&2
  echo "${output}" >&2
  exit 1
fi

run_gate 0 \
  --baseline "${BASELINE}" \
  --candidates "${FIXTURES}/performance-gate-envelope-breach.json" \
  --deterministic-only >/dev/null

output="$(run_gate 0 \
  --baseline "${BASELINE}" \
  --candidates "${FIXTURES}/performance-gate-warn-band.json")"
if ! grep -q '^warning:' <<<"${output}"; then
  echo "warn-band candidate did not warn" >&2
  echo "${output}" >&2
  exit 1
fi

output="$(run_gate 0 \
  --baseline "${BASELINE}" \
  --candidates "${FIXTURES}/performance-gate-warn-policy.json")"
if ! grep -q '^warning:' <<<"${output}"; then
  echo "warn-policy candidate did not warn" >&2
  echo "${output}" >&2
  exit 1
fi
if grep -q '^error:' <<<"${output}"; then
  echo "warn-policy candidate should not fail" >&2
  echo "${output}" >&2
  exit 1
fi

output="$(run_gate 2 \
  --baseline "${BASELINE}" \
  --candidates \
    "${FIXTURES}/performance-gate-clean-a.json" \
    "${FIXTURES}/performance-gate-count-regression.json")"
if ! grep -q 'candidates disagree' <<<"${output}"; then
  echo "cross-candidate variance was not reported" >&2
  echo "${output}" >&2
  exit 1
fi

run_gate 0 \
  --baseline "${BASELINE}" \
  --candidates \
    "${FIXTURES}/performance-gate-clean-a.json" \
    "${FIXTURES}/performance-gate-clean-b.json" \
    "${FIXTURES}/performance-gate-outlier.json" >/dev/null

tmpdir="$(mktemp -d)"
trap 'rm -rf "${tmpdir}"' EXIT
written="${tmpdir}/written-baseline.json"
run_gate 0 \
  --candidates \
    "${FIXTURES}/performance-gate-clean-a.json" \
    "${FIXTURES}/performance-gate-clean-b.json" \
    "${FIXTURES}/performance-gate-clean-c.json" \
  --write-baseline "${written}" >/dev/null

python3 - "${written}" <<'PY'
import json
import sys
from pathlib import Path

baseline = json.loads(Path(sys.argv[1]).read_text(encoding="utf-8"))
timing = baseline["scenarios"]["cold-sequential"]["timing"]
first = timing["firstByteMs"]
# median(10, 12, 11) = 11; envelope = max(33, 11+15) = 33
if first["baseline"] != 11.0:
    raise SystemExit(f"write-baseline firstByteMs baseline: {first}")
if first["envelope"] != 33.0:
    raise SystemExit(f"write-baseline firstByteMs envelope: {first}")
if first["policy"] != "fail":
    raise SystemExit(f"write-baseline firstByteMs policy: {first}")
cpu = timing["cpuSeconds"]
# median(0.1, 0.11, 0.12) = 0.11; envelope = max(0.33, 0.11+0.25) = 0.36
if cpu["baseline"] != 0.11:
    raise SystemExit(f"write-baseline cpuSeconds baseline: {cpu}")
if cpu["envelope"] != 0.36:
    raise SystemExit(f"write-baseline cpuSeconds envelope: {cpu}")
if cpu["policy"] != "warn":
    raise SystemExit(f"write-baseline cpuSeconds policy: {cpu}")
throughput = timing["throughputMiBs"]
# median(90, 88, 85) = 88; floor = 88/3
if abs(throughput["floor"] - (88.0 / 3.0)) > 0.001:
    raise SystemExit(f"write-baseline throughput floor: {throughput}")
PY

run_gate 0 \
  --baseline "${written}" \
  --candidates \
    "${FIXTURES}/performance-gate-clean-a.json" \
    "${FIXTURES}/performance-gate-clean-b.json" \
    "${FIXTURES}/performance-gate-clean-c.json" >/dev/null

echo "Performance baseline gate fixtures passed."

#!/usr/bin/env bash
# Fixture checks for scripts/check-quality-ratchets.py
set -euo pipefail

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
GATE="${ROOT}/scripts/check-quality-ratchets.py"
FIXTURES="${ROOT}/scripts/fixtures/quality-ratchets"

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

run_gate 0 \
  --thresholds "${FIXTURES}/thresholds.json" \
  --summary "${FIXTURES}/summary-ok.json" \
  --exceptions "${ROOT}/quality/ratchet-exceptions.json"

run_gate 1 \
  --thresholds "${FIXTURES}/thresholds.json" \
  --summary "${FIXTURES}/summary-high.json"

run_gate 1 \
  --thresholds "${FIXTURES}/thresholds-lowered.json" \
  --base-thresholds "${FIXTURES}/thresholds.json" \
  --summary "${FIXTURES}/summary-ok.json"

run_gate 0 \
  --thresholds "${FIXTURES}/thresholds-lowered.json" \
  --base-thresholds "${FIXTURES}/thresholds.json" \
  --summary "${FIXTURES}/summary-ok.json" \
  --exceptions "${FIXTURES}/exceptions-ok.json"

run_gate 1 \
  --exceptions "${FIXTURES}/exceptions-expired.json"

run_gate 1 \
  --exceptions "${FIXTURES}/exceptions-malformed.json"

run_gate 0 \
  --package-json "${FIXTURES}/package-ok.json" \
  --base-package-json "${FIXTURES}/package-ok.json"

run_gate 1 \
  --package-json "${FIXTURES}/package-raised.json" \
  --base-package-json "${FIXTURES}/package-ok.json"

run_gate 1 \
  --package-json "${FIXTURES}/package-missing-ceiling.json"

echo "Quality ratchet fixtures passed."

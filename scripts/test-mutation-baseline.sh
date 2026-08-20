#!/usr/bin/env bash
set -euo pipefail

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
GATE="${ROOT}/scripts/check-mutation-baseline.py"
FIXTURES="${ROOT}/scripts/fixtures/mutation"

run_gate() {
  local expected="$1"
  shift
  local actual=0
  set +e
  python3 "${GATE}" "$@" >/tmp/mutation-gate.out 2>/tmp/mutation-gate.err
  actual=$?
  set -e
  if [[ "${actual}" -ne "${expected}" ]]; then
    echo "expected exit ${expected}, got ${actual} for: $*" >&2
    cat /tmp/mutation-gate.out /tmp/mutation-gate.err >&2
    exit 1
  fi
}

run_gate 0 \
  --report "${FIXTURES}/report-ok.json" \
  --baseline "${FIXTURES}/baseline.json"

run_gate 1 \
  --report "${FIXTURES}/report-drop.json" \
  --baseline "${FIXTURES}/baseline.json"

run_gate 0 \
  --report "${FIXTURES}/report-drop.json" \
  --baseline "${FIXTURES}/baseline.json" \
  --informational

echo "Mutation baseline fixtures passed."

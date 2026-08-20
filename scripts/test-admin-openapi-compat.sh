#!/usr/bin/env bash
# Fixture checks for scripts/check-admin-openapi-compat.py
set -euo pipefail

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
GATE="${ROOT}/scripts/check-admin-openapi-compat.py"
FIXTURES="${ROOT}/scripts/fixtures/admin-openapi"

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

output="$(run_gate 0 --current "${FIXTURES}/current-unchanged.json")"
if ! grep -q '^skip:' <<<"${output}"; then
  echo "missing base should skip" >&2
  echo "${output}" >&2
  exit 1
fi

output="$(run_gate 0 \
  --base "${FIXTURES}/base.json" \
  --current "${FIXTURES}/current-unchanged.json")"
if ! grep -q '^ok:' <<<"${output}"; then
  echo "unchanged contract should pass" >&2
  echo "${output}" >&2
  exit 1
fi

output="$(run_gate 1 \
  --base "${FIXTURES}/base.json" \
  --current "${FIXTURES}/current-breaking.json")"
if ! grep -q 'removed GET /api/is-onboarding' <<<"${output}"; then
  echo "breaking fixture did not name the removed operation" >&2
  echo "${output}" >&2
  exit 1
fi

output="$(run_gate 0 \
  --base "${FIXTURES}/base.json" \
  --current "${FIXTURES}/current-breaking-major.json")"
if ! grep -q 'major version bump' <<<"${output}"; then
  echo "major bump should allow the breaking change" >&2
  echo "${output}" >&2
  exit 1
fi

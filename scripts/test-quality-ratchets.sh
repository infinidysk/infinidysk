#!/bin/sh
set -eu

root="$(CDPATH= cd -- "$(dirname "$0")/.." && pwd)"
script="$root/scripts/check-quality-ratchets.py"
fixtures="$root/scripts/fixtures/quality-ratchets"
tmp="$(mktemp -d)"
trap 'rm -rf "$tmp"' EXIT

python3 "$script" \
  --thresholds "$fixtures/thresholds.json" \
  --summary "$fixtures/summary-ok.json" \
  --base-thresholds "$fixtures/thresholds.json"

if python3 "$script" \
  --thresholds "$fixtures/thresholds-lowered.json" \
  --summary "$fixtures/summary-ok.json" \
  --base-thresholds "$fixtures/thresholds.json"; then
  echo "expected a failure when a floor decreases" >&2
  exit 1
fi

if python3 "$script" \
  --thresholds "$fixtures/thresholds.json" \
  --summary "$fixtures/summary-high.json"; then
  echo "expected a failure when actual coverage exceeds the floor by 5pp" >&2
  exit 1
fi

echo "quality ratchet fixtures passed"

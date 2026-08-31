#!/usr/bin/env bash
# Collect restricted, reproducible CPU evidence for a provider-backed NNTP range.
# This script never uploads artifacts. Credentials must be held in CURL_CONFIG.
set -euo pipefail

usage() {
  echo "Usage: RANGE_URL=... RANGE_START=... RANGE_END=... CURL_CONFIG=/private/curl.conf $0 {result|trace|counters|perf} [run-id]" >&2
  exit 64
}

[[ $# -ge 1 && $# -le 2 ]] || usage
MODE="$1"
RUN_ID="${2:-$(date -u +%Y%m%dT%H%M%SZ)-$(git rev-parse --short HEAD)}"
: "${RANGE_URL:?Set RANGE_URL to the benchmark endpoint.}"
: "${RANGE_START:?Set RANGE_START to the inclusive byte offset.}"
: "${RANGE_END:?Set RANGE_END to the inclusive byte offset.}"
: "${CURL_CONFIG:?Set CURL_CONFIG to a private curl configuration file.}"
[[ -f "$CURL_CONFIG" ]] || { echo "CURL_CONFIG does not exist: $CURL_CONFIG" >&2; exit 64; }
CONFIG_MODE="$(stat -c '%a' "$CURL_CONFIG" 2>/dev/null || stat -f '%Lp' "$CURL_CONFIG")"
(( (8#$CONFIG_MODE & 077) == 0 )) ||
  { echo "CURL_CONFIG must not be group/world-readable." >&2; exit 64; }

RUN_DIR="/var/tmp/infinidysk-cpu/$RUN_ID"
install -d -m 0700 "$RUN_DIR"
umask 077
BACKEND_PID="${BACKEND_PID:-$(pgrep -xo NzbWebDAV || true)}"
NODE_PID="${NODE_PID:-$(pgrep -xo node || true)}"
[[ -n "$BACKEND_PID" ]] || { echo "Unable to find NzbWebDAV; set BACKEND_PID." >&2; exit 1; }
[[ -d "/proc/$BACKEND_PID" ]] || { echo "BACKEND_PID is not visible in this PID namespace." >&2; exit 1; }

BACKEND_CGROUP_PATH="$(awk -F: '$1 == "0" { print $3; exit }' "/proc/$BACKEND_PID/cgroup")"
[[ -n "$BACKEND_CGROUP_PATH" && "$BACKEND_CGROUP_PATH" != *".."* ]] ||
  { echo "Unable to resolve the backend cgroup v2 path." >&2; exit 1; }
BACKEND_CGROUP_DIR="/sys/fs/cgroup${BACKEND_CGROUP_PATH}"
[[ -d "$BACKEND_CGROUP_DIR" ]] ||
  { echo "Backend cgroup directory is not mounted: $BACKEND_CGROUP_DIR" >&2; exit 1; }

copy_if_readable() {
  local source="$1" destination="$2"
  [[ -r "$source" ]] && cp "$source" "$destination" || true
}

snapshot_process() {
  local label="$1" pid="$2"
  [[ -n "$pid" && -r "/proc/$pid/stat" ]] || return 0
  awk '{print $14, $15, $22, $24}' "/proc/$pid/stat" > "$RUN_DIR/${label}-process.txt"
  copy_if_readable "/proc/$pid/status" "$RUN_DIR/${label}-status.txt"
  copy_if_readable "/proc/$pid/smaps_rollup" "$RUN_DIR/${label}-smaps-rollup.txt"
}

snapshot_cgroup() {
  local label="$1" source
  for name in cpu.max cpu.stat memory.current memory.peak memory.events memory.pressure; do
    source="$BACKEND_CGROUP_DIR/$name"
    [[ -r "$source" ]] && cp "$source" "$RUN_DIR/${label}-cgroup-${name//./-}.txt"
  done
}

write_manifest() {
  {
    printf 'run_id=%s\n' "$RUN_ID"
    printf 'git_sha=%s\n' "$(git rev-parse HEAD)"
    printf 'utc=%s\n' "$(date -u +%FT%TZ)"
    printf 'backend_pid=%s\n' "$BACKEND_PID"
    printf 'backend_cgroup=%s\n' "$BACKEND_CGROUP_PATH"
    printf 'node_pid=%s\n' "$NODE_PID"
    printf 'kernel=%s\n' "$(uname -srvmo)"
    printf 'cpu=%s\n' "$(uname -m)"
    printf 'dotnet=%s\n' "$(dotnet --info | awk 'NR==1 {print}')"
    printf 'mode=%s\n' "$MODE"
  } > "$RUN_DIR/manifest.txt"
}

run_curl() {
  local prefix="$1"
  curl --config "$CURL_CONFIG" --fail --silent --show-error \
    --header "Range: bytes=${RANGE_START}-${RANGE_END}" \
    --output "$RUN_DIR/${prefix}-body.bin" \
    --write-out '%{json}\n' "$RANGE_URL" > "$RUN_DIR/${prefix}-curl.json"
  sha256sum "$RUN_DIR/${prefix}-body.bin" > "$RUN_DIR/${prefix}-body.sha256"
  wc -c "$RUN_DIR/${prefix}-body.bin" > "$RUN_DIR/${prefix}-body.bytes"
}

write_manifest
case "$MODE" in
  result)
    snapshot_process result-before "$BACKEND_PID"
    snapshot_process result-before-node "$NODE_PID"
    snapshot_cgroup result-before
    run_curl result
    snapshot_process result-after "$BACKEND_PID"
    snapshot_process result-after-node "$NODE_PID"
    snapshot_cgroup result-after
    ;;
  trace)
    command -v dotnet-trace >/dev/null || { echo "dotnet-trace is required." >&2; exit 127; }
    dotnet-trace collect --process-id "$BACKEND_PID" --profile cpu-sampling \
      --duration 00:01:30 --output "$RUN_DIR/diagnostic-trace.nettrace" &
    TRACE_PID=$!
    sleep 2
    run_curl diagnostic-trace
    wait "$TRACE_PID"
    ;;
  counters)
    command -v dotnet-counters >/dev/null || { echo "dotnet-counters is required." >&2; exit 127; }
    dotnet-counters collect --process-id "$BACKEND_PID" --refresh-interval 1 \
      --duration 00:01:30 --counters System.Runtime,Microsoft.AspNetCore.Hosting \
      --format csv --output "$RUN_DIR/diagnostic-counters.csv" &
    COUNTERS_PID=$!
    sleep 2
    run_curl diagnostic-counters
    wait "$COUNTERS_PID"
    ;;
  perf)
    command -v perf >/dev/null || { echo "perf is required." >&2; exit 127; }
    perf stat --pid "$BACKEND_PID" --timeout 90000 \
      --event task-clock,cycles,instructions,branches,branch-misses,cache-references,cache-misses,context-switches,cpu-migrations,page-faults \
      --output "$RUN_DIR/diagnostic-perf-stat.txt" &
    PERF_STAT_PID=$!
    sleep 2
    run_curl diagnostic-perf
    wait "$PERF_STAT_PID"
    perf record --pid "$BACKEND_PID" --frequency 199 --call-graph dwarf --timeout 90000 \
      --output "$RUN_DIR/diagnostic-perf.data" &
    PERF_RECORD_PID=$!
    sleep 2
    run_curl diagnostic-perf-record
    wait "$PERF_RECORD_PID"
    perf buildid-list --input "$RUN_DIR/diagnostic-perf.data" > "$RUN_DIR/diagnostic-perf-buildids.txt"
    ;;
  *) usage ;;
esac

chmod 0600 "$RUN_DIR"/*
echo "Restricted profiling artifacts written to $RUN_DIR"

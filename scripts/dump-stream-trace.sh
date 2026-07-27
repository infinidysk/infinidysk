#!/usr/bin/env bash
set -euo pipefail

ROOT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
CONFIG_PATH="${CONFIG_PATH:-/tmp/nzbdav-config}"
BACKEND_URL="${BACKEND_URL:-http://localhost:5000}"
API_KEY_FILE="$CONFIG_PATH/.frontend-backend-api-key"
SWAP_DIR="$ROOT_DIR/swap"
TS="$(date -u +%Y%m%dT%H%M%SZ)"

usage() {
  cat <<'EOF'
Dump a correlated stream playback trace (and recent logs) into swap/.

Usage: scripts/dump-stream-trace.sh [session-id]

If session-id is omitted, uses the most recently active traced session.

Environment:
  CONFIG_PATH=/tmp/nzbdav-config
  BACKEND_URL=http://localhost:5000
EOF
}

if [[ "${1:-}" == "-h" || "${1:-}" == "--help" ]]; then
  usage
  exit 0
fi

if [[ ! -f "$API_KEY_FILE" ]]; then
  echo "API key file not found at $API_KEY_FILE (start the backend with scripts/run-backend.sh first)" >&2
  exit 1
fi

API_KEY="$(tr -d '[:space:]' < "$API_KEY_FILE")"
mkdir -p "$SWAP_DIR"

auth_hdr=(-H "x-api-key: $API_KEY")

SESSIONS_JSON="$(curl -fsS "${auth_hdr[@]}" "$BACKEND_URL/api/get-stream-traces?limit=1")"
ENABLED="$(python3 -c 'import json,sys; print(str(json.load(sys.stdin).get("enabled", False)).lower())' <<<"$SESSIONS_JSON")"
if [[ "$ENABLED" != "true" ]]; then
  echo "Stream tracing is disabled." >&2
  echo "Enable it from Settings -> Support (no restart needed), or export STREAM_TRACE_EVENTS=20000 and restart." >&2
  exit 1
fi

SESSION_ID="${1:-}"
if [[ -z "$SESSION_ID" ]]; then
  SESSION_ID="$(python3 -c 'import json,sys; s=json.load(sys.stdin).get("sessions") or []; print(s[0]["sessionId"] if s else "")' <<<"$SESSIONS_JSON")"
  if [[ -z "$SESSION_ID" ]]; then
    echo "No stream traces in memory yet. Play a file (with seeking) first." >&2
    exit 1
  fi
  echo "Using latest session: $SESSION_ID"
fi

OUT_TRACE="$SWAP_DIR/stream-trace-${SESSION_ID}-${TS}.json"
OUT_LOGS="$SWAP_DIR/stream-logs-${SESSION_ID}-${TS}.json"

curl -fsS "${auth_hdr[@]}" "$BACKEND_URL/api/get-stream-trace?sessionId=$SESSION_ID" -o "$OUT_TRACE"
curl -fsS "${auth_hdr[@]}" "$BACKEND_URL/api/get-logs?limit=1000&levels=Debug,Information,Warning,Error,Fatal&search=$SESSION_ID" -o "$OUT_LOGS" \
  || curl -fsS "${auth_hdr[@]}" "$BACKEND_URL/api/get-logs?limit=1000&levels=Warning,Error,Fatal" -o "$OUT_LOGS"

echo "Wrote $OUT_TRACE"
echo "Wrote $OUT_LOGS"

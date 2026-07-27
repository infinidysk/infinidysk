#!/usr/bin/env bash
set -euo pipefail

ROOT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
BACKEND_DIR="$ROOT_DIR/backend"
PUBLISH_DIR="$BACKEND_DIR/publish"
BINARY="$PUBLISH_DIR/NzbWebDAV"

FORCE_BUILD=0
SKIP_BUILD=0
SKIP_MIGRATE=0
MIGRATE_ONLY=0

usage() {
  cat <<'EOF'
Run the NzbDav backend locally for development and testing.

Usage: scripts/run-backend.sh [options]

Options:
  --build          Always rebuild before starting
  --no-build       Skip build even if the published binary is missing
  --no-migrate     Skip database migration
  --migrate-only   Run database migration and exit
  -h, --help       Show this help

Environment (defaults shown):
  CONFIG_PATH=/tmp/nzbdav-config
  BACKEND_URL=http://localhost:5000
  FRONTEND_BACKEND_API_KEY=<persisted in $CONFIG_PATH/.frontend-backend-api-key>
  LOG_LEVEL=Debug
  LOG_BUFFER_SIZE=2000

Stream tracing is off by default; enable it from Settings -> Support, or export
STREAM_TRACE_EVENTS=20000 before starting for an always-on local capture.

frontend/.env is written automatically for npm run dev.
EOF
}

while [[ $# -gt 0 ]]; do
  case "$1" in
    --build)
      FORCE_BUILD=1
      shift
      ;;
    --no-build)
      SKIP_BUILD=1
      shift
      ;;
    --no-migrate)
      SKIP_MIGRATE=1
      shift
      ;;
    --migrate-only)
      MIGRATE_ONLY=1
      shift
      ;;
    -h|--help)
      usage
      exit 0
      ;;
    *)
      echo "Unknown option: $1" >&2
      usage >&2
      exit 1
      ;;
  esac
done

export CONFIG_PATH="${CONFIG_PATH:-/tmp/nzbdav-config}"
export BACKEND_URL="${BACKEND_URL:-http://localhost:5000}"
export ASPNETCORE_URLS="${ASPNETCORE_URLS:-$BACKEND_URL}"
# Local-dev defaults only (Docker/entrypoint leave LOG_LEVEL unset → Information).
export LOG_LEVEL="${LOG_LEVEL:-Debug}"
export LOG_BUFFER_SIZE="${LOG_BUFFER_SIZE:-2000}"
# STREAM_TRACE_EVENTS is deliberately not defaulted: tracing is togglable from
# Settings -> Support, and forcing it on here hid that path during local testing.

API_KEY_FILE="$CONFIG_PATH/.frontend-backend-api-key"
if [[ -z "${FRONTEND_BACKEND_API_KEY:-}" ]]; then
  if [[ -f "$API_KEY_FILE" ]]; then
    export FRONTEND_BACKEND_API_KEY="$(tr -d '[:space:]' < "$API_KEY_FILE")"
  else
    export FRONTEND_BACKEND_API_KEY="$(head -c 32 /dev/urandom | hexdump -ve '1/1 "%.2x"')"
    mkdir -p "$CONFIG_PATH"
    printf '%s\n' "$FRONTEND_BACKEND_API_KEY" > "$API_KEY_FILE"
  fi
fi

mkdir -p "$CONFIG_PATH"

"$ROOT_DIR/scripts/sync-dev-env.sh"

needs_build=0
if [[ ! -x "$BINARY" ]]; then
  needs_build=1
fi

if [[ "$FORCE_BUILD" -eq 1 || "$needs_build" -eq 1 ]]; then
  if [[ "$SKIP_BUILD" -eq 1 ]]; then
    echo "Published backend binary not found at $BINARY (use without --no-build)" >&2
    exit 1
  fi
  echo "Building backend (Release)..."
  (
    cd "$BACKEND_DIR"
    dotnet publish -c Release -o ./publish
  )
fi

run_migration() {
  echo "Running database migration..."
  "$BINARY" --db-migration
}

if [[ "$MIGRATE_ONLY" -eq 1 ]]; then
  run_migration
  exit 0
fi

RESTORE_RESTART_EXIT_CODE=86

while true; do
  if [[ "$SKIP_MIGRATE" -eq 0 ]]; then
    run_migration
  fi

  echo "Starting backend at $BACKEND_URL (config: $CONFIG_PATH)"
  set +e
  "$BINARY"
  EXIT_CODE=$?
  set -e

  if [[ "$EXIT_CODE" -eq "$RESTORE_RESTART_EXIT_CODE" ]]; then
    echo "Backend requested restore restart (exit $RESTORE_RESTART_EXIT_CODE). Re-running migration."
    SKIP_MIGRATE=0
    continue
  fi

  exit "$EXIT_CODE"
done

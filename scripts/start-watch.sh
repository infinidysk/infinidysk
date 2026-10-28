#!/usr/bin/env bash
# Start the localhost-only catalog deployment.
#
# Reads provider credentials from $NEWSDEMON_ENV (default
# ~/Documents/newsdemon.env) and the catalog profile token from .env.watch,
# builds the image, and waits for /healthz. Secret values are never printed.
set -euo pipefail

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
cd "${ROOT}"

NEWSDEMON_ENV="${NEWSDEMON_ENV:-${HOME}/Documents/newsdemon.env}"

for tool in docker openssl curl; do
    if ! command -v "${tool}" >/dev/null 2>&1; then
        echo "error: ${tool} is required but was not found on PATH" >&2
        exit 1
    fi
done
if ! docker compose version >/dev/null 2>&1; then
    echo "error: 'docker compose' is required" >&2
    exit 1
fi

# Exact-string redaction for the one place this script prints log output. awk's
# index() keeps password and key characters free of regex semantics.
redact_secrets() {
    awk -v secrets="$(printf '%s\n' "${USENET_USER:-}" "${USENET_PASS:-}" "${NZBGEEK_KEY:-}" \
        "${TMDB_READ_TOKEN:-}" "${TVDB_API_KEY:-}" "${WATCH_PROFILE_TOKEN:-}")" '
        BEGIN { count = split(secrets, values, "\n") }
        {
            line = $0
            for (i = 1; i <= count; i++) {
                if (values[i] == "") continue
                replaced = ""
                rest = line
                while ((position = index(rest, values[i])) > 0) {
                    replaced = replaced substr(rest, 1, position - 1) "[REDACTED]"
                    rest = substr(rest, position + length(values[i]))
                }
                line = replaced rest
            }
            print line
        }'
}

if [ ! -r "${NEWSDEMON_ENV}" ]; then
    echo "error: credentials file is missing or unreadable: ${NEWSDEMON_ENV}" >&2
    exit 1
fi

# shellcheck disable=SC1090
set -a
. "${NEWSDEMON_ENV}"
set +a

for name in USENET_USER USENET_PASS NZBGEEK_KEY TMDB_READ_TOKEN TVDB_API_KEY; do
    if [ -z "${!name:-}" ]; then
        echo "error: ${name} is empty or unset in ${NEWSDEMON_ENV}" >&2
        exit 1
    fi
done

# One stable profile token; created once and reused on every later start.
umask 077
if [ ! -f .env.watch ]; then
    printf 'WATCH_PROFILE_TOKEN=%s\n' "$(openssl rand -hex 32)" > .env.watch
fi
# shellcheck disable=SC1091
. ./.env.watch
if [ -z "${WATCH_PROFILE_TOKEN:-}" ]; then
    echo "error: .env.watch does not define WATCH_PROFILE_TOKEN" >&2
    exit 1
fi

export PUID="${PUID:-$(id -u)}"
export PGID="${PGID:-$(id -g)}"
export NEWSDEMON_ENV

if [ ! -f libs/rapidyenc/CMakeLists.txt ]; then
    git submodule update --init libs/rapidyenc
fi

mkdir -p config

docker compose up --build -d

health_url="http://127.0.0.1:3000/healthz"
echo "Waiting for ${health_url} (up to 120s)…"
for _ in $(seq 1 120); do
    if curl -fsS "${health_url}" >/dev/null 2>&1; then
        echo "InfiniDysk is up: http://localhost:3000/watch"
        exit 0
    fi
    sleep 1
done

echo "error: the service did not become healthy within 120 seconds" >&2
docker compose ps >&2 || true
docker compose logs --tail 80 infinidysk 2>&1 | redact_secrets >&2 || true
exit 1

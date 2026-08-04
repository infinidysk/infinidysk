#!/bin/sh

set -u

SCRIPT_DIR=$(CDPATH= cd -- "$(dirname -- "$0")" && pwd)
BACKEND_PID=""
FRONTEND_PID=""

wait_either() {
    pid1=$1
    pid2=$2

    while true; do
        if ! kill -0 "$pid1" 2>/dev/null; then
            wait "$pid1"
            exit_code=$?
            EXITED_PID=$pid1
            REMAINING_PID=$pid2
            return "$exit_code"
        fi

        if ! kill -0 "$pid2" 2>/dev/null; then
            wait "$pid2"
            exit_code=$?
            EXITED_PID=$pid2
            REMAINING_PID=$pid1
            return "$exit_code"
        fi

        sleep 0.5
    done
}

terminate() {
    echo "Caught termination signal. Shutting down..."
    if [ -n "$BACKEND_PID" ] && kill -0 "$BACKEND_PID" 2>/dev/null; then
        kill "$BACKEND_PID"
    fi
    if [ -n "$FRONTEND_PID" ] && kill -0 "$FRONTEND_PID" 2>/dev/null; then
        kill "$FRONTEND_PID"
    fi
    wait
    exit 0
}

trap terminate TERM INT

export CONFIG_PATH=${CONFIG_PATH:-"$SCRIPT_DIR/config"}
export BACKEND_URL=${BACKEND_URL:-"http://127.0.0.1:8080"}
export ASPNETCORE_URLS=${ASPNETCORE_URLS:-"$BACKEND_URL"}
export PORT=${PORT:-3000}
export NODE_ENV=production

if [ -z "${FRONTEND_BACKEND_API_KEY:-}" ]; then
    FRONTEND_BACKEND_API_KEY=$(od -An -N32 -tx1 /dev/urandom | tr -d ' \n')
    export FRONTEND_BACKEND_API_KEY
fi

if [ -z "${NZBDAV_VERSION:-}" ] && [ -f "$SCRIPT_DIR/version.txt" ]; then
    NZBDAV_VERSION=$(tr -d '[:space:]' < "$SCRIPT_DIR/version.txt")
    export NZBDAV_VERSION
fi

mkdir -p "$CONFIG_PATH"

cd "$SCRIPT_DIR/frontend"
node dist-node/server.js &
FRONTEND_PID=$!

RESTORE_RESTART_EXIT_CODE=86

while true; do
    cd "$SCRIPT_DIR/backend"
    echo "Running database maintenance."
    ./NzbWebDAV --db-migration
    migration_exit_code=$?
    if [ "$migration_exit_code" -ne 0 ]; then
        echo "Database migration failed. Exiting with error code $migration_exit_code."
        if kill -0 "$FRONTEND_PID" 2>/dev/null; then
            kill "$FRONTEND_PID"
            wait "$FRONTEND_PID" 2>/dev/null
        fi
        exit "$migration_exit_code"
    fi
    echo "Done with database maintenance."

    ./NzbWebDAV &
    BACKEND_PID=$!

    echo "Waiting for backend to start."
    max_retries=${MAX_BACKEND_HEALTH_RETRIES:-30}
    retry_delay=${MAX_BACKEND_HEALTH_RETRY_DELAY:-1}
    attempt=0
    while true; do
        if curl --fail --silent --output /dev/null "$BACKEND_URL/health"; then
            echo "Backend is healthy. InfiniDysk is available at http://localhost:$PORT"
            break
        fi

        attempt=$((attempt + 1))
        if [ "$attempt" -ge "$max_retries" ]; then
            echo "Backend failed its health check after $max_retries attempts."
            kill "$BACKEND_PID"
            wait "$BACKEND_PID" 2>/dev/null
            if kill -0 "$FRONTEND_PID" 2>/dev/null; then
                kill "$FRONTEND_PID"
                wait "$FRONTEND_PID" 2>/dev/null
            fi
            exit 1
        fi

        sleep "$retry_delay"
    done

    wait_either "$BACKEND_PID" "$FRONTEND_PID"
    exit_code=$?

    if [ "$EXITED_PID" -eq "$BACKEND_PID" ] && [ "$exit_code" -eq "$RESTORE_RESTART_EXIT_CODE" ]; then
        echo "Backend requested a maintenance restart."
        BACKEND_PID=""
        continue
    fi

    if [ "$EXITED_PID" -eq "$FRONTEND_PID" ]; then
        echo "The frontend exited with code $exit_code. Shutting down the backend."
    else
        echo "The backend exited with code $exit_code. Shutting down the frontend."
    fi

    kill "$REMAINING_PID" 2>/dev/null
    wait "$REMAINING_PID" 2>/dev/null
    exit "$exit_code"
done

#!/bin/sh

wait_either() {
    local pid1=$1
    local pid2=$2

    while true; do
        if ! kill -0 "$pid1" 2>/dev/null; then
            wait "$pid1"
            local exit_code=$?
            EXITED_PID=$pid1
            REMAINING_PID=$pid2
            return "$exit_code"
        fi

        if ! kill -0 "$pid2" 2>/dev/null; then
            wait "$pid2"
            local exit_code=$?
            EXITED_PID=$pid2
            REMAINING_PID=$pid1
            return "$exit_code"
        fi

        sleep 0.5
    done
}

# Signal handling for graceful shutdown
terminate() {
    echo "Caught termination signal. Shutting down..."
    if [ -n "$BACKEND_PID" ] && kill -0 "$BACKEND_PID" 2>/dev/null; then
        kill "$BACKEND_PID"
    fi
    if [ -n "$FRONTEND_PID" ] && kill -0 "$FRONTEND_PID" 2>/dev/null; then
        kill "$FRONTEND_PID"
    fi
    # Wait for children to exit
    wait
    exit 0
}
trap terminate TERM INT

# Use env vars or default to 1000
PUID=${PUID:-1000}
PGID=${PGID:-1000}

# Create or reuse group based on PGID
if getent group "$PGID" >/dev/null; then
    EXISTING_GROUP=$(getent group "$PGID" | cut -d: -f1)
    echo "GID $PGID already exists, using group $EXISTING_GROUP"
    GROUP_NAME="$EXISTING_GROUP"
else
    addgroup -g "$PGID" appgroup
    GROUP_NAME=appgroup
fi

# Create or reuse user based on PUID
if getent passwd "$PUID" >/dev/null; then
    EXISTING_USER=$(getent passwd "$PUID" | cut -d: -f1)
    echo "UID $PUID already exists, using user $EXISTING_USER"
    USER_NAME="$EXISTING_USER"
else
    if ! id appuser >/dev/null 2>&1; then
        adduser -D -H -u "$PUID" -G "$GROUP_NAME" appuser
    fi
    USER_NAME=appuser
fi

# Set environment variables
if [ -z "${BACKEND_URL}" ]; then
    export BACKEND_URL="http://localhost:8080"
fi

if [ -z "${FRONTEND_BACKEND_API_KEY}" ]; then
    export FRONTEND_BACKEND_API_KEY=$(head -c 32 /dev/urandom | hexdump -ve '1/1 "%.2x"')
fi

if [ -z "${CONFIG_PATH}" ]; then
    export CONFIG_PATH="/config"
fi

# Fail before frontend start or migrations when the config path is missing
# or not writable by the runtime user. Ownership repair is best-effort.
# shellcheck disable=SC1091
. /preflight-config-path.sh
preflight_config_path || exit 1

# Repair the small state files used by the frontend and backend without walking
# blobs or backup trees. The session key is mode 0600, so it must be owned by
# the runtime user after an upgrade.
# shellcheck disable=SC1091
. /repair-config-path-ownership.sh
repair_config_path_ownership

# Recursively update permissions when a SQLite database or its WAL files were
# created by a different container user.
OWNERSHIP_MISMATCH=""
for DB_FILE in \
    "$CONFIG_PATH/db.sqlite" \
    "$CONFIG_PATH/db.sqlite-wal" \
    "$CONFIG_PATH/db.sqlite-shm" \
    "$CONFIG_PATH/metrics.sqlite" \
    "$CONFIG_PATH/metrics.sqlite-wal" \
    "$CONFIG_PATH/metrics.sqlite-shm" \
    "$CONFIG_PATH/warden.db" \
    "$CONFIG_PATH/warden.db-wal" \
    "$CONFIG_PATH/warden.db-shm" \
    "$CONFIG_PATH/usenet-migration.db" \
    "$CONFIG_PATH/usenet-migration.db-wal" \
    "$CONFIG_PATH/usenet-migration.db-shm"; do
    if [ ! -e "$DB_FILE" ]; then
        continue
    fi
    DB_UID=$(stat -c '%u' "$DB_FILE")
    DB_GID=$(stat -c '%g' "$DB_FILE")
    if [ "$DB_UID" -ne "$PUID" ] || [ "$DB_GID" -ne "$PGID" ]; then
        echo "$DB_FILE ownership mismatch: (uid:$DB_UID gid:$DB_GID) vs expected (uid:$PUID gid:$PGID)"
        OWNERSHIP_MISMATCH="yes"
        break
    fi
done
if [ -n "$OWNERSHIP_MISMATCH" ]; then
    echo "Updating ownership of $CONFIG_PATH/* to (uid:$PUID gid:$PGID)"
    chown -R "$PUID:$PGID" "$CONFIG_PATH"
fi

# Start the frontend first so it can serve the database-maintenance progress
# page (via the backend's migration status server) while migrations run. The
# migration step below can take a long time on large databases.
cd /app/frontend
su-exec "$USER_NAME" npm run start &
FRONTEND_PID=$!

# Exit code 86 means the backend staged a database restore and needs the
# maintenance phase to run again (swap DBs with WebDAV/SAB offline). Keep the
# frontend alive and loop.
RESTORE_RESTART_EXIT_CODE=86

while true; do
    # Run backend database migration / restore swap
    cd /app/backend
    echo "Running database maintenance."
    su-exec "$USER_NAME" ./NzbWebDAV --db-migration
    MIGRATION_EXIT_CODE=$?
    if [ "$MIGRATION_EXIT_CODE" -ne 0 ]; then
        echo "Database migration failed. Exiting with error code $MIGRATION_EXIT_CODE."
        if [ -n "$FRONTEND_PID" ] && kill -0 "$FRONTEND_PID" 2>/dev/null; then
            kill "$FRONTEND_PID"
            wait "$FRONTEND_PID" 2>/dev/null
        fi
        exit "$MIGRATION_EXIT_CODE"
    fi
    echo "Done with database maintenance."

    # Run backend as "$USER_NAME" in background
    su-exec "$USER_NAME" ./NzbWebDAV &
    BACKEND_PID=$!

    # Wait for backend health check
    echo "Waiting for backend to start."
    MAX_BACKEND_HEALTH_RETRIES=${MAX_BACKEND_HEALTH_RETRIES:-30}
    MAX_BACKEND_HEALTH_RETRY_DELAY=${MAX_BACKEND_HEALTH_RETRY_DELAY:-1}
    i=0
    while true; do
        echo "Checking backend health: $BACKEND_URL/health ..."
        if curl -s -o /dev/null -w "%{http_code}" "$BACKEND_URL/health" | grep -q "^200$"; then
            echo "Backend is healthy."
            break
        fi

        i=$((i+1))
        if [ "$i" -ge "$MAX_BACKEND_HEALTH_RETRIES" ]; then
            echo "Backend failed health check after $MAX_BACKEND_HEALTH_RETRIES retries. Exiting."
            kill "$BACKEND_PID"
            wait "$BACKEND_PID"
            if [ -n "$FRONTEND_PID" ] && kill -0 "$FRONTEND_PID" 2>/dev/null; then
                kill "$FRONTEND_PID"
                wait "$FRONTEND_PID" 2>/dev/null
            fi
            exit 1
        fi

        sleep "$MAX_BACKEND_HEALTH_RETRY_DELAY"
    done

    # Wait for either to exit
    wait_either "$BACKEND_PID" "$FRONTEND_PID"
    EXIT_CODE=$?

    # Staged restore: backend asked to re-enter maintenance. Keep frontend and loop.
    if [ "$EXITED_PID" -eq "$BACKEND_PID" ] && [ "$EXIT_CODE" -eq "$RESTORE_RESTART_EXIT_CODE" ]; then
        echo "Backend requested restore restart (exit $RESTORE_RESTART_EXIT_CODE). Re-running database maintenance."
        BACKEND_PID=""
        continue
    fi

    # Determine which process exited. Exit codes >128 encode a fatal signal
    # (128+N) — log it so native crashes (SIGSEGV/SIGILL) are diagnosable.
    SIGNAL_HINT=""
    if [ "$EXIT_CODE" -gt 128 ] 2>/dev/null; then
        SIG=$((EXIT_CODE - 128))
        case "$SIG" in
            4)  SIGNAL_HINT=" (SIGILL: illegal instruction — broken native library or unsupported CPU)" ;;
            6)  SIGNAL_HINT=" (SIGABRT: abort — native assertion or runtime failure)" ;;
            9)  SIGNAL_HINT=" (SIGKILL: killed — often the OOM killer)" ;;
            11) SIGNAL_HINT=" (SIGSEGV: segmentation fault — native crash)" ;;
            *)  SIGNAL_HINT=" (terminated by signal $SIG)" ;;
        esac
    fi
    if [ "$EXITED_PID" -eq "$FRONTEND_PID" ]; then
        echo "The web-frontend has exited with code $EXIT_CODE$SIGNAL_HINT. Shutting down the web-backend..."
    else
        echo "The web-backend has exited with code $EXIT_CODE$SIGNAL_HINT. Shutting down the web-frontend..."
    fi

    # Kill the remaining process
    kill "$REMAINING_PID"
    wait "$REMAINING_PID" 2>/dev/null

    # Exit with the code of the process that died first
    exit $EXIT_CODE
done

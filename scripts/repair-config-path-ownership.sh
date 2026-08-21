#!/bin/sh
# CONFIG_PATH ownership repair used by the container entrypoint.
# Requires CONFIG_PATH, PUID, and PGID in the environment.

repair_config_path_ownership() {
    chown "$PUID:$PGID" "$CONFIG_PATH"

    for STATE_FILE in \
        "$CONFIG_PATH/session.key" \
        "$CONFIG_PATH/pending-restore.json" \
        "$CONFIG_PATH/db.sqlite" \
        "$CONFIG_PATH/db.sqlite-wal" \
        "$CONFIG_PATH/db.sqlite-shm" \
        "$CONFIG_PATH/db.sqlite.maintenance.lock" \
        "$CONFIG_PATH/metrics.sqlite" \
        "$CONFIG_PATH/metrics.sqlite-wal" \
        "$CONFIG_PATH/metrics.sqlite-shm" \
        "$CONFIG_PATH/warden.db" \
        "$CONFIG_PATH/warden.db-wal" \
        "$CONFIG_PATH/warden.db-shm" \
        "$CONFIG_PATH/usenet-migration.db" \
        "$CONFIG_PATH/usenet-migration.db-wal" \
        "$CONFIG_PATH/usenet-migration.db-shm"; do
        [ -e "$STATE_FILE" ] || continue
        chown "$PUID:$PGID" "$STATE_FILE"
    done

    if [ -d "$CONFIG_PATH/data-protection" ]; then
        chown -R "$PUID:$PGID" "$CONFIG_PATH/data-protection"
    fi
}

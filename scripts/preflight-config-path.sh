#!/bin/sh
# CONFIG_PATH preflight used by the container entrypoint.
# Requires CONFIG_PATH, PUID, PGID, and USER_NAME in the environment.
# chown / su-exec may be stubbed via PATH in tests.

preflight_config_path() {
    if [ -z "$CONFIG_PATH" ]; then
        echo "Fatal: CONFIG_PATH is not set." >&2
        return 1
    fi

    if [ -e "$CONFIG_PATH" ] && [ ! -d "$CONFIG_PATH" ]; then
        echo "Fatal: CONFIG_PATH '$CONFIG_PATH' exists but is not a directory." >&2
        echo "Mount a persistent directory at '$CONFIG_PATH' (default /config) and grant PUID=${PUID:-?} PGID=${PGID:-?} read/write access." >&2
        return 1
    fi

    if [ ! -d "$CONFIG_PATH" ]; then
        echo "Fatal: CONFIG_PATH '$CONFIG_PATH' is not an existing directory." >&2
        echo "Mount a persistent directory at '$CONFIG_PATH' (default /config) and grant PUID=${PUID:-?} PGID=${PGID:-?} read/write access." >&2
        echo "The image contains an empty default /config; a path existing inside the container is not proof a host volume is mounted." >&2
        return 1
    fi

    if ! chown "$PUID:$PGID" "$CONFIG_PATH"; then
        echo "Warning: could not adjust ownership of '$CONFIG_PATH'; checking effective access." >&2
    fi

    if ! su-exec "$USER_NAME" sh -c 'umask 077; probe=$(mktemp "$1/.config-path-probe.XXXXXX") && rm -f "$probe"' sh "$CONFIG_PATH"; then
        echo "Fatal: CONFIG_PATH '$CONFIG_PATH' is not writable by PUID=$PUID PGID=$PGID." >&2
        echo "Verify the persistent mount at '$CONFIG_PATH' and host permissions." >&2
        return 1
    fi

    return 0
}

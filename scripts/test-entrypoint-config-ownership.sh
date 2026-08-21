#!/bin/sh
# Fixture checks for scripts/repair-config-path-ownership.sh
set -eu

ROOT="$(CDPATH= cd -- "$(dirname "$0")/.." && pwd)"
REPAIR="$ROOT/scripts/repair-config-path-ownership.sh"
STUBS="$(mktemp -d "${TMPDIR:-/tmp}/nzbdav-ownership-stubs.XXXXXX")"
WORKDIR="$(mktemp -d "${TMPDIR:-/tmp}/nzbdav-ownership-work.XXXXXX")"

cleanup() {
    rm -rf "$STUBS" "$WORKDIR"
}
trap cleanup EXIT INT HUP TERM

cat > "$STUBS/chown" <<'EOF'
#!/bin/sh
printf '%s\n' "$*" >> "$CHOWN_LOG"
EOF
chmod +x "$STUBS/chown"

export PATH="$STUBS:$PATH"
export PUID=1000
export PGID=1000
export CONFIG_PATH="$WORKDIR/config"
export CHOWN_LOG="$WORKDIR/chown.log"
mkdir -p "$CONFIG_PATH"
: > "$CONFIG_PATH/db.sqlite"
: > "$CONFIG_PATH/session.key"

# shellcheck disable=SC1090
. "$REPAIR"
repair_config_path_ownership

if grep -Fqx "1000:1000 $CONFIG_PATH/session.key" "$CHOWN_LOG"; then
    echo "ok - repairs session key ownership when databases already exist"
else
    echo "not ok - session key ownership was not repaired" >&2
    exit 1
fi

#!/bin/sh
# Fixture checks for scripts/preflight-config-path.sh
set -eu

ROOT="$(CDPATH= cd -- "$(dirname "$0")/.." && pwd)"
PREFLIGHT="$ROOT/scripts/preflight-config-path.sh"
STUBS="$(mktemp -d "${TMPDIR:-/tmp}/nzbdav-preflight-stubs.XXXXXX")"
WORKDIR="$(mktemp -d "${TMPDIR:-/tmp}/nzbdav-preflight-work.XXXXXX")"
FAILED=0

cleanup() {
    rm -rf "$STUBS" "$WORKDIR"
}
trap cleanup EXIT INT HUP TERM

write_stub() {
    name=$1
    body=$2
    path="$STUBS/$name"
    printf '%s\n' "#!/bin/sh" "$body" > "$path"
    chmod +x "$path"
}

pass() {
    printf 'ok - %s\n' "$1"
}

fail() {
    printf 'not ok - %s\n' "$1" >&2
    FAILED=1
}

# Ignore the requested user and run the remaining command as the test user.
write_stub su-exec 'shift; exec "$@"'
write_stub chown 'exit 0'

export PATH="$STUBS:$PATH"
# shellcheck disable=SC1090
. "$PREFLIGHT"

export PUID=1000
export PGID=1000
export USER_NAME=appuser

missing="$WORKDIR/missing"
if CONFIG_PATH="$missing" preflight_config_path >/dev/null 2>"$WORKDIR/missing.err"; then
    fail "missing path should fail"
else
    grep -q "not an existing directory" "$WORKDIR/missing.err" || fail "missing path message"
    [ ! -e "$missing" ] || fail "missing path must not be created"
    pass "missing path"
fi

regular="$WORKDIR/regular-file"
: > "$regular"
if CONFIG_PATH="$regular" preflight_config_path >/dev/null 2>"$WORKDIR/regular.err"; then
    fail "regular file should fail"
else
    grep -q "not a directory" "$WORKDIR/regular.err" || fail "regular file message"
    pass "regular file"
fi

readonly_dir="$WORKDIR/readonly"
mkdir "$readonly_dir"
chmod a-w "$readonly_dir"
if ( : > "$readonly_dir/.write-check" ) 2>/dev/null; then
    rm -f "$readonly_dir/.write-check"
    chmod u+w "$readonly_dir"
    pass "read-only directory skipped (running as root)"
else
    if CONFIG_PATH="$readonly_dir" preflight_config_path >/dev/null 2>"$WORKDIR/readonly.err"; then
        fail "read-only directory should fail"
    else
        grep -q "not writable" "$WORKDIR/readonly.err" || fail "read-only directory message"
        pass "read-only directory"
    fi
    chmod u+w "$readonly_dir"
fi

write_stub chown 'echo "chown invoked" >&2; exit 1'
# shellcheck disable=SC1090
. "$PREFLIGHT"
writable="$WORKDIR/writable-after-chown-fail"
mkdir "$writable"
if CONFIG_PATH="$writable" preflight_config_path >"$WORKDIR/chown.out" 2>"$WORKDIR/chown.err"; then
    grep -q "could not adjust ownership" "$WORKDIR/chown.err" || fail "failed chown warning"
    pass "failed chown with effective write access"
else
    fail "failed chown should still pass when the directory is writable"
fi

write_stub chown 'exit 0'
# shellcheck disable=SC1090
. "$PREFLIGHT"
success="$WORKDIR/success"
mkdir "$success"
if CONFIG_PATH="$success" preflight_config_path >/dev/null 2>"$WORKDIR/success.err"; then
    leaked=0
    for probe in "$success"/.config-path-probe*; do
        [ -e "$probe" ] || continue
        leaked=1
    done
    [ "$leaked" -eq 0 ] || fail "probe file leaked"
    pass "writable directory"
else
    fail "writable directory should pass"
fi

if [ "$FAILED" -ne 0 ]; then
    echo "CONFIG_PATH preflight fixtures failed." >&2
    exit 1
fi

echo "CONFIG_PATH preflight fixtures passed."

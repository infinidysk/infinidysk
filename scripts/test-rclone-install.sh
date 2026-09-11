#!/bin/sh
# Fixture checks for scripts/install-rclone.sh
set -eu

ROOT="$(CDPATH= cd -- "$(dirname "$0")/.." && pwd)"
INSTALL="$ROOT/scripts/install-rclone.sh"
STUBS="$(mktemp -d "${TMPDIR:-/tmp}/nzbdav-rclone-stubs.XXXXXX")"
WORKDIR="$(mktemp -d "${TMPDIR:-/tmp}/nzbdav-rclone-work.XXXXXX")"
FAILED=0

cleanup() {
    rm -rf "$STUBS" "$WORKDIR"
}
trap cleanup EXIT INT HUP TERM

pass() {
    printf 'ok - %s\n' "$1"
}

fail() {
    printf 'not ok - %s\n' "$1" >&2
    FAILED=1
}

# The stub curl serves files out of $FIXTURES keyed by the requested URL path,
# so a case can control exactly which assets the release directory appears to
# publish. A missing fixture reproduces curl's --fail behavior (exit 22).
cat > "$STUBS/curl" <<'STUB'
#!/bin/sh
output=""
url=""
while [ $# -gt 0 ]; do
    case "$1" in
        -o) output="$2"; shift 2 ;;
        -*) shift ;;
        *) url="$1"; shift ;;
    esac
done
case "$url" in
    "$KEY_URL")
        if [ -n "$output" ]; then cat "$KEY_FILE" > "$output"; else cat "$KEY_FILE"; fi
        exit 0
        ;;
esac
path="${url#https://downloads.rclone.org/}"
fixture="$FIXTURES/$(printf '%s' "$path" | tr '/' '_')"
if [ ! -f "$fixture" ]; then
    echo "curl: (22) The requested URL returned error: 404" >&2
    exit 22
fi
if [ -n "$output" ]; then
    cat "$fixture" > "$output"
else
    cat "$fixture"
fi
STUB

# The real archive layout is rclone-<version>-linux-<arch>/rclone, and the
# install script extracts it with `unzip -qj`. The stub reproduces that flat
# extraction without needing a real zip file.
cat > "$STUBS/unzip" <<'STUB'
#!/bin/sh
archive=""
dest="."
while [ $# -gt 0 ]; do
    case "$1" in
        -d) dest="$2"; shift 2 ;;
        -*) shift ;;
        *) [ -z "$archive" ] && archive="$1"; shift ;;
    esac
done
mkdir -p "$dest"
cp "$archive" "$dest/rclone"
STUB

chmod +x "$STUBS/curl" "$STUBS/unzip"
export PATH="$STUBS:$PATH"

# The installer verifies that SHA256SUMS is signed by rclone's release key. The
# fixtures cannot use the real key, so a throwaway one is generated here and the
# installer is pointed at it through its documented test seams. Everything the
# installer does with the signature is exercised for real.
SIGNING_HOME="$WORKDIR/signing-gnupg"
mkdir -p "$SIGNING_HOME"
chmod 700 "$SIGNING_HOME"
KEY_URL="https://fixtures.invalid/pgp-key.txt"
KEY_FILE="$WORKDIR/signing-key.asc"
export KEY_URL KEY_FILE

if ! GNUPGHOME="$SIGNING_HOME" gpg --batch --quiet --passphrase '' \
    --quick-generate-key "InfiniDysk Fixture <fixture@example.invalid>" default default never \
    >/dev/null 2>&1; then
    echo "gpg is required to run the rclone install fixture checks" >&2
    exit 1
fi

SIGNING_FINGERPRINT="$(GNUPGHOME="$SIGNING_HOME" gpg --batch --with-colons --list-secret-keys \
    | awk -F: '/^fpr:/ { print $10; exit }')"
GNUPGHOME="$SIGNING_HOME" gpg --batch --quiet --armor --export "$SIGNING_FINGERPRINT" > "$KEY_FILE"

export RCLONE_KEY_URL="$KEY_URL"
export RCLONE_KEY_FINGERPRINT="$SIGNING_FINGERPRINT"

# Replaces a fixture's SHA256SUMS with a clearsigned copy of itself.
sign_sums() {
    _sums="$1"
    GNUPGHOME="$SIGNING_HOME" gpg --batch --quiet --yes --passphrase '' \
        --clearsign --output "$_sums.signed" "$_sums"
    mv "$_sums.signed" "$_sums"
}

# Build a release directory fixture: a fake zip payload plus the SHA256SUMS
# file that lists its real checksum.
make_release() {
    version=$1
    arch=$2
    FIXTURES="$WORKDIR/fixtures-$version-$arch"
    mkdir -p "$FIXTURES"
    printf 'rclone %s\n' "$version" > "$FIXTURES/version.txt"

    asset="rclone-$version-linux-$arch.zip"
    printf '#!/bin/sh\necho "rclone %s"\n' "$version" > "$FIXTURES/${version}_${asset}"
    sum=$(sha256sum "$FIXTURES/${version}_${asset}" | cut -d' ' -f1)
    {
        printf '%s  rclone-%s-osx-amd64.zip\n' "0000000000000000000000000000000000000000000000000000000000000000" "$version"
        printf '%s  %s\n' "$sum" "$asset"
    } > "$FIXTURES/${version}_SHA256SUMS"
    sign_sums "$FIXTURES/${version}_SHA256SUMS"
    export FIXTURES
}

# --- latest version is resolved from version.txt ---
make_release v1.75.1 amd64
dest="$WORKDIR/dest-latest"
if TARGETARCH=amd64 DEST="$dest" sh "$INSTALL" >"$WORKDIR/latest.out" 2>&1; then
    if [ -x "$dest/rclone" ]; then
        pass "latest release installs an executable"
    else
        fail "latest release should install an executable at $dest/rclone"
    fi
    grep -q "v1.75.1" "$WORKDIR/latest.out" || fail "latest release should report the resolved version"
    pass "latest version resolved from version.txt"
else
    fail "latest release install should succeed: $(cat "$WORKDIR/latest.out")"
fi

# --- an explicit RCLONE_VERSION pins the download and skips version.txt ---
make_release v1.70.3 amd64
rm -f "$FIXTURES/version.txt"
dest="$WORKDIR/dest-pinned"
if RCLONE_VERSION=v1.70.3 TARGETARCH=amd64 DEST="$dest" sh "$INSTALL" >"$WORKDIR/pinned.out" 2>&1; then
    grep -q "v1.70.3" "$WORKDIR/pinned.out" || fail "pinned install should report the pinned version"
    pass "explicit RCLONE_VERSION does not consult version.txt"
else
    fail "pinned install should succeed without version.txt: $(cat "$WORKDIR/pinned.out")"
fi

# --- a bare version number is accepted and normalized to a v-prefixed tag ---
make_release v1.70.3 amd64
dest="$WORKDIR/dest-bare"
if RCLONE_VERSION=1.70.3 TARGETARCH=amd64 DEST="$dest" sh "$INSTALL" >"$WORKDIR/bare.out" 2>&1; then
    pass "bare version number is normalized"
else
    fail "bare version number should be accepted: $(cat "$WORKDIR/bare.out")"
fi

# --- arm64 maps to the arm64 asset ---
make_release v1.75.1 arm64
dest="$WORKDIR/dest-arm64"
if TARGETARCH=arm64 DEST="$dest" sh "$INSTALL" >"$WORKDIR/arm64.out" 2>&1; then
    pass "arm64 downloads the arm64 asset"
else
    fail "arm64 install should succeed: $(cat "$WORKDIR/arm64.out")"
fi

# --- an unknown architecture fails with an actionable message ---
make_release v1.75.1 amd64
dest="$WORKDIR/dest-badarch"
if TARGETARCH=s390x DEST="$dest" sh "$INSTALL" >"$WORKDIR/badarch.out" 2>&1; then
    fail "unsupported architecture should fail"
else
    grep -qi "s390x" "$WORKDIR/badarch.out" || fail "unsupported architecture message should name the architecture"
    [ ! -e "$dest/rclone" ] || fail "unsupported architecture must not install a binary"
    pass "unsupported architecture rejected"
fi

# --- a checksum mismatch aborts before installing ---
make_release v1.75.1 amd64
printf '%s  rclone-v1.75.1-linux-amd64.zip\n' \
    "1111111111111111111111111111111111111111111111111111111111111111" \
    > "$FIXTURES/v1.75.1_SHA256SUMS"
sign_sums "$FIXTURES/v1.75.1_SHA256SUMS"
dest="$WORKDIR/dest-badsum"
if TARGETARCH=amd64 DEST="$dest" sh "$INSTALL" >"$WORKDIR/badsum.out" 2>&1; then
    fail "checksum mismatch should fail"
else
    [ ! -e "$dest/rclone" ] || fail "checksum mismatch must not install a binary"
    pass "checksum mismatch rejected"
fi

# --- a release that does not publish our asset fails cleanly ---
make_release v1.75.1 amd64
rm -f "$FIXTURES/v1.75.1_rclone-v1.75.1-linux-amd64.zip"
dest="$WORKDIR/dest-missing"
if TARGETARCH=amd64 DEST="$dest" sh "$INSTALL" >"$WORKDIR/missing.out" 2>&1; then
    fail "missing release asset should fail"
else
    [ ! -e "$dest/rclone" ] || fail "missing release asset must not install a binary"
    pass "missing release asset rejected"
fi

# --- the checksum file must actually list our asset ---
make_release v1.75.1 amd64
printf '%s  rclone-v1.75.1-osx-amd64.zip\n' \
    "2222222222222222222222222222222222222222222222222222222222222222" \
    > "$FIXTURES/v1.75.1_SHA256SUMS"
sign_sums "$FIXTURES/v1.75.1_SHA256SUMS"
dest="$WORKDIR/dest-nosum"
if TARGETARCH=amd64 DEST="$dest" sh "$INSTALL" >"$WORKDIR/nosum.out" 2>&1; then
    fail "an asset absent from SHA256SUMS should fail"
else
    [ ! -e "$dest/rclone" ] || fail "an asset absent from SHA256SUMS must not install a binary"
    pass "asset absent from SHA256SUMS rejected"
fi

# --- an unsigned SHA256SUMS is refused ---
make_release v1.75.1 amd64
asset="rclone-v1.75.1-linux-amd64.zip"
sum=$(sha256sum "$FIXTURES/v1.75.1_$asset" | cut -d' ' -f1)
printf '%s  %s\n' "$sum" "$asset" > "$FIXTURES/v1.75.1_SHA256SUMS"
dest="$WORKDIR/dest-unsigned"
if TARGETARCH=amd64 DEST="$dest" sh "$INSTALL" >"$WORKDIR/unsigned.out" 2>&1; then
    fail "an unsigned SHA256SUMS should fail"
else
    grep -qi "signature" "$WORKDIR/unsigned.out" || fail "unsigned SHA256SUMS should mention the signature"
    [ ! -e "$dest/rclone" ] || fail "an unsigned SHA256SUMS must not install a binary"
    pass "unsigned SHA256SUMS rejected"
fi

# --- a checksum appended after the signature block is refused ---
# The signature stays valid and the signer is right: only the appended line is
# unsigned. Verifying the file and then reading the file back would select it.
make_release v1.75.1 amd64
asset="rclone-v1.75.1-linux-amd64.zip"
printf '%s  rclone-v1.75.1-osx-amd64.zip\n' \
    "2222222222222222222222222222222222222222222222222222222222222222" \
    > "$FIXTURES/v1.75.1_SHA256SUMS"
sign_sums "$FIXTURES/v1.75.1_SHA256SUMS"
printf '%s  %s\n' \
    "$(sha256sum "$FIXTURES/v1.75.1_$asset" | cut -d' ' -f1)" "$asset" \
    >> "$FIXTURES/v1.75.1_SHA256SUMS"
dest="$WORKDIR/dest-appended"
if TARGETARCH=amd64 DEST="$dest" sh "$INSTALL" >"$WORKDIR/appended.out" 2>&1; then
    fail "a checksum appended after the signature should fail"
else
    [ ! -e "$dest/rclone" ] || fail "an appended checksum must not install a binary"
    pass "checksum appended after the signature rejected"
fi

# --- a duplicate entry in the signed list is refused ---
make_release v1.75.1 amd64
asset="rclone-v1.75.1-linux-amd64.zip"
sum=$(sha256sum "$FIXTURES/v1.75.1_$asset" | cut -d' ' -f1)
{
    printf '%s  %s\n' "$sum" "$asset"
    printf '%s  %s\n' "3333333333333333333333333333333333333333333333333333333333333333" "$asset"
} > "$FIXTURES/v1.75.1_SHA256SUMS"
sign_sums "$FIXTURES/v1.75.1_SHA256SUMS"
dest="$WORKDIR/dest-dupe"
if TARGETARCH=amd64 DEST="$dest" sh "$INSTALL" >"$WORKDIR/dupe.out" 2>&1; then
    fail "a duplicated asset entry should fail"
else
    [ ! -e "$dest/rclone" ] || fail "a duplicated asset entry must not install a binary"
    pass "duplicated asset entry rejected"
fi

# --- a SHA256SUMS signed by the wrong key is refused ---
make_release v1.75.1 amd64
OTHER_HOME="$WORKDIR/other-gnupg"
mkdir -p "$OTHER_HOME"
chmod 700 "$OTHER_HOME"
GNUPGHOME="$OTHER_HOME" gpg --batch --quiet --passphrase '' \
    --quick-generate-key "Impostor <impostor@example.invalid>" default default never >/dev/null 2>&1
asset="rclone-v1.75.1-linux-amd64.zip"
sum=$(sha256sum "$FIXTURES/v1.75.1_$asset" | cut -d' ' -f1)
printf '%s  %s\n' "$sum" "$asset" > "$WORKDIR/impostor-sums"
GNUPGHOME="$OTHER_HOME" gpg --batch --quiet --yes --passphrase '' \
    --clearsign --output "$FIXTURES/v1.75.1_SHA256SUMS" "$WORKDIR/impostor-sums"
dest="$WORKDIR/dest-wrongkey"
if TARGETARCH=amd64 DEST="$dest" sh "$INSTALL" >"$WORKDIR/wrongkey.out" 2>&1; then
    fail "a SHA256SUMS signed by another key should fail"
else
    [ ! -e "$dest/rclone" ] || fail "a wrongly signed SHA256SUMS must not install a binary"
    pass "SHA256SUMS signed by the wrong key rejected"
fi

# --- a release older than the supported minimum is refused ---
make_release v1.68.2 amd64
dest="$WORKDIR/dest-old"
if RCLONE_VERSION=v1.68.2 TARGETARCH=amd64 DEST="$dest" sh "$INSTALL" >"$WORKDIR/old.out" 2>&1; then
    fail "a release below the minimum should fail"
else
    grep -q "1.70.3" "$WORKDIR/old.out" || fail "the minimum-version message should name the minimum"
    [ ! -e "$dest/rclone" ] || fail "a release below the minimum must not install a binary"
    pass "release below the supported minimum rejected"
fi

# --- the boundary release is accepted ---
make_release v1.70.3 amd64
dest="$WORKDIR/dest-minimum"
if RCLONE_VERSION=v1.70.3 TARGETARCH=amd64 DEST="$dest" sh "$INSTALL" >"$WORKDIR/minimum.out" 2>&1; then
    pass "the minimum supported release is accepted"
else
    fail "the minimum supported release should install: $(cat "$WORKDIR/minimum.out")"
fi

if [ "$FAILED" -ne 0 ]; then
    echo "rclone install fixture checks failed" >&2
    exit 1
fi

echo "All rclone install fixture checks passed."

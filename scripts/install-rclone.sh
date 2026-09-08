#!/bin/sh
# Install the rclone binary used by the built-in mount feature.
#
# The version is pinned by the Dockerfile so an image rebuild ships the rclone
# that was reviewed, not whatever happens to be newest that day. Leaving
# RCLONE_VERSION empty falls back to the latest published release, which is
# convenient for local builds but deliberately not what CI does.
#
# Whichever version is selected, its published SHA256SUMS is verified against
# rclone's release signing key before any checksum in it is trusted, and the
# downloaded archive is then checked against that checksum. A tampered mirror,
# a corrupted download, and a truncated one all fail the build rather than
# shipping.
#
# Environment:
#   RCLONE_VERSION  Release tag to install. Empty or unset means latest.
#   TARGETARCH      Docker-style target architecture (amd64, arm64, arm, 386).
#                   Defaults to the build host's architecture.
#   DEST            Directory to install the binary into. Defaults to
#                   /usr/local/bin.
#   RCLONE_KEY_URL, RCLONE_KEY_FINGERPRINT
#                   Test seams. Production leaves these unset so the pinned
#                   rclone release key below is the only one accepted.
set -eu

BASE_URL="https://downloads.rclone.org"
DEST="${DEST:-/usr/local/bin}"

# rclone's release signing key. Pinned by fingerprint so that fetching the key
# over the network cannot substitute a different one.
# https://rclone.org/release_signing/
RCLONE_KEY_URL="${RCLONE_KEY_URL:-https://www.craig-wood.com/nick/pub/pgp-key.txt}"
RCLONE_KEY_FINGERPRINT="${RCLONE_KEY_FINGERPRINT:-FBF737ECE9F8AB18604BD2AC93935E02FF3B54FA}"

# --links, which the built-in mount relies on to resolve imported symlinks,
# only behaves correctly from this release onwards.
MINIMUM_VERSION="1.70.3"

host_arch() {
    case "$(uname -m)" in
        x86_64) echo amd64 ;;
        aarch64 | arm64) echo arm64 ;;
        armv7l) echo arm ;;
        i386 | i686) echo 386 ;;
        *) uname -m ;;
    esac
}

# Compares dotted versions without depending on `sort -V`, which busybox does
# not always provide. Prints "older" when $1 < $2.
version_is_older() {
    _left="$1"
    _right="$2"
    _index=1
    while [ "$_index" -le 3 ]; do
        _l="$(echo "$_left" | cut -d. -f"$_index")"
        _r="$(echo "$_right" | cut -d. -f"$_index")"
        [ -n "$_l" ] || _l=0
        [ -n "$_r" ] || _r=0
        # Anything non-numeric (a release candidate suffix, say) sorts as 0.
        case "$_l" in *[!0-9]*) _l="$(echo "$_l" | tr -dc '0-9')"; [ -n "$_l" ] || _l=0 ;; esac
        case "$_r" in *[!0-9]*) _r="$(echo "$_r" | tr -dc '0-9')"; [ -n "$_r" ] || _r=0 ;; esac
        if [ "$_l" -lt "$_r" ]; then echo older; return 0; fi
        if [ "$_l" -gt "$_r" ]; then return 0; fi
        _index=$((_index + 1))
    done
}

TARGETARCH="${TARGETARCH:-$(host_arch)}"

# Map the Docker platform name onto the name rclone uses in its release assets.
case "$TARGETARCH" in
    amd64) asset_arch=amd64 ;;
    arm64) asset_arch=arm64 ;;
    arm) asset_arch=arm-v7 ;;
    386) asset_arch=386 ;;
    *)
        echo "install-rclone: unsupported architecture '$TARGETARCH'." >&2
        echo "install-rclone: supported architectures are amd64, arm64, arm, 386." >&2
        exit 1
        ;;
esac

version="${RCLONE_VERSION:-}"
if [ -z "$version" ]; then
    # version.txt holds a single line of the form "rclone v1.75.1".
    version="$(curl -fsSL "$BASE_URL/version.txt" | awk '{print $2}')"
    if [ -z "$version" ]; then
        echo "install-rclone: could not resolve the latest version from $BASE_URL/version.txt." >&2
        exit 1
    fi
    echo "install-rclone: RCLONE_VERSION is unset; resolved the latest release ($version)." >&2
    echo "install-rclone: pin RCLONE_VERSION for a reproducible image." >&2
fi

# Accept a bare version number so callers do not have to remember the v prefix.
case "$version" in
    v*) ;;
    *) version="v$version" ;;
esac

bare_version="${version#v}"
if [ "$(version_is_older "$bare_version" "$MINIMUM_VERSION")" = "older" ]; then
    echo "install-rclone: rclone $version is older than the required $MINIMUM_VERSION." >&2
    echo "install-rclone: the built-in mount needs --links to resolve imported symlinks." >&2
    exit 1
fi

asset="rclone-$version-linux-$asset_arch.zip"
workdir="$(mktemp -d "${TMPDIR:-/tmp}/install-rclone.XXXXXX")"
cleanup() {
    rm -rf "$workdir"
}
trap cleanup EXIT INT HUP TERM

echo "install-rclone: installing rclone $version ($asset_arch) into $DEST"

curl -fsSL -o "$workdir/$asset" "$BASE_URL/$version/$asset"
curl -fsSL -o "$workdir/SHA256SUMS" "$BASE_URL/$version/SHA256SUMS"
curl -fsSL -o "$workdir/rclone-key.asc" "$RCLONE_KEY_URL"

# Verify the signing key is the one we expect before trusting anything it signed.
GNUPGHOME="$workdir/gnupg"
export GNUPGHOME
mkdir -p "$GNUPGHOME"
chmod 700 "$GNUPGHOME"

if ! gpg --batch --quiet --import "$workdir/rclone-key.asc"; then
    echo "install-rclone: could not import rclone's release signing key." >&2
    exit 1
fi

if ! gpg --batch --with-colons --fingerprint "$RCLONE_KEY_FINGERPRINT" >/dev/null 2>&1; then
    echo "install-rclone: the key fetched from $RCLONE_KEY_URL does not contain the expected" >&2
    echo "install-rclone: fingerprint $RCLONE_KEY_FINGERPRINT." >&2
    exit 1
fi

# SHA256SUMS is a clearsigned message; this proves the checksums below came from
# rclone rather than from whoever served the file.
if ! gpg --batch --status-fd 1 --verify "$workdir/SHA256SUMS" 2>/dev/null \
    | grep -q "^\[GNUPG:\] VALIDSIG .*$RCLONE_KEY_FINGERPRINT"; then
    echo "install-rclone: the signature on SHA256SUMS for $version could not be verified" >&2
    echo "install-rclone: against $RCLONE_KEY_FINGERPRINT." >&2
    exit 1
fi

expected="$(awk -v asset="$asset" '$2 == asset { print $1 }' "$workdir/SHA256SUMS")"
if [ -z "$expected" ]; then
    echo "install-rclone: $asset is not listed in the SHA256SUMS for $version." >&2
    exit 1
fi

actual="$(sha256sum "$workdir/$asset" | cut -d' ' -f1)"
if [ "$actual" != "$expected" ]; then
    echo "install-rclone: checksum mismatch for $asset." >&2
    echo "install-rclone: expected $expected" >&2
    echo "install-rclone: actual   $actual" >&2
    exit 1
fi

# The archive contains rclone-<version>-linux-<arch>/rclone; -j flattens it.
unzip -qj -o "$workdir/$asset" "*/rclone" -d "$workdir/extracted"
if [ ! -f "$workdir/extracted/rclone" ]; then
    echo "install-rclone: $asset did not contain an rclone binary." >&2
    exit 1
fi

mkdir -p "$DEST"
install -m 0755 "$workdir/extracted/rclone" "$DEST/rclone"

echo "install-rclone: installed rclone $version at $DEST/rclone"

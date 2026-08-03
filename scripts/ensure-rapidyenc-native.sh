#!/usr/bin/env bash
# Ensures that the rapidyenc native library exists for one or more runtime IDs.
#
# Prefer building the checked-out submodule. GitHub source archives do not
# contain submodule contents, so source-build consumers such as DUMB fall back
# to the matching published rapidyenc native asset.

set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
ROOT_DIR="$(cd "$SCRIPT_DIR/.." && pwd)"
RAPIDYENC_DIR="$ROOT_DIR/libs/rapidyenc"
OUTPUT_DIR="$ROOT_DIR/libs/RapidYencSharp/runtimes"
RAPIDYENC_NATIVE_VERSION="${RAPIDYENC_NATIVE_VERSION:-v1.2.1}"

error() {
  echo "Error: $*" >&2
}

detect_host_rid() {
  local os arch
  case "$(uname -s)" in
    Darwin) os=osx ;;
    Linux) os=linux ;;
    MINGW*|MSYS*|CYGWIN*) os=win ;;
    *)
      error "unsupported host OS $(uname -s); set TARGET_RIDS to a supported runtime ID."
      exit 1
      ;;
  esac

  case "$(uname -m)" in
    x86_64|amd64) arch=x64 ;;
    arm64|aarch64) arch=arm64 ;;
    *)
      error "unsupported host architecture $(uname -m); set TARGET_RIDS to a supported runtime ID."
      exit 1
      ;;
  esac

  echo "${os}-${arch}"
}

native_name_for_rid() {
  case "$1" in
    linux-x64|linux-arm64|linux-musl-x64|linux-musl-arm64)
      echo "librapidyenc.so"
      ;;
    osx-arm64|osx-x64)
      echo "librapidyenc.dylib"
      ;;
    win-x64)
      echo "rapidyenc.dll"
      ;;
    *)
      return 1
      ;;
  esac
}

download_file() {
  local url="$1"
  local destination="$2"

  if command -v curl >/dev/null 2>&1; then
    curl --fail --location --retry 3 --silent --show-error \
      --output "$destination" "$url"
  elif command -v wget >/dev/null 2>&1; then
    wget --quiet --output-document="$destination" "$url"
  else
    error "cannot download rapidyenc because neither curl nor wget is installed; initialize libs/rapidyenc and install cmake/ninja, or install curl."
    return 1
  fi
}

download_native() {
  local rid="$1"
  local native_name="$2"
  local version="${RAPIDYENC_NATIVE_VERSION#v}"
  local extension="tar.gz"

  case "$rid" in
    linux-x64|linux-arm64|osx-arm64|osx-x64) ;;
    win-x64) extension="zip" ;;
    linux-musl-x64|linux-musl-arm64)
      # Published rapidyenc releases do not include musl assets. Docker builds
      # these from source before dotnet publish.
      error "no published rapidyenc native is available for $rid; initialize libs/rapidyenc and install cmake/ninja."
      return 1
      ;;
    *)
      error "unsupported rapidyenc runtime ID $rid."
      return 1
      ;;
  esac

  local asset="rapidyenc-${version}-${rid}.${extension}"
  local url="https://github.com/nzbdav/rapidyenc/releases/download/${RAPIDYENC_NATIVE_VERSION}/${asset}"
  local temp_dir
  temp_dir="$(mktemp -d)"
  local archive="$temp_dir/$asset"

  echo "Downloading rapidyenc ${RAPIDYENC_NATIVE_VERSION} native for $rid..."
  if ! download_file "$url" "$archive"; then
    rm -rf "$temp_dir"
    error "failed to download $url; initialize libs/rapidyenc and install cmake/ninja, or check network access."
    return 1
  fi

  if [[ "$extension" == "zip" ]]; then
    if ! command -v unzip >/dev/null 2>&1; then
      rm -rf "$temp_dir"
      error "unzip is required to extract the rapidyenc native for $rid."
      return 1
    fi
    if ! unzip -q "$archive" -d "$temp_dir"; then
      rm -rf "$temp_dir"
      error "failed to extract rapidyenc archive $asset."
      return 1
    fi
  elif ! tar -xzf "$archive" -C "$temp_dir"; then
    rm -rf "$temp_dir"
    error "failed to extract rapidyenc archive $asset."
    return 1
  fi

  local extracted="$temp_dir/rapidyenc-${version}-${rid}/lib/$native_name"
  if [[ ! -f "$extracted" ]]; then
    rm -rf "$temp_dir"
    error "downloaded rapidyenc archive $asset did not contain lib/$native_name."
    return 1
  fi

  local destination="$OUTPUT_DIR/$rid/native"
  mkdir -p "$destination"
  cp "$extracted" "$destination/$native_name"
  rm -rf "$temp_dir"
  echo "Installed $native_name to $destination"
}

if [[ $# -gt 0 ]]; then
  TARGET_RIDS=("$@")
elif [[ -n "${TARGET_RIDS:-}" ]]; then
  # shellcheck disable=SC2206
  TARGET_RIDS=($TARGET_RIDS)
else
  TARGET_RIDS=("$(detect_host_rid)")
fi

for rid in "${TARGET_RIDS[@]}"; do
  if ! native_name="$(native_name_for_rid "$rid")"; then
    error "unsupported rapidyenc runtime ID $rid."
    exit 1
  fi

  native_path="$OUTPUT_DIR/$rid/native/$native_name"
  if [[ -f "$native_path" ]]; then
    echo "Using existing rapidyenc native: $native_path"
    continue
  fi

  if [[ -f "$RAPIDYENC_DIR/CMakeLists.txt" ]] \
    && command -v cmake >/dev/null 2>&1 \
    && command -v ninja >/dev/null 2>&1; then
    echo "Building rapidyenc native library for $rid..."
    if "$SCRIPT_DIR/build-rapidyenc.sh" "$rid" && [[ -f "$native_path" ]]; then
      continue
    fi
    echo "Native build failed for $rid; trying the published release asset." >&2
  fi

  if ! download_native "$rid" "$native_name" || [[ ! -f "$native_path" ]]; then
    error "could not provide rapidyenc native for $rid; initialize libs/rapidyenc and install cmake/ninja, or install curl and check network access."
    exit 1
  fi
done

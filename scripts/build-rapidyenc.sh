#!/usr/bin/env bash
# Builds the rapidyenc native library for a given (or host) runtime identifier.
#
# Output: libs/RapidYencSharp/runtimes/<rid>/native/{librapidyenc.so|dylib|dll}
#
# Usage:
#   scripts/build-rapidyenc.sh              # host RID (linux-x64, osx-arm64, …)
#   scripts/build-rapidyenc.sh linux-x64
#   TARGET_RIDS="linux-x64 osx-arm64" scripts/build-rapidyenc.sh

set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
ROOT_DIR="$(cd "$SCRIPT_DIR/.." && pwd)"
RAPIDYENC_DIR="$ROOT_DIR/libs/rapidyenc"
BUILD_ROOT="$ROOT_DIR/libs/rapidyenc/build"
OUTPUT_DIR="$ROOT_DIR/libs/RapidYencSharp/runtimes"

detect_host_rid() {
  local os arch
  case "$(uname -s)" in
    Darwin) os=osx ;;
    Linux)  os=linux ;;
    MINGW*|MSYS*|CYGWIN*) os=win ;;
    *)
      echo "Unsupported host OS: $(uname -s)" >&2
      exit 1
      ;;
  esac
  case "$(uname -m)" in
    x86_64|amd64) arch=x64 ;;
    arm64|aarch64) arch=arm64 ;;
    *)
      echo "Unsupported host arch: $(uname -m)" >&2
      exit 1
      ;;
  esac
  echo "${os}-${arch}"
}

if [[ $# -gt 0 ]]; then
  TARGET_RIDS=("$@")
elif [[ -n "${TARGET_RIDS:-}" ]]; then
  # shellcheck disable=SC2206
  TARGET_RIDS=($TARGET_RIDS)
else
  TARGET_RIDS=("$(detect_host_rid)")
fi

if [[ ! -f "$RAPIDYENC_DIR/CMakeLists.txt" ]]; then
  echo "Error: rapidyenc submodule is not initialized at $RAPIDYENC_DIR" >&2
  echo "Run: git submodule update --init libs/rapidyenc" >&2
  exit 1
fi

if ! command -v cmake >/dev/null 2>&1; then
  echo "Error: cmake is required. On macOS: brew install cmake ninja" >&2
  exit 1
fi

if ! command -v ninja >/dev/null 2>&1; then
  echo "Error: ninja is required. On macOS: brew install cmake ninja" >&2
  exit 1
fi

build_target() {
  local rid="$1"
  local -a lib_names=()
  local -a cmake_args=(-G Ninja -DCMAKE_BUILD_TYPE=Release)

  case "$rid" in
    linux-x64)
      lib_names=("librapidyenc.so")
      ;;
    linux-arm64)
      lib_names=("librapidyenc.so")
      if ! command -v aarch64-linux-gnu-g++ >/dev/null 2>&1; then
        echo "Error: aarch64-linux-gnu-g++ is required to build for linux-arm64." >&2
        return 1
      fi
      cmake_args+=(
        -DCMAKE_SYSTEM_NAME=Linux
        -DCMAKE_SYSTEM_PROCESSOR=aarch64
        -DCMAKE_C_COMPILER=aarch64-linux-gnu-gcc
        -DCMAKE_CXX_COMPILER=aarch64-linux-gnu-g++
      )
      ;;
    linux-musl-x64|linux-musl-arm64)
      lib_names=("librapidyenc.so")
      # Prefer the Dockerfile Alpine stages for musl; this path is for musl hosts.
      ;;
    osx-arm64|osx-x64)
      lib_names=("librapidyenc.dylib")
      ;;
    win-x64)
      lib_names=("rapidyenc.dll" "librapidyenc.dll")
      if ! command -v x86_64-w64-mingw32-g++ >/dev/null 2>&1; then
        echo "Error: x86_64-w64-mingw32-g++ is required to build for win-x64." >&2
        return 1
      fi
      cmake_args+=(
        -DCMAKE_SYSTEM_NAME=Windows
        -DCMAKE_SYSTEM_PROCESSOR=x86_64
        -DCMAKE_C_COMPILER=x86_64-w64-mingw32-gcc
        -DCMAKE_CXX_COMPILER=x86_64-w64-mingw32-g++
        -DCMAKE_RC_COMPILER=x86_64-w64-mingw32-windres
        "-DCMAKE_SHARED_LINKER_FLAGS=-static-libstdc++ -static-libgcc"
      )
      ;;
    *)
      echo "Unsupported runtime identifier: $rid" >&2
      return 1
      ;;
  esac

  local build_dir="$BUILD_ROOT/$rid"
  rm -rf "$build_dir"
  mkdir -p "$build_dir"

  echo "Configuring rapidyenc for $rid..."
  cmake -S "$RAPIDYENC_DIR" -B "$build_dir" "${cmake_args[@]}"

  echo "Building rapidyenc for $rid..."
  cmake --build "$build_dir" --config Release --target rapidyenc_shared

  local lib_path=""
  local candidate
  for candidate in "${lib_names[@]}"; do
    lib_path="$(find "$build_dir" -name "$candidate" -type f | head -n 1 || true)"
    if [[ -n "$lib_path" ]]; then
      break
    fi
  done

  if [[ -z "$lib_path" ]]; then
    echo "Error: Failed to locate expected library (${lib_names[*]}) for $rid" >&2
    return 1
  fi

  local output_path="$OUTPUT_DIR/$rid/native"
  mkdir -p "$output_path"
  local dest_name
  dest_name="$(basename "$lib_path")"
  if [[ "$rid" == win-x64 && "$dest_name" == "librapidyenc.dll" ]]; then
    dest_name="rapidyenc.dll"
  fi
  cp "$lib_path" "$output_path/$dest_name"
  echo "Copied $dest_name to $output_path"
}

for rid in "${TARGET_RIDS[@]}"; do
  build_target "$rid"
done

echo "Native builds complete. Output available in $OUTPUT_DIR."

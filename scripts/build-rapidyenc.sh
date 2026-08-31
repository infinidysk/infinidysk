#!/usr/bin/env bash
# Builds the rapidyenc native library for a given (or host) runtime identifier.
#
# Output: libs/RapidYencSharp/runtimes/<rid>/native/{librapidyenc.so|dylib|dll}
#
# Usage:
#   scripts/build-rapidyenc.sh              # host RID (linux-x64, osx-arm64, …)
#   scripts/build-rapidyenc.sh linux-x64
#   TARGET_RIDS="linux-x64 osx-arm64" scripts/build-rapidyenc.sh
#   scripts/build-rapidyenc.sh --symbols linux-x64
#
# --symbols is a Linux diagnostic mode. It keeps Release optimization, adds GNU
# debug information/build IDs, and writes split symbols plus a build manifest to
# libs/RapidYencSharp/symbols/<rid>. Ordinary runtime output is unchanged.

set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
ROOT_DIR="$(cd "$SCRIPT_DIR/.." && pwd)"
RAPIDYENC_DIR="$ROOT_DIR/libs/rapidyenc"
BUILD_ROOT="$ROOT_DIR/libs/rapidyenc/build"
OUTPUT_DIR="$ROOT_DIR/libs/RapidYencSharp/runtimes"
SYMBOLS_DIR="$ROOT_DIR/libs/RapidYencSharp/symbols"
BUILD_SYMBOLS="${RAPIDYENC_SYMBOLS:-0}"

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

TARGET_ARGS=()
for arg in "$@"; do
  if [[ "$arg" == "--symbols" ]]; then
    BUILD_SYMBOLS=1
  else
    TARGET_ARGS+=("$arg")
  fi
done

if [[ ${#TARGET_ARGS[@]} -gt 0 ]]; then
  TARGET_RIDS=("${TARGET_ARGS[@]}")
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
      case "$(uname -m)" in
        arm64|aarch64)
          # Build natively on ARM runners and hosts.
          ;;
        *)
          if ! command -v aarch64-linux-gnu-g++ >/dev/null 2>&1; then
            echo "Error: aarch64-linux-gnu-g++ is required to cross-build for linux-arm64." >&2
            return 1
          fi
          cmake_args+=(
            -DCMAKE_SYSTEM_NAME=Linux
            -DCMAKE_SYSTEM_PROCESSOR=aarch64
            -DCMAKE_C_COMPILER=aarch64-linux-gnu-gcc
            -DCMAKE_CXX_COMPILER=aarch64-linux-gnu-g++
          )
          ;;
      esac
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

  if [[ "$BUILD_SYMBOLS" == "1" ]]; then
    case "$rid" in
      linux-x64|linux-arm64|linux-musl-x64|linux-musl-arm64)
        for tool in objcopy readelf nm sha256sum; do
          if ! command -v "$tool" >/dev/null 2>&1; then
            echo "Error: $tool is required for --symbols." >&2
            return 1
          fi
        done
        cmake_args+=(
          "-DCMAKE_CXX_FLAGS_RELEASE=-O3 -DNDEBUG -g"
          "-DCMAKE_SHARED_LINKER_FLAGS=-Wl,--build-id=sha1"
        )
        ;;
      *)
        echo "Error: --symbols currently supports Linux runtime identifiers only." >&2
        return 1
        ;;
    esac
  fi

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

  if [[ "$BUILD_SYMBOLS" == "1" ]]; then
    local symbol_output="$SYMBOLS_DIR/$rid"
    local debug_path="$symbol_output/$dest_name.debug"
    local exports
    mkdir -p "$symbol_output"
    objcopy --only-keep-debug "$lib_path" "$debug_path"
    objcopy --add-gnu-debuglink="$(basename "$debug_path")" "$output_path/$dest_name"
    exports="$(nm -D --defined-only "$output_path/$dest_name")"
    [[ "$exports" == *rapidyenc_decode_ex* && "$exports" == *rapidyenc_crc* ]] || {
        echo "Error: expected rapidyenc decoder/CRC exports were not found." >&2
        return 1
      }
    {
      printf '{\n'
      printf '  "rid": "%s",\n' "$rid"
      printf '  "runtimeLibrarySha256": "%s",\n' "$(sha256sum "$output_path/$dest_name" | awk '{print $1}')"
      printf '  "debugLibrarySha256": "%s",\n' "$(sha256sum "$debug_path" | awk '{print $1}')"
      printf '  "compiler": "%s",\n' "$(c++ --version | awk 'NR==1 {print}' | sed 's/"/\\"/g')"
      printf '  "cmakeCacheSha256": "%s",\n' "$(sha256sum "$build_dir/CMakeCache.txt" | awk '{print $1}')"
      printf '  "buildId": "%s"\n' "$(readelf -n "$output_path/$dest_name" | awk '/Build ID:/ {print $3; exit}')"
      printf '}\n'
    } > "$symbol_output/manifest.json"
    echo "Wrote split symbols and manifest to $symbol_output"
  fi
}

for rid in "${TARGET_RIDS[@]}"; do
  build_target "$rid"
done

echo "Native builds complete. Output available in $OUTPUT_DIR."

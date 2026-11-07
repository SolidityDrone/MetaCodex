#!/usr/bin/env bash
# Usage: Native/pty/build.sh [android|host|all]   (default all)
#   android: Assets/Plugins/Android/libs/arm64-v8a/libmcxpty.so + Native/pty/out/android/pty_test (NDK r27c, API 32)
#   host:    Native/pty/out/host/{libmcxpty.so,pty_test} for tests in WSL (gcc)
# NDK path: $ANDROID_NDK, else ~/android-ndk/android-ndk-r27c.
set -euo pipefail
HERE="$(cd "$(dirname "$0")" && pwd)"
ROOT="$(cd "$HERE/../.." && pwd)"
WHAT="${1:-all}"
FLAGS=(-O2 -Wall -Wextra -fvisibility=hidden -fPIC)

if [[ "$WHAT" == android || "$WHAT" == all ]]; then
  NDK="${ANDROID_NDK:-$HOME/android-ndk/android-ndk-r27c}"
  CC="$NDK/toolchains/llvm/prebuilt/linux-x86_64/bin/aarch64-linux-android32-clang"
  LIBDIR="$ROOT/Assets/Plugins/Android/libs/arm64-v8a"
  mkdir -p "$LIBDIR" "$HERE/out/android"
  # 16 KB page alignment: required on 16 KB-page Android devices, harmless elsewhere
  "$CC" "${FLAGS[@]}" -shared -Wl,-z,max-page-size=16384 -Wl,-soname,libmcxpty.so \
        -o "$LIBDIR/libmcxpty.so" "$HERE/mcx_pty.c"
  "$CC" -O2 -Wall -fPIE -pie -o "$HERE/out/android/pty_test" "$HERE/pty_test.c" "$HERE/mcx_pty.c"
  "$NDK/toolchains/llvm/prebuilt/linux-x86_64/bin/llvm-strip" "$LIBDIR/libmcxpty.so"
  echo "android: $(stat -c %s "$LIBDIR/libmcxpty.so") bytes libmcxpty.so"
fi
if [[ "$WHAT" == host || "$WHAT" == all ]]; then
  mkdir -p "$HERE/out/host"
  gcc "${FLAGS[@]}" -shared -o "$HERE/out/host/libmcxpty.so" "$HERE/mcx_pty.c"
  gcc -O2 -Wall -o "$HERE/out/host/pty_test" "$HERE/pty_test.c" "$HERE/mcx_pty.c"
  echo "host: ok"
fi

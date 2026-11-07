#!/bin/bash
# Builds the local AI service (docs/qvac.md) from the versions pinned in qvac/package.json + qvac/bun.lock:
#   unity/Assets/StreamingAssets/qvac/runtime.tar.gz   service JS + QVAC addons (android-arm64, CPU backends only)
#                                                      + libc++_shared.so from NDK r29 (the addons need it)
#   unity/Assets/StreamingAssets/qvac/runtime.json     version and sha256; the app re-extracts when it changes
#   unity/Assets/Plugins/Android/libs/arm64-v8a/libbare.so   the Bare runtime executable (stripped)
# All three are generated and gitignored. Work tree: $QVAC_WORK (default ~/mcxwork/qvac-build), which also keeps
# the unpruned node_modules (with linux-x64 addons) that tools/qvac-dev.sh runs on the PC.
# Usage: tools/qvac-bundle.sh [--no-unity]   (--no-unity: only refresh the work tree)
set -euo pipefail
ROOT=$(cd "$(dirname "$0")/.." && pwd)
WORK=${QVAC_WORK:-$HOME/mcxwork/qvac-build}
BARE_VERSION=1.36.0
NDK_ZIP=https://dl.google.com/android/repository/android-ndk-r29-linux.zip
UNITY_VER=$(sed -n 's/^m_EditorVersion: //p' "$ROOT/unity/ProjectSettings/ProjectVersion.txt")
STRIP="/mnt/c/Program Files/Unity $UNITY_VER/Editor/Data/PlaybackEngines/AndroidPlayer/NDK/toolchains/llvm/prebuilt/windows-x86_64/bin/llvm-strip.exe"
OUT_SA="$ROOT/unity/Assets/StreamingAssets/qvac"
OUT_LIB="$ROOT/unity/Assets/Plugins/Android/libs/arm64-v8a"
say() { echo "[qvac-bundle] $*"; }

mkdir -p "$WORK/pkg"
# 1. install the pinned packages (bun; the lockfile lives in the repo)
cp "$ROOT/qvac/package.json" "$WORK/pkg/"
[ -f "$ROOT/qvac/bun.lock" ] && cp "$ROOT/qvac/bun.lock" "$WORK/pkg/"
(cd "$WORK/pkg" && if [ -f bun.lock ]; then bun install --frozen-lockfile; else bun install; fi) | tail -2
[ -f "$ROOT/qvac/bun.lock" ] || { cp "$WORK/pkg/bun.lock" "$ROOT/qvac/"; say "created qvac/bun.lock (commit it)"; }
rm -rf "$WORK/pkg/server" && cp -r "$ROOT/qvac/server" "$WORK/pkg/server"

# 2. the Bare runtimes (npm tarballs; bun skips packages whose os/cpu do not match the PC)
for plat in android-arm64 linux-x64; do
  if [ ! -x "$WORK/bare-$plat/bare" ]; then
    rm -rf "$WORK/bare-$plat" && mkdir -p "$WORK/bare-$plat"
    (cd "$WORK/bare-$plat" && npm pack --silent "bare-runtime-$plat@$BARE_VERSION" >/dev/null && tar -xzf ./*.tgz && cp "$(find package -type f -name bare | head -1)" bare && chmod +x bare && rm -rf package ./*.tgz)
    say "bare $BARE_VERSION for $plat"
  fi
done
[ "${1:-}" = "--no-unity" ] && { say "work tree ready: $WORK/pkg"; exit 0; }

# 3. libc++_shared.so from NDK r29 (the QVAC addons are built with clang 21 and need its newer symbols;
#    Unity's NDK r27 lacks __cxa_init_primary_exception). Only that file is read from the NDK zip.
if [ ! -f "$WORK/ndk-r29/libc++_shared.so" ]; then
  mkdir -p "$WORK/ndk-r29"
  python3 -I "$ROOT/tools/remote-unzip.py" "$NDK_ZIP" "$WORK/ndk-r29" "sysroot/usr/lib/aarch64-linux-android/libc++_shared.so"
  mv "$WORK/ndk-r29/"*libc++_shared.so "$WORK/ndk-r29/libc++_shared.so"
fi

# 4. stage: android-arm64 prebuilds only, no GPU backends (the GPU belongs to the frame loop), CPU
#    variants up to armv8.2_2 (Quest 3/3S: Cortex-A78C, dotprod + fp16, no i8mm), no tests/docs/types
STAGE="$WORK/stage"
rm -rf "$STAGE" && mkdir -p "$STAGE/lib"
rsync -a --exclude '.bin' --exclude '*-linux-x64' --exclude '*-linux-arm64' --exclude '*-darwin-*' --exclude '*-win32-*' \
  --exclude '*-ios*' --exclude 'test' --exclude 'tests' --exclude 'examples' --exclude 'docs' --exclude 'benchmark*' \
  --exclude 'include' --exclude '*.d.ts' --exclude '*.md' --exclude '*.map' --exclude 'CMakeLists.txt' --exclude '*.c' \
  --exclude '*.cc' --exclude '*.h' --exclude '*vulkan*' --exclude '*opencl*' --exclude '*-rpc.so' \
  --exclude '*armv8.6*' --exclude '*armv9*' "$WORK/pkg/node_modules" "$STAGE/"
find "$STAGE/node_modules" -type d -path '*/prebuilds/*' -prune ! -name 'android-arm64' -exec rm -rf {} +
cp -r "$WORK/pkg/server" "$WORK/pkg/package.json" "$STAGE/"
cp "$WORK/ndk-r29/libc++_shared.so" "$STAGE/lib/"
# strip native files through Windows (llvm-strip from Unity's NDK; it needs Windows paths)
WTMP=$(wslpath -u "$(cmd.exe /c 'echo %TEMP%' 2>/dev/null | tr -d '\r')")/mcx-qvac-strip
rm -rf "$WTMP" && mkdir -p "$WTMP"
n=0
while IFS= read -r -d '' f; do
  if file "$f" | grep -q 'not stripped'; then
    cp "$f" "$WTMP/x" && "$STRIP" --strip-unneeded "$(wslpath -w "$WTMP/x")" < /dev/null && cp "$WTMP/x" "$f" && n=$((n + 1))
  fi
done < <(find "$STAGE" -type f \( -name '*.bare' -o -name '*.so' \) -print0)
say "stripped $n native files"
VERSION=$(python3 -c "import json;print(json.load(open('$ROOT/qvac/package.json'))['version'])")
SRCHASH=$( (cd "$STAGE" && find . -type f -print0 | sort -z | xargs -0 sha256sum) | sha256sum | cut -c1-16)

# 5. outputs for Unity
mkdir -p "$OUT_SA" "$OUT_LIB"
tar -czf "$OUT_SA/runtime.tar.gz" -C "$STAGE" .
python3 - "$OUT_SA" "$VERSION" "$SRCHASH" "$BARE_VERSION" "$STAGE" <<'EOF'
import json, os, sys, hashlib, time
out, version, srchash, bare, stage = sys.argv[1:]
tar = os.path.join(out, 'runtime.tar.gz')
size = sum(os.path.getsize(os.path.join(d, f)) for d, _, fs in os.walk(stage) for f in fs)
pk = json.load(open(os.path.join(stage, 'package.json')))
json.dump({'service': 'metacodex-ai', 'version': version, 'hash': srchash, 'bare': bare,
           'sha256': hashlib.sha256(open(tar, 'rb').read()).hexdigest(), 'unpackedBytes': size,
           'packages': pk['dependencies'], 'built': time.strftime('%Y-%m-%dT%H:%M:%SZ', time.gmtime())},
          open(os.path.join(out, 'runtime.json'), 'w'), indent=2)
EOF
cp "$WORK/bare-android-arm64/bare" "$WTMP/bare" && "$STRIP" --strip-unneeded "$(wslpath -w "$WTMP/bare")"
cp "$WTMP/bare" "$OUT_LIB/libbare.so"
rm -rf "$WTMP"
# importer settings like libproot.so (Android, ARM64); fixed GUIDs so scene references never churn
meta() { # file guid
  [ -f "$1.meta" ] && return
  sed "s/^guid: .*/guid: $2/" "$OUT_LIB/libproot.so.meta" > "$1.meta"
}
meta "$OUT_LIB/libbare.so" 7a1e5c0b3d2f4e8a9b6c1d0e2f3a4b5c
for f in runtime.tar.gz runtime.json; do
  [ -f "$OUT_SA/$f.meta" ] || printf 'fileFormatVersion: 2\nguid: %s\nDefaultImporter:\n  externalObjects: {}\n  userData: \n  assetBundleName: \n  assetBundleVariant: \n' \
    "$(echo -n "metacodex-qvac-$f" | md5sum | cut -c1-32)" > "$OUT_SA/$f.meta"
done
[ -f "$OUT_SA.meta" ] || printf 'fileFormatVersion: 2\nguid: %s\nfolderAsset: yes\nDefaultImporter:\n  externalObjects: {}\n  userData: \n  assetBundleName: \n  assetBundleVariant: \n' \
  "$(echo -n 'metacodex-qvac-folder' | md5sum | cut -c1-32)" > "$OUT_SA.meta"
say "runtime $VERSION ($SRCHASH): $(du -h "$OUT_SA/runtime.tar.gz" | cut -f1) tar.gz, $(du -sh "$STAGE" | cut -f1) unpacked; libbare.so $(du -h "$OUT_LIB/libbare.so" | cut -f1)"

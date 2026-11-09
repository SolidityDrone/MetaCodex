#!/usr/bin/env bash
# A native Quest app (OpenXR + OpenGL ES, no Java) -> build/<folder>.apk, without Gradle or Unity:
# compile src/*.cpp src/*.c -> link lib<name>.so -> aapt2 link (manifest, optional res/ and assets/) ->
# add lib/arm64-v8a -> zipalign -> sign. Package name, label and library name come from AndroidManifest.xml.
#   ./build.sh device   inside MetaCodex's Linux (what `mcx-app build` runs; `mcx-setup quest` installs the tools)
#   ./build.sh pc       in WSL on the PC, with the Android tools of the Unity install on Windows (NDK r27,
#                       build-tools 36, OpenJDK); needs OPENXR_INCLUDE and OPENXR_LOADER (Khronos OpenXR loader AAR)
set -euo pipefail
HERE="$(cd "$(dirname "$0")" && pwd)"
MODE=${1:-device}
OUT="$HERE/build"
NAME=$(basename "$HERE")
MANIFEST="$HERE/AndroidManifest.xml"
PKG=$(sed -n 's/.*package="\([^"]*\)".*/\1/p' "$MANIFEST" | head -1)
LIB=$(grep -o 'android.app.lib_name" android:value="[^"]*"' "$MANIFEST" | sed 's/.*value="//; s/"$//' | head -1)
[[ -n "$PKG" && -n "$LIB" ]] || { echo "build.sh: AndroidManifest.xml needs package=\"...\" and the android.app.lib_name meta-data" >&2; exit 2; }

if [[ "$MODE" == pc ]]; then
  : "${OPENXR_INCLUDE:?set OPENXR_INCLUDE to the folder holding openxr/openxr.h}"
  : "${OPENXR_LOADER:?set OPENXR_LOADER to libopenxr_loader.so (arm64-v8a)}"
  A="${UNITY_ANDROID:-/mnt/c/Program Files/Unity 6000.4.5f1/Editor/Data/PlaybackEngines/AndroidPlayer}"
  BIN="$A/NDK/toolchains/llvm/prebuilt/windows-x86_64/bin"
  CC=("$BIN/clang.exe") CXX=("$BIN/clang++.exe")
  BT="$A/SDK/build-tools/36.0.0"
  AAPT2=("$BT/aapt2.exe") ZIPALIGN=("$BT/zipalign.exe")
  JAVA="$A/OpenJDK/bin/java.exe" KEYTOOL=("$A/OpenJDK/bin/keytool.exe")
  APKSIGNER=("$JAVA" -jar "$(wslpath -w "$BT/lib/apksigner.jar")")
  ANDROID_JAR="${ANDROID_JAR:-$A/SDK/platforms/android-34/android.jar}"
  GLUE="$A/NDK/sources/android/native_app_glue"
  KS="${KEYSTORE:-$HERE/../.debug.keystore}"
  w() { wslpath -w "$1"; } # the tools are Windows programs: Windows paths
elif [[ "$MODE" == device ]]; then
  NDK="${ANDROID_NDK_HOME:-/opt/android-ndk}"
  SDK="${ANDROID_HOME:-/opt/android-sdk}"
  BIN="$NDK/toolchains/llvm/prebuilt/linux-arm64/bin"
  CC=("$BIN/clang") CXX=("$BIN/clang++")
  AAPT2=(aapt2) ZIPALIGN=(zipalign) APKSIGNER=(apksigner) KEYTOOL=(keytool)
  ANDROID_JAR="${ANDROID_JAR:-$SDK/platforms/android-34/android.jar}"
  OPENXR_INCLUDE="${OPENXR_INCLUDE:-/opt/openxr/include}"
  OPENXR_LOADER="${OPENXR_LOADER:-/opt/openxr/lib/arm64-v8a/libopenxr_loader.so}"
  GLUE="$NDK/sources/android/native_app_glue"
  # one key for every app built here: an update must be signed like the installed app (never delete it)
  KS="${KEYSTORE:-$HOME/.android/metacodex-debug.keystore}"
  w() { printf '%s' "$1"; }
  missing=()
  for t in "${CXX[0]}" "$GLUE/android_native_app_glue.c" "$ANDROID_JAR" "$OPENXR_INCLUDE/openxr/openxr.h" "$OPENXR_LOADER"; do
    [[ -e "$t" ]] || missing+=("$t")
  done
  for t in aapt2 zipalign apksigner keytool python3; do command -v "$t" >/dev/null || missing+=("$t"); done
  if (( ${#missing[@]} )); then echo "build.sh: missing ${missing[*]} (run: mcx-setup quest)" >&2; exit 3; fi
else
  echo "usage: $0 device|pc" >&2; exit 2
fi

rm -rf "$OUT"
mkdir -p "$OUT/obj" "$OUT/lib/arm64-v8a"
T=(--target=aarch64-linux-android32 -O2 -fPIC -DAPP_ID_RAW="$PKG")
INC=(-I"$(w "$OPENXR_INCLUDE")" -I"$(w "$GLUE")" -I"$(w "$HERE/src")")
echo "== compile $PKG (lib$LIB.so)"
objs=()
shopt -s nullglob
for src in "$HERE"/src/*.cpp "$HERE"/src/*.c "$GLUE/android_native_app_glue.c"; do
  o="$OUT/obj/$(basename "$src").o"
  case "$src" in
    *.cpp) "${CXX[@]}" "${T[@]}" -std=c++17 "${INC[@]}" -c "$(w "$src")" -o "$(w "$o")" ;;
    *) "${CC[@]}" "${T[@]}" "${INC[@]}" -c "$(w "$src")" -o "$(w "$o")" ;;
  esac
  objs+=("$(w "$o")")
done
cp "$OPENXR_LOADER" "$OUT/lib/arm64-v8a/libopenxr_loader.so"
echo "== link"
# the glue's entry point must stay (NativeActivity calls it); libc++ linked in, so no libc++_shared.so to ship
"${CXX[@]}" --target=aarch64-linux-android32 -shared -o "$(w "$OUT/lib/arm64-v8a/lib$LIB.so")" "${objs[@]}" \
  -L"$(w "$OUT/lib/arm64-v8a")" -lopenxr_loader -landroid -llog -lEGL -lGLESv3 -static-libstdc++ \
  -Wl,-u,ANativeActivity_onCreate -Wl,--no-undefined -Wl,-z,max-page-size=16384
echo "== package"
LINK=(link -o "$(w "$OUT/base.apk")" --manifest "$(w "$MANIFEST")" -I "$(w "$ANDROID_JAR")")
if [[ -d "$HERE/res" ]]; then
  "${AAPT2[@]}" compile --dir "$(w "$HERE/res")" -o "$(w "$OUT/res.zip")"
  LINK+=("$(w "$OUT/res.zip")" --auto-add-overlay)
fi
[[ -d "$HERE/assets" ]] && LINK+=(-A "$(w "$HERE/assets")")
"${AAPT2[@]}" "${LINK[@]}"
python3 -I - "$OUT/base.apk" "$OUT/lib" <<'PY'
import os, sys, zipfile
apk, lib = sys.argv[1], sys.argv[2]
with zipfile.ZipFile(apk, "a", zipfile.ZIP_DEFLATED) as z:
    for root, _, files in os.walk(lib):
        for f in sorted(files):
            p = os.path.join(root, f)
            z.write(p, "lib/" + os.path.relpath(p, lib).replace(os.sep, "/"))
print("libraries added:", ", ".join(sorted(os.listdir(os.path.join(lib, "arm64-v8a")))))
PY
"${ZIPALIGN[@]}" -f 4 "$(w "$OUT/base.apk")" "$(w "$OUT/aligned.apk")"
echo "== sign"
if [[ ! -f "$KS" ]]; then
  mkdir -p "$(dirname "$KS")"
  "${KEYTOOL[@]}" -genkeypair -keystore "$(w "$KS")" -storepass android -keypass android -alias metacodex \
    -keyalg RSA -keysize 2048 -validity 10000 -dname "CN=MetaCodex native debug" >/dev/null 2>&1
  echo "new signing key: $KS (keep it: updates must be signed with it)"
fi
"${APKSIGNER[@]}" sign --ks "$(w "$KS")" --ks-pass pass:android --key-pass pass:android --out "$(w "$OUT/$NAME.apk")" "$(w "$OUT/aligned.apk")"
"${APKSIGNER[@]}" verify "$(w "$OUT/$NAME.apk")"
rm -f "$OUT/base.apk" "$OUT/aligned.apk" "$OUT/$NAME.apk.idsig"
echo "APK $OUT/$NAME.apk ($(du -k "$OUT/$NAME.apk" | cut -f1) KB) package $PKG"

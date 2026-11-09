#!/usr/bin/env bash
# Hello Native -> build/hello-native.apk, without Gradle or Unity: compile -> aapt2 link -> add the libraries ->
# zipalign -> sign. Two places to run it:
#   ./build.sh pc       in WSL, with the Android tools Unity installed on Windows (NDK r27, build-tools, OpenJDK)
#   ./build.sh device   inside MetaCodex's Linux (SYSTEM > TOOLS: ndk, build-tools, java; ANDROID_JAR=...android.jar)
# Both need OPENXR_INCLUDE (the folder holding openxr/openxr.h, Khronos OpenXR-SDK) and OPENXR_LOADER (Khronos'
# libopenxr_loader.so for arm64-v8a, from the openxr_loader_for_android AAR).
set -euo pipefail
HERE="$(cd "$(dirname "$0")" && pwd)"
MODE=${1:-pc}
OUT="$HERE/build"
: "${OPENXR_INCLUDE:?set OPENXR_INCLUDE to the OpenXR-SDK include folder}"
: "${OPENXR_LOADER:?set OPENXR_LOADER to libopenxr_loader.so (arm64-v8a)}"

if [[ "$MODE" == pc ]]; then
  A="${UNITY_ANDROID:-/mnt/c/Program Files/Unity 6000.4.5f1/Editor/Data/PlaybackEngines/AndroidPlayer}"
  BIN="$A/NDK/toolchains/llvm/prebuilt/windows-x86_64/bin"
  CC=("$BIN/clang.exe") CXX=("$BIN/clang++.exe")
  BT="$A/SDK/build-tools/36.0.0"
  AAPT2=("$BT/aapt2.exe") ZIPALIGN=("$BT/zipalign.exe")
  JAVA="$A/OpenJDK/bin/java.exe" KEYTOOL=("$A/OpenJDK/bin/keytool.exe")
  APKSIGNER=("$JAVA" -jar "$(wslpath -w "$BT/lib/apksigner.jar")")
  ANDROID_JAR="${ANDROID_JAR:-$A/SDK/platforms/android-34/android.jar}"
  GLUE="$A/NDK/sources/android/native_app_glue"
  w() { wslpath -w "$1"; } # the tools are Windows programs: Windows paths
elif [[ "$MODE" == device ]]; then
  NDK="${ANDROID_NDK_HOME:-/opt/android-ndk}"
  BIN="$NDK/toolchains/llvm/prebuilt/linux-arm64/bin"
  CC=("$BIN/clang") CXX=("$BIN/clang++")
  AAPT2=(aapt2) ZIPALIGN=(zipalign) APKSIGNER=(apksigner) KEYTOOL=(keytool)
  : "${ANDROID_JAR:?set ANDROID_JAR to an android.jar (platforms;android-32 or newer)}"
  GLUE="$NDK/sources/android/native_app_glue"
  w() { printf '%s' "$1"; }
else
  echo "usage: $0 pc|device" >&2; exit 2
fi

rm -rf "$OUT"
mkdir -p "$OUT/lib/arm64-v8a"
T=(--target=aarch64-linux-android32 -O2 -fPIC)
echo "== compile"
"${CXX[@]}" "${T[@]}" -std=c++17 -I"$(w "$OPENXR_INCLUDE")" -I"$(w "$GLUE")" -c "$(w "$HERE/src/main.cpp")" -o "$(w "$OUT/main.o")"
"${CC[@]}" "${T[@]}" -I"$(w "$GLUE")" -c "$(w "$GLUE/android_native_app_glue.c")" -o "$(w "$OUT/glue.o")"
cp "$OPENXR_LOADER" "$OUT/lib/arm64-v8a/libopenxr_loader.so"
echo "== link"
# the glue's entry point must stay (NativeActivity calls it); libc++ linked in, so no libc++_shared.so to ship
"${CXX[@]}" --target=aarch64-linux-android32 -shared -o "$(w "$OUT/lib/arm64-v8a/libhellonative.so")" \
  "$(w "$OUT/main.o")" "$(w "$OUT/glue.o")" -L"$(w "$OUT/lib/arm64-v8a")" -lopenxr_loader \
  -landroid -llog -lEGL -lGLESv3 -static-libstdc++ -Wl,-u,ANativeActivity_onCreate -Wl,--no-undefined -Wl,-z,max-page-size=16384
echo "== package"
"${AAPT2[@]}" link -o "$(w "$OUT/base.apk")" --manifest "$(w "$HERE/AndroidManifest.xml")" -I "$(w "$ANDROID_JAR")" \
  --min-sdk-version 32 --target-sdk-version 32
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
KS="${KEYSTORE:-$HERE/../.debug.keystore}"
if [[ ! -f "$KS" ]]; then
  "${KEYTOOL[@]}" -genkeypair -keystore "$(w "$KS")" -storepass android -keypass android -alias hellonative \
    -keyalg RSA -keysize 2048 -validity 10000 -dname "CN=MetaCodex native debug" >/dev/null
fi
"${APKSIGNER[@]}" sign --ks "$(w "$KS")" --ks-pass pass:android --key-pass pass:android --out "$(w "$OUT/hello-native.apk")" "$(w "$OUT/aligned.apk")"
"${APKSIGNER[@]}" verify "$(w "$OUT/hello-native.apk")"
ls -la "$OUT/hello-native.apk"

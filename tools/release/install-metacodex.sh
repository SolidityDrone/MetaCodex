#!/usr/bin/env bash
# Install MetaCodex on a Quest 3 / 3S over USB, ready to use (no permission prompts):
#   ./install-metacodex.sh [MetaCodex.apk]
# Needs adb (Android platform-tools) and the headset in developer mode, plugged in, the USB debugging prompt accepted.
set -u
APK="${1:-$(dirname "$0")/MetaCodex.apk}"
PKG=com.pnncl.metacodex
command -v adb >/dev/null || { echo "adb not found: install Android platform-tools"; exit 1; }
[ -f "$APK" ] || { echo "no APK at $APK"; exit 1; }
echo "== headset"; adb wait-for-device; adb devices -l | sed -n 2p
echo "== installing $(basename "$APK") ($(( $(stat -c %s "$APK" 2>/dev/null || stat -f %z "$APK") / 1048576 )) MB)"
adb install -r -g "$APK" || { echo "install failed"; exit 1; }
echo "== permissions"
for p in android.permission.RECORD_AUDIO com.oculus.permission.USE_SCENE horizonos.permission.HEADSET_CAMERA; do
  if adb shell pm grant "$PKG" "$p" 2>/dev/null; then echo "  granted $p"; else echo "  $p: asked at first start instead"; fi
done
adb shell appops set "$PKG" REQUEST_INSTALL_PACKAGES allow 2>/dev/null && echo "  may install the apps it builds" || true
echo "== done: open MetaCodex from the Library (Unknown sources)"

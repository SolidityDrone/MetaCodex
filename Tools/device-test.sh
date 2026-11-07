#!/usr/bin/env bash
# Usage: Tools/device-test.sh [--no-install] [--setup]     (headset on adb; skill set bin/ on PATH)
#
# G0-app: proves the terminal inside OUR app (its SELinux domain, W^X, the pty) without wearing the
# headset: installs Builds/MetaCodex.apk (-r -g keeps the userland and grants permissions), launches it,
# waits for the in-app rootfs install, then types commands into the app's own terminal through the
# control port (Tools/qterm) and prints what the terminal shows. --setup also runs mcx-setup in the app.
# Ends with a screenshot (Bench/device/screen.png) and the app's session log. Lines start with "APP".
set -uo pipefail
ROOT="$(cd "$(dirname "$0")/.." && pwd)"
PKG=com.pnncl.metacodex
OUT="$ROOT/Bench/device"; mkdir -p "$OUT"
Q="$ROOT/Tools/qterm"
INSTALL=1; SETUP=0
for a in "$@"; do [[ "$a" == --no-install ]] && INSTALL=0; [[ "$a" == --setup ]] && SETUP=1; done
[[ "$(adb get-state 2>/dev/null | tr -d '\r')" == device ]] || { echo "APP no device on adb"; exit 2; }

adb shell am broadcast -a com.oculus.vrpowermanager.prox_close >/dev/null 2>&1 # keep the headset awake while unworn
if (( INSTALL )); then
  t=$(date +%s)
  r=$(adb install -r -g "$(wslpath -w "$ROOT/Builds/MetaCodex.apk")" 2>&1 | tr -d '\r' | tail -1)
  echo "APP install: $r ($(( $(date +%s) - t )) s, $(du -m "$ROOT/Builds/MetaCodex.apk" | cut -f1) MB)"
fi
adb logcat -c
adb shell am force-stop $PKG
adb shell rm -f /sdcard/Android/data/$PKG/files/control.token
adb shell am start -n $PKG/com.unity3d.player.UnityPlayerActivity >/dev/null 2>&1
t0=$(date +%s)
for i in $(seq 1 60); do
  adb shell "[ -s /sdcard/Android/data/$PKG/files/control.token ]" 2>/dev/null && break
  sleep 1
done
echo "APP control port up after $(( $(date +%s) - t0 )) s"

state=""
for i in $(seq 1 120); do
  state=$("$Q" status 2>/dev/null | head -1)
  [[ "$state" == *"host=Ready"* || "$state" == *"host=Failed"* || "$state" == *"host=Unsupported"* ]] && break
  sleep 2
done
echo "APP host after $(( $(date +%s) - t0 )) s: $state"
"$Q" status 2>/dev/null | sed 's/^/APP   /'

run() { echo "APP \$ $1"; "$Q" run "$1" "${2:-30}" 2>&1 | grep -v '^$' | tail -"${3:-12}" | sed 's/^/APP   /'; }
if [[ "$state" == *"host=Ready"* ]]; then
  run 'clear; uname -a; head -2 /etc/os-release; id; echo TERM=$TERM; ls /mnt/quest | head -3; tty' 20 12
  run 'cat /proc/loadavg; nproc; free -m | head -2' 20 6
  if (( SETUP )); then run 'mcx-setup' 300 8; run 'node -v; npm -v' 30 4; fi
  "$Q" screen > "$OUT/screen.txt" 2>&1
fi
adb exec-out screencap -p > "$OUT/screen.png" 2>/dev/null || true
metavr capture screenshot -o "$(wslpath -w "$OUT/view.png")" >/dev/null 2>&1 || true
adb pull /sdcard/Android/data/$PKG/files/session_log.txt "$(wslpath -w "$OUT/session_log.txt")" >/dev/null 2>&1
echo "APP session log (last lines):"; tail -15 "$OUT/session_log.txt" 2>/dev/null | sed 's/^/APP   /'
echo "APP errors in logcat:"; adb logcat -d -s Unity:E AndroidRuntime:E DEBUG:F libc:F 2>/dev/null | tr -d '\r' | grep -v "^---" | tail -15 | sed 's/^/APP   /'
adb logcat -d 2>/dev/null | tr -d '\r' | grep -i -E "avc: denied.*metacodex|avc: denied.*proot|PhantomProcess|Killing.*$PKG" | tail -8 | sed 's/^/APP   sel\/kill: /'
echo "APP done"

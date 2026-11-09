#!/bin/bash
# Installs an APK on this headset from MetaCodex's Linux, through the headset's own adbd (adb over TCP, 127.0.0.1:5555;
# needs developer mode and `adb tcpip 5555` once from a PC: Horizon OS resets it at reboot).
#   install-from-linux.sh [apk] [package to start]     (default: /root/native/hello-native.apk, com.metacodex.hellonative)
# The first time, Horizon OS asks "Allow USB debugging?" for this Linux's adb key (/root/.android/adbkey):
# tick "Always allow from this computer", then Allow. One connection only, kept while waiting: every new connection
# makes a new prompt and an OK given to a dropped one is lost (the first version reconnected every 2 s: 3 OKs, no access).
D=127.0.0.1:5555
APK=${1:-/root/native/hello-native.apk}
PKG=${2:-com.metacodex.hellonative}
WAIT=${WAIT:-300}
command -v adb >/dev/null || { echo "== installing adb"; DEBIAN_FRONTEND=noninteractive apt-get install -y -q adb >/dev/null || exit 1; }
state() { adb devices 2>/dev/null | awk -v d="$D" '$1 == d { print $2 }'; }
adb start-server >/dev/null 2>&1
echo "connect: $(adb connect $D 2>&1)"
t0=$(date +%s); last=-; gone=0
while :; do
  st=$(state); now=$(( $(date +%s) - t0 ))
  [ "$st" != "$last" ] && { echo "${now}s state: ${st:-not listed}"; last=$st; }
  [ "$st" = device ] && break
  [ "$now" -ge "$WAIT" ] && { echo "RESULT: not authorized in ${WAIT}s"; adb devices -l; exit 1; }
  # reconnect only when the server lost the device for 10 s (unauthorized and offline are waiting states)
  if [ -z "$st" ]; then gone=$((gone + 1)); else gone=0; fi
  [ "$gone" -ge 5 ] && { echo "reconnect: $(adb connect $D 2>&1)"; gone=0; }
  sleep 2
done
echo "authorized: $(adb -s $D shell getprop ro.product.model | tr -d '\r')"
adb -s $D install -r "$APK" && echo "RESULT: installed from MetaCodex Linux"
adb -s $D shell pm list packages "$PKG"
ACT=$(adb -s $D shell cmd package resolve-activity --brief -c android.intent.category.LAUNCHER "$PKG" | tr -d '\r' | tail -1)
adb -s $D shell am start -n "$ACT"

#!/usr/bin/env bash
# gpu-switch-test.sh <package of an installed app> [switches=10]: MetaCodex's GPU memory across app switches.
# Brings MetaCodex to the front, then N times: the app in front 8 s, MetaCodex in front 12 s; after each return it
# reads `dumpsys meminfo com.pnncl.metacodex` (GL mtrack, EGL mtrack, TOTAL PSS) and whether :ai is still alive.
# ONLY with the headset free (it drives the headset); keep it awake with prox_close (restored at the end).
# Flat GL mtrack = no leak per switch. Run once with BLUR off and once with it on to split the suspects.
set -uo pipefail
export PATH="/home/drone/projects/unity-skill-set/bin:$PATH"
APP=${1:?package of an installed app}; N=${2:-10}; M=com.pnncl.metacodex
cd "$HOME"
mem() { adb shell "dumpsys meminfo $M" | tr -d '\r' | awk '/^ *GL mtrack/{gl=$3} /^ *EGL mtrack/{egl=$3} /TOTAL PSS:/{pss=$3} END{printf "GL %d MB  EGL %d MB  PSS %d MB", gl/1024, egl/1024, pss/1024}'; }
adb shell am broadcast -a com.oculus.vrpowermanager.prox_close >/dev/null
adb shell am start -n $M/com.unity3d.player.UnityPlayerActivity >/dev/null; sleep 20
echo "start: $(mem)  ai=$(adb shell pidof $M:ai | tr -d '\r')"
for i in $(seq 1 "$N"); do
  adb shell monkey -p "$APP" -c android.intent.category.LAUNCHER 1 >/dev/null 2>&1; sleep 8
  adb shell am start -n $M/com.unity3d.player.UnityPlayerActivity >/dev/null; sleep 12
  echo "switch $i: $(mem)  ai=$(adb shell pidof $M:ai | tr -d '\r')"
done
adb shell am broadcast -a com.oculus.vrpowermanager.automation_disable >/dev/null

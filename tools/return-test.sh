#!/usr/bin/env bash
# return-test.sh <app package> [report] [cold]: times the way back from an app on mcx_xr to MetaCodex.
# Launches the app as MetaCodex would (CALLER, a LAUNCH_ID) plus TEST_RETURN_AFTER=5 (and TEST_REPORT=1 with
# "report"), so the app takes its menu's Back / Report path by itself; "cold" force-stops MetaCodex first.
# Reads logcat (epoch times): the app's "back: ... started" -> MetaCodex's wm_on_resume_called -> the next VrApi
# line (frames again, +-1 s). ONLY with the headset free.
set -uo pipefail
export PATH="/home/drone/projects/unity-skill-set/bin:$PATH"
APP=${1:?package}; REPORT=${2:-}; COLD=${3:-}; M=com.pnncl.metacodex
cd "$HOME"
adb shell am broadcast -a com.oculus.vrpowermanager.prox_close >/dev/null
if [ "$COLD" = cold ]; then adb shell am force-stop $M; sleep 2; fi
adb logcat -c; adb logcat -b events -c
extra=(--es com.metacodex.extra.CALLER $M --es com.metacodex.extra.LAUNCH_ID test-$(date +%s) --es com.metacodex.extra.TEST_RETURN_AFTER 5)
[ "$REPORT" = report ] && extra+=(--es com.metacodex.extra.TEST_REPORT 1)
adb shell am start -n "$APP/android.app.NativeActivity" "${extra[@]}" >/dev/null
sleep 16
adb logcat -d -v epoch -b main -b events -b system > /tmp/return-test.log
python3 - "$APP" <<'PY'
import re, sys
app = sys.argv[1]
back = resume = frame = None
for l in open("/tmp/return-test.log", errors="replace"):
    m = re.match(r"\s*(\d+\.\d+)", l)
    if not m: continue
    t = float(m.group(1))
    if back is None and "back: com.pnncl.metacodex" in l: back = t; print("app:", l.strip()[l.find("back:"):][:120])
    if "report: picture" in l: print("app:", l.strip()[l.find("report:"):][:120])
    if back and resume is None and "wm_on_resume_called" in l and "UnityPlayerActivity" in l: resume = t
    if resume and frame is None and "VrApi" in l and "FPS=" in l and t > resume: frame = t; fl = l.strip()[l.find("FPS="):][:40]
    if back and "FATAL" in l: print("FATAL:", l.strip()[:200])
print(f"back -> MetaCodex resumed: {resume - back:.2f} s" if back and resume else "no resume seen (back %s)" % ("seen" if back else "not seen"))
if frame and back: print(f"back -> first VrApi frame line: {frame - back:.2f} s ({fl})")
PY
adb shell am broadcast -a com.oculus.vrpowermanager.automation_disable >/dev/null

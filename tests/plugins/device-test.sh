#!/usr/bin/env bash
# Usage: tests/plugins/device-test.sh [timers|calendar]     (WSL; the headset on adb; the app running; NOBODY wearing it)
#
# The app plugins on the headset, after an APK with the plugin API (ui.panel, menu.add, app.clock, json, ui.notify):
# installs an example plugin, checks its window, tile and voice commands, presses a button, restarts the app and
# checks that the window, the tile and the saved state are back, then disables it and checks that all of it went.
# Talks to the app's control port through tools/qterm (adb forward + token). Prints ok / FAIL lines; exit = FAILs.
set -uo pipefail
ROOT="$(cd "$(dirname "$0")/../.." && pwd)"
ID=${1:-timers}
PKG=com.pnncl.metacodex
STATE=/sdcard/Android/data/$PKG/files/scripts/.state.json
Q() { python3 "$ROOT/tools/qterm" "$@" 2>&1 | tr -d '\r'; }
fails=0
ok()   { echo "ok   $*"; }
fail() { echo "FAIL $*"; fails=$((fails+1)); }
expect() { local what=$1 pat=$2 out=$3; if grep -qE "$pat" <<<"$out"; then ok "$what"; else fail "$what"; sed 's/^/     | /' <<<"$out" | head -12; fi; }
store_of() { adb shell cat "$STATE" 2>/dev/null | tr -d '\r' | python3 -c '
import json,sys
try: d=json.load(sys.stdin)
except Exception: print("{}"); sys.exit()
for s in d.get("stores",[]):
    if s.get("script")=="plugin/'"$ID"'": print(json.dumps({kv["k"]:kv["v"] for kv in s.get("values",[])},sort_keys=True)); sys.exit()
print("{}")'; }
wait_loaded() { for _ in $(seq 1 60); do out=$(Q plugin check "$ID"); grep -q "loaded" <<<"$out" && grep -q "tiles: [A-Z]" <<<"$out" && { echo "$out"; return; }; sleep 2; done; echo "$out"; }

adb get-state >/dev/null 2>&1 || { echo "no headset on adb"; exit 1; }
echo "== $ID: install"
had=$(Q plugin list | grep -c "^$ID ")
expect "plugin example $ID" "^ok" "$(Q plugin example "$ID")"
sleep 2
expect "plugin list has $ID enabled, no error" "^$ID .*enabled=True.*error=-" "$(Q plugin list)"
c=$(Q plugin check "$ID")
expect "check: loaded" "plugin $ID: loaded" "$c"
expect "check: a window" "windows: main" "$c"
expect "check: a tile" "tiles: [A-Z]" "$c"
expect "check: voice commands" "voice: ${ID}_" "$c"
expect "check: no warnings" "warnings: 0" "$c"

echo "== window and a button"
expect "view open plugin-$ID-main" "^ok" "$(Q view open "plugin-$ID-main")"
sleep 1
expect "ui list: the window is open" "view-plugin-$ID-main open=True" "$(Q ui list)"
BTN=START; [[ $ID == calendar ]] && BTN=NEXT
expect "ui click $BTN" "^ok" "$(Q ui click "view-plugin-$ID-main" "$BTN")"
sleep 2
c=$(Q plugin check "$ID")
expect "still loaded after the click" "plugin $ID: loaded" "$c"
expect "the window took the event (builds >= 1)" "main\(builds=[1-9]" "$c"
before=$(store_of); echo "     store before the restart: ${before:0:300}"
[[ "$before" != "{}" ]] && ok "state saved in store" || fail "state saved in store (empty)"

echo "== restart the app"
adb shell am force-stop $PKG; sleep 2
adb shell monkey -p $PKG -c android.intent.category.LAUNCHER 1 >/dev/null 2>&1
sleep 25
c=$(wait_loaded)
expect "after restart: loaded" "plugin $ID: loaded" "$c"
expect "after restart: window back" "windows: main" "$c"
expect "after restart: tile back" "tiles: [A-Z]" "$c"
expect "after restart: no warnings" "warnings: 0" "$c"
after=$(store_of); echo "     store after the restart: ${after:0:300}"
[[ -n "$before" && "$after" != "{}" ]] && ok "state still in store" || fail "state still in store"
expect "after restart: view open" "^ok" "$(Q view open "plugin-$ID-main")"
[[ $ID == timers ]] && { sleep 1; s=$(Q ui list | grep "view-plugin-$ID-main"); expect "the timer window is there" "open=True" "$s"; }
echo "     look at the headset now: the $ID window shows the state from before the restart (timers: the round still runs)"

echo "== disable"
expect "plugin disable" "^ok" "$(Q plugin disable "$ID")"
sleep 2
c=$(Q plugin check "$ID")
expect "check: disabled" "plugin $ID: disabled" "$c"
expect "check: no window" "windows: none" "$c"
expect "check: no tile" "tiles: none" "$c"
expect "check: no voice commands" "voice: none" "$c"
if Q ui list | grep -q "view-plugin-$ID-"; then fail "the window panel is gone"; else ok "the window panel is gone"; fi
Q plugin log "$ID" | tail -5 | sed 's/^/     log | /'

echo "== put it back as it was"
if [[ $had -gt 0 ]]; then Q plugin enable "$ID" >/dev/null; echo "     $ID enabled again (it was installed before)"; else Q plugin delete "$ID" >/dev/null; echo "     $ID deleted (it was not installed before)"; fi
echo "$fails failure(s)"
exit $fails

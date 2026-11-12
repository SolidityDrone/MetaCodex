#!/usr/bin/env bash
# Render MetaCodex diagrams: lint report + dark/light PNGs (3200x1800) with Windows Chrome headless.
# Usage: docs/diagrams/render.sh [name|all] [--debug]
#   name   source file docs/diagrams/src/<name>.html (default: all)
#   --debug  also writes <name>-debug.png (dark, offenders outlined, guides) to $DEBUG_DIR
# Exit code: 0 = all lints clean, 1 = lint problems, 2 = tool/Chrome failure.
set -u
HERE="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
SRC="$HERE/src"
OUT="$(cd "$HERE/../assets/diagrams" 2>/dev/null && pwd || (mkdir -p "$HERE/../assets/diagrams" && cd "$HERE/../assets/diagrams" && pwd))"
CHROME="${CHROME:-/mnt/c/Program Files/Google/Chrome/Application/chrome.exe}"
DEBUG_DIR="${DEBUG_DIR:-$HOME/mcxwork/readme/kit/debug}"
PROFILE_WSL="${PROFILE_WSL:-/mnt/c/Users/pnncl/AppData/Local/Temp/mcx-diagram-chrome}"
BUDGET="${BUDGET:-8000}"       # virtual time budget (ms): fonts, layout, lint must finish within it
TARGET="all"; DEBUG=0
for a in "$@"; do case "$a" in --debug) DEBUG=1;; *) TARGET="$a";; esac; done

[ -x "$CHROME" ] || { echo "Chrome not found: $CHROME" >&2; exit 2; }
mkdir -p "$OUT" "$DEBUG_DIR"

if [ "$TARGET" = all ]; then
  mapfile -t NAMES < <(cd "$SRC" && ls *.html 2>/dev/null | sed 's/\.html$//' | grep -v '^_broken')
else
  NAMES=("${TARGET%.html}")
fi
[ ${#NAMES[@]} -gt 0 ] || { echo "no diagrams in $SRC" >&2; exit 2; }

# Flags that matter in this setup (documented in README.md):
#  --allow-file-access-from-files  lets the page fetch ../banned-words.txt from file://
#  --virtual-time-budget           makes Chrome wait for fonts + layout + lint before dumping/capturing
#  --force-device-scale-factor=2   1600x900 CSS px -> 3200x1800 PNG
#  --user-data-dir                 private profile, never attaches to a running Chrome
FLAGS=(--headless=new --disable-gpu --hide-scrollbars --allow-file-access-from-files
       --no-first-run --no-default-browser-check --disable-extensions --disable-background-networking
       --force-device-scale-factor=2 --window-size=1600,900 --virtual-time-budget="$BUDGET")

run_chrome() { # run_chrome <extra flags...> <url>; one Chrome at a time; cleans profile afterwards
  rm -rf "$PROFILE_WSL"; mkdir -p "$PROFILE_WSL"
  timeout 120 "$CHROME" "${FLAGS[@]}" --user-data-dir="$(wslpath -w "$PROFILE_WSL")" "$@"
  local rc=$?
  rm -rf "$PROFILE_WSL" 2>/dev/null
  return $rc
}
url_for() { # url_for <name> <theme> [extra query]
  local w; w="$(wslpath -w "$SRC/$1.html")"
  echo "file:///${w//\\//}?theme=$2${3:-}"
}

LINTPY='
import sys, re, json, html
dom = sys.stdin.read()
m = re.search(r"<pre id=\"lint\"[^>]*>(.*?)</pre>", dom, re.S)
if not m or not m.group(1).strip():
    print("  LINT MISSING: page produced no report (fonts/JS error?)"); sys.exit(3)
rep = json.loads(html.unescape(m.group(1)))
st = rep.get("stats", {})
print("  lint %s: %s problems (components %s, connectors %s, labels %s)" % (rep.get("theme","?"), rep.get("problems",0), st.get("components","?"), st.get("connectors","?"), st.get("labels","?")))
for i in rep.get("issues", []):
    print("    [%s] %s  <%s>" % (i.get("check"), i.get("msg"), i.get("el","")))
sys.exit(0 if rep.get("ok") else 1)
'

STATUS=0
for n in "${NAMES[@]}"; do
  if [ ! -f "$SRC/$n.html" ]; then echo "missing: $SRC/$n.html" >&2; STATUS=2; continue; fi
  echo "== $n"
  for theme in dark light; do
    dom=""; rc=1
    for try in 1 2 3; do   # retry when the page did not finish (data-kit attribute is set when the lint is done)
      dom="$(run_chrome --dump-dom "$(url_for "$n" "$theme")" 2>/dev/null)"; rc=$?
      case "$dom" in *'data-kit="'*) break;; esac
    done
    if [ $rc -ne 0 ] || [ -z "$dom" ]; then echo "  Chrome failed (dump-dom, $theme, rc=$rc)" >&2; STATUS=2; continue; fi
    printf '%s' "$dom" | python3 -I -c "$LINTPY"; lrc=$?
    [ $lrc -ne 0 ] && { [ $STATUS -lt 1 ] && STATUS=1; [ $lrc -eq 3 ] && STATUS=2; }
    png="$OUT/$n-$theme.png"; rm -f "$png"
    run_chrome --screenshot="$(wslpath -w "$png")" "$(url_for "$n" "$theme")" >/dev/null 2>&1; rc=$?
    if [ ! -s "$png" ]; then echo "  Chrome failed (screenshot, $theme, rc=$rc)" >&2; STATUS=2; continue; fi
    echo "  -> $png"
    webp=$(python3 "$(dirname "$0")/to-webp.py" "$png") && echo "  -> $webp"
  done
  if [ $DEBUG -eq 1 ]; then
    png="$DEBUG_DIR/$n-debug.png"; rm -f "$png"
    run_chrome --screenshot="$(wslpath -w "$png")" "$(url_for "$n" dark '&debug=1&guides=1')" >/dev/null 2>&1
    [ -s "$png" ] && echo "  -> debug: $png"
  fi
done
[ $STATUS -eq 0 ] && echo "ALL CLEAN" || echo "PROBLEMS FOUND (exit $STATUS)"
exit $STATUS

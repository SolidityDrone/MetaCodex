#!/bin/bash
# Runs the local AI service (local-ai/server) without an APK, for development and the Editor tests:
#   tools/local-ai-dev.sh pc start|stop|status|log      in WSL with the linux-x64 addons (port 47810)
#   tools/local-ai-dev.sh device start|stop|status|log  on the headset over adb (/data/local/tmp/mcx-ai),
#                                                    port forwarded to the PC with adb forward
# Both write unity/Bench/qvac-dev.json ({url, token}) so the Editor's QvacHost uses that service.
# Models: ~/mcxwork/qvac-native/models (sha256-checked downloads) are reused when present.
# Needs tools/local-ai-bundle.sh to have run once (work tree in $QVAC_WORK, default ~/mcxwork/qvac-build).
set -euo pipefail
ROOT=$(cd "$(dirname "$0")/.." && pwd)
WORK=${QVAC_WORK:-$HOME/mcxwork/qvac-build}
MODELS=${QVAC_MODELS:-$HOME/mcxwork/qvac-native/models}
PORT=${QVAC_PORT:-47810}
DEVJSON="$ROOT/unity/Bench/qvac-dev.json"
export PATH="/home/drone/projects/unity-skill-set/bin:$PATH" # adb = the Windows adb shim
where=${1:-}; cmd=${2:-status}
[ -d "$WORK/pkg/node_modules" ] || { echo "run tools/local-ai-bundle.sh first"; exit 2; }

devjson() { mkdir -p "$(dirname "$DEVJSON")"; printf '{ "url": "http://127.0.0.1:%s", "token": "%s", "where": "%s" }\n' "$PORT" "$1" "$where" > "$DEVJSON"; }
sha_of() { python3 -I -c "import sys,json,re;s=open(sys.argv[1]).read();print(re.search(sys.argv[2]+r\"',\s*bytes: \d+,\s*sha256: '([0-9a-f]+)'\",s.replace('\n',' ')).group(1))" "$ROOT/local-ai/server/models.js" "$1"; }
status() { curl -s -m 5 -H "Authorization: Bearer $1" "http://127.0.0.1:$PORT/status" || echo "not reachable on $PORT"; echo; }

case "$where:$cmd" in
pc:start)
  D=$HOME/mcxwork/qvac-pc-data; mkdir -p "$D/models"
  rm -rf "$WORK/pkg/server" && cp -r "$ROOT/local-ai/server" "$WORK/pkg/server"
  [ -f "$D/token" ] || head -c 24 /dev/urandom | od -An -tx1 | tr -d ' \n' > "$D/token"
  [ -f "$D/coder.key" ] || head -c 24 /dev/urandom | od -An -tx1 | tr -d ' \n' > "$D/coder.key" # the local coder's stable key
  for f in ggml-base-q8_0.bin Qwen3-0.6B-Q4_0.gguf supertonic3-q4_0.gguf multilingual-e5-small-q8_0.gguf qwen2.5-coder-0.5b-instruct-q4_0.gguf; do # verified at download: mark them
    if [ -f "$MODELS/$f" ] && [ ! -f "$D/models/$f" ]; then ln -sf "$MODELS/$f" "$D/models/$f"; sha_of "$f" > "$D/models/$f.sha256"; fi
  done
  pkill -f '[q]vac-build/pkg/server/main.js' 2>/dev/null || true
  setsid nohup "$WORK/bare-linux-x64/bare" "$WORK/pkg/server/main.js" --data "$D" --token-file "$D/token" --port "$PORT" --threads "${QVAC_THREADS:-4}" > "$D/stdout.log" 2>&1 < /dev/null &
  sleep 2; devjson "$(cat "$D/token")"; status "$(cat "$D/token")" ;;
pc:stop) pkill -f '[q]vac-build/pkg/server/main.js' && echo stopped || echo "not running"; rm -f "$DEVJSON" ;;
pc:status) status "$(cat "$HOME/mcxwork/qvac-pc-data/token" 2>/dev/null)" ;;
pc:log) tail -n "${3:-40}" "$HOME/mcxwork/qvac-pc-data/service.log" ;;
device:start)
  R=/data/local/tmp/mcx-ai
  STAGE="$WORK/stage"; [ -d "$STAGE" ] || { echo "no stage: run tools/local-ai-bundle.sh"; exit 2; }
  rm -rf "$STAGE/server" && cp -r "$ROOT/local-ai/server" "$STAGE/server"
  tar -cf "$WORK/dev-runtime.tar" -C "$STAGE" . && cp "$WORK/bare-android-arm64/bare" "$WORK/dev-bare"
  adb shell "pkill -f '[m]cx-ai/bare'; mkdir -p $R/runtime $R/data/models; rm -rf $R/runtime/*" || true
  adb push "$(wslpath -w "$WORK/dev-runtime.tar")" $R/dev-runtime.tar | tail -1
  adb push "$(wslpath -w "$WORK/dev-bare")" $R/bare | tail -1
  adb shell "cd $R && tar -xf dev-runtime.tar -C runtime && rm dev-runtime.tar && chmod 755 bare"
  for f in ggml-base-q8_0.bin Qwen3-0.6B-Q4_0.gguf supertonic3-q4_0.gguf; do
    if [ -f "$MODELS/$f" ] && ! adb shell "test -f $R/data/models/$f.sha256"; then
      if adb shell "test -f /data/local/tmp/qvac-native/models/$f"; then adb shell "cp /data/local/tmp/qvac-native/models/$f $R/data/models/"; else adb push "$(wslpath -w "$MODELS/$f")" $R/data/models/$f | tail -1; fi
      adb shell "echo $(sha_of "$f") > $R/data/models/$f.sha256"
    fi
  done
  TOKEN=$(head -c 24 /dev/urandom | od -An -tx1 | tr -d ' \n'); adb shell "echo $TOKEN > $R/data/token"
  # adb shell waits for every process it started, even detached ones: let the client go after a few seconds
  # (setsid + nohup keep the service alive when it does)
  timeout 6 adb shell "cd $R/runtime && LD_LIBRARY_PATH=$R/runtime/lib setsid nohup taskset 7 nice -n 10 $R/bare server/main.js --data $R/data --token-file $R/data/token --port $PORT --threads ${QVAC_THREADS:-2} > $R/data/stdout.log 2>&1 < /dev/null &" < /dev/null || true
  adb forward tcp:$PORT tcp:$PORT >/dev/null
  sleep 3; devjson "$TOKEN"; status "$TOKEN" ;;
device:stop) adb shell "pkill -f '[m]cx-ai/bare'" && echo stopped || echo "not running"; rm -f "$DEVJSON" ;;
device:status) status "$(adb shell cat /data/local/tmp/mcx-ai/data/token 2>/dev/null | tr -d '\r')" ;;
device:log) adb shell "tail -n ${3:-40} /data/local/tmp/mcx-ai/data/service.log" ;;
*) sed -n '2,9p' "$0"; exit 1 ;;
esac

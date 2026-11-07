#!/usr/bin/env bash
# Usage: tools/mqh-dev.sh start | stop | status | logs | cli <mqh args...>
#
# Runs the MQH build mirror (~/mcxwork/mqh-build, see tools/mqh-build.sh) in WSL, fully isolated from the
# real bb on this machine: its own HOME (~/mcxwork/mqh-home, so data in ~/mcxwork/mqh-home/.mqh), a
# cleared environment (no BB_* / MQH_* from this shell), ports 38990/38991, and a PATH with only node and
# the system folders (the real `bb` CLI and its daemon folder are on the normal PATH).
set -euo pipefail
B="${MQH_BUILD_DIR:-$HOME/mcxwork/mqh-build}"
H="${MQH_DEV_HOME:-$HOME/mcxwork/mqh-home}"
mkdir -p "$H/bin" "$H/logs"
NODE_BIN="$(dirname "$(command -v node)")"
for t in node npm npx corepack; do ln -sf "$NODE_BIN/$t" "$H/bin/$t"; done
command -v claude >/dev/null && ln -sf "$(readlink -f "$(command -v claude)")" "$H/bin/claude"
CLEAN=(env -i "HOME=$H" "PATH=$H/bin:/usr/local/bin:/usr/bin:/bin" "LANG=C.UTF-8" "TERM=${TERM:-xterm-256color}"
  "NODE_ENV=production" "TMPDIR=$H/tmp")
mkdir -p "$H/tmp"
URL="http://127.0.0.1:38990"

# a pid counts only while it is our node process (cwd inside the build mirror), never someone else's
running() { [[ -f "$H/$1.pid" ]] && kill -0 "$(cat "$H/$1.pid")" 2>/dev/null &&
  [[ "$(readlink "/proc/$(cat "$H/$1.pid")/cwd")" == "$B/apps/$1" ]]; }

case "${1:-status}" in
  start)
    # What upstream's launcher (packages/bb-app) does for a local machine, minus its npm self-update:
    # start the server, wait for /health, enroll the local daemon once over the loopback-only route, then
    # start the daemon with the server URL, its data dir and the bundle dir (provider bridges + `mqh` CLI).
    if running server; then echo "server already running (pid $(cat "$H/server.pid"))"; else
      (cd "$B/apps/server" && exec nohup "${CLEAN[@]}" node dist/index.js >> "$H/logs/server.log" 2>&1 < /dev/null) &
      echo $! > "$H/server.pid"
      echo "server started (pid $(cat "$H/server.pid")), log $H/logs/server.log"
    fi
    for _ in $(seq 60); do curl -fsS -o /dev/null "$URL/health" 2>/dev/null && break; sleep 0.5; done
    curl -fsS -o /dev/null "$URL/health" || { echo "server did not answer on $URL/health"; exit 1; }
    if running host-daemon; then echo "host-daemon already running (pid $(cat "$H/host-daemon.pid"))"; exit 0; fi
    ENROLL=()
    if [[ ! -f "$H/.mqh/auth.json" ]]; then
      resp=$(curl -fsS -X POST -H 'content-type: application/json' -d '{}' "$URL/internal/hosts/enroll-key")
      ENROLL=("MQH_HOST_ENROLL_KEY=$(jq -r .enrollKey <<<"$resp")" "MQH_HOST_ID=$(jq -r .hostId <<<"$resp")")
    fi
    (cd "$B/apps/host-daemon" && exec nohup "${CLEAN[@]}" "MQH_SERVER_URL=$URL" "MQH_DATA_DIR=$H/.mqh" \
      "MQH_HOST_DAEMON_PORT=38991" "MQH_BRIDGE_DIR=$B/apps/host-daemon/dist" "MQH_CLI_DIR=$B/apps/host-daemon/dist" \
      "${ENROLL[@]}" node dist/index.js >> "$H/logs/host-daemon.log" 2>&1 < /dev/null) &
    echo $! > "$H/host-daemon.pid"
    echo "host-daemon started (pid $(cat "$H/host-daemon.pid")), log $H/logs/host-daemon.log" ;;
  stop)
    for svc in host-daemon server; do
      if running "$svc"; then kill "$(cat "$H/$svc.pid")"; echo "$svc stopped"; fi
      rm -f "$H/$svc.pid"
    done ;;
  status)
    for svc in server host-daemon; do running "$svc" && echo "$svc: running (pid $(cat "$H/$svc.pid"))" || echo "$svc: stopped"; done
    ss -ltnp 2>/dev/null | grep -E ":3899[01]\b" || true ;;
  logs) tail -n 40 "$H/logs/server.log" "$H/logs/host-daemon.log" ;;
  cli) shift; cd "$B" && "${CLEAN[@]}" "MQH_SERVER_URL=$URL" node "$B/apps/cli/dist/index.js" "$@" ;;
  *) sed -n 2,8p "$0"; exit 2 ;;
esac

#!/bin/bash
# Tests of userland/overlay/usr/local/bin/mcx-ide (the code editor, docs/ide.md) on the PC (WSL or any Linux), without
# the real release: a fake release archive whose bin/code-server prints its arguments and environment and answers
# /healthz like the real server (python3). Everything happens under a temporary prefix (MCX_IDE_ROOT); ports 47920+.
# Checks: a wrong sha256 is refused and the file deleted; install, a second install that does nothing; the password
# (64 hex, mode 600) and the HASHED_PASSWORD the server gets (its sha256: the app's cookie value); the flags (127.0.0.1,
# auth password, telemetry and update checks off, our config file); the settings written once and valid JSON; start,
# start when running, run when running (refused), the next free port when the first is taken; stop; uninstall keeps
# the data, --all removes it.
set -u
HERE=$(cd "$(dirname "$0")" && pwd)
IDE_SH=$HERE/../../userland/overlay/usr/local/bin/mcx-ide
T=$(mktemp -d /tmp/mcx-ide-test.XXXXXX)
trap 'MCX_IDE_ROOT=$T/root "$T/mcx-ide" stop >/dev/null 2>&1; [ -n "${BUSY:-}" ] && kill "$BUSY" 2>/dev/null; rm -rf "$T"' EXIT
fails=0
ok() { if eval "$2"; then echo "ok   $1"; else echo "FAIL $1"; fails=$((fails + 1)); fi; }

V=$(sed -n 's/^VERSION=//p' "$IDE_SH")
R=$T/src/code-server-$V-linux-arm64
mkdir -p "$R/bin" "$R/lib" "$T/root"
cat > "$R/bin/code-server" <<'EOF'
#!/bin/bash
port=""; prev=""
for a in "$@"; do [ "$prev" = --bind-addr ] && port=${a##*:}; prev=$a; done
echo "ARGS: $*"
echo "ENV HASHED_PASSWORD=${HASHED_PASSWORD:-unset} PASSWORD=${PASSWORD:-unset} NODE_OPTIONS=${NODE_OPTIONS:-unset}"
echo "[info] HTTP server listening on http://127.0.0.1:$port/"
exec python3 -I -c '
import http.server, sys
class H(http.server.BaseHTTPRequestHandler):
    def do_GET(self):
        ok = self.path.startswith("/healthz")
        self.send_response(200 if ok else 302); self.end_headers()
        self.wfile.write(b"{\"status\":\"alive\",\"lastHeartbeat\":0}" if ok else b"")
    def log_message(self, *a): pass
http.server.HTTPServer(("127.0.0.1", int(sys.argv[1])), H).serve_forever()' "$port"
EOF
chmod +x "$R/bin/code-server"
echo fake > "$R/lib/node"
tar -czf "$T/fake.tar.gz" -C "$T/src" "code-server-$V-linux-arm64"
SUM=$(sha256sum "$T/fake.tar.gz" | cut -d' ' -f1)
cp "$IDE_SH" "$T/mcx-ide"; chmod +x "$T/mcx-ide"
export MCX_IDE_ROOT=$T/root MCX_IDE_URL=file://$T/fake.tar.gz MCX_IDE_LOCAL=1 MCX_IDE_PORT=47920
M=$T/mcx-ide
D=$T/root/root/.local/share/mcx-ide

MCX_IDE_SHA256=0000 $M install >/dev/null 2>"$T/err"; rc=$?
ok "a wrong sha256 is refused, the download deleted" '[ $rc -ne 0 ] && grep -q "wrong sha256" "$T/err" && [ ! -e "$T/root/opt/ide/VERSION" ] && [ -z "$(ls -A "$T/root/opt/ide")" ]'
MCX_IDE_SHA256=$SUM $M install >/dev/null; rc=$?
ok "install: unpacked, marker written last, shim written" '[ $rc -eq 0 ] && [ "$(cat "$T/root/opt/ide/VERSION")" = "$V" ] && [ -x "$T/root/opt/ide/code-server-$V-linux-arm64/bin/code-server" ] && [ -f "$T/root/opt/ide/mcx-netfix.js" ] && [ -z "$(ls "$T/root/opt/ide" | grep -e "\.part" -e "\.download")" ]'
ok "install again: nothing to do" 'MCX_IDE_SHA256=$SUM $M install | grep -q "is installed"'
$M status >/dev/null; rc=$?
ok "status before start: stopped (exit 3)" '[ $rc -eq 3 ]'

timeout 60 $M start >"$T/out" 2>&1; rc=$?
ok "start: runs, prints the URL and port" '[ $rc -eq 0 ] && grep -q "http://127.0.0.1:47920/" "$T/out" && grep -q "(port 47920)" "$T/out"'
PW=$(cat "$D/password")
ok "password: 64 hex characters, mode 600" '[[ "$PW" =~ ^[0-9a-f]{64}$ ]] && [ "$(stat -c %a "$D/password")" = 600 ] && [ "$($M password)" = "$PW" ]'
H=$(printf %s "$PW" | sha256sum | cut -d" " -f1)
ok "the server gets HASHED_PASSWORD = sha256(password) and no PASSWORD" 'grep -q "HASHED_PASSWORD=$H PASSWORD=unset" "$D/run/server.log"'
ok "the server gets the netfix shim in NODE_OPTIONS" 'grep -q "NODE_OPTIONS=--require=$T/root/opt/ide/mcx-netfix.js" "$D/run/server.log"'
ok "flags: 127.0.0.1, auth password, telemetry and update checks off, own config and data dirs" 'grep -q -- "--config $D/config.yaml --bind-addr 127.0.0.1:47920 --auth password --disable-telemetry --disable-update-check" "$D/run/server.log" && grep -q -- "--user-data-dir $D --extensions-dir $D/extensions" "$D/run/server.log" && ! grep -q "password:" "$D/config.yaml"'
ok "settings written and valid JSON (autosave, solid cursor, telemetry off)" 'python3 -I -c "import json,sys; d=json.load(open(sys.argv[1])); sys.exit(0 if d[\"files.autoSave\"]==\"afterDelay\" and d[\"editor.cursorBlinking\"]==\"solid\" and d[\"telemetry.telemetryLevel\"]==\"off\" else 1)" "$D/User/settings.json"'
echo '{"mine": true}' > "$D/User/settings.json"
ok "start when running: says so" '$M start | grep -q "is running"'
$M run >/dev/null 2>&1; rc=$?
ok "run when running: refused (exit 5)" '[ $rc -eq 5 ]'
$M status >/dev/null; rc=$?
ok "status: running (exit 0)" '[ $rc -eq 0 ]'
$M stop >/dev/null
$M status >/dev/null; rc=$?
ok "stop: stopped (exit 3), no pid file" '[ $rc -eq 3 ] && [ ! -e "$D/run/pid" ]'

python3 -I -m http.server 47920 --bind 127.0.0.1 >/dev/null 2>&1 & BUSY=$!
sleep 1
timeout 60 $M start >"$T/out" 2>&1; rc=$?
ok "first port taken: the next free one (47921)" '[ $rc -eq 0 ] && grep -q "http://127.0.0.1:47921/" "$T/out" && [ "$(cat "$D/run/port")" = 47921 ]'
ok "the user's settings stay" 'grep -q "\"mine\"" "$D/User/settings.json"'
$M stop >/dev/null; kill $BUSY 2>/dev/null; BUSY=""

$M uninstall >/dev/null
$M status >/dev/null; rc=$?
ok "uninstall: program gone (status exit 4), data kept" '[ $rc -eq 4 ] && [ ! -e "$T/root/opt/ide" ] && [ -f "$D/password" ]'
$M uninstall --all >/dev/null
ok "uninstall --all: data gone too" '[ ! -e "$D" ]'

echo
[ $fails -eq 0 ] && echo "mcx-ide: all passed" || echo "mcx-ide: $fails failed"
exit $fails

#!/usr/bin/env bash
# Usage: tools/mqh-bundle.sh        (after tools/mqh-build.sh; WSL)
#
# Packs MQH for the headset's Linux (Debian arm64, glibc, Node 24) into
#   unity/Assets/StreamingAssets/linux/mqh.tar.gz + mqh.json (sha256)      (both generated, gitignored)
# laid by the app over the rootfs at /opt/mqh (LinuxHost.ApplyBundles) and started by /opt/mqh/bin/mqh-run.
# What goes in: the server, host daemon and CLI bundles (esbuild, no node_modules), the bundled plugins except
# provider-claude-code (its bundle contains Anthropic's Agent SDK: never shipped; installed at first use), and the
# three native modules, as prebuilt linux-arm64 binaries: better-sqlite3 (its GitHub release for Node 24, ABI 137,
# sha256-pinned below), node-pty (its own linux-arm64 prebuild) and @parcel/watcher (its linux-arm64-glibc package).
set -euo pipefail
ROOT="$(cd "$(dirname "$0")/.." && pwd)"
B="${MQH_BUILD_DIR:-$HOME/mcxwork/mqh-build}"
W="${MQH_BUNDLE_WORK:-$HOME/mcxwork/mqh-bundle}"
OUT="$ROOT/unity/Assets/StreamingAssets/linux"
PNPM="$B/node_modules/.pnpm"
SQLITE_VER=12.11.1
SQLITE_TGZ="better-sqlite3-v$SQLITE_VER-node-v137-linux-arm64.tar.gz"
SQLITE_SHA=60da4cbe1b1714c8db62f2ee71a9928cde5303dab57bde14c02debd12a784439
[[ -f "$B/apps/server/dist/index.js" ]] || { echo "no MQH build in $B: run tools/mqh-build.sh first"; exit 1; }

rm -rf "$W/root"; mkdir -p "$W/root/opt/mqh" "$W/dl"
D="$W/root/opt/mqh"
for a in server host-daemon cli; do
  mkdir -p "$D/apps/$a"
  cp -r "$B/apps/$a/dist" "$D/apps/$a/"
  cp "$B/apps/$a/package.json" "$D/apps/$a/"
done
mkdir -p "$D/packages/bundled-plugins"
cp -r "$B/packages/bundled-plugins/dist" "$D/packages/bundled-plugins/"
rm -rf "$D/packages/bundled-plugins/dist/provider-claude-code"
find "$D" -name '*.map' -delete

# the packages the bundles keep outside (natives + the logger), installed for linux-arm64 / glibc from the lockfile
# in userland/mqh/runtime (no install scripts run: the natives come prebuilt, below)
NPMW="$W/npm"; rm -rf "$NPMW"; mkdir -p "$NPMW"
cp "$ROOT/userland/mqh/runtime/package.json" "$ROOT/userland/mqh/runtime/package-lock.json" "$NPMW/"
(cd "$NPMW" && npm ci --omit=dev --ignore-scripts --no-audit --no-fund --os=linux --cpu=arm64 --libc=glibc >/dev/null)
NM="$D/node_modules"
cp -r "$NPMW/node_modules" "$NM"
[[ -d "$NM/@parcel/watcher-linux-arm64-glibc" ]] || { echo "npm did not pick @parcel/watcher-linux-arm64-glibc"; exit 1; }
for p in "$NM"/node-pty/prebuilds/*; do [[ "$(basename "$p")" == linux-arm64 ]] || rm -rf "$p"; done
rm -rf "$NM/node-pty/src" "$NM/node-pty/third_party" "$NM/better-sqlite3/deps" "$NM/better-sqlite3/src" "$NM/@parcel/watcher/src"

# better-sqlite3's own prebuilt binary for Node 24 on linux-arm64 (WiseLibs' GitHub release)
if [[ ! -f "$W/dl/$SQLITE_TGZ" ]]; then
  curl -fsSL -o "$W/dl/$SQLITE_TGZ" "https://github.com/WiseLibs/better-sqlite3/releases/download/v$SQLITE_VER/$SQLITE_TGZ"
fi
sha=$(sha256sum "$W/dl/$SQLITE_TGZ" | cut -d' ' -f1)
if [[ -n "$SQLITE_SHA" && "$sha" != "$SQLITE_SHA" ]]; then echo "sha256 mismatch for $SQLITE_TGZ: $sha"; exit 1; fi
[[ -z "$SQLITE_SHA" ]] && echo "note: $SQLITE_TGZ sha256 $sha (pin it in SQLITE_SHA)"
tar -xzf "$W/dl/$SQLITE_TGZ" -C "$NM/better-sqlite3"   # -> build/Release/better_sqlite3.node
file "$NM/better-sqlite3/build/Release/better_sqlite3.node" | grep -q "ARM aarch64" || { echo "better_sqlite3.node is not aarch64"; exit 1; }
file "$NM/node-pty/prebuilds/linux-arm64/pty.node" | grep -q "ARM aarch64" || { echo "pty.node is not aarch64"; exit 1; }

mkdir -p "$D/bin"
cp "$ROOT/userland/mqh/mqh-run" "$D/bin/mqh-run"
cp "$ROOT/mqh/LICENSE" "$D/LICENSE"
git -C "$ROOT" rev-parse --short HEAD > "$D/VERSION"

python3 -I - "$W/root" "$OUT" <<'PY'
import gzip, hashlib, io, json, os, sys, tarfile
src, out = sys.argv[1], sys.argv[2]
MTIME = 1767225600
raw = io.BytesIO()
n = 0
with tarfile.open(fileobj=raw, mode="w", format=tarfile.PAX_FORMAT) as tf:
    for dp, dn, fn in os.walk(src):
        dn.sort()
        for name in sorted(dn) + sorted(fn):
            full = os.path.join(dp, name)
            rel = os.path.relpath(full, src).replace(os.sep, "/")
            ti = tarfile.TarInfo(rel)
            ti.uid = ti.gid = 0; ti.uname = ti.gname = "root"; ti.mtime = MTIME
            if os.path.isdir(full):
                ti.type, ti.mode = tarfile.DIRTYPE, 0o755
                tf.addfile(ti)
            else:
                data = open(full, "rb").read()   # binary-safe: natives are copied byte for byte
                ex = "/bin/" in "/" + rel or rel.endswith((".node", "/mqh"))
                ti.size, ti.mode = len(data), 0o755 if ex else 0o644
                tf.addfile(ti, io.BytesIO(data))
            n += 1
    # `mqh` on the PATH of every shell (agents and the terminals)
    ti = tarfile.TarInfo("usr/local/bin/mqh"); ti.type = tarfile.SYMTYPE; ti.linkname = "/opt/mqh/apps/host-daemon/dist/mqh"
    ti.uid = ti.gid = 0; ti.uname = ti.gname = "root"; ti.mtime = MTIME; ti.mode = 0o777
    tf.addfile(ti); n += 1
gz = io.BytesIO()
with gzip.GzipFile(fileobj=gz, mode="wb", mtime=MTIME, compresslevel=9) as g:
    g.write(raw.getvalue())
blob = gz.getvalue()
os.makedirs(out, exist_ok=True)
open(os.path.join(out, "mqh.tar.gz"), "wb").write(blob)
info = {"sha256": hashlib.sha256(blob).hexdigest(), "files": n, "bytes": len(blob), "unpacked": len(raw.getvalue())}
open(os.path.join(out, "mqh.json"), "w").write(json.dumps(info, indent=2) + "\n")
print(json.dumps(info))
PY

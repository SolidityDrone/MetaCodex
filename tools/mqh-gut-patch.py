"""Second half of tools/mqh-gut.sh: unregister what the gut step deleted, so the workspace still builds.

Usage: python3 -I tools/mqh-gut-patch.py <mqh dir> <dropped plugin dir names...>
- removes their entries from the server's built-in plugin registry (BUILTIN_PLUGINS / OFFICIAL_PLUGINS),
- removes their packages from @bb/bundled-plugins,
- drops the examples/ workspace glob (the folder is gone).
Fails loudly when an expected entry is missing, so an upstream change cannot slip through silently.
"""
import json, os, re, sys

mqh, dropped = sys.argv[1], sys.argv[2:]

reg = os.path.join(mqh, "apps/server/src/services/plugins/builtin-registry.ts")
s = open(reg, encoding="utf-8").read()
for name in dropped:
    pat = re.compile(r'\n  \{\n    name: "' + re.escape(name) + r'",\n[^{}]*?\n  \},')
    s, n = pat.subn("", s)
    if n != 1:
        sys.exit(f"registry: expected one entry for {name}, found {n}")
open(reg, "w", encoding="utf-8").write(s)

bp = os.path.join(mqh, "packages/bundled-plugins/package.json")
j = json.load(open(bp, encoding="utf-8"))
# a bundled dependency whose plugin folder no longer exists is one the gut step deleted
names = {k for k in j.get("dependencies", {}) if k.startswith("bb-plugin-")}
kept_dirs = set(os.listdir(os.path.join(mqh, "plugins")))
kept_pkgs = set()
for d in kept_dirs:
    p = os.path.join(mqh, "plugins", d, "package.json")
    if os.path.exists(p):
        kept_pkgs.add(json.load(open(p, encoding="utf-8"))["name"])
removed = sorted(k for k in names if k not in kept_pkgs)
for k in removed:
    del j["dependencies"][k]
open(bp, "w", encoding="utf-8").write(json.dumps(j, indent=2) + "\n")

# Safety rebrand before anything runs: on a development machine the real bb lives in ~/.bb on ports
# 38886/38887; MQH must never read its data or answer (or call) on its ports.
rt = os.path.join(mqh, "packages/config/src/runtime.ts")
r = open(rt, encoding="utf-8").read()
for a, b in [('const BB_PROD_DATA_DIR_NAME = ".bb";', 'const BB_PROD_DATA_DIR_NAME = ".mqh";'),
             ('const BB_DEV_DATA_ROOT_DIR = ".bb-dev";', 'const BB_DEV_DATA_ROOT_DIR = ".mqh-dev";'),
             ("export const BB_PROD_SERVER_PORT = 38886;", "export const BB_PROD_SERVER_PORT = 38990;"),
             ("export const BB_PROD_HOST_DAEMON_PORT = 38887;", "export const BB_PROD_HOST_DAEMON_PORT = 38991;"),
             ('const BB_SQLITE_DATABASE_FILE_NAME = "bb.db";', 'const BB_SQLITE_DATABASE_FILE_NAME = "mqh.db";')]:
    if a not in r:
        sys.exit(f"runtime.ts: {a!r} not found (upstream changed?)")
    r = r.replace(a, b)
open(rt, "w", encoding="utf-8").write(r)
for rel in ["plugins/automations/src/server.ts", "packages/plugin-sdk/src/backend-contract.ts"]:
    p = os.path.join(mqh, rel)
    t = open(p, encoding="utf-8").read()
    if "127.0.0.1:38886" not in t:
        sys.exit(f"{rel}: hard-coded bb port not found (upstream changed?)")
    open(p, "w", encoding="utf-8").write(t.replace("127.0.0.1:38886", "127.0.0.1:38990"))

# pnpm refuses to install when a patched package is no longer in the graph (ERR_PNPM_PATCH_NOT_APPLIED):
# drop the patches of what the gut step removed (mobile, desktop packaging, mermaid in the web app)
UNUSED_PATCHES = ["@react-native-cookies/cookies@6.2.1", "react-native-webview@14.0.1", "@hugeicons/react-native@1.0.16",
                  "mermaid@11.17.2", "app-builder-lib@26.18.0"]
rp = os.path.join(mqh, "package.json")
rj = json.load(open(rp, encoding="utf-8"))
patched = rj.get("pnpm", {}).get("patchedDependencies", {})
for k in UNUSED_PATCHES:
    f = patched.pop(k, None)
    if f and os.path.exists(os.path.join(mqh, f)):
        os.remove(os.path.join(mqh, f))
open(rp, "w", encoding="utf-8").write(json.dumps(rj, indent=2) + "\n")

ws = os.path.join(mqh, "pnpm-workspace.yaml")
w = open(ws, encoding="utf-8").read()
w = re.sub(r'\n\s*# Bundled and example plugins.*?\n\s*- "examples/plugins/\*"', "", w, flags=re.S)
w = w.replace('  - "examples/plugins/*"\n', "")
open(ws, "w", encoding="utf-8").write(w)
print(f"registry: {len(dropped)} entries removed; bundled-plugins: {len(removed)} deps removed ({', '.join(removed)})")

"""Rebrand the user-facing surface of the bb fork to MQH. Idempotent; run after tools/mqh-gut.sh.

Usage: python3 -I tools/mqh-rebrand.py <mqh dir>

What changes (and why it is safe):
- environment variables BB_* -> MQH_*: every reader and writer is inside the fork, renamed together;
  this also keeps a fork process from picking up the real bb's variables on a development machine
- the CLI command: package bin `bb` -> `mqh`, the program name, and "bb <command>" in help texts, hints,
  guides and skills (only before known command names, so code identifiers are untouched)
- workspace hook files .bb-env-setup.sh / .bb-env-teardown.sh -> .mqh-env-*.sh
Internal package names (@bb/*, bb-plugin-*) and the plugin manifest field ("bb") stay as they are: they
are not user-facing, and keeping them makes a re-sync with upstream possible.
"""
import json, os, re, sys

root = sys.argv[1]
TEXT = {".ts", ".tsx", ".js", ".mjs", ".cjs", ".json", ".md", ".sh", ".yaml", ".yml", ".txt", ".html"}
SKIP_DIRS = {"node_modules", "dist", ".turbo", ".git"}
SKIP_FILES = {"pnpm-lock.yaml", "UPSTREAM.md"}
COMMANDS = ("thread|threads|plugin|plugins|terminal|terminals|environment|environments|status|guide|project|projects|"
            "provider|providers|machine|machines|settings|skill|skills|voice|diagnostics|account|automation|automations|"
            "browser|marketplace|server|host-daemon|cli|help|login|logout|open|start|stop|restart|update|version|doctor|"
            "secrets|section|queue|tell|spawn|config|files|file|attach|run|tunnel|mcp|memory|docs|tasks|workflow|workflows")
cmd_re = re.compile(r"(?<![\w$./@-])bb (?=(?:" + COMMANDS + r")\b)")
env_re = re.compile(r"\bBB_([A-Z0-9][A-Z0-9_]*)")
tpl_re = re.compile(r"(?<![\w$./@-])bb \$\{")
changed = 0
for dp, dns, fns in os.walk(root):
    dns[:] = [d for d in dns if d not in SKIP_DIRS]
    for fn in fns:
        if fn in SKIP_FILES or os.path.splitext(fn)[1] not in TEXT:
            continue
        p = os.path.join(dp, fn)
        try:
            s = open(p, encoding="utf-8").read()
        except UnicodeDecodeError:
            continue
        o = s
        s = env_re.sub(r"MQH_\1", s)
        s = cmd_re.sub("mqh ", s)
        s = tpl_re.sub("mqh ${", s)  # "bb ${name}" in help texts and hints built from template strings
        s = s.replace("`bb`", "`mqh`").replace(".bb-env-setup.sh", ".mqh-env-setup.sh").replace(".bb-env-teardown.sh", ".mqh-env-teardown.sh")
        if s != o:
            open(p, "w", encoding="utf-8").write(s)
            changed += 1

cli = os.path.join(root, "apps/cli")
pj = os.path.join(cli, "package.json")
j = json.load(open(pj, encoding="utf-8"))
if "bb" in j.get("bin", {}):
    j["bin"] = {("mqh" if k == "bb" else k): (v.replace("./bin/bb", "./bin/mqh") if k == "bb" else v) for k, v in j["bin"].items()}
    open(pj, "w", encoding="utf-8").write(json.dumps(j, indent=2) + "\n")
if os.path.exists(os.path.join(cli, "bin/bb")):
    os.replace(os.path.join(cli, "bin/bb"), os.path.join(cli, "bin/mqh"))
idx = os.path.join(cli, "src/index.ts")
t = open(idx, encoding="utf-8").read()
if '.name("bb")' in t:
    open(idx, "w", encoding="utf-8").write(t.replace('.name("bb")', '.name("mqh")'))

# The CLI executable is `mqh` everywhere code names it: the daemon's CLI bundle (put on agents' PATH), its
# Windows wrapper, the source-checkout path, usage prefixes, the PATH installer, the ACP client name.
CODE_RENAMES = [
    ("apps/host-daemon/scripts/bundle-manifest.mjs", 'label: "bb cli",', 'label: "mqh cli",'),
    ("apps/host-daemon/scripts/bundle-manifest.mjs", 'outfile: resolve(packageRoot, "dist", "bb"),', 'outfile: resolve(packageRoot, "dist", "mqh"),'),
    ("apps/host-daemon/scripts/bundle-manifest.mjs", "// dist/bb-chunks, which", "// dist/mqh-chunks, which"),
    ("apps/host-daemon/scripts/build-bundles.mjs", 'resolve(packageRoot, "dist", "bb.cmd"),', 'resolve(packageRoot, "dist", "mqh.cmd"),'),
    ("apps/host-daemon/scripts/build-bundles.mjs", "'@echo off\\r\\nnode \"%~dp0bb\" %*\\r\\n'", "'@echo off\\r\\nnode \"%~dp0mqh\" %*\\r\\n'"),
    ("apps/host-daemon/src/runtime-shell-env.ts", 'new URL("../../cli/bin/bb", import.meta.url)', 'new URL("../../cli/bin/mqh", import.meta.url)'),
    ("apps/host-daemon/src/runtime-shell-env.ts", 'return resolve(bbExecutableDirectory, "bb");', 'return resolve(bbExecutableDirectory, "mqh");'),
    ("apps/cli/src/command-resolution.ts", 'const prefix = ["bb", ...invocation.path].join(" ");', 'const prefix = ["mqh", ...invocation.path].join(" ");'),
    ("apps/cli/src/index.ts", '      "node",\n      "bb",\n', '      "node",\n      "mqh",\n'),
    ("apps/cli/src/commands/skill.ts", 'entry.provider ?? "bb",', 'entry.provider ?? "mqh",'),
    ("apps/server/src/services/system/cli-command.ts", 'const CLI_COMMAND_NAME = "bb";', 'const CLI_COMMAND_NAME = "mqh";'),
    ("apps/server/src/services/system/cli-command.ts", '"No installed bb machine CLI is available."', '"No installed mqh machine CLI is available."'),
    ("packages/agent-runtime/src/bridge-protocol-adapter.ts", 'client: { name: "bb", version: "1.0.0" },', 'client: { name: "mqh", version: "1.0.0" },'),
    ("apps/server/src/server.ts", 'app.get("/", (context) => context.text("bb server"));', 'app.get("/", (context) => context.text("mqh server"));'),
]
for rel, a, b in CODE_RENAMES:
    f = os.path.join(root, rel)
    t = open(f, encoding="utf-8").read()
    if b in t:
        continue
    if a not in t:
        sys.exit(f"rebrand: {rel}: {a!r} not found (upstream changed?)")
    open(f, "w", encoding="utf-8").write(t.replace(a, b))
print(f"rebrand: {changed} files changed; CLI is `mqh`")

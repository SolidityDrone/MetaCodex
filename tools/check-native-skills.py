"""Accuracy gate of the quest-native-* skills: every command, flag, path, API name and quoted log line they mention
must exist, and every C++ block must compile.

Usage: python3 -I tools/check-native-skills.py [--ndk <ndk dir>] [--openxr <include dir>]
Checks (against the sources in this repo):
  * `mcx-app <sub>` / its flags            -> the case arms and options of userland/overlay/usr/local/bin/mcx-app
  * `mcx-build --flag`                     -> the options mcx-build parses
  * `mcx-buildd <sub>`                     -> the commands mcx-buildd handles
  * /opt/metacodex/quest-native/... paths  -> the files build-overlay.py lays there
  * mcx::Names, .members, ->members, mcx_app_*/Mcx* -> native/xr/mcx_xr.h, native/runner/mcx_module.h, openxr.h
  * quoted log / RESULT lines              -> the sources that print them
  * ```cpp blocks                          -> compiled with the NDK (-fsyntax-only) against mcx_xr.h
Exit 0 = all good."""
import glob, os, re, subprocess, sys, tempfile

root = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
args = sys.argv[1:]
ndk = args[args.index("--ndk") + 1] if "--ndk" in args else "/usr/lib/android-sdk/ndk/27.1.12297006"
xrinc = args[args.index("--openxr") + 1] if "--openxr" in args else os.environ.get("OPENXR_INCLUDE", os.path.expanduser("~/mcxwork/native/dl-sdk/OpenXR-SDK-release-1.1.65/include"))

skills = sorted(glob.glob(os.path.join(root, "mqh/plugins/bb-guide/skills/quest-native-*")))
docs = {p: open(p, encoding="utf-8").read() for d in skills for p in glob.glob(d + "/**/*.md", recursive=True)}
bin_ = os.path.join(root, "userland/overlay/usr/local/bin")
mcx_app = open(os.path.join(bin_, "mcx-app")).read()
mcx_build = open(os.path.join(bin_, "mcx-build")).read()
mcx_buildd = open(os.path.join(bin_, "mcx-buildd")).read()
xr_h = open(os.path.join(root, "native/xr/mcx_xr.h")).read()
mod_h = open(os.path.join(root, "native/runner/mcx_module.h")).read()
sources = "\n".join(open(p, encoding="utf-8", errors="replace").read() for p in
                    [os.path.join(root, "native/xr/mcx_xr.cpp"), os.path.join(root, "native/runner/host.cpp"),
                     os.path.join(root, "native/examples/nightvision/src/main.cpp"), os.path.join(bin_, "mcx-app"),
                     os.path.join(bin_, "mcx-build"), os.path.join(bin_, "mcx-buildd"), os.path.join(bin_, "mcx-setup")])
oxr = ""
for f in ("openxr.h", "openxr_platform.h"):
    p = os.path.join(xrinc, "openxr", f)
    if os.path.exists(p):
        oxr += open(p).read()
problems = []
checked = {"commands": 0, "paths": 0, "names": 0, "messages": 0, "blocks": 0}


def bad(doc, what):
    problems.append(f"{os.path.relpath(doc, root)}: {what}")


# ------------------------------------------------------------------ commands and flags
app_subs = set(re.findall(r"^  ([a-z-]+)\) ", mcx_app, re.M))
app_flags = set(re.findall(r"\s(--[a-z-]+)\)", mcx_app)) | {"-f"}
build_flags = set(re.findall(r'a == "(--?[a-z-]+)"', mcx_build)) | {"-j"}
buildd_subs = set(re.findall(r'cmd == "([a-z-]+)"', mcx_buildd)) | set(re.findall(r'cmd in \("([a-z-]+)", "([a-z-]+)"\)', mcx_buildd)[0])
for doc, text in docs.items():
    spans = re.findall(r"`([^`\n]+)`", text) + re.findall(r"```(?:bash|sh)?\n(.*?)```", text, re.S)
    for span in spans:
        for m in re.finditer(r"\bmcx-app ([a-z-]+)((?: (?:--?[a-z-]+|<[^>]+>|\[[^\]]*\]|[^\s`|]+))*)", span):
            checked["commands"] += 1
            sub = m.group(1)
            if sub not in app_subs:
                bad(doc, f"mcx-app {sub}: no such command (has: {', '.join(sorted(app_subs))})")
            for fl in re.findall(r"(?<![\w-])(--?[a-z][a-z-]*)", m.group(2)):
                if fl not in app_flags:
                    bad(doc, f"mcx-app {sub} {fl}: no such option")
        for m in re.finditer(r"\bmcx-build (--?[a-z-]+)", span):
            checked["commands"] += 1
            if m.group(1) not in build_flags:
                bad(doc, f"mcx-build {m.group(1)}: no such option")
        for m in re.finditer(r"\bmcx-buildd ([a-z-]+)", span):
            checked["commands"] += 1
            if m.group(1) not in buildd_subs:
                bad(doc, f"mcx-buildd {m.group(1)}: no such command")

# ------------------------------------------------------------------ paths laid by the overlay
laid = set()
subprocess.run([sys.executable, "-I", os.path.join(root, "userland/build-overlay.py")], capture_output=True, check=True)
import tarfile
with tarfile.open(os.path.join(root, "unity/Assets/StreamingAssets/linux/overlay.tar.gz")) as t:
    for n in t.getnames():
        laid.add("/" + n.rstrip("/"))
subprocess.run(["git", "-C", root, "checkout", "--", "unity/Assets/StreamingAssets/linux/overlay.json",
                "unity/Assets/StreamingAssets/linux/overlay.tar.gz"], capture_output=True)
for doc, text in docs.items():
    for p in set(re.findall(r"(/opt/metacodex/quest-native[\w./-]*)", text)):
        checked["paths"] += 1
        p = p.rstrip(".,")
        if p not in laid:
            bad(doc, f"{p}: not laid by the overlay")
    for p in set(re.findall(r"(/opt/(?:openxr|android-ndk|android-sdk)[\w./-]*)", text)):
        checked["paths"] += 1
        if p.rstrip(".,/") not in sources and p.rstrip(".,/") not in mcx_app:
            bad(doc, f"{p}: not set up by mcx-setup")

# ------------------------------------------------------------------ C++ names
known = set(re.findall(r"\b([A-Za-z_]\w*)\b", xr_h + mod_h + oxr))
# names the sources define outside the headers: env variables, dlsym'ed entry points, manifest attributes
manifests = "".join(open(p).read() for p in glob.glob(os.path.join(root, "native/**/AndroidManifest.xml"), recursive=True))
source_words = set(re.findall(r"\b([A-Za-z_]\w*)\b", sources + manifests))
std_ok = {"find", "npos", "c_str", "size", "push_back", "empty", "data", "x", "y", "z", "w", "m", "first", "second", "r", "g", "b", "a"}
for doc, text in docs.items():
    code = re.findall(r"`([^`\n]+)`", text) + re.findall(r"```cpp\n(.*?)```", text, re.S)
    # what the document's own code declares (a recipe's struct fields, locals)
    own = set(re.findall(r"\b(?:bool|float|int|double|auto|XrPosef|XrVector3f|char)\s+(\w+)", "\n".join(re.findall(r"```cpp\n(.*?)```", text, re.S))))
    for c in code:
        for n in re.findall(r"\bmcx::(\w+)", c):
            checked["names"] += 1
            if n not in known:
                bad(doc, f"mcx::{n}: not in mcx_xr.h")
        for n in re.findall(r"\b(mcx_app_\w+|Mcx[A-Z]\w+|MCX_\w+)\b", c):
            checked["names"] += 1
            if n not in known and n not in source_words:
                bad(doc, f"{n}: not in mcx_module.h / mcx_xr.h")
        if re.search(r"[;{}()=]|\bmcx::|\bapp\.|\bf\.|\bcfg\.|\bhost->", c):  # looks like code
            for n in re.findall(r"(?:\.|->)([A-Za-z_]\w*)\b", c):
                if n in ("h", "cpp", "so", "apk", "md", "png", "pem", "keystore", "conf", "xml", "log", "txt", "c", "cc"):
                    continue
                checked["names"] += 1
                if n not in known and n not in std_ok and n not in own and n not in manifests:
                    bad(doc, f".{n}: no such member in mcx_xr.h / mcx_module.h / OpenXR")

# ------------------------------------------------------------------ quoted messages
prefixes = ("RESULT: ", "passthrough: ", "depth: ", "scene: ", "anchors: ", "extension ", "permission ", "dev channel: ",
            "module loaded", "paused: ", "stopped ", "mcx-buildd: ", "warning: ", "mcx-build: ", "OpenXR ", "WAIT", "MISSING",
            "warn ", "Night Vision: ")
# messages built from a format string: the quoted form -> the format text that must be in the sources
FORMATTED = {"verify ok": "RESULT: verify {'ok' if not problems", "scene: 0 objects": 'Log("scene: %zu objects"',
             "extension X: not offered": 'Log("extension %s: not offered',
             "paused: MetaCodex is in the background": 'MetaCodex is in the background (adj {adj})'}
for k, fmt in FORMATTED.items():
    if fmt not in sources:
        problems.append(f"check-native-skills.py: the format of `{k}` ({fmt}) is gone from the sources")
for doc, text in docs.items():
    for span in re.findall(r"`([^`\n]+)`", text):
        if not span.startswith(prefixes):
            continue
        checked["messages"] += 1
        if any(span.replace("RESULT: ", "").startswith(k) for k in FORMATTED):
            continue
        head = re.split(r"\.\.\.|<|\bN\b|\(|:\s*$| -N", span)[0].strip()
        head = head.replace("RESULT: ", "")
        if len(head) < 4:
            continue
        probe = re.sub(r"\s+", " ", head)[:40]
        if probe not in re.sub(r"\s+", " ", sources):
            bad(doc, f"message `{span}`: not printed by the sources (looked for '{probe}')")

# ------------------------------------------------------------------ compile the C++ blocks
cxx = os.path.join(ndk, "toolchains/llvm/prebuilt/linux-x86_64/bin/clang++")
glue = os.path.join(ndk, "sources/android/native_app_glue")
blocks = []
for doc, text in docs.items():
    bs = re.findall(r"```cpp\n(.*?)```", text, re.S)
    if bs:
        blocks.append((doc, bs))
for doc, bs in blocks:
    # a document's blocks form one file (recipes: the app first, then the functions), headers on top
    body = "\n".join(bs)
    if "#include <mcx_xr.h>" not in body:
        body = "#include <mcx_xr.h>\n#include <cmath>\n" + body
    with tempfile.NamedTemporaryFile("w", suffix=".cpp", delete=False) as f:
        f.write(body)
        name = f.name
    checked["blocks"] += len(bs)
    r = subprocess.run([cxx, "--target=aarch64-linux-android32", "-std=c++17", "-fsyntax-only", "-Wall", "-Wno-unused-function",
                        "-I" + os.path.join(root, "native/xr"), "-I" + os.path.join(root, "native/runner"), "-I" + glue,
                        "-I" + xrinc, name], capture_output=True, text=True)
    os.unlink(name)
    if r.returncode != 0:
        bad(doc, "C++ blocks do not compile:\n" + "\n".join(l for l in r.stderr.splitlines() if "error" in l)[:2000])

print(f"checked {len(docs)} files: {checked['commands']} commands, {checked['paths']} paths, {checked['names']} names, "
      f"{checked['messages']} messages, {checked['blocks']} C++ blocks")
for p in problems:
    print("PROBLEM:", p)
print("RESULT:", "ok" if not problems else f"{len(problems)} problem(s)")
sys.exit(1 if problems else 0)

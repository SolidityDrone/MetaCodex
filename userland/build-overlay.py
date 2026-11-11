"""Pack userland/overlay/ into unity/Assets/StreamingAssets/linux/overlay.tar.gz + overlay.json.

Usage: python3 -I userland/build-overlay.py        (from anywhere)
The overlay is our own files laid over the Debian rootfs (scripts now, the control-plane daemon later).
The app applies it at install and again whenever overlay.json's sha256 changes, without touching user
data. Deterministic: sorted entries, root-owned, fixed mtime, explicit modes (files on /mnt/c all look
0777): directories 0755, files 0644, anything under a bin/ or sbin/ directory 0755.
"""
import gzip, hashlib, io, json, os, sys, tarfile

root = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
src = os.path.join(root, "userland", "overlay")
out_dir = os.path.join(root, "unity", "Assets", "StreamingAssets", "linux")
MTIME = 1767225600  # 2026-01-01


def mode_for(rel, is_dir):
    if is_dir:
        return 0o755
    parts = rel.split("/")
    return 0o755 if any(p in ("bin", "sbin") for p in parts[:-1]) or rel.endswith(".sh") else 0o644


def junk(name):
    """Local caches a test run leaves behind (git ignores them): never shipped."""
    return name in ("__pycache__", ".pytest_cache") or name.endswith((".pyc", ".pyo"))


raw = io.BytesIO()
with tarfile.open(fileobj=raw, mode="w", format=tarfile.PAX_FORMAT) as tf:
    entries = []
    for dirpath, dirnames, filenames in os.walk(src):
        dirnames[:] = sorted(d for d in dirnames if not junk(d))
        for d in dirnames:
            entries.append(os.path.join(dirpath, d))
        for f in sorted(filenames):
            if not junk(f):
                entries.append(os.path.join(dirpath, f))
    # files kept elsewhere in the repo, placed into the rootfs too (one source of truth): agent skills, so
    # `claude` / `opencode` in a terminal find them as well as MQH threads do. MetaCodex's own skills come
    # from the MQH guide plugin; third-party ones from userland/agent-skills (tools/sync-agent-skills.sh).
    skills_dst = "root/.claude/skills"
    trees = {}
    mqh_skills = os.path.join(root, "mqh", "plugins", "bb-guide", "skills")
    for name in ("quest-scripting", "quest-gesture-plugins", "metacodex-context", "quest-views", "quest-native-apps"):
        trees[f"{skills_dst}/{name}"] = os.path.join(mqh_skills, name)
    # the template of `mcx-app new` is Hello Native itself (sources only: its build/ folder stays out)
    template = os.path.join(root, "native", "hello-native")
    template_dst = "opt/metacodex/quest-native/template"
    bundled = os.path.join(root, "userland", "agent-skills")
    for group in ("unity", "meta"):
        gdir = os.path.join(bundled, group)
        for name in sorted(os.listdir(gdir)) if os.path.isdir(gdir) else []:
            if os.path.isdir(os.path.join(gdir, name)):
                trees[f"{skills_dst}/{name}"] = os.path.join(gdir, name)
    extra = {}
    for f in ("AndroidManifest.xml", "build.sh"):
        extra[f"{template_dst}/{f}"] = os.path.join(template, f)
    for f in sorted(os.listdir(os.path.join(template, "src"))):
        extra[f"{template_dst}/src/{f}"] = os.path.join(template, "src", f)
    # module apps (`mcx-app new --module`, `mcx-app dev`): the native host + the Runner app, and the module template
    native = os.path.join(root, "native")
    for f in ("host.cpp", "mcx_module.h", "AndroidManifest.xml", "build.conf"):
        extra[f"opt/metacodex/quest-native/runner/{f}"] = os.path.join(native, "runner", f)
    for f in ("AndroidManifest.xml", "build.conf", "src/app.cpp"):
        extra[f"opt/metacodex/quest-native/module-template/{f}"] = os.path.join(native, "module-template", f)
    # the native XR layer (mcx-build compiles it once per toolchain) and its worked example
    for f in ("mcx_xr.h", "mcx_xr.cpp", "mcx_font.h"):
        extra[f"opt/metacodex/quest-native/xr/{f}"] = os.path.join(native, "xr", f)
    for f in ("AndroidManifest.xml", "src/main.cpp", "res/drawable/icon.png"):
        extra[f"opt/metacodex/quest-native/examples/nightvision/{f}"] = os.path.join(native, "examples", "nightvision", f)
    for dst, srcdir in trees.items():
        for dp, dn, fn in os.walk(srcdir):
            dn[:] = sorted(d for d in dn if not junk(d))
            for f in sorted(fn):
                if junk(f):
                    continue
                full = os.path.join(dp, f)
                extra[dst + "/" + os.path.relpath(full, srcdir).replace(os.sep, "/")] = full
    for f in ("SOURCES.md", "LICENSE-unity-skills.md", "LICENSE-meta-agentic-tools"):
        if os.path.isfile(os.path.join(bundled, f)):
            extra[f"{skills_dst}/THIRD-PARTY/{f}"] = os.path.join(bundled, f)
    # the skills this overlay lays: read-only in the app's MQH > SKILLS (each update lays them again)
    shipped = sorted(k.split("/")[-1] for k in trees if k.startswith(skills_dst + "/"))
    generated = {"opt/metacodex/shipped-skills.txt": ("# skills shipped by MetaCodex in /root/.claude/skills (read-only in MQH > SKILLS)\n"
                                                      + "".join(n + "\n" for n in shipped)).encode()}
    extra_dirs = sorted({"/".join(k.split("/")[:i]) for k in list(extra) + list(generated) for i in range(1, len(k.split("/")))})
    entries = [(os.path.relpath(f, src).replace(os.sep, "/"), f) for f in entries]
    entries += [(d, None) for d in extra_dirs if not os.path.isdir(os.path.join(src, d))] + sorted(extra.items()) + sorted(generated.items())
    for rel, full in entries:
        ti = tarfile.TarInfo(rel)
        ti.uid = ti.gid = 0
        ti.uname = ti.gname = "root"
        ti.mtime = MTIME
        if isinstance(full, bytes):
            ti.size, ti.mode = len(full), mode_for(rel, False)
            tf.addfile(ti, io.BytesIO(full))
        elif full is None or os.path.isdir(full):
            ti.type, ti.mode = tarfile.DIRTYPE, mode_for(rel, True)
            tf.addfile(ti)
        else:
            data = open(full, "rb").read()
            if not full.endswith((".png", ".jpg", ".webp", ".so", ".jar")):  # binaries stay as they are
                data = data.replace(b"\r\n", b"\n")
            ti.size, ti.mode = len(data), mode_for(rel, False)
            tf.addfile(ti, io.BytesIO(data))

gz = io.BytesIO()
with gzip.GzipFile(fileobj=gz, mode="wb", mtime=MTIME) as g:
    g.write(raw.getvalue())
blob = gz.getvalue()
os.makedirs(out_dir, exist_ok=True)
open(os.path.join(out_dir, "overlay.tar.gz"), "wb").write(blob)
info = {"sha256": hashlib.sha256(blob).hexdigest(), "files": len(entries), "bytes": len(blob)}
open(os.path.join(out_dir, "overlay.json"), "w").write(json.dumps(info, indent=2) + "\n")
print(json.dumps(info))

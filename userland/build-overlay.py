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
    return 0o755 if any(p in ("bin", "sbin") for p in parts[:-1]) else 0o644


raw = io.BytesIO()
with tarfile.open(fileobj=raw, mode="w", format=tarfile.PAX_FORMAT) as tf:
    entries = []
    for dirpath, dirnames, filenames in os.walk(src):
        dirnames.sort()
        for d in dirnames:
            entries.append(os.path.join(dirpath, d))
        for f in sorted(filenames):
            entries.append(os.path.join(dirpath, f))
    # files kept elsewhere in the repo, placed into the rootfs too (one source of truth): agent skills, so
    # `claude` / `opencode` in a terminal find them as well as MQH threads do. MetaCodex's own skills come
    # from the MQH guide plugin; third-party ones from userland/agent-skills (tools/sync-agent-skills.sh).
    skills_dst = "root/.claude/skills"
    trees = {}
    mqh_skills = os.path.join(root, "mqh", "plugins", "bb-guide", "skills")
    for name in ("quest-scripting", "quest-gesture-plugins", "metacodex-context", "quest-views"):
        trees[f"{skills_dst}/{name}"] = os.path.join(mqh_skills, name)
    bundled = os.path.join(root, "userland", "agent-skills")
    for group in ("unity", "meta"):
        gdir = os.path.join(bundled, group)
        for name in sorted(os.listdir(gdir)) if os.path.isdir(gdir) else []:
            if os.path.isdir(os.path.join(gdir, name)):
                trees[f"{skills_dst}/{name}"] = os.path.join(gdir, name)
    extra = {}
    for dst, srcdir in trees.items():
        for dp, dn, fn in os.walk(srcdir):
            dn.sort()
            for f in sorted(fn):
                full = os.path.join(dp, f)
                extra[dst + "/" + os.path.relpath(full, srcdir).replace(os.sep, "/")] = full
    for f in ("SOURCES.md", "LICENSE-unity-skills.md", "LICENSE-meta-agentic-tools"):
        if os.path.isfile(os.path.join(bundled, f)):
            extra[f"{skills_dst}/THIRD-PARTY/{f}"] = os.path.join(bundled, f)
    extra_dirs = sorted({"/".join(k.split("/")[:i]) for k in extra for i in range(1, len(k.split("/")))})
    entries = [(os.path.relpath(f, src).replace(os.sep, "/"), f) for f in entries]
    entries += [(d, None) for d in extra_dirs if not os.path.isdir(os.path.join(src, d))] + sorted(extra.items())
    for rel, full in entries:
        ti = tarfile.TarInfo(rel)
        ti.uid = ti.gid = 0
        ti.uname = ti.gname = "root"
        ti.mtime = MTIME
        if full is None or os.path.isdir(full):
            ti.type, ti.mode = tarfile.DIRTYPE, mode_for(rel, True)
            tf.addfile(ti)
        else:
            data = open(full, "rb").read().replace(b"\r\n", b"\n")
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

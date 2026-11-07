"""Pack Linux/overlay/ into Assets/StreamingAssets/linux/overlay.tar.gz + overlay.json.

Usage: python3 -I Linux/build-overlay.py        (from anywhere)
The overlay is our own files laid over the Debian rootfs (scripts now, the control-plane daemon later).
The app applies it at install and again whenever overlay.json's sha256 changes, without touching user
data. Deterministic: sorted entries, root-owned, fixed mtime, explicit modes (files on /mnt/c all look
0777): directories 0755, files 0644, anything under a bin/ or sbin/ directory 0755.
"""
import gzip, hashlib, io, json, os, sys, tarfile

root = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
src = os.path.join(root, "Linux", "overlay")
out_dir = os.path.join(root, "Assets", "StreamingAssets", "linux")
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
    for full in entries:
        rel = os.path.relpath(full, src).replace(os.sep, "/")
        ti = tarfile.TarInfo(rel)
        ti.uid = ti.gid = 0
        ti.uname = ti.gname = "root"
        ti.mtime = MTIME
        if os.path.isdir(full):
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

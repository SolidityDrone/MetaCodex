# Usage: python3 -I Tests/corpus/make_synth_tar.py <out.tar.gz>
# Edge cases for TarGzExtractor: PAX long path, symlink, hard link, read-only dir, setuid file, ../ escape.
import io, sys, tarfile

def add(tf, name, kind=tarfile.REGTYPE, mode=0o644, data=b"", link=""):
    ti = tarfile.TarInfo(name)
    ti.type, ti.mode, ti.linkname = kind, mode, link
    ti.size = len(data) if kind == tarfile.REGTYPE else 0
    tf.addfile(ti, io.BytesIO(data) if kind == tarfile.REGTYPE else None)

with tarfile.open(sys.argv[1], "w:gz", format=tarfile.PAX_FORMAT) as tf:
    add(tf, "./", tarfile.DIRTYPE, 0o755)
    add(tf, "deep", tarfile.DIRTYPE, 0o755)
    add(tf, "deep/" + "d" * 120 + "/file-with-a-long-name.txt", data=b"long\n")
    add(tf, "plain.txt", data=b"plain\n")
    add(tf, "deep/link", tarfile.SYMTYPE, 0o777, link="../plain.txt")
    add(tf, "plain-hardlink.txt", tarfile.LNKTYPE, 0o644, link="plain.txt")
    add(tf, "ro-dir", tarfile.DIRTYPE, 0o555)
    add(tf, "setuid-bin", mode=0o4755, data=b"#!/bin/sh\n")
    add(tf, "../evil.txt", data=b"escape\n")
print("ok", sys.argv[1])

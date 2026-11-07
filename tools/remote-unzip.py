#!/usr/bin/env python3
# Extract single members from a remote zip with HTTP range requests (no full download).
# usage: python3 -I remote_unzip.py URL OUTDIR SUFFIX [SUFFIX...]
import sys, zipfile, urllib.request, io, os
class HttpFile(io.RawIOBase):
    def __init__(s, url):
        s.url, s.pos = url, 0
        s.size = int(urllib.request.urlopen(urllib.request.Request(url, method='HEAD')).headers['Content-Length'])
        s.cache = {}
    def seekable(s): return True
    def readable(s): return True
    def tell(s): return s.pos
    def seek(s, off, whence=0):
        s.pos = off if whence == 0 else s.pos + off if whence == 1 else s.size + off
        return s.pos
    def read(s, n=-1):
        if n < 0: n = s.size - s.pos
        n = min(n, s.size - s.pos)
        if n <= 0: return b''
        r = urllib.request.Request(s.url, headers={'Range': 'bytes=%d-%d' % (s.pos, s.pos + n - 1)})
        d = urllib.request.urlopen(r).read(); s.pos += len(d); return d
    def readinto(s, b):
        d = s.read(len(b)); b[:len(d)] = d; return len(d)
url, out, sufs = sys.argv[1], sys.argv[2], sys.argv[3:]
z = zipfile.ZipFile(io.BufferedReader(HttpFile(url), buffer_size=1 << 20))
for info in z.infolist():
    if any(info.filename.endswith(x) for x in sufs):
        dst = os.path.join(out, info.filename.replace('/', '__'))
        with z.open(info) as src, open(dst, 'wb') as f: f.write(src.read())
        print(info.filename, info.file_size, '->', dst)

#!/usr/bin/env python3
"""to-webp.py <png> [width]: writes <png without .png>.webp next to it.

The README shows the WebP files: about 8x smaller than the PNG at the same look,
and not caught by the repo's git LFS rule for *.png (LFS bandwidth would limit
how often the README images can load). Default width 2400 px (1.5x of the
1600 px canvas), Lanczos, quality 92, the slowest (best) encoder method.
"""
import sys
from PIL import Image

src = sys.argv[1]
width = int(sys.argv[2]) if len(sys.argv) > 2 else 2400
im = Image.open(src).convert("RGB")
if im.width != width:
    im = im.resize((width, round(im.height * width / im.width)), Image.LANCZOS)
dst = src[:-4] + ".webp" if src.endswith(".png") else src + ".webp"
im.save(dst, "WEBP", quality=92, method=6)
print(dst)

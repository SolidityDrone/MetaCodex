# tools/brand/key_checkerboard.py <src.png> <out.png>: needs numpy, pillow, scipy (~/sbwork/py). The boxes below are the 1671 x 941 source logo's parts.
# Removes the checkerboard drawn into the MetaCodex logo (RGB, no alpha) -> RGBA PNG, by texture:
# the checker is a ~7.4 px grey pattern (two greys ~0.55 / ~0.78, high local contrast, no colour); chrome is smooth.
import sys, numpy as np
from PIL import Image
from scipy import ndimage as ndi
src, dst = sys.argv[1], sys.argv[2]
im = np.asarray(Image.open(src).convert('RGB')).astype(np.float32) / 255
r, g, b = im[..., 0], im[..., 1], im[..., 2]
v = im.max(2); sat = v - im.min(2)
def mean(x, k): return ndi.uniform_filter(x, k)
k = 7
m = mean(v, k); s = np.sqrt(np.maximum(mean(v * v, k) - m * m, 0))
tex = (s > 0.065) & (mean(sat, k) < 0.07) & (np.abs(m - 0.665) < 0.075)
tex = ndi.binary_closing(tex, iterations=2)
tex = ndi.binary_opening(tex, iterations=1)
# glow over the checker keeps a weaker pattern: same test, scaled by how much grey is left
blue = np.clip((b - r) / 0.85, 0, 1)
left = np.maximum(1 - blue, 0.15)
glowtex = (s > 0.04 * left) & (s < 0.2) & (blue > 0.02) & (blue < 0.85)
bg = tex | (glowtex & ndi.binary_dilation(tex, iterations=6))
bg = ndi.binary_closing(bg, iterations=1)
a = np.where(bg, np.where(blue < 0.04, 0, blue), 1.0)
# soft edge: 1 px blur of alpha at the logo boundary only
edge = ndi.binary_dilation(~bg, iterations=1) & ndi.binary_dilation(bg, iterations=1)
a = np.where(edge, ndi.uniform_filter(a, 3), a)
grey = 0.665
fg = np.where((a[..., None] > 0.02) & bg[..., None], (im - grey * (1 - a[..., None])) / np.maximum(a[..., None], 0.02), im)
out = np.dstack([np.clip(fg, 0, 1), a])
Image.fromarray((out * 255 + 0.5).astype(np.uint8), 'RGBA').save(dst, optimize=True)
print('transparent %.1f%%' % ((a < 0.02).mean() * 100))
# cleanup: only the logo's four parts keep alpha (the source's background has smudges baked in), then small specks
im2 = np.asarray(Image.open(dst)).astype(np.float32) / 255
a = im2[..., 3].copy()
H, W = a.shape
keep = np.zeros_like(a, dtype=bool)
for x0, y0, x1, y1 in ((420, 15, 1255, 495), (75, 500, 1615, 662), (70, 668, 1605, 765), (300, 760, 1375, 822)):
    keep[y0:y1, x0:x1] = True
a = np.where(keep, a, 0)
solid = a > 0.35
lab, n = ndi.label(solid)
area = ndi.sum(np.ones_like(a), lab, index=np.arange(1, n + 1))
small = np.isin(lab, 1 + np.where(area < 40)[0])
a = np.where(ndi.binary_dilation(small, iterations=2) & (im2[..., 2] - im2[..., 0] < 0.1), 0, a)
im2[..., 3] = a
Image.fromarray((im2 * 255 + 0.5).astype(np.uint8), 'RGBA').save(dst, optimize=True)
print('cleanup: %d comps' % n)
# the M's box and the line's box: grey bits that are not the M itself / not on the line go (blue ones stay: stars, glow)
im3 = np.asarray(Image.open(dst)).astype(np.float32) / 255
a = im3[..., 3].copy()
blu = im3[..., 2] - im3[..., 0]
lab, n = ndi.label(a > 0.12)
idx = np.arange(1, n + 1)
area = ndi.sum(np.ones_like(a), lab, idx)
bmean = ndi.mean(blu, lab, idx)
objs = ndi.find_objects(lab)
for i, sl in enumerate(objs):
    if sl is None: continue
    y0, y1, x0, x1 = sl[0].start, sl[0].stop, sl[1].start, sl[1].stop
    inM = y1 <= 495 and x0 >= 420
    inLine = 668 <= y0 and y1 <= 765
    on_line = inLine and y0 <= 730 and y1 >= 705 and (x1 - x0) > 200
    if bmean[i] > 0.12: continue
    if (inM and area[i] < 6000) or (inLine and not on_line):
        a[lab == i + 1] = 0
im3[..., 3] = a
Image.fromarray((im3 * 255 + 0.5).astype(np.uint8), 'RGBA').save(dst, optimize=True)
print('final pass: %d comps' % n)

#!/usr/bin/env python3
"""Builds the keyboard's letter statistics: unity/Assets/MetaCodex/Resources/Typing/letters.bytes

Usage: tools/typing/build_letter_model.py [text files or folders ...]     (WSL; python3, no packages)

The table says how likely each character is after the two before it (KeyDecoder / LetterModel): 28 symbols
(a-z, the space, everything else), 28 x 28 x 28 bytes, each -ln(p) x 32 (255 = p < 0.00035).
Counts of three characters in a row, mixed with the counts of two and of one so nothing is impossible.
Without arguments it reads this project's own writing: the repo's Markdown (docs, README, AGENTS, MQH's docs, the
skills shipped in the userland) - English as it is typed here: sentences, commands, code words.
Only these counts are kept, no text.
"""
import math, os, sys

ROOT = os.path.abspath(os.path.join(os.path.dirname(__file__), "..", ".."))
OUT = os.path.join(ROOT, "unity", "Assets", "MetaCodex", "Resources", "Typing", "letters.bytes")
N = 28
SKIP_DIRS = {"node_modules", ".git", "Library", "Temp", "Bench", "dist", "build", "PackageCache"}


def symbol(ch):
    o = ord(ch)
    if 97 <= o <= 122:
        return o - 97
    if 65 <= o <= 90:
        return o - 65
    if ch in " \n\r\t":
        return 26
    return 27


def files(paths):
    for p in paths:
        if os.path.isfile(p):
            yield p
        elif os.path.isdir(p):
            for d, dirs, names in os.walk(p):
                dirs[:] = [x for x in dirs if x not in SKIP_DIRS]
                for n in names:
                    if n.lower().endswith((".md", ".txt")):
                        yield os.path.join(d, n)


def main():
    paths = sys.argv[1:] or [os.path.join(ROOT, x) for x in ("docs", "README.md", "AGENTS.md", "mqh", "userland", "native", "qvac")]
    tri = [0] * (N * N * N)
    bi = [0] * (N * N)
    uni = [0] * N
    chars = 0
    nfiles = 0
    for f in files(paths):
        try:
            text = open(f, encoding="utf-8", errors="ignore").read()
        except OSError:
            continue
        nfiles += 1
        a, b = 26, 26  # a text starts like a word after a space
        prev_space = True
        for ch in text:
            s = symbol(ch)
            if s == 26:
                if prev_space:
                    continue  # runs of blanks count once
                prev_space = True
            else:
                prev_space = False
            tri[(a * N + b) * N + s] += 1
            bi[b * N + s] += 1
            uni[s] += 1
            a, b = b, s
            chars += 1
    if chars < 200000:
        sys.exit(f"too little text ({chars} characters in {nfiles} files)")
    total = sum(uni)
    out = bytearray(N * N * N)
    for a in range(N):
        for b in range(N):
            ctx3 = sum(tri[(a * N + b) * N + c] for c in range(N))
            ctx2 = sum(bi[b * N + c] for c in range(N))
            # trust three in a row more the more often its two-character start was seen
            l3 = ctx3 / (ctx3 + 40.0) * 0.9
            l2 = (1.0 - l3) * (ctx2 / (ctx2 + 40.0)) * 0.9
            l1 = (1.0 - l3 - l2) * 0.9
            l0 = 1.0 - l3 - l2 - l1
            for c in range(N):
                p = l0 / N + l1 * uni[c] / total
                if ctx2:
                    p += l2 * bi[b * N + c] / ctx2
                if ctx3:
                    p += l3 * tri[(a * N + b) * N + c] / ctx3
                out[(a * N + b) * N + c] = min(255, max(0, round(-math.log(p) * 32)))
    os.makedirs(os.path.dirname(OUT), exist_ok=True)
    with open(OUT, "wb") as fh:
        fh.write(out)
    def lp(x, y, z):
        return math.exp(-out[(symbol(x) * N + symbol(y)) * N + symbol(z)] / 32)
    print(f"{OUT}: {len(out)} bytes from {chars} characters in {nfiles} files")
    print("checks: P(e|th)=%.2f P(u|.q)=%.2f P(space|ng)=%.2f P(t|gi)=%.2f P(s|.l)=%.3f P(a|.l)=%.3f" % (
        lp("t", "h", "e"), lp(" ", "q", "u"), lp("n", "g", " "), lp("g", "i", "t"), lp(" ", "l", "s"), lp(" ", "l", "a")))


if __name__ == "__main__":
    main()

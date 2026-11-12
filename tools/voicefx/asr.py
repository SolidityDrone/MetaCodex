#!/usr/bin/env python3
"""Intelligibility proxy for the voice effect: the service's speech-to-text on each preset's output, word error rate
against the text (tools/voicefx/fx.py, docs/voice.md "Voice effect"). The dev AI service must run
(tools/qvac-dev.sh pc start; url + token in unity/Bench/qvac-dev.json).

  python3 asr.py --make-dry              -> <samples>/asr/dry-<voice>-<n>.wav for every line of texts.txt (voices F1, M1)
  python3 asr.py clean adjutant starship comm adjutant-no-plate ...
                                         -> one WER line per variant ("<preset>-no-<stage>": that stage left out)

Whisper base mishears even the clean voice (about 60 % on these 12 short replies x 2 voices): only differences
between variants count, and about +-3 points is noise.
"""
import sys, os, io, re, copy, json, urllib.request
import numpy as np
sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import fx

REPO = os.path.dirname(os.path.dirname(os.path.dirname(os.path.abspath(__file__))))
HERE = os.path.dirname(os.path.abspath(__file__))
TEXTS = [l.strip() for l in open(os.path.join(HERE, "texts.txt"), encoding="utf-8") if l.strip()]
VOICES = ("F1", "M1")
DRY = os.path.join(fx.OUT, "asr")  # not the listening samples (fx.py render): other texts


def dev():
    return json.load(open(os.path.join(REPO, "unity", "Bench", "qvac-dev.json")))


def post(path, data, ctype):
    d = dev()
    r = urllib.request.Request(d["url"].rstrip("/") + path, data=data, headers={"Authorization": "Bearer " + d["token"], "Content-Type": ctype})
    with urllib.request.urlopen(r, timeout=120) as f: return f.read()


def norm(s): return re.sub(r"[^a-z0-9 ]", "", s.lower().replace("-", " ")).split()


def wer(ref, hyp):
    r, h = norm(ref), norm(hyp); d = list(range(len(h) + 1))
    for i in range(1, len(r) + 1):
        p = d[:]; d[0] = i
        for j in range(1, len(h) + 1): d[j] = min(p[j] + 1, d[j - 1] + 1, p[j - 1] + (r[i - 1] != h[j - 1]))
    return d[len(h)], len(r)


def wavbytes(x):
    b = io.BytesIO(); fx.wavio.write(b, fx.SR, np.clip(np.round(x * 32767), -32768, 32767).astype(np.int16)); return b.getvalue()


def asr(x): return json.loads(post("/v1/audio/transcriptions", wavbytes(x), "audio/wav"))["text"]


NO = {"ring": dict(ring=None), "sub": dict(sub_db=None), "double": dict(double=None), "metal": dict(metal=None), "plate": dict(plate=None),
      "sat": dict(sat=dict(drive=1.0, bias=0, wet=0.0)), "comp": dict(comp=dict(thr=0, ratio=1, att=5, rel=50, makeup=0))}


def variant(name):
    if name == "clean": return None
    if "-no-" in name:
        base, stage = name.split("-no-")
        p = copy.deepcopy(fx.PRESETS[base])
        if stage == "shimmer": p["plate"]["shimmer_db"] = None
        else: p.update(NO[stage])
        return p
    return fx.PRESETS[name]


def main(args):
    os.makedirs(DRY, exist_ok=True)
    if args[:1] == ["--make-dry"]:
        for v in VOICES:
            for i, t in enumerate(TEXTS, 1):
                w = post("/v1/audio/speech", json.dumps({"input": t, "voice": v}).encode(), "application/json")
                open(os.path.join(DRY, f"dry-{v}-{i}.wav"), "wb").write(w)
        print(f"{len(TEXTS) * len(VOICES)} dry replies in {DRY}")
        return
    dry = {(v, i): fx.read_wav(os.path.join(DRY, f"dry-{v}-{i}.wav")) for v in VOICES for i in range(1, len(TEXTS) + 1)}
    for name in args or ["clean", "adjutant", "starship", "comm"]:
        p = variant(name); e = n = 0
        for (v, i), d in dry.items():
            y = fx.finish(fx.trim(d), high_pass=False) if p is None else fx.finish(fx.chain(d, p))
            a, b = wer(TEXTS[i - 1], asr(y)); e += a; n += b
        print(f"{name:28s} WER {100 * e / n:5.1f}%  ({e}/{n})", flush=True)


if __name__ == "__main__":
    main(sys.argv[1:])

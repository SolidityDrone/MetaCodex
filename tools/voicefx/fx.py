#!/usr/bin/env python3
"""Sci-fi voice post-processing prototype for MetaCodex spoken replies.

Input: the AI service's WAV (Supertonic 3, 44.1 kHz mono, 16 bit). Output: a float chain result.
Everything here is time-domain (biquads, delay lines, comb/allpass, a block compressor); no FFT.
The app's code is unity/Assets/MetaCodex/Runtime/AI/VoiceFx.cs: PRESETS below map 1:1 to VoiceFxParams.For, the
stages to its methods (retune here, listen, then copy the numbers there). tests/voicefx compares the C# output
with this script's renders (correlation > 0.97 per preset). README.md: the files and the samples folder.

Needs numpy + scipy. Samples folder: $MCX_VOICEFX_SAMPLES (default mcx-voicefx-samples in the Windows user folder of this checkout), with the
service's replies as dry-<voice>-<n>.wav (the 4 texts in README.md; voices F1 and M1).
Run:  python3 fx.py render      -> <samples>/<preset>-<voice>-<n>.wav (+ clean = effect off)
      python3 fx.py bench       -> timing of every stage
      python3 fx.py metrics     -> loudness / peak / clip / spectral table
"""
import sys, os, time, glob, json
import numpy as np
from scipy.signal import lfilter, sosfilt, tf2sos
from scipy.ndimage import maximum_filter1d, uniform_filter1d
import scipy.io.wavfile as wavio

SR = 44100
HERE = os.path.dirname(os.path.abspath(__file__))
def _samples_dir():
    """$MCX_VOICEFX_SAMPLES, else mcx-voicefx-samples in the Windows user folder this checkout lives in, else ~."""
    if os.environ.get("MCX_VOICEFX_SAMPLES"): return os.environ["MCX_VOICEFX_SAMPLES"]
    import re
    m = re.match(r"^(/mnt/[a-z]/Users/[^/]+)/", HERE)
    return os.path.join(m.group(1) if m else os.path.expanduser("~"), "mcx-voicefx-samples")


OUT = _samples_dir()


# ----------------------------------------------------------------------------------------------
# helpers

def db(x): return 10.0 ** (x / 20.0)


def read_wav(path):
    r, x = wavio.read(path)
    assert r == SR and x.dtype == np.int16, (r, x.dtype)
    return x.astype(np.float64) / 32768.0


def write_wav(path, x):
    y = np.clip(np.round(x * 32767.0), -32768, 32767).astype(np.int16)
    wavio.write(path, SR, y)


def biquad(kind, f0, q=0.7071, gain_db=0.0, fs=SR):
    """RBJ cookbook -> (b, a). kinds: hp lp bp peak hshelf lshelf"""
    A = 10 ** (gain_db / 40.0)
    w0 = 2 * np.pi * f0 / fs
    c, s = np.cos(w0), np.sin(w0)
    al = s / (2 * q)
    if kind == "hp":
        b = [(1 + c) / 2, -(1 + c), (1 + c) / 2]; a = [1 + al, -2 * c, 1 - al]
    elif kind == "lp":
        b = [(1 - c) / 2, 1 - c, (1 - c) / 2]; a = [1 + al, -2 * c, 1 - al]
    elif kind == "bp":  # constant 0 dB peak gain
        b = [al, 0, -al]; a = [1 + al, -2 * c, 1 - al]
    elif kind == "peak":
        b = [1 + al * A, -2 * c, 1 - al * A]; a = [1 + al / A, -2 * c, 1 - al / A]
    elif kind == "hshelf":
        sq = 2 * np.sqrt(A) * al
        b = [A * ((A + 1) + (A - 1) * c + sq), -2 * A * ((A - 1) + (A + 1) * c), A * ((A + 1) + (A - 1) * c - sq)]
        a = [(A + 1) - (A - 1) * c + sq, 2 * ((A - 1) - (A + 1) * c), (A + 1) - (A - 1) * c - sq]
    elif kind == "lshelf":
        sq = 2 * np.sqrt(A) * al
        b = [A * ((A + 1) - (A - 1) * c + sq), 2 * A * ((A - 1) - (A + 1) * c), A * ((A + 1) - (A - 1) * c - sq)]
        a = [(A + 1) + (A - 1) * c + sq, -2 * ((A - 1) + (A + 1) * c), (A + 1) + (A - 1) * c - sq]
    else:
        raise ValueError(kind)
    b = np.array(b) / a[0]; a = np.array(a) / a[0]
    return b, a


def filt(x, *bas):
    for b, a in bas:
        x = lfilter(b, a, x)
    return x


def delay(x, samples):
    if samples <= 0: return x.copy()
    return np.concatenate([np.zeros(samples), x])[: len(x)]


def delay_ms(x, ms): return delay(x, int(round(ms * SR / 1000)))


def pad_to(x, n):
    return np.concatenate([x, np.zeros(max(0, n - len(x)))]) if len(x) < n else x[:n]


def mix(*parts):
    n = max(len(p) for p, _ in parts)
    y = np.zeros(n)
    for p, g in parts:
        y[: len(p)] += p * g
    return y


# ----------------------------------------------------------------------------------------------
# loudness (ITU-R BS.1770 K-weighting + gating, 400 ms blocks) : used to make presets comparable

def k_weight(x):
    # pyloudnorm's constants, RBJ shelf + 2nd-order highpass
    b1, a1 = biquad("hshelf", 1681.9744509555319, 0.7071752369554196, 3.999843853973347)
    K = np.tan(np.pi * 38.13547087613982 / SR); Q = 0.5003270373253953
    a0 = 1 + K / Q + K * K
    b2 = np.array([1, -2, 1.0]); a2 = np.array([1, 2 * (K * K - 1) / a0, (1 - K / Q + K * K) / a0])
    return filt(x, (b1, a1), (b2, a2))


def lufs(x):
    z = k_weight(x)
    blk = int(0.4 * SR); hop = int(0.1 * SR)
    if len(z) < blk: z = np.concatenate([z, np.zeros(blk - len(z))])
    p = np.array([np.mean(z[i:i + blk] ** 2) for i in range(0, len(z) - blk + 1, hop)])
    l = -0.691 + 10 * np.log10(p + 1e-12)
    g1 = p[l > -70]
    if len(g1) == 0: return -70.0
    rel = -0.691 + 10 * np.log10(g1.mean()) - 10
    g2 = p[l > max(rel, -70)]
    return -0.691 + 10 * np.log10(g2.mean()) if len(g2) else -70.0


# ----------------------------------------------------------------------------------------------
# stages

def trim(x, lead_ms=25, tail_ms=20, thr=db(-60)):
    """The service pads ~0.4 s of digital silence in front and 0.4-0.7 s behind: cut it (saves latency)."""
    idx = np.where(np.abs(x) > thr)[0]
    if len(idx) == 0: return x
    a = max(0, idx[0] - int(lead_ms * SR / 1000)); b = min(len(x), idx[-1] + int(tail_ms * SR / 1000))
    return x[a:b]


def pitch_shift(x, ratio, win_ms=35.0):
    """Two-tap crossfaded delay-line (granular) pitch shifter. ratio 1.0 = unchanged. Time stays the same."""
    N = len(x); W = win_ms * SR / 1000.0
    n = np.arange(N, dtype=np.float64)
    ph = np.mod(n * (1.0 - ratio) / W, 1.0)
    out = np.zeros(N)
    for off in (0.0, 0.5):
        p = np.mod(ph + off, 1.0)
        w = 0.5 - 0.5 * np.cos(2 * np.pi * p)  # Hann: the two taps always sum to 1
        pos = n - (p * W + 2.0)
        i0 = np.floor(pos).astype(np.int64); f = pos - i0
        xi = np.concatenate([np.zeros(2), x])
        a = xi[np.clip(i0 + 2, 0, N + 1)]; b = xi[np.clip(i0 + 3, 0, N + 1)]
        out += w * (a * (1 - f) + b * f)
    return out


def mod_delay(x, base_ms, depth_ms, rate_hz, phase=0.0):
    """Delay line with a sine LFO (flanger / chorus voice), linear interpolation."""
    N = len(x); n = np.arange(N)
    d = (base_ms + depth_ms * np.sin(2 * np.pi * rate_hz * n / SR + phase)) * SR / 1000.0
    pos = n - d
    i0 = np.floor(pos).astype(np.int64); f = pos - i0
    xi = np.concatenate([np.zeros(2), x])
    a = xi[np.clip(i0 + 2, 0, N + 1)]; b = xi[np.clip(i0 + 3, 0, N + 1)]
    return a * (1 - f) + b * f


def comb_fb(x, ms, g):
    """Feedback comb: a metallic tube resonance with peaks every 1000/ms Hz."""
    D = max(2, int(round(ms * SR / 1000)))
    b = np.zeros(D + 1); b[D] = 1.0
    a = np.zeros(D + 1); a[0] = 1.0; a[D] = -g
    # block form, same maths as lfilter(b, a) but O(N)
    y = np.zeros(len(x)); xp = np.concatenate([np.zeros(D), x])
    for k in range(0, len(x), D):
        e = min(k + D, len(x))
        prev = y[k - D:e - D] if k >= D else np.zeros(e - k)
        y[k:e] = xp[k:e] + g * prev
    return y


def ring_mod(x, f, depth):
    """x * ((1-depth) + depth*sin): depth 1 = full ring modulation (inharmonic sidebands)."""
    n = np.arange(len(x))
    return x * ((1 - depth) + depth * np.sin(2 * np.pi * f * n / SR))


def compressor(x, thr_db, ratio, att_ms, rel_ms, makeup_db=0.0, block=32):
    """Feed-forward peak compressor, gain computed per 32-sample block (0.7 ms) and interpolated."""
    nb = (len(x) + block - 1) // block
    pad = np.concatenate([x, np.zeros(nb * block - len(x))])
    pk = np.abs(pad).reshape(nb, block).max(axis=1)
    ca = np.exp(-block / (att_ms * SR / 1000)); cr = np.exp(-block / (rel_ms * SR / 1000))
    env = 0.0; g = np.empty(nb)
    for i in range(nb):
        v = pk[i]
        env = ca * env + (1 - ca) * v if v > env else cr * env + (1 - cr) * v
        lv = 20 * np.log10(env + 1e-9)
        over = max(0.0, lv - thr_db)
        g[i] = -over * (1 - 1.0 / ratio)
    gl = db(g + makeup_db)
    gain = np.interp(np.arange(len(x)), (np.arange(nb) + 0.5) * block, gl)
    return x * gain


def saturate(x, drive, bias=0.0):
    """tanh waveshaper, unity small-signal gain. bias > 0 adds a little even harmonic (tube-ish warmth)."""
    y = np.tanh(drive * (x + bias)) - np.tanh(drive * bias)
    return y / (drive * (1 - np.tanh(drive * bias) ** 2))


def decimate_hold(x, fs_new, lp_ratio=0.9):
    """Digital 'comm' grit: low-pass, then sample-and-hold at fs_new (images fold back as faint buzz)."""
    b, a = biquad("lp", fs_new * 0.5 * lp_ratio, 0.7071)
    y = filt(x, (b, a), (b, a))
    step = SR / fs_new
    idx = np.floor(np.floor(np.arange(len(y)) / step) * step).astype(np.int64)
    return y[np.clip(idx, 0, len(y) - 1)]


def _damped_comb(x, D, g, damp):
    """Freeverb comb: delay D with a one-pole low-pass in the feedback path (high frequencies die first)."""
    y = np.zeros(len(x)); zi = np.zeros(1)
    lpb = np.array([1 - damp]); lpa = np.array([1.0, -damp])
    for k in range(0, len(x), D):
        e = min(k + D, len(x))
        if k >= D:
            v, zi = lfilter(lpb, lpa, y[k - D:e - D], zi=zi)
        else:
            v = np.zeros(e - k)
        y[k:e] = x[k:e] + g * v
    return y


def _allpass(x, D, g):
    y = np.zeros(len(x)); xp = np.concatenate([np.zeros(D), x])
    for k in range(0, len(x), D):
        e = min(k + D, len(x))
        yprev = y[k - D:e - D] if k >= D else np.zeros(e - k)
        y[k:e] = -g * x[k:e] + xp[k:e] + g * yprev
    return y


COMB_D = [1116, 1277, 1422, 1617]  # mutually non-harmonic, ~25-37 ms (Freeverb values at 44.1 kHz, 4 of 8)
AP_D = [556, 441]


def plate(x, rt60, predelay_ms=10.0, damp=0.25, room=1.0, tail_s=None, bright_db=3.0):
    """Short bright reverb. x -> wet only (mix outside). 4 damped combs + 2 allpass, high-passed so the wet stays clear."""
    tail = int((tail_s if tail_s is not None else rt60) * SR)
    xin = np.concatenate([delay_ms(x, predelay_ms), np.zeros(tail)])
    acc = np.zeros(len(xin))
    for D0 in COMB_D:
        D = int(D0 * room)
        g = 10 ** (-3.0 * D / (rt60 * SR))
        acc += _damped_comb(xin, D, g, damp)
    acc *= 0.25
    for D in AP_D:
        acc = _allpass(acc, int(D * room), 0.5)
    hp = biquad("hp", 350, 0.7071); sh = biquad("hshelf", 4500, 0.7071, bright_db)
    return filt(acc, hp, sh)


def limiter(x, ceiling_db=-1.0, look_ms=2.0):
    """Look-ahead brick-wall: running max over the window, box-smoothed gain (never above the ceiling)."""
    L = max(1, int(look_ms * SR / 1000)); c = db(ceiling_db)
    pk = maximum_filter1d(np.abs(x), size=2 * L + 1, mode="nearest")
    g = np.minimum(1.0, c / np.maximum(pk, 1e-9))
    g = uniform_filter1d(g, size=2 * L + 1, mode="nearest")
    return x * g


def fade_out(x, ms):
    n = min(len(x), int(ms * SR / 1000)); y = x.copy()
    y[len(y) - n:] *= np.cos(np.linspace(0, np.pi / 2, n)) ** 2
    return y


def speech_envelope(x, ms=12):
    return uniform_filter1d(np.abs(x), size=int(ms * SR / 1000), mode="nearest")


# ----------------------------------------------------------------------------------------------
# presets. Every number below is a constructor argument of a C# class later.

PRESETS = {
    # tasteful default: same voice, "a computer that cares": a touch of air, faint double, glue, small plate
    "adjutant": dict(
        hp=80, presence=(2800, 1.0, 2.0), air=(6500, 3.0),
        double=dict(cents=(+9, -11), ms=(11.0, 17.0), level_db=-21.0),
        metal=dict(comb_ms=1.3, comb_g=0.30, comb_mix_db=-19.0, flange=(0.9, 0.35, 0.22, -20.0)),
        sub_db=None, ring=None, band=None, crush=None, squelch=None,
        comp=dict(thr=-22.0, ratio=2.5, att=6.0, rel=90.0, makeup=3.0),
        sat=dict(drive=1.4, bias=0.03, wet=0.7),
        plate=dict(rt60=0.55, predelay=9.0, damp=0.35, mix_db=-22.0, shimmer_db=None),
    ),
    # starship computer: clearly synthetic, still clean: sub layer, metal ring, shimmer tail, longer plate
    "starship": dict(
        hp=90, presence=(2600, 1.1, 2.5), air=(6000, 4.0),
        double=dict(cents=(+12, -14), ms=(12.0, 19.0), level_db=-17.0),
        metal=dict(comb_ms=1.1, comb_g=0.45, comb_mix_db=-15.0, flange=(0.8, 0.30, 0.30, -17.0)),
        sub_db=-19.0,
        ring=dict(f=1900.0, depth=1.0, mix_db=-27.0, hp=2200),
        band=None, crush=None, squelch=None,
        comp=dict(thr=-24.0, ratio=3.2, att=5.0, rel=110.0, makeup=4.0),
        sat=dict(drive=2.0, bias=0.05, wet=0.8),
        plate=dict(rt60=0.85, predelay=12.0, damp=0.3, mix_db=-18.5, shimmer_db=-27.0),
    ),
    # comm channel: narrow, gritty, squelch bursts, no tail to speak of
    "comm": dict(
        hp=80, presence=(2300, 1.2, 3.0), air=None,
        double=None,
        metal=dict(comb_ms=1.6, comb_g=0.28, comb_mix_db=-17.0, flange=None),
        sub_db=None, ring=None,
        band=dict(lo=380, hi=3500, order=2),
        crush=dict(fs=8000, post_lp=6000),
        squelch=dict(noise_db=-50.0, burst_ms=70, burst_db=-26.0),
        comp=dict(thr=-26.0, ratio=4.0, att=3.0, rel=70.0, makeup=5.0),
        sat=dict(drive=2.6, bias=0.0, wet=0.9),
        plate=dict(rt60=0.22, predelay=4.0, damp=0.5, mix_db=-18.0, shimmer_db=None),
    ),
}


def chain(x, p, seed=7):
    """x: float mono @44.1 kHz (the service's reply). Returns the processed signal, longer by the reverb tail."""
    x = trim(x)
    x = filt(x, biquad("hp", p["hp"], 0.7071)) * db(-4.0)           # DC / rumble off; headroom (the TTS already clips)
    x = filt(x, biquad("peak", *p["presence"])) if p["presence"] else x
    if p.get("air"): x = filt(x, biquad("hshelf", p["air"][0], 0.7071, p["air"][1]))
    tail = int(max(p["plate"]["rt60"] if p["plate"] else 0, 0.2) * SR)
    x = np.concatenate([x, np.zeros(tail)])
    voice = x
    layers = [(voice, 1.0)]

    d = p["double"]
    if d:  # faint detuned doubles: the "more than one processor speaking" shimmer, no pitch jump
        for c, ms in zip(d["cents"], d["ms"]):
            layers.append((delay_ms(pitch_shift(voice, 2 ** (c / 1200.0)), ms), db(d["level_db"])))
    if p["sub_db"] is not None:  # octave-down body, low-passed
        sub = filt(pitch_shift(voice, 0.5, 45.0), biquad("lp", 500, 0.7071))
        layers.append((sub, db(p["sub_db"])))
    if p["ring"]:
        r = p["ring"]
        hi = filt(voice, biquad("hp", r["hp"], 0.7071))
        layers.append((ring_mod(hi, r["f"], r["depth"]), db(r["mix_db"])))
    m = p["metal"]
    if m:
        layers.append((comb_fb(voice, m["comb_ms"], m["comb_g"]) - voice, db(m["comb_mix_db"])))
        if m["flange"]:
            base, depth, rate, lvl = m["flange"]
            layers.append((mod_delay(voice, base + depth, depth, rate), db(lvl)))
    y = mix(*layers)
    if p["band"]:
        b = p["band"]
        y = filt(y, biquad("hp", b["lo"], 0.9), biquad("hp", b["lo"], 0.9), biquad("lp", b["hi"], 0.9), biquad("lp", b["hi"], 0.9))
    if p["crush"]:
        y = decimate_hold(y, p["crush"]["fs"])
        y = filt(y, biquad("lp", p["crush"]["post_lp"], 0.7071))   # tame the hold's images above the radio band
    c = p["comp"]
    y = compressor(y, c["thr"], c["ratio"], c["att"], c["rel"], c["makeup"])
    s = p["sat"]
    y = (1 - s["wet"]) * y + s["wet"] * saturate(y, s["drive"], s["bias"])
    if p["plate"]:
        pl = p["plate"]
        send = y
        wet = plate(send, pl["rt60"], pl["predelay"], pl["damp"], tail_s=0.0)
        if pl["shimmer_db"] is not None:  # octave-up copy of the send, only inside the tail
            sh = pitch_shift(filt(send, biquad("hp", 700, 0.7071)), 2.0, 30.0)
            wet = mix((wet, 1.0), (plate(sh, pl["rt60"] * 1.2, pl["predelay"] + 6, 0.2, tail_s=0.0), db(pl["shimmer_db"] - pl["mix_db"])))
        y = mix((y, 1.0), (wet, db(pl["mix_db"])))
    if p["squelch"]:
        q = p["squelch"]; rng = np.random.RandomState(seed)
        env = speech_envelope(y, 25)
        nz = filt(rng.randn(len(y)), biquad("hp", 600, 0.7071), biquad("lp", 5000, 0.7071))
        nz *= db(q["noise_db"]) * np.clip(env / (env.max() + 1e-9) * 8, 0, 1)   # static rides the speech
        bl = int(q["burst_ms"] * SR / 1000)
        bn = filt(rng.randn(bl), biquad("hp", 900, 0.7071), biquad("lp", 6000, 0.7071)) * db(q["burst_db"]) * np.hanning(bl) ** 0.5
        pre = np.concatenate([bn, np.zeros(int(0.04 * SR))])
        y = np.concatenate([pre, y + nz])
        post = np.concatenate([bn[::-1][: int(bl * 0.7)] * 0.8])
        i = int(np.where(np.abs(y) > db(-50))[0][-1]) + int(0.015 * SR)
        y = pad_to(y, i + len(post)); y[i:i + len(post)] += post
    y = fade_out(y, 40)
    return y


TARGET_LUFS = -18.0
CEILING_DB = -1.0


def finish(y, target=TARGET_LUFS, ceiling=CEILING_DB, high_pass=True):
    """Loudness-match (BS.1770-ish), then brick-wall to the ceiling; iterate so the limiter's loss is made up.
    high_pass=False for off ('clean'): like VoiceFx.Finish, off is the dry voice at another gain (a 60 Hz high-pass
    changes nothing audible but turns the phase: correlation 0.94-0.98 with the dry voice instead of 0.999+)."""
    if high_pass: y = filt(y, biquad("hp", 60, 0.7071))
    g = 1.0
    for _ in range(4):
        z = limiter(y * g, ceiling)
        l = lufs(z)
        g *= db(target - l)
        if abs(l - target) < 0.15: break
    return np.clip(limiter(y * g, ceiling), -db(ceiling), db(ceiling))


def render_all(voices=("F1", "M1"), n_texts=4, out=OUT):
    os.makedirs(out, exist_ok=True)
    rows = []
    for v in voices:
        for i in range(1, n_texts + 1):
            dry = read_wav(os.path.join(out, f"dry-{v}-{i}.wav"))
            clean = finish(trim(dry), high_pass=False)      # 'clean' = effect off: trimmed, level-matched
            write_wav(os.path.join(out, f"clean-{v}-{i}.wav"), clean)
            rows.append(("clean", v, i, clean))
            for name, p in PRESETS.items():
                y = finish(chain(dry, p))
                write_wav(os.path.join(out, f"{name}-{v}-{i}.wav"), y)
                rows.append((name, v, i, y))
    return rows


def metrics(rows=None):
    if rows is None:
        rows = []
        for f in sorted(glob.glob(OUT + "/*.wav")):
            nm = os.path.basename(f)[:-4].split("-")
            rows.append((nm[0], nm[1], int(nm[2]), read_wav(f)))
    print(f"{'preset':9s} {'voice':3s} n  {'dur s':>6s} {'LUFS':>6s} {'peak dBFS':>9s} {'crest dB':>8s} {'clip':>4s} {'centroid Hz':>11s} {'rms99 dB':>8s}")
    for name, v, i, y in rows:
        pk = np.abs(y).max()
        rms = np.sqrt(np.mean(y ** 2))
        S = np.abs(np.fft.rfft(y * np.hanning(len(y)))) ** 2
        fr = np.fft.rfftfreq(len(y), 1 / SR)
        cen = (S * fr).sum() / S.sum()
        print(f"{name:9s} {v:3s} {i}  {len(y)/SR:6.2f} {lufs(y):6.1f} {20*np.log10(pk):9.2f} {20*np.log10(pk/rms):8.1f} {(np.abs(y)>=0.9995).sum():4d} {cen:11.0f} {20*np.log10(rms):8.1f}")


def bench(reps=3):
    """CPU seconds per second of audio, per stage (numpy/scipy, one thread, 44.1 kHz)."""
    dry = read_wav(os.path.join(OUT, "dry-F1-3.wav"))
    x = trim(dry); sec = len(x) / SR
    res = {}

    def t(name, fn):
        best = 1e9
        for _ in range(reps):
            c = time.process_time(); fn(); best = min(best, time.process_time() - c)
        res[name] = best / sec
    t("biquad x1", lambda: filt(x, biquad("peak", 2800, 1.0, 2.0)))
    t("pitch_shift", lambda: pitch_shift(x, 1.005))
    t("mod_delay", lambda: mod_delay(x, 1.0, 0.3, 0.3))
    t("comb_fb", lambda: comb_fb(x, 1.3, 0.3))
    t("ring_mod", lambda: ring_mod(x, 1900, 1.0))
    t("compressor", lambda: compressor(x, -22, 2.5, 6, 90, 3))
    t("saturate", lambda: saturate(x, 1.4, 0.03))
    t("plate (no tail)", lambda: plate(x, 0.55, tail_s=0.0))
    t("limiter", lambda: limiter(x))
    t("lufs (offline only)", lambda: lufs(x))
    for name, p in PRESETS.items():
        c = time.process_time(); y = finish(chain(dry, p)); res["CHAIN " + name] = (time.process_time() - c) / sec
        c = time.process_time(); y = chain(dry, p); res["chain-only " + name] = (time.process_time() - c) / sec
    print(f"input {sec:.2f} s of speech")
    for k, v in res.items():
        print(f"{k:24s} {v*1000:8.1f} ms CPU per second of audio")
    return res


if __name__ == "__main__":
    cmd = sys.argv[1] if len(sys.argv) > 1 else "render"
    if cmd == "render":
        rows = render_all()
        metrics(rows)
    elif cmd == "metrics":
        metrics()
    elif cmd == "bench":
        bench()

using System;
using System.Collections.Generic;

namespace MetaCodex.AI
{
    /// The voice effect presets (setting voice.fx; VOICE > SOUND: OFF, AI, SHIP, COMM).
    public enum VoiceFxPreset { Off = 0, Adjutant = 1, Starship = 2, Comm = 3 }

    /// All numbers of one preset (tools/voicefx/fx.py PRESETS, 1:1). Null for off.
    public sealed class VoiceFxParams
    {
        public float Hp = 80f;
        public float PresenceHz = 2800f, PresenceQ = 1f, PresenceDb = 2f;
        public float AirHz = 6500f, AirDb = 3f;                            // AirDb 0 = off
        public float[] DoubleCents; public float[] DoubleMs; public float DoubleDb = -17f;
        public float CombMs, CombG, CombMixDb = -19f;                      // CombMs 0 = off
        public float FlBase, FlDepth, FlRate, FlDb = -20f;                 // FlBase 0 = off
        public float SubDb;                                                 // 0 = off
        public float RingHz, RingMixDb = -23f, RingHp = 2200f;             // RingHz 0 = off
        public float BandLo, BandHi;                                        // BandLo 0 = off
        public float CrushFs;                                               // 0 = off
        public float NoiseDb, BurstMs, BurstDb = -24f;                     // BurstMs 0 = off
        public float CompThr = -22f, CompRatio = 2.5f, CompAtt = 6f, CompRel = 90f, CompMakeup = 3f;
        public float SatDrive = 1.4f, SatBias = 0.03f, SatWet = 0.7f;
        public float PlateRt60 = 0.55f, PlatePre = 9f, PlateDamp = 0.35f, PlateMixDb = -20f, ShimmerDb; // Rt60 0 = off

        public static VoiceFxParams For(VoiceFxPreset p)
        {
            switch (p)
            {
                case VoiceFxPreset.Adjutant:
                    // a calm computer, clearly the same voice: air, a faint double, light metal, glue, a small bright room
                    return new VoiceFxParams
                    {
                        DoubleCents = new[] { 9f, -11f }, DoubleMs = new[] { 11f, 17f }, DoubleDb = -21f,
                        PlateMixDb = -22f,
                        CombMs = 1.3f, CombG = 0.30f, CombMixDb = -19f, FlBase = 0.9f, FlDepth = 0.35f, FlRate = 0.22f, FlDb = -20f,
                    };
                case VoiceFxPreset.Starship:
                    // clearly synthetic: sub octave, ring-mod sheen, stronger doubles and comb, shimmer in a longer tail
                    return new VoiceFxParams
                    {
                        Hp = 90f, PresenceHz = 2600f, PresenceQ = 1.1f, PresenceDb = 2.5f, AirHz = 6000f, AirDb = 4f,
                        DoubleCents = new[] { 12f, -14f }, DoubleMs = new[] { 12f, 19f }, DoubleDb = -17f,
                        CombMs = 1.1f, CombG = 0.45f, CombMixDb = -15f, FlBase = 0.8f, FlDepth = 0.30f, FlRate = 0.30f, FlDb = -17f,
                        SubDb = -19f, RingHz = 1900f, RingMixDb = -27f, RingHp = 2200f,
                        CompThr = -24f, CompRatio = 3.2f, CompAtt = 5f, CompRel = 110f, CompMakeup = 4f,
                        SatDrive = 2f, SatBias = 0.05f, SatWet = 0.8f,
                        PlateRt60 = 0.85f, PlatePre = 12f, PlateDamp = 0.3f, PlateMixDb = -18.5f, ShimmerDb = -27f,
                    };
                case VoiceFxPreset.Comm:
                    // a comm channel: 380-3500 Hz, 8 kHz grit, squelch at both ends, almost no tail
                    return new VoiceFxParams
                    {
                        PresenceHz = 2300f, PresenceQ = 1.2f, PresenceDb = 3f, AirDb = 0f,
                        CombMs = 1.6f, CombG = 0.28f, CombMixDb = -17f,
                        BandLo = 380f, BandHi = 3500f, CrushFs = 8000f,
                        NoiseDb = -50f, BurstMs = 70f, BurstDb = -26f,
                        CompThr = -26f, CompRatio = 4f, CompAtt = 3f, CompRel = 70f, CompMakeup = 5f,
                        SatDrive = 2.6f, SatBias = 0f, SatWet = 0.9f,
                        PlateRt60 = 0.22f, PlatePre = 4f, PlateDamp = 0.5f, PlateMixDb = -18f,
                    };
                default: return null;
            }
        }
    }

    /// The sci-fi voice effect for spoken replies (docs/voice.md "Voice effect"; prototype and retuning:
    /// tools/voicefx/fx.py). Pure C# (no UnityEngine), whole buffer, run once per sentence on the worker thread that
    /// fetched it (QvacClient.Speak), never on the main thread. Every preset, off included, trims the silence the
    /// service pads around speech (0.2-0.5 s in front: that much sooner the first word) and brings it to -18 LUFS
    /// with a -1 dBFS ceiling (the dry audio hard-clips); off does nothing else. Scratch buffers are pooled: once
    /// the pool holds a sentence as long, a sentence allocates only its output.
    public static class VoiceFx
    {
        public const float TargetLufs = -18f, CeilingDb = -1f;
        /// Silence kept in front of and behind the speech (seconds).
        public const float KeepFront = 0.025f, KeepBack = 0.020f;
        /// The tail kept after a sentence that is not the reply's last (the next one follows after a short gap).
        public const float InnerTail = 0.1f;
        /// Setting values, in the VOICE > SOUND order, and their labels there.
        public static readonly string[] Names = { "off", "adjutant", "starship", "comm" };
        public static readonly string[] Labels = { "OFF", "AI", "SHIP", "COMM" };

        private static float Db(float d) => (float)Math.Pow(10.0, d / 20.0);

        /// "off" | "adjutant" | "starship" | "comm" (and a few spoken names); anything else is off.
        public static VoiceFxPreset Parse(string s)
        {
            switch ((s ?? "").Trim().ToLowerInvariant())
            {
                case "adjutant": case "ai": case "on": case "scifi": case "sci-fi": case "computer": return VoiceFxPreset.Adjutant;
                case "starship": case "ship": case "strong": return VoiceFxPreset.Starship;
                case "comm": case "radio": return VoiceFxPreset.Comm;
                default: return VoiceFxPreset.Off;
            }
        }

        /// A known preset name (the setting values, and off / none / clean / dry / normal for off)?
        public static bool TryParse(string s, out VoiceFxPreset preset)
        {
            string t = (s ?? "").Trim().ToLowerInvariant();
            preset = Parse(t);
            return preset != VoiceFxPreset.Off || t == "off" || t == "none" || t == "clean" || t == "dry" || t == "normal";
        }

        /// The setting value of a preset ("adjutant").
        public static string Name(VoiceFxPreset p) => Names[(int)p];

        private static readonly VoiceFxParams[] Cached = { null, VoiceFxParams.For(VoiceFxPreset.Adjutant), VoiceFxParams.For(VoiceFxPreset.Starship), VoiceFxParams.For(VoiceFxPreset.Comm) };

        /// 16-bit mono PCM WAV (the AI service's reply) in, 16-bit mono WAV out, trimmed and levelled, with the preset's
        /// effect. `first` / `last`: where the sentence sits in its reply (the comm squelch opens and closes the reply,
        /// a long tail only after the last). Not a 16-bit mono WAV, or too short: the input unchanged.
        public static byte[] ProcessWav(byte[] wav, VoiceFxPreset preset, bool first = true, bool last = true)
        {
            if (wav == null || wav.Length < 48) return wav;
            int ch = BitConverter.ToInt16(wav, 22), rate = BitConverter.ToInt32(wav, 24), bits = BitConverter.ToInt16(wav, 34);
            if (ch != 1 || bits != 16 || rate < 8000 || rate > 192000) return wav;
            int off = 12, dOff = -1, dLen = 0;
            while (off + 8 <= wav.Length)
            {
                int size = BitConverter.ToInt32(wav, off + 4);
                if (wav[off] == 'd' && wav[off + 1] == 'a' && wav[off + 2] == 't' && wav[off + 3] == 'a') { dOff = off + 8; dLen = Math.Min(size, wav.Length - dOff); break; }
                if (size < 0) break;
                off += 8 + size + (size & 1);
            }
            if (dOff < 0) return wav;
            int n = dLen / 2;
            if (n < 64) return wav;
            var f = Pool<float>.Rent(n, false);
            const float k = 1f / 32768f;
            for (int i = 0, o = dOff; i < n; i++, o += 2) f[i] = (short)(wav[o] | wav[o + 1] << 8) * k;
            float[] y = Core(f, n, rate, Cached[(int)preset], first, last, 7, out int len);
            if (ReferenceEquals(y, f)) { Pool<float>.Return(f); return wav; }
            Pool<float>.Return(f);
            var b = new byte[44 + len * 2]; // the only allocation of a sentence
            void S(int at, string s) { for (int i = 0; i < s.Length; i++) b[at + i] = (byte)s[i]; }
            void U32(int at, int v) { b[at] = (byte)v; b[at + 1] = (byte)(v >> 8); b[at + 2] = (byte)(v >> 16); b[at + 3] = (byte)(v >> 24); }
            void U16(int at, int v) { b[at] = (byte)v; b[at + 1] = (byte)(v >> 8); }
            S(0, "RIFF"); U32(4, 36 + len * 2); S(8, "WAVE"); S(12, "fmt "); U32(16, 16); U16(20, 1); U16(22, 1); U32(24, rate); U32(28, rate * 2); U16(32, 2); U16(34, 16); S(36, "data"); U32(40, len * 2);
            for (int i = 0, o = 44; i < len; i++, o += 2)
            {
                int s = (int)Math.Round(y[i] * 32767f);
                if (s > 32767) s = 32767; else if (s < -32768) s = -32768;
                b[o] = (byte)s; b[o + 1] = (byte)(s >> 8);
            }
            Pool<float>.Return(y);
            return b;
        }

        /// Mono float in, mono float out (trimmed; longer by the effect's tail). Too short or a strange rate: the input.
        public static float[] Process(float[] input, int rate, VoiceFxPreset preset, bool first = true, bool last = true, int seed = 7) =>
            input == null ? null : Process(input, input.Length, rate, Cached[(int)preset], first, last, seed);

        /// The same with explicit numbers (tools, retuning); `p` null is off.
        public static float[] Process(float[] input, int rate, VoiceFxParams p, bool first = true, bool last = true, int seed = 7) =>
            input == null ? null : Process(input, input.Length, rate, p, first, last, seed);

        private static float[] Process(float[] input, int count, int rate, VoiceFxParams p, bool first, bool last, int seed)
        {
            var b = Core(input, count, rate, p, first, last, seed, out int len);
            if (ReferenceEquals(b, input)) return input;
            var o = new float[len];
            Array.Copy(b, o, len);
            Pool<float>.Return(b);
            return o;
        }

        /// The chain into a pooled buffer of `outLen` samples (the caller returns it), or `input` itself when it is
        /// passed through (too short, a strange rate).
        private static float[] Core(float[] input, int count, int rate, VoiceFxParams p, bool first, bool last, int seed, out int outLen)
        {
            outLen = count;
            if (input == null || count < 64 || rate < 8000 || rate > 192000) return input;
            for (int i = 0; i < count; i++) if (float.IsNaN(input[i]) || float.IsInfinity(input[i])) return Pool<float>.Rent(count, true); // broken: silence
            Bounds(input, count, rate, out int a, out int e);
            int len = e - a;
            if (len <= 0) { outLen = Math.Min(count, rate / 10); return Pool<float>.Rent(outLen, true); } // only silence: a short silence
            if (p == null)
            {
                var o0 = Pool<float>.Rent(len, false);
                Array.Copy(input, a, o0, 0, len);
                Finish(o0, len, rate, false); // no filter: off is today's voice, only trimmed and at a safe level
                outLen = len;
                return o0;
            }
            float tailSec = last ? Math.Max(p.PlateRt60, 0.2f) : InnerTail;
            int n = len + (int)(tailSec * rate);
            float[] v = Pool<float>.Rent(n, false), y = Pool<float>.Rent(n, false), tmp = Pool<float>.Rent(n, false);
            try
            {
                Array.Copy(input, a, v, 0, len);
                Array.Clear(v, len, n - len);
                new Biquad(Biquad.Kind.Hp, p.Hp, 0.7071f, 0f, rate).Run(v, n);
                float g0 = Db(-4f); // headroom: the dry audio sits at full scale
                for (int i = 0; i < n; i++) v[i] *= g0;
                new Biquad(Biquad.Kind.Peak, p.PresenceHz, p.PresenceQ, p.PresenceDb, rate).Run(v, n);
                if (p.AirDb != 0f) new Biquad(Biquad.Kind.HShelf, p.AirHz, 0.7071f, p.AirDb, rate).Run(v, n);

                Array.Copy(v, y, n); // the mix bus
                if (p.DoubleCents != null)
                    for (int k = 0; k < p.DoubleCents.Length; k++)
                    {
                        GranularPitch(v, tmp, n, (float)Math.Pow(2.0, p.DoubleCents[k] / 1200.0), 35f, rate);
                        AddDelayed(y, tmp, n, (int)Math.Round(p.DoubleMs[k] * rate / 1000f), Db(p.DoubleDb));
                    }
                if (p.SubDb != 0f)
                {
                    GranularPitch(v, tmp, n, 0.5f, 45f, rate);
                    new Biquad(Biquad.Kind.Lp, 500f, 0.7071f, 0f, rate).Run(tmp, n);
                    AddDelayed(y, tmp, n, 0, Db(p.SubDb));
                }
                if (p.RingHz > 0f)
                {
                    Array.Copy(v, tmp, n);
                    new Biquad(Biquad.Kind.Hp, p.RingHp, 0.7071f, 0f, rate).Run(tmp, n);
                    double w = 2.0 * Math.PI * p.RingHz / rate, cw = Math.Cos(w), sw = Math.Sin(w), sn = 0, cs = 1;
                    float m = Db(p.RingMixDb);
                    for (int i = 0; i < n; i++)
                    {
                        y[i] += tmp[i] * (float)sn * m;
                        double ns = sn * cw + cs * sw; cs = cs * cw - sn * sw; sn = ns; // a rotating phasor
                        if ((i & 4095) == 4095) { double r = 1.0 / Math.Sqrt(sn * sn + cs * cs); sn *= r; cs *= r; }
                    }
                }
                if (p.CombMs > 0f)
                {
                    int D = Math.Max(2, (int)Math.Round(p.CombMs * rate / 1000f));
                    float g = p.CombG, m = Db(p.CombMixDb);
                    // tmp = feedback comb of v; only what it adds is mixed in
                    for (int i = 0; i < n; i++) { tmp[i] = v[i] + (i >= D ? g * tmp[i - D] : 0f); y[i] += (tmp[i] - v[i]) * m; }
                }
                if (p.FlBase > 0f)
                {
                    ModDelay(v, tmp, n, p.FlBase + p.FlDepth, p.FlDepth, p.FlRate, rate);
                    float m = Db(p.FlDb);
                    for (int i = 0; i < n; i++) y[i] += tmp[i] * m;
                }
                if (p.BandLo > 0f)
                {
                    new Biquad(Biquad.Kind.Hp, p.BandLo, 0.9f, 0f, rate).Run(y, n); new Biquad(Biquad.Kind.Hp, p.BandLo, 0.9f, 0f, rate).Run(y, n);
                    new Biquad(Biquad.Kind.Lp, p.BandHi, 0.9f, 0f, rate).Run(y, n); new Biquad(Biquad.Kind.Lp, p.BandHi, 0.9f, 0f, rate).Run(y, n);
                }
                if (p.CrushFs > 0f && p.CrushFs < rate)
                {
                    new Biquad(Biquad.Kind.Lp, p.CrushFs * 0.45f, 0.7071f, 0f, rate).Run(y, n); new Biquad(Biquad.Kind.Lp, p.CrushFs * 0.45f, 0.7071f, 0f, rate).Run(y, n);
                    double step = (double)rate / p.CrushFs;
                    Array.Copy(y, tmp, n);
                    for (int i = 0; i < n; i++) y[i] = tmp[Math.Min(n - 1, (int)(Math.Floor(i / step) * step))];
                    new Biquad(Biquad.Kind.Lp, Math.Min(6000f, rate * 0.45f), 0.7071f, 0f, rate).Run(y, n); // the hold's images above the band
                }
                Compress(y, n, rate, p.CompThr, p.CompRatio, p.CompAtt, p.CompRel, p.CompMakeup);
                {
                    float d = p.SatDrive, bi = p.SatBias, wet = p.SatWet;
                    float tb = (float)Math.Tanh(d * bi), norm = 1f / (d * (1f - tb * tb));
                    for (int i = 0; i < n; i++)
                    {
                        float s = ((float)Math.Tanh(d * (y[i] + bi)) - tb) * norm;
                        y[i] = (1f - wet) * y[i] + wet * s;
                    }
                }
                if (p.PlateRt60 > 0f)
                {
                    var wetBus = Pool<float>.Rent(n, true);
                    try
                    {
                        Plate.Run(y, wetBus, n, rate, p.PlateRt60, p.PlatePre, p.PlateDamp);
                        if (p.ShimmerDb != 0f)
                        {
                            // an octave-up copy of the send, only inside the tail
                            var sh = Pool<float>.Rent(n, true);
                            Array.Copy(y, tmp, n);
                            new Biquad(Biquad.Kind.Hp, 700f, 0.7071f, 0f, rate).Run(tmp, n);
                            GranularPitch(tmp, v, n, 2f, 30f, rate); // v is free from here on
                            Plate.Run(v, sh, n, rate, p.PlateRt60 * 1.2f, p.PlatePre + 6f, 0.2f);
                            float gs = Db(p.ShimmerDb - p.PlateMixDb);
                            for (int i = 0; i < n; i++) wetBus[i] += sh[i] * gs;
                            Pool<float>.Return(sh);
                        }
                        float m = Db(p.PlateMixDb);
                        for (int i = 0; i < n; i++) y[i] += wetBus[i] * m;
                    }
                    finally { Pool<float>.Return(wetBus); }
                }
                float[] o;
                int on;
                if (p.BurstMs > 0f) o = Squelch(y, n, rate, p, seed, first, last, out on);
                else { o = y; on = n; y = null; } // the mix bus is the output
                FadeOut(o, on, Math.Min((int)(0.040 * rate), on));
                Finish(o, on, rate, true);
                outLen = on;
                return o;
            }
            finally
            {
                Pool<float>.Return(v); Pool<float>.Return(y); Pool<float>.Return(tmp);
            }
        }

        // ---------------------------------------------------------------- stages

        /// The speech inside the service's padding (gate -60 dBFS), keeping KeepFront / KeepBack around it.
        public static void Bounds(float[] x, int n, int rate, out int start, out int end)
        {
            float thr = Db(-60f);
            int a = -1, b = -1;
            for (int i = 0; i < n; i++) if (Math.Abs(x[i]) > thr) { a = i; break; }
            if (a < 0) { start = 0; end = 0; return; }
            for (int i = n - 1; i >= 0; i--) if (Math.Abs(x[i]) > thr) { b = i; break; }
            start = Math.Max(0, a - (int)(KeepFront * rate));
            end = Math.Min(n, b + 1 + (int)(KeepBack * rate));
        }

        /// The input without the silence around the speech (a copy).
        public static float[] Trim(float[] x, int rate)
        {
            if (x == null) return null;
            Bounds(x, x.Length, rate, out int a, out int b);
            var r = new float[Math.Max(0, b - a)];
            Array.Copy(x, a, r, 0, r.Length);
            return r;
        }

        private static readonly float[] Hann = MakeHann();
        private static float[] MakeHann() { var t = new float[4097]; for (int i = 0; i <= 4096; i++) t[i] = (float)(0.5 - 0.5 * Math.Cos(2.0 * Math.PI * i / 4096.0)); return t; }

        private static void AddDelayed(float[] y, float[] src, int n, int d, float g)
        {
            for (int i = d; i < n; i++) y[i] += src[i - d] * g;
        }

        /// Two-tap crossfaded delay-line pitch shifter (ratio 1 = unchanged), same length. Only for faint layers.
        private static void GranularPitch(float[] x, float[] o, int n, float ratio, float winMs, int rate)
        {
            double W = winMs * rate / 1000.0, inc = (1.0 - ratio) / W, ph = 0.0;
            var hann = Hann;
            for (int i = 0; i < n; i++)
            {
                double acc = 0;
                for (int t = 0; t < 2; t++)
                {
                    double q = ph + 0.5 * t; if (q >= 1.0) q -= 1.0;
                    double w = hann[(int)(q * 4096.0)];
                    double pos = i - (q * W + 2.0);
                    int i0 = (int)Math.Floor(pos); double f = pos - i0;
                    double a0 = i0 < 0 || i0 >= n ? 0 : x[i0], a1 = i0 + 1 < 0 || i0 + 1 >= n ? 0 : x[i0 + 1];
                    acc += w * (a0 * (1 - f) + a1 * f);
                }
                o[i] = (float)acc;
                ph += inc; ph -= Math.Floor(ph);
            }
        }

        private static void ModDelay(float[] x, float[] o, int n, float baseMs, float depthMs, float rateHz, int rate)
        {
            double w = 2.0 * Math.PI * rateHz / rate, cw = Math.Cos(w), sw = Math.Sin(w), sn = 0, cs = 1;
            for (int i = 0; i < n; i++)
            {
                double d = (baseMs + depthMs * sn) * rate / 1000.0, pos = i - d;
                double ns = sn * cw + cs * sw; cs = cs * cw - sn * sw; sn = ns;
                if ((i & 4095) == 4095) { double r = 1.0 / Math.Sqrt(sn * sn + cs * cs); sn *= r; cs *= r; }
                int i0 = (int)Math.Floor(pos); double f = pos - i0;
                double a0 = i0 < 0 ? 0 : x[i0], a1 = i0 + 1 < 0 ? 0 : x[i0 + 1];
                o[i] = (float)(a0 * (1 - f) + a1 * f);
            }
        }

        /// Feed-forward peak compressor; the gain is worked out per 32-sample block and interpolated.
        private static void Compress(float[] x, int n, int rate, float thr, float ratio, float attMs, float relMs, float makeup)
        {
            const int B = 32;
            int nb = (n + B - 1) / B;
            var gl = Pool<float>.Rent(nb, false);
            double ca = Math.Exp(-B / (attMs * rate / 1000.0)), cr = Math.Exp(-B / (relMs * rate / 1000.0)), env = 0;
            for (int k = 0; k < nb; k++)
            {
                float pk = 0; int e = Math.Min(n, (k + 1) * B);
                for (int i = k * B; i < e; i++) { float a = Math.Abs(x[i]); if (a > pk) pk = a; }
                env = pk > env ? ca * env + (1 - ca) * pk : cr * env + (1 - cr) * pk;
                double lv = 20.0 * Math.Log10(env + 1e-9), over = Math.Max(0.0, lv - thr);
                gl[k] = (float)Math.Pow(10.0, (-over * (1 - 1.0 / ratio) + makeup) / 20.0);
            }
            for (int i = 0; i < n; i++)
            {
                double c = (double)i / B - 0.5; int k0 = (int)Math.Floor(c); double f = c - k0;
                float g = k0 < 0 ? gl[0] : k0 >= nb - 1 ? gl[nb - 1] : (float)(gl[k0] * (1 - f) + gl[k0 + 1] * f);
                x[i] *= g;
            }
            Pool<float>.Return(gl);
        }

        private static void FadeOut(float[] y, int len, int n)
        {
            for (int i = 0; i < n; i++) { double c = Math.Cos((double)i / Math.Max(1, n - 1) * Math.PI / 2); y[len - n + i] *= (float)(c * c); }
        }

        /// The comm channel: a squelch burst opens the reply (first sentence) and closes it (last), a faint static rides
        /// the speech. Returns a pooled buffer of `outLen` samples.
        private static float[] Squelch(float[] y, int n, int rate, VoiceFxParams p, int seed, bool first, bool last, out int outLen)
        {
            var rng = new Rng((uint)seed);
            int bl = Math.Max(2, (int)(p.BurstMs * rate / 1000f));
            var burst = Pool<float>.Rent(bl, false);
            var nz = Pool<float>.Rent(n, false);
            var env = Pool<float>.Rent(n, false);
            try
            {
                for (int i = 0; i < bl; i++) burst[i] = rng.Gauss();
                new Biquad(Biquad.Kind.Hp, 900f, 0.7071f, 0f, rate).Run(burst, bl); new Biquad(Biquad.Kind.Lp, Math.Min(6000f, rate * 0.45f), 0.7071f, 0f, rate).Run(burst, bl);
                float bg = Db(p.BurstDb);
                for (int i = 0; i < bl; i++) burst[i] *= bg * (float)Math.Sqrt(0.5 - 0.5 * Math.Cos(2 * Math.PI * (i + 0.5) / bl));
                for (int i = 0; i < n; i++) nz[i] = rng.Gauss();
                new Biquad(Biquad.Kind.Hp, 600f, 0.7071f, 0f, rate).Run(nz, n); new Biquad(Biquad.Kind.Lp, Math.Min(5000f, rate * 0.45f), 0.7071f, 0f, rate).Run(nz, n);
                // the speech envelope: 25 ms moving mean of |y|, by its max
                int w = Math.Max(1, (int)(0.025 * rate)); double run = 0, mx = 1e-9;
                for (int i = 0; i < n; i++) { run += Math.Abs(y[i]); if (i >= w) run -= Math.Abs(y[i - w]); env[i] = (float)(run / w); if (env[i] > mx) mx = env[i]; }
                float ng = Db(p.NoiseDb), quiet = Db(-50f);
                int lead = first ? bl + (int)(0.04 * rate) : 0, post = last ? (int)(bl * 0.7) : 0, lastLoud = 0;
                for (int i = n - 1; i >= 0; i--) if (Math.Abs(y[i]) > quiet) { lastLoud = i; break; }
                int at = lead + lastLoud + (int)(0.015 * rate);
                int end = last ? at + post : lead + n;
                outLen = Math.Max(end, 1);
                var r = Pool<float>.Rent(outLen, true);
                if (first) Array.Copy(burst, 0, r, 0, bl);
                for (int i = 0; i < n && lead + i < outLen; i++) r[lead + i] = y[i] + nz[i] * ng * (float)Math.Min(1.0, env[i] / mx * 8.0);
                for (int i = 0; i < post && at + i < outLen; i++) r[at + i] += burst[bl - 1 - i] * 0.8f;
                return r;
            }
            finally { Pool<float>.Return(burst); Pool<float>.Return(nz); Pool<float>.Return(env); }
        }

        // ---------------------------------------------------------------- loudness + limiter

        /// BS.1770-style integrated loudness (K-weighting, 400 ms blocks every 100 ms, absolute and relative gate).
        public static float Lufs(float[] x, int rate) => x == null ? -70f : Lufs(x, x.Length, rate);

        private static float Lufs(float[] x, int n, int rate)
        {
            if (n == 0) return -70f;
            var z = Pool<float>.Rent(n, false);
            var cs = Pool<double>.Rent(n + 1, false);
            try
            {
                Array.Copy(x, z, n);
                new Biquad(Biquad.Kind.HShelf, 1681.9744509555319f, 0.7071752369554196f, 3.999843853973347f, rate).Run(z, n);
                new Biquad(Biquad.Kind.HpK, 38.13547087613982f, 0.5003270373253953f, 0f, rate).Run(z, n);
                int blk = (int)(0.4 * rate), hop = (int)(0.1 * rate);
                cs[0] = 0;
                for (int i = 0; i < n; i++) cs[i + 1] = cs[i] + (double)z[i] * z[i];
                int cnt = n < blk ? 1 : (n - blk) / hop + 1;
                double Pw(int k) { int a = k * hop, e = Math.Min(n, a + blk); return (cs[e] - cs[a]) / Math.Min(blk, Math.Max(1, n)); }
                double s = 0; int c = 0;
                for (int k = 0; k < cnt; k++) { double pw = Pw(k); if (-0.691 + 10 * Math.Log10(pw + 1e-12) > -70) { s += pw; c++; } }
                if (c == 0) return -70f;
                double thr = Math.Max(-0.691 + 10 * Math.Log10(s / c) - 10, -70);
                s = 0; c = 0;
                for (int k = 0; k < cnt; k++) { double pw = Pw(k); if (-0.691 + 10 * Math.Log10(pw + 1e-12) > thr) { s += pw; c++; } }
                return c == 0 ? -70f : (float)(-0.691 + 10 * Math.Log10(s / c));
            }
            finally { Pool<float>.Return(z); Pool<double>.Return(cs); }
        }

        /// Look-ahead brick-wall limiter: sliding max over +-L samples, box-smoothed gain.
        private static void Limit(float[] x, int n, float ceilingDb, int L)
        {
            if (n == 0) return;
            float c = Db(ceilingDb); int W = 2 * L + 1;
            var g = Pool<float>.Rent(n, false);
            var o = Pool<float>.Rent(n, false);
            var dq = Pool<int>.Rent(n, false);
            try
            {
                int h = 0, t = 0;
                for (int i = 0, j = 0; i < n; i++)
                {
                    int hi = Math.Min(n - 1, i + L);
                    for (; j <= hi; j++) { float a = Math.Abs(x[j]); while (t > h && Math.Abs(x[dq[t - 1]]) <= a) t--; dq[t++] = j; }
                    while (dq[h] < i - L) h++;
                    float pk = Math.Abs(x[dq[h]]);
                    g[i] = pk > c ? c / pk : 1f;
                }
                double run = 0; // centred box mean; the edges repeat the nearest value
                for (int k = -L; k <= L; k++) run += g[Math.Min(n - 1, Math.Max(0, k))];
                for (int i = 0; i < n; i++)
                {
                    o[i] = (float)(run / W);
                    run += g[Math.Min(n - 1, i + L + 1)] - g[Math.Max(0, i - L)];
                }
                for (int i = 0; i < n; i++) x[i] *= o[i];
            }
            finally { Pool<float>.Return(g); Pool<float>.Return(o); Pool<int>.Return(dq); }
        }

        /// HP 60 Hz (the presets; not off), -18 LUFS (at most +24 dB: near-silence is not pumped up), limiter to -1 dBFS,
        /// a second pass when the limiter took more than 0.3 dB back, then a hard clamp at the ceiling.
        /// Off skips the high-pass: on the service's audio it changes nothing above 100 Hz (-0.01 dB) but its phase
        /// turn alone takes the waveform to a 0.94-0.98 correlation with the dry voice; without it off is the dry
        /// voice at another gain (correlation 0.999-1.000 on the 8 sample replies).
        private static void Finish(float[] y, int n, int rate, bool highPass)
        {
            if (n == 0) return;
            if (highPass) new Biquad(Biquad.Kind.Hp, 60f, 0.7071f, 0f, rate).Run(y, n);
            int L = Math.Max(1, (int)(0.002 * rate));
            float gainDb = 0f;
            for (int pass = 0; pass < 2; pass++)
            {
                float l = Lufs(y, n, rate);
                if (l <= -69f) break;
                float want = Math.Min(TargetLufs - l, 24f - gainDb);
                if (pass > 0 && Math.Abs(want) < 0.3f) break;
                float gain = Db(want);
                gainDb += want;
                for (int i = 0; i < n; i++) y[i] *= gain;
                Limit(y, n, CeilingDb, L);
            }
            float c = Db(CeilingDb);
            for (int i = 0; i < n; i++) y[i] = Math.Max(-c, Math.Min(c, y[i]));
        }

        // ---------------------------------------------------------------- building blocks

        /// Scratch buffers shared by every call (locked; a few are kept): a sentence allocates only its output.
        private static class Pool<T>
        {
            private static readonly List<T[]> Free = new List<T[]>();
            private const int Keep = 16; // one sentence holds about 7 large and 3 small ones at its peak

            public static T[] Rent(int n, bool clear)
            {
                T[] a = null;
                lock (Free)
                {
                    int best = -1;
                    for (int i = 0; i < Free.Count; i++)
                        if (Free[i].Length >= n && (best < 0 || Free[i].Length < Free[best].Length)) best = i;
                    if (best >= 0) { a = Free[best]; Free.RemoveAt(best); }
                }
                if (a == null) return new T[Math.Max(n, 1)];
                if (clear) Array.Clear(a, 0, n);
                return a;
            }

            public static void Return(T[] a)
            {
                if (a == null) return;
                lock (Free)
                {
                    if (Free.Count < Keep) { Free.Add(a); return; }
                    // full: keep the larger buffers (the next long sentence needs them)
                    int small = 0;
                    for (int i = 1; i < Free.Count; i++) if (Free[i].Length < Free[small].Length) small = i;
                    if (Free[small].Length < a.Length) Free[small] = a;
                }
            }

            public static void Clear() { lock (Free) Free.Clear(); }
        }

        /// Drops the pooled scratch buffers (a few MB after long replies).
        public static void ReleaseScratch() { Pool<float>.Clear(); Pool<double>.Clear(); Pool<int>.Clear(); }

        private sealed class Rng
        {
            private uint _s;
            public Rng(uint s) { _s = s * 2654435761u + 1; }
            private float U() { _s ^= _s << 13; _s ^= _s >> 17; _s ^= _s << 5; return (_s >> 8) / 16777216f; }
            public float Gauss() { float a = 0; for (int i = 0; i < 4; i++) a += U(); return (a - 2f) * 1.7320508f; } // sum of 4 uniforms ~ N(0,1)
        }

        /// RBJ biquad, transposed direct form II, double state (the 60-80 Hz high-passes need it).
        private struct Biquad
        {
            public enum Kind { Hp, Lp, Peak, HShelf, HpK }
            private readonly double _b0, _b1, _b2, _a1, _a2;

            public Biquad(Kind k, float f0, float q, float gainDb, int fs)
            {
                f0 = Math.Min(f0, fs * 0.45f);
                double A = Math.Pow(10, gainDb / 40.0), w0 = 2 * Math.PI * f0 / fs, c = Math.Cos(w0), s = Math.Sin(w0), al = s / (2 * q);
                double b0, b1, b2, a0, a1, a2;
                switch (k)
                {
                    case Kind.Hp: b0 = (1 + c) / 2; b1 = -(1 + c); b2 = (1 + c) / 2; a0 = 1 + al; a1 = -2 * c; a2 = 1 - al; break;
                    case Kind.Lp: b0 = (1 - c) / 2; b1 = 1 - c; b2 = (1 - c) / 2; a0 = 1 + al; a1 = -2 * c; a2 = 1 - al; break;
                    case Kind.Peak: b0 = 1 + al * A; b1 = -2 * c; b2 = 1 - al * A; a0 = 1 + al / A; a1 = -2 * c; a2 = 1 - al / A; break;
                    case Kind.HpK: // BS.1770 RLB high-pass (bilinear of the analog prototype)
                    {
                        double K = Math.Tan(Math.PI * f0 / fs); a0 = 1 + K / q + K * K;
                        b0 = a0; b1 = -2 * a0; b2 = a0; a1 = 2 * (K * K - 1); a2 = 1 - K / q + K * K; break; // numerator unnormalised, as the standard has it
                    }
                    default: // high shelf
                    {
                        double sq = 2 * Math.Sqrt(A) * al;
                        b0 = A * ((A + 1) + (A - 1) * c + sq); b1 = -2 * A * ((A - 1) + (A + 1) * c); b2 = A * ((A + 1) + (A - 1) * c - sq);
                        a0 = (A + 1) - (A - 1) * c + sq; a1 = 2 * ((A - 1) - (A + 1) * c); a2 = (A + 1) - (A - 1) * c - sq; break;
                    }
                }
                _b0 = b0 / a0; _b1 = b1 / a0; _b2 = b2 / a0; _a1 = a1 / a0; _a2 = a2 / a0;
            }

            public void Run(float[] x, int n)
            {
                double z1 = 0, z2 = 0;
                for (int i = 0; i < n; i++)
                {
                    double xi = x[i], y = _b0 * xi + z1;
                    z1 = _b1 * xi - _a1 * y + z2; z2 = _b2 * xi - _a2 * y;
                    x[i] = (float)y;
                }
            }
        }

        /// A short bright reverb: pre-delay, 4 damped combs, 2 all-passes, high-pass + air shelf. Adds the wet signal
        /// into `acc` (cleared by the caller).
        private static class Plate
        {
            private static readonly int[] CombD = { 1116, 1277, 1422, 1617 }, ApD = { 556, 441 };

            public static void Run(float[] x, float[] acc, int n, int rate, float rt60, float preMs, float damp)
            {
                int pd = (int)Math.Round(preMs * rate / 1000f);
                double sc = rate / 44100.0;
                foreach (int d0 in CombD)
                {
                    int D = Math.Max(2, (int)(d0 * sc));
                    float g = (float)Math.Pow(10, -3.0 * D / (rt60 * rate));
                    var line = Pool<float>.Rent(D, true); // the low-passed feedback, one delay line
                    float lp = 0;
                    for (int i = 0, k = 0; i < n; i++, k = k + 1 == D ? 0 : k + 1)
                    {
                        float xin = i >= pd ? x[i - pd] : 0f;
                        float yi = xin + g * line[k];
                        lp = (1f - damp) * yi + damp * lp;
                        line[k] = lp; acc[i] += 0.25f * yi;
                    }
                    Pool<float>.Return(line);
                }
                foreach (int a0 in ApD)
                {
                    int D = Math.Max(2, (int)(a0 * sc));
                    var xb = Pool<float>.Rent(D, true); var yb = Pool<float>.Rent(D, true);
                    for (int i = 0, k = 0; i < n; i++, k = k + 1 == D ? 0 : k + 1)
                    {
                        float xi = acc[i], yi = -0.5f * xi + xb[k] + 0.5f * yb[k];
                        xb[k] = xi; yb[k] = yi; acc[i] = yi;
                    }
                    Pool<float>.Return(xb); Pool<float>.Return(yb);
                }
                new Biquad(Biquad.Kind.Hp, 350f, 0.7071f, 0f, rate).Run(acc, n);
                new Biquad(Biquad.Kind.HShelf, 4500f, 0.7071f, 3f, rate).Run(acc, n);
            }
        }
    }
}

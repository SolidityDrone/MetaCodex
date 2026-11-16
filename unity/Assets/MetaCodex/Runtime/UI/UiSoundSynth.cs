using System;

namespace MetaCodex.UI
{
    /// The app's short sounds as pure math: no Unity types, no assets, no randomness from the clock, so the same call
    /// gives the same samples on the headset and in tests/sounds/ (which compiles this file as it is).
    ///
    /// One palette: the notes of G major pentatonic (G A B D E), so sounds that overlap never clash; one small dark
    /// room (early reflections and a short tail, high and low ends cut, like light in carved slate); one set of voices:
    /// struck glass (inharmonic partials of a bar, the high ones die first), a soft FM bell, filtered air (noise through a
    /// moving band-pass), a low body (a sine that drops in pitch), beads of light (tiny glass grains), and a tone with
    /// an optional glide, harmonics, detune and tremolo. Every clip: mono, 44.1 kHz, no DC (high-pass at 70 Hz), rounded
    /// off above 6 kHz, one loudness for all (K-weighted, the loudest 100 ms; each sound has its own offset in dB),
    /// a 2 ms fade in and a fade out of 5 ms or more, the peak at -1 dBFS at most.
    public static class UiSoundSynth
    {
        public const int Rate = 44100;

        /// Every sound, the ones needed first first (UiSounds makes them in this order).
        public static readonly string[] Names = { "click", "open", "close", "toast", "ping", "wake", "error", "alert", "hover", "orb-on", "orb-off" };

        /// The common loudness in dB (K-weighted mean square of the loudest 100 ms, like LUFS on a short window).
        public const double TargetLoudness = -17.0;

        /// The highest peak a clip may have (-1 dBFS).
        public const float PeakLimit = 0.891f;

        // the palette: G major pentatonic
        private const double G3 = 196.00, B3 = 246.94, D4 = 293.66, G4 = 392.00, A4 = 440.00, B4 = 493.88, D5 = 587.33, E5 = 659.26,
            G5 = 783.99, A5 = 880.00, B5 = 987.77, D6 = 1174.66, E6 = 1318.51, G6 = 1567.98, A6 = 1760.00, B6 = 1975.53, D7 = 2349.32, E7 = 2637.02;

        /// The samples of one sound (null for an unknown name). Allocates its own buffers; safe on any thread.
        public static float[] Render(string name)
        {
            switch (name)
            {
                case "click": return Click();
                case "hover": return Hover();
                case "open": return Open();
                case "close": return Close();
                case "orb-on": return OrbOn();
                case "orb-off": return OrbOff();
                case "wake": return Wake();
                case "ping": return Ping();
                case "toast": return Toast();
                case "alert": return Alert();
                case "error": return Error();
                default: return null;
            }
        }

        // ------------------------------------------------------------------ the sounds

        /// A tap on a small pane of glass: a short D6 with a woody G4 under it and a breath of contact.
        private static float[] Click()
        {
            var m = new Mix(0.050);
            m.Glass(0, D6, 1.0, decay: 75, bright: 0.30, attack: 0.0005);
            m.Mode(0, G4, 0.45, decay: 110, attack: 0.0008);
            m.Air(0, 0.006, 3200, 3200, q: 1.0, amp: 0.30, attackFrac: 0.15, seed: 1);
            return m.Finish(wet: 0.10, rt: 0.12, offsetDb: -2, fadeOutMs: 6);
        }

        /// Almost nothing: a soft breath with a hint of G6.
        private static float[] Hover()
        {
            var m = new Mix(0.022);
            m.Air(0, 0.022, 1500, 2200, q: 2.5, amp: 1.0, attackFrac: 0.35, seed: 2);
            m.Mode(0.002, G6, 0.12, decay: 140, attack: 0.002);
            return m.Finish(wet: 0.06, rt: 0.10, offsetDb: -16, fadeOutMs: 5, lowPass: 4000);
        }

        /// A window is cast (UiPanelFx, 0.35 s): two ticks as the brackets lock on, a rising stream of air and light as
        /// the scan line crosses with beads of light in it, then it settles: a low body and a clear G5 + D6.
        private static float[] Open()
        {
            var m = new Mix(0.50);
            m.Glass(0, E6, 0.55, decay: 95, bright: 0.45, attack: 0.0004);
            m.Air(0, 0.005, 3600, 3600, q: 1.2, amp: 0.20, attackFrac: 0.15, seed: 11);
            m.Glass(0.05, A6, 0.60, decay: 95, bright: 0.40, attack: 0.0004);
            m.Air(0.05, 0.005, 4000, 4000, q: 1.2, amp: 0.20, attackFrac: 0.15, seed: 12);
            m.Air(0.045, 0.26, 350, 2600, q: 2.2, amp: 0.40, attackFrac: 0.70, seed: 13);
            m.Tone(0.045, 0.26, G3, G5, 0.28, attack: 0.18, release: 0.08, h2: 0.30, h3: 0.10, detune: 7);
            m.Beads(0.10, 0.28, new[] { D6, E6, G6, A6, B6, D7 }, 6, 0.15, seed: 14);
            m.Thump(0.285, 300, G3, pitchRate: 45, amp: 0.55, decay: 22);
            m.Glass(0.285, G5, 0.40, decay: 11, bright: 0.30, attack: 0.002, beat: 2);
            m.Glass(0.285, D6, 0.28, decay: 13, bright: 0.25, attack: 0.002);
            return m.Finish(wet: 0.22, rt: 0.40, offsetDb: 0, fadeOutMs: 60);
        }

        /// A window goes (UiPanelFx, 0.22 s): a surge, the line runs back (a falling stream), the brackets let go, the
        /// spark flies home (a quick rise) and is taken in (a soft low body and a faint G6).
        private static float[] Close()
        {
            var m = new Mix(0.34);
            m.Air(0, 0.05, 2400, 1800, q: 1.8, amp: 0.30, attackFrac: 0.30, seed: 21);
            m.Glass(0, E6, 0.35, decay: 70, bright: 0.40, attack: 0.0006);
            m.Air(0.02, 0.15, 2600, 350, q: 2.2, amp: 0.38, attackFrac: 0.25, seed: 22);
            m.Tone(0.02, 0.15, G5, G3, 0.28, attack: 0.02, release: 0.07, h2: 0.30, h3: 0.10, detune: 7);
            m.Glass(0.14, D6, 0.22, decay: 110, bright: 0.40, attack: 0.0005);
            m.Tone(0.158, 0.062, D6, D7, 0.16, attack: 0.03, release: 0.03);
            m.Thump(0.22, 260, G3, pitchRate: 50, amp: 0.40, decay: 30);
            m.Glass(0.22, G6, 0.16, decay: 40, bright: 0.20, attack: 0.001);
            return m.Finish(wet: 0.20, rt: 0.35, offsetDb: -1, fadeOutMs: 50);
        }

        /// The Whisp ignites: a soft rush of air, a G major chord blooming note by note and gliding up into tune, a
        /// glint of G6 and a few beads of light as it catches.
        private static float[] OrbOn()
        {
            var m = new Mix(0.68);
            m.Air(0, 0.28, 250, 1800, q: 1.4, amp: 0.25, attackFrac: 0.55, seed: 31);
            double[] notes = { G4, D5, G5, B5 }, at = { 0, 0.03, 0.06, 0.09 }, amp = { 0.9, 0.7, 0.55, 0.4 };
            for (int k = 0; k < notes.Length; k++)
                m.Tone(at[k], 0.68 - at[k], notes[k] * 0.971, notes[k], amp[k], attack: 0.18, release: 0.12, glide: 0.25, shape: 1.0,
                    decay: 2.5, h2: 0.18, h3: 0.05, detune: 5, trem: 0.08, tremHz: 6);
            m.Glass(0.30, G6, 0.22, decay: 9, bright: 0.20, attack: 0.002, beat: 2);
            m.Beads(0.28, 0.46, new[] { G6, B6, D7, E7 }, 4, 0.09, seed: 32);
            return m.Finish(wet: 0.25, rt: 0.50, offsetDb: -1, fadeOutMs: 120);
        }

        /// The Whisp goes out: its light (D5 + G5) sinks a fourth, faster and faster, and the air closes down.
        private static float[] OrbOff()
        {
            var m = new Mix(0.42);
            m.Tone(0, 0.42, D5, A4, 0.8, attack: 0.015, release: 0.15, glide: 0.34, shape: -0.5, decay: 3, h2: 0.18, h3: 0.05, detune: 5);
            m.Tone(0, 0.42, G5, D5, 0.6, attack: 0.015, release: 0.15, glide: 0.34, shape: -0.5, decay: 3, h2: 0.15, detune: 5);
            m.Air(0, 0.30, 1800, 250, q: 1.4, amp: 0.20, attackFrac: 0.10, seed: 41);
            return m.Finish(wet: 0.22, rt: 0.40, offsetDb: -2, fadeOutMs: 90);
        }

        /// "Hey Whisp" was heard: two soft rising glass notes (G5, D6) with a breath of air. Short: the user speaks next.
        private static float[] Wake()
        {
            var m = new Mix(0.20);
            m.Glass(0, G5, 1.0, decay: 22, bright: 0.22, attack: 0.003);
            m.Mode(0, G6, 0.12, decay: 40, attack: 0.003);
            m.Glass(0.075, D6, 0.95, decay: 22, bright: 0.22, attack: 0.003);
            m.Mode(0.075, D7, 0.10, decay: 40, attack: 0.003);
            m.Air(0, 0.16, 1800, 3000, q: 2.5, amp: 0.10, attackFrac: 0.50, seed: 3);
            return m.Finish(wet: 0.18, rt: 0.30, offsetDb: -3, fadeOutMs: 30); // a little under the others: the microphone is open
        }

        /// Here: one crystal G6 over a D6 with a shimmer, and a few beads of light running up round it.
        private static float[] Ping()
        {
            var m = new Mix(0.22);
            m.Glass(0, G6, 1.0, decay: 17, bright: 0.28, attack: 0.001, beat: 3);
            m.Glass(0, D6, 0.45, decay: 26, bright: 0.25, attack: 0.0015);
            m.Beads(0.015, 0.09, new[] { B6, D7, E7, D7 }, 4, 0.16, seed: 4);
            return m.Finish(wet: 0.20, rt: 0.35, offsetDb: 0, fadeOutMs: 35);
        }

        /// A message: two quick soft bell plucks rising a fourth (D6, G6).
        private static float[] Toast()
        {
            var m = new Mix(0.12);
            m.Fm(0, 0.12, D6, ratio: 1, index0: 1.1, index1: 0.05, indexDecay: 90, amp: 1.0, decay: 32, attack: 0.0015);
            m.Fm(0.05, 0.07, G6, ratio: 1, index0: 1.0, index1: 0.05, indexDecay: 90, amp: 0.9, decay: 36, attack: 0.0015);
            return m.Finish(wet: 0.14, rt: 0.22, offsetDb: -1, fadeOutMs: 18);
        }

        /// Look at this: the same glass dyad (A5 + D6, over A4) struck twice, the second a little softer.
        private static float[] Alert()
        {
            var m = new Mix(0.32);
            for (int k = 0; k < 2; k++)
            {
                double t = k * 0.13, g = k == 0 ? 1.0 : 0.85;
                m.Glass(t, A5, 1.0 * g, decay: 15, bright: 0.45, attack: 0.0012, beat: 2);
                m.Glass(t, D6, 0.70 * g, decay: 17, bright: 0.40, attack: 0.0012);
                m.Mode(t, A4, 0.30 * g, decay: 35, attack: 0.002);
            }
            return m.Finish(wet: 0.18, rt: 0.30, offsetDb: 1, fadeOutMs: 35);
        }

        /// Did not work: a warm, rounded falling minor third (D5, then B4: one note at a time), firm but not a buzz.
        private static float[] Error()
        {
            var m = new Mix(0.22);
            m.Fm(0, 0.10, D5, ratio: 1, index0: 1.8, index1: 0.3, indexDecay: 30, amp: 1.0, decay: 20, attack: 0.003, release: 0.02);
            m.Mode(0, D4, 0.30, decay: 45, attack: 0.003);
            m.Fm(0.085, 0.135, B4, ratio: 1, index0: 1.8, index1: 0.3, indexDecay: 30, amp: 0.95, decay: 20, attack: 0.003);
            m.Mode(0.085, B3, 0.30, decay: 45, attack: 0.003);
            return m.Finish(wet: 0.12, rt: 0.25, offsetDb: 0, fadeOutMs: 30, lowPass: 3500, drive: 1.2);
        }

        // ------------------------------------------------------------------ the toolkit

        /// A clip being made: voices add into it, Finish adds the room and the mastering.
        private sealed class Mix
        {
            public readonly float[] B;
            public readonly int N;

            public Mix(double seconds) { N = (int)Math.Ceiling(seconds * Rate); B = new float[N]; }

            private static int I(double t) => (int)Math.Round(t * Rate);

            /// One-pole step for an attack of about `sec` (63 % reached), 1 = instant.
            private static double Attack(double sec) => sec <= 0 ? 1 : 1 - Math.Exp(-1.0 / (sec * Rate));

            /// A struck mode: a sine from phase 0 that decays exponentially (a damped rotation: no sin per sample).
            public void Mode(double t0, double f, double amp, double decay, double attack = 0.001)
            {
                int i0 = I(t0);
                if (i0 >= N || f <= 0 || f > 0.45 * Rate || amp == 0) return;
                int len = Math.Min(N - i0, (int)(11.5 / decay * Rate) + 1); // to -100 dB
                double w = 2 * Math.PI * f / Rate, c = Math.Cos(w), s = Math.Sin(w), r = Math.Exp(-decay / Rate);
                double x = 1, y = 0, a = 0, ka = Attack(attack);
                for (int i = 0; i < len; i++)
                {
                    a += (1 - a) * ka;
                    B[i0 + i] += (float)(amp * a * y);
                    double nx = r * (x * c - y * s);
                    y = r * (x * s + y * c);
                    x = nx;
                }
            }

            private static readonly double[] GlassRatio = { 1, 2.756, 5.404 }, GlassDecay = { 1, 2.2, 3.8 };

            /// Struck glass: the partials of a free bar (1, 2.76, 5.40); `bright` = the level of the upper ones, which die
            /// faster; partials above 4.5 kHz are cut steeply and none above 6.5 kHz is made. `beat` splits the
            /// fundamental into two modes that many Hz apart: a slow shimmer.
            public void Glass(double t0, double f, double amp, double decay, double bright = 0.35, double attack = 0.0008, double beat = 0)
            {
                for (int k = 0; k < GlassRatio.Length; k++)
                {
                    double fk = f * GlassRatio[k], ak = amp * (k == 0 ? 1 : k == 1 ? bright : 0.4 * bright), dk = decay * GlassDecay[k];
                    if (fk > 6500) continue;
                    if (fk > 4500) { ak *= Math.Pow(4500 / fk, 3); dk *= 1.5; }
                    if (k == 0 && beat > 0)
                    {
                        Mode(t0, fk - beat * 0.5, ak * 0.62, dk, attack);
                        Mode(t0, fk + beat * 0.5, ak * 0.38, dk, attack);
                    }
                    else Mode(t0, fk, ak, dk, k == 0 ? attack : attack * 0.5);
                }
            }

            /// A two-operator FM voice (a soft bell or pluck): carrier fc, modulator fc * ratio, the index falls from
            /// index0 to index1 (so it starts brighter), an exponential decay after a short attack, a cos² release over
            /// the last `release` seconds of `dur` (0: it runs to the end of the clip).
            public void Fm(double t0, double dur, double fc, double ratio, double index0, double index1, double indexDecay,
                double amp, double decay, double attack, double release = 0)
            {
                int i0 = I(t0), n = Math.Min(N - i0, I(dur)), r0 = n - I(release);
                double wc = 2 * Math.PI * fc / Rate, wm = wc * ratio, ka = Attack(attack), a = 0;
                for (int i = 0; i < n; i++)
                {
                    double t = i / (double)Rate;
                    double index = index1 + (index0 - index1) * Math.Exp(-indexDecay * t);
                    double r = release > 0 && i >= r0 ? Sq(Math.Cos(0.5 * Math.PI * (i - r0) / (n - r0))) : 1;
                    a += (1 - a) * ka;
                    B[i0 + i] += (float)(amp * a * r * Math.Exp(-decay * t) * Math.Sin(wc * i + index * Math.Sin(wm * i)));
                }
            }

            /// Air: white noise through a band-pass (a state-variable filter, stable while it moves) whose centre glides
            /// from fc0 to fc1; it swells in over `attackFrac` of its length and out over the rest.
            public void Air(double t0, double dur, double fc0, double fc1, double q, double amp, double attackFrac, uint seed)
            {
                int i0 = I(t0), n = Math.Min(N - i0, I(dur));
                uint s = seed * 2654435761u + 0x9E3779B9u;
                double ic1 = 0, ic2 = 0, k = 1 / q, a1 = 0, a2 = 0, a3 = 0, ln = Math.Log(fc1 / fc0);
                for (int i = 0; i < n; i++)
                {
                    double u = i / (double)n;
                    if ((i & 7) == 0)
                    {
                        double fc = Math.Min(fc0 * Math.Exp(ln * u), 0.45 * Rate), g = Math.Tan(Math.PI * fc / Rate);
                        a1 = 1 / (1 + g * (g + k)); a2 = g * a1; a3 = g * a2;
                    }
                    s ^= s << 13; s ^= s >> 17; s ^= s << 5;
                    double v0 = s / 2147483648.0 - 1;
                    double v3 = v0 - ic2, v1 = a1 * ic1 + a2 * v3, v2 = ic2 + a2 * ic1 + a3 * v3;
                    ic1 = 2 * v1 - ic1; ic2 = 2 * v2 - ic2;
                    B[i0 + i] += (float)(amp * Swell(u, attackFrac) * v1 * k);
                }
            }

            /// A tone: a sine (plus 2nd and 3rd harmonics) that glides from f0 to f1 over `glide` seconds (shape > 0
            /// eases out, < 0 eases in), a sin² attack, an exponential decay, a cos² release at the end, a second voice
            /// `detune` cents up (half level), and a tremolo. One cos/sin per 16 samples (a rotation in between).
            public void Tone(double t0, double dur, double f0, double f1, double amp, double attack, double release,
                double glide = -1, double shape = 0, double decay = 0, double h2 = 0, double h3 = 0, double detune = 0,
                double trem = 0, double tremHz = 6)
            {
                int i0 = I(t0), n = Math.Min(N - i0, I(dur));
                if (glide <= 0) glide = dur;
                double ln = Math.Log(f1 / f0), dr = Math.Pow(2, detune / 1200), norm = 1 / (1 + h2 + h3) / (detune != 0 ? 1.5 : 1);
                var o1 = new Osc(); var o2 = new Osc();
                double e = 0, de = 0;
                for (int i = 0; i < n; i++)
                {
                    if ((i & 15) == 0)
                    {
                        double t = i / (double)Rate, u = Math.Min(1, t / glide);
                        u = shape > 0 ? 1 - Math.Pow(1 - u, 1 + shape) : shape < 0 ? Math.Pow(u, 1 - shape) : u;
                        double f = f0 * Math.Exp(ln * u);
                        o1.Set(f);
                        if (detune != 0) o2.Set(f * dr);
                        e = Env(t); de = (Env(t + 16.0 / Rate) - e) / 16;
                    }
                    double x = o1.X, y = o1.Y, v = y + h2 * 2 * x * y + h3 * y * (3 - 4 * y * y);
                    o1.Step();
                    if (detune != 0)
                    {
                        double x2 = o2.X, y2 = o2.Y;
                        v += 0.5 * (y2 + h2 * 2 * x2 * y2 + h3 * y2 * (3 - 4 * y2 * y2));
                        o2.Step();
                    }
                    B[i0 + i] += (float)(amp * norm * e * v);
                    e += de;
                }

                double Env(double t)
                {
                    if (t >= dur) return 0;
                    double a = t < attack ? Sq(Math.Sin(0.5 * Math.PI * t / attack)) : 1;
                    double d = decay > 0 ? Math.Exp(-decay * Math.Max(0, t - attack)) : 1;
                    double r = t > dur - release ? Sq(Math.Cos(0.5 * Math.PI * (t - (dur - release)) / release)) : 1;
                    double tr = trem > 0 ? (1 + trem * Math.Sin(2 * Math.PI * tremHz * t)) / (1 + trem) : 1;
                    return a * d * r * tr;
                }
            }

            /// A low body: a sine (and a little 2nd harmonic, so small speakers carry it) that drops from f0 to f1.
            public void Thump(double t0, double f0, double f1, double pitchRate, double amp, double decay)
            {
                int i0 = I(t0);
                if (i0 >= N) return;
                int n = Math.Min(N - i0, (int)(11.5 / decay * Rate) + 1);
                var o = new Osc();
                double a = 0, ka = Attack(0.0015), r = Math.Exp(-decay / Rate), e = 1;
                for (int i = 0; i < n; i++)
                {
                    if ((i & 15) == 0) o.Set(f1 + (f0 - f1) * Math.Exp(-pitchRate * i / (double)Rate));
                    double x = o.X, y = o.Y;
                    o.Step();
                    a += (1 - a) * ka;
                    B[i0 + i] += (float)(amp * a * e * (y + 0.6 * x * y) / 1.3);
                    e *= r;
                }
            }

            /// Beads of light: `count` tiny glass grains spread over t0..t1 (jittered, fixed seed), stepping through
            /// `notes`, loudest in the middle of the span.
            public void Beads(double t0, double t1, double[] notes, int count, double amp, uint seed)
            {
                uint s = seed * 2654435761u + 0x7F4A7C15u;
                for (int k = 0; k < count; k++)
                {
                    double u = Clamp01((k + 0.5 + 0.6 * (Rand(ref s) - 0.5)) / count);
                    double g = amp * (0.65 + 0.35 * Rand(ref s)) * (0.5 + 0.5 * Math.Sin(Math.PI * u));
                    Mode(t0 + (t1 - t0) * u, notes[k % notes.Length], g, decay: 60, attack: 0.0006);
                }
            }

            /// Adds the room, then masters: high-pass 70 Hz, low-pass, optional soft saturation, the common loudness plus
            /// `offsetDb`, the fades, the DC left in a short clip, the peak limit.
            public float[] Finish(double wet, double rt, double offsetDb, double fadeOutMs, double lowPass = 6000, double drive = 0)
            {
                Room(wet, rt);
                var hp = Biquad.HighPass(70, 0.7071);
                var lp = Biquad.LowPass(lowPass, 0.7071);
                for (int i = 0; i < N; i++) B[i] = (float)lp.Run(hp.Run(B[i]));
                if (drive > 0)
                {
                    float pk = Peak(B);
                    if (pk > 0)
                    {
                        double norm = 1 / Math.Tanh(drive);
                        for (int i = 0; i < N; i++) B[i] = (float)(Math.Tanh(drive * B[i] / pk) * norm);
                    }
                }
                double l = Loudness(B);
                if (l > -100) Scale(B, Math.Pow(10, (TargetLoudness + offsetDb - l) / 20));
                int fin = Math.Min(N, (int)(0.002 * Rate)), fout = Math.Min(N, (int)(fadeOutMs / 1000 * Rate));
                for (int i = 0; i < fin; i++) B[i] *= (float)Sq(Math.Sin(0.5 * Math.PI * i / fin));
                for (int i = 0; i < fout; i++) B[N - 1 - i] *= (float)Sq(Math.Sin(0.5 * Math.PI * i / fout));
                // what DC the high-pass leaves in a short clip: subtract the mean as a Hann bump (the ends stay at 0)
                double mean = 0;
                for (int i = 0; i < N; i++) mean += B[i];
                mean /= N;
                for (int i = 0; i < N; i++) B[i] -= (float)(2 * mean * Sq(Math.Sin(Math.PI * i / (N - 1))));
                float peak = Peak(B);
                if (peak > PeakLimit) Scale(B, PeakLimit / peak);
                return B;
            }

            private static readonly double[] ErMs = { 3.1, 5.3, 7.9, 11.3, 14.9, 19.7, 24.1, 29.3 }, ErGain = { 0.50, 0.38, -0.30, 0.25, -0.19, 0.14, -0.10, 0.07 };
            private static readonly double[] CombMs = { 16.1, 18.7, 21.3, 23.9 }, AllpassMs = { 5.1, 1.7 };

            /// A small dark room: the send is band-limited (220 Hz - 4.5 kHz), eight early reflections, four damped
            /// combs (decay time `rt` seconds to -60 dB) through two all-passes; `wet` of it is added to the dry sound.
            /// Its tail ends with the clip (the fade out covers it).
            private void Room(double wet, double rt)
            {
                if (wet <= 0) return;
                var send = new float[N];
                var hp = Biquad.HighPass(220, 0.7071);
                var lp = Biquad.LowPass(4500, 0.7071);
                for (int i = 0; i < N; i++) send[i] = (float)lp.Run(hp.Run(B[i]));
                int[] erD = new int[ErMs.Length];
                for (int j = 0; j < erD.Length; j++) erD[j] = (int)(ErMs[j] * Rate / 1000);
                int nc = CombMs.Length;
                var combs = new float[nc][]; var cPos = new int[nc]; var cG = new double[nc]; var cLp = new double[nc];
                for (int j = 0; j < nc; j++)
                {
                    combs[j] = new float[(int)(CombMs[j] * Rate / 1000)];
                    cG[j] = Math.Pow(10, -3 * CombMs[j] / 1000 / rt);
                }
                var aps = new float[AllpassMs.Length][]; var aPos = new int[AllpassMs.Length];
                for (int j = 0; j < aps.Length; j++) aps[j] = new float[(int)(AllpassMs[j] * Rate / 1000)];
                const double damp = 0.35;
                for (int i = 0; i < N; i++)
                {
                    double er = 0;
                    for (int j = 0; j < erD.Length; j++) if (i >= erD[j]) er += ErGain[j] * send[i - erD[j]];
                    double x = send[i], late = 0;
                    for (int j = 0; j < nc; j++)
                    {
                        float[] buf = combs[j];
                        double o = buf[cPos[j]];
                        cLp[j] = o * (1 - damp) + cLp[j] * damp;
                        buf[cPos[j]] = (float)(x + cG[j] * cLp[j]);
                        if (++cPos[j] == buf.Length) cPos[j] = 0;
                        late += o;
                    }
                    late *= 0.25;
                    for (int j = 0; j < aps.Length; j++)
                    {
                        float[] buf = aps[j];
                        double d = buf[aPos[j]], v = late + 0.5 * d;
                        buf[aPos[j]] = (float)v;
                        if (++aPos[j] == buf.Length) aPos[j] = 0;
                        late = d - 0.5 * v;
                    }
                    B[i] += (float)(wet * (0.6 * er + 0.6 * late));
                }
            }
        }

        /// A sine oscillator as a rotation: X = cos, Y = sin of the phase; Set changes the frequency (and renormalises).
        private struct Osc
        {
            public double X, Y, C, S;
            private bool _init;

            public void Set(double f)
            {
                if (!_init) { X = 1; Y = 0; _init = true; }
                double w = 2 * Math.PI * f / Rate;
                C = Math.Cos(w); S = Math.Sin(w);
                double m = 1 / Math.Sqrt(X * X + Y * Y);
                X *= m; Y *= m;
            }

            public void Step() { double nx = X * C - Y * S; Y = X * S + Y * C; X = nx; }
        }

        /// A biquad (transposed direct form II), RBJ cookbook coefficients.
        private struct Biquad
        {
            private double _b0, _b1, _b2, _a1, _a2, _z1, _z2;

            public double Run(double x)
            {
                double y = _b0 * x + _z1;
                _z1 = _b1 * x - _a1 * y + _z2;
                _z2 = _b2 * x - _a2 * y;
                return y;
            }

            private static Biquad Make(double b0, double b1, double b2, double a0, double a1, double a2) =>
                new Biquad { _b0 = b0 / a0, _b1 = b1 / a0, _b2 = b2 / a0, _a1 = a1 / a0, _a2 = a2 / a0 };

            public static Biquad LowPass(double f, double q)
            {
                double w = 2 * Math.PI * f / Rate, c = Math.Cos(w), al = Math.Sin(w) / (2 * q);
                return Make((1 - c) / 2, 1 - c, (1 - c) / 2, 1 + al, -2 * c, 1 - al);
            }

            public static Biquad HighPass(double f, double q)
            {
                double w = 2 * Math.PI * f / Rate, c = Math.Cos(w), al = Math.Sin(w) / (2 * q);
                return Make((1 + c) / 2, -(1 + c), (1 + c) / 2, 1 + al, -2 * c, 1 - al);
            }

            public static Biquad HighShelf(double f, double db, double q)
            {
                double A = Math.Pow(10, db / 40), w = 2 * Math.PI * f / Rate, c = Math.Cos(w), al = Math.Sin(w) / (2 * q), sa = 2 * Math.Sqrt(A) * al;
                return Make(A * (A + 1 + (A - 1) * c + sa), -2 * A * (A - 1 + (A + 1) * c), A * (A + 1 + (A - 1) * c - sa),
                    A + 1 - (A - 1) * c + sa, 2 * (A - 1 - (A + 1) * c), A + 1 - (A - 1) * c - sa);
            }
        }

        // ------------------------------------------------------------------ measures (also used by tests/sounds/)

        /// Loudness in dB: K-weighted (the BS.1770 shelf and high-pass) mean square of the loudest 100 ms window (a
        /// shorter clip is measured over 100 ms too, as the ear sums a short sound), -0.691 like LUFS. -120 for silence.
        public static double Loudness(float[] x)
        {
            var shelf = Biquad.HighShelf(1681.97, 4.0, 0.7072);
            var hp = Biquad.HighPass(38.13, 0.5003);
            int w = Rate / 10;
            var ring = new double[w];
            double sum = 0, best = 0;
            for (int i = 0; i < x.Length; i++)
            {
                double y = hp.Run(shelf.Run(x[i])), q = y * y;
                sum += q - ring[i % w];
                ring[i % w] = q;
                if (sum > best) best = sum;
            }
            return best <= 1e-12 ? -120 : 10 * Math.Log10(best / w) - 0.691;
        }

        public static float Peak(float[] x)
        {
            float p = 0;
            for (int i = 0; i < x.Length; i++) { float a = Math.Abs(x[i]); if (a > p) p = a; }
            return p;
        }

        private static void Scale(float[] x, double g) { for (int i = 0; i < x.Length; i++) x[i] = (float)(x[i] * g); }
        private static double Sq(double v) => v * v;
        private static double Clamp01(double v) => v < 0 ? 0 : v > 1 ? 1 : v;
        private static double Swell(double u, double a) => u < a ? Sq(Math.Sin(0.5 * Math.PI * u / a)) : Sq(Math.Cos(0.5 * Math.PI * (u - a) / (1 - a)));
        private static double Rand(ref uint s) { s ^= s << 13; s ^= s >> 17; s ^= s << 5; return s / 4294967296.0; }
    }
}

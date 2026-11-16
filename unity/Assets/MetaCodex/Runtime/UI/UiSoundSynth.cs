using System;

namespace MetaCodex.UI
{
    /// The app's short sounds as pure math: no Unity types, no assets, no randomness from the clock, so the same call
    /// gives the same samples on the headset and in tests/sounds/ (which compiles this file as it is).
    ///
    /// Four styles (ShellSettings.soundStyle), each with all eleven sounds:
    /// - hud (the default): the holographic HUD of a sci-fi workstation, crisp and digital; key of A.
    /// - holo: softer, airy, a hologram's shimmer (sines, triangles, FM, vibrato, a longer darker echo); key of D.
    /// - core: deeper, a reactor and its servos (low saws, a servo whine, ring-modulated latches, more sub); key of E.
    /// - classic: the first sounds, formula for formula (Classic below), not mastered.
    /// The toolkit: a synth voice (sine, triangle, band-limited saw and pulse, noise; a detuned stack, pitch glide,
    /// vibrato, an envelope, a resonant state-variable filter whose cutoff sweeps, ring modulation, tremolo), a
    /// two-operator FM voice and a sub thump; per clip: rate / bit reduction mixed in as a texture, a short ping-pong-like
    /// delay, then the mastering: high-pass 40 Hz, a 4th-order low-pass (5-6.5 kHz), one loudness for all (K-weighted, the
    /// loudest 100 ms; each sound has its own offset in dB), a 2 ms fade in, a fade out of 5 ms or more, no DC, the peak
    /// at -1 dBFS at most. Mono, 44.1 kHz.
    public static class UiSoundSynth
    {
        public const int Rate = 44100;

        /// Every sound, the ones needed first first (UiSounds makes them in this order).
        public static readonly string[] Names = { "click", "open", "close", "toast", "ping", "wake", "error", "alert", "hover", "orb-on", "orb-off" };

        /// The styles, the default first (ShellSettings.soundStyle; LOOK & SOUND > SOUND shows them in this order).
        public static readonly string[] Styles = { "hud", "holo", "core", "classic" };

        /// A known style for any text (unknown or empty: hud).
        public static string StyleOf(string style)
        {
            string s = (style ?? "").Trim().ToLowerInvariant();
            return Array.IndexOf(Styles, s) >= 0 ? s : Styles[0];
        }

        /// The common loudness in dB (K-weighted mean square of the loudest 100 ms, like LUFS on a short window).
        public const double TargetLoudness = -17.0;

        /// The highest peak a clip may have (-1 dBFS).
        public const float PeakLimit = 0.891f;

        /// The samples of one sound in one style (an unknown style is hud; null for an unknown name). Allocates its own
        /// buffers; safe on any thread.
        public static float[] Render(string style, string name)
        {
            switch (StyleOf(style))
            {
                case "holo": return Holo(name);
                case "core": return Core(name);
                case "classic": return Classic.Render(name);
                default: return Hud(name);
            }
        }

        private const double E2 = 82.41, A2 = 110.00, B2 = 123.47, D3 = 146.83, E3 = 164.81, G3 = 196.00, A3 = 220.00, B3 = 246.94,
            D4 = 293.66, E4 = 329.63, A4 = 440.00, B4 = 493.88, D5 = 587.33, E5 = 659.26, A5 = 880.00, B5 = 987.77,
            D6 = 1174.66, E6 = 1318.51, A6 = 1760.00, B6 = 1975.53, D7 = 2349.32, E7 = 2637.02;

        // ------------------------------------------------------------------ HUD: crisp, precise, digital (key of A)

        /// A tight slap echo (55 / 85 ms), light rate and bit reduction, bright but cut above 6.5 kHz.
        private static readonly Fx HudFx = new Fx { DelayA = 0.055, DelayB = 0.085, Feedback = 0.28, Wet = 0.16, DelayLowPass = 4000,
            CrushRate = 15000, CrushBits = 10, CrushMix = 0.22, LowPass = 6500 };

        private static float[] Hud(string name)
        {
            Mix m;
            switch (name)
            {
                case "click": // a data tick: a square blip on A6 whose filter snaps shut, a 1 ms tick of noise on top
                    m = new Mix(0.045);
                    m.Synth(0, 0.045, new P { Wave = W.Square, Hz = A6, Amp = 0.5, Attack = 0.0004, Decay = 120, Cut = 5200, CutTo = 1900, CutTime = 0.018, Q = 1.1 });
                    m.Synth(0, 0.004, new P { Wave = W.Noise, Amp = 0.5, Attack = 0.0002, Release = 0.003, Cut = 3800, Q = 1.6, Filter = F.Band, Seed = 1 });
                    return m.Finish(HudFx, offsetDb: -2, fadeOutMs: 6, wet: 0.08, delayScale: 0.35);

                case "hover": // almost nothing: a faint E6 data tick
                    m = new Mix(0.022);
                    m.Synth(0, 0.022, new P { Wave = W.Sine, Hz = E6, Amp = 0.6, Attack = 0.001, Decay = 160 });
                    m.Synth(0, 0.003, new P { Wave = W.Noise, Amp = 0.3, Attack = 0.0003, Release = 0.002, Cut = 3000, Q = 1.5, Filter = F.Band, Seed = 2 });
                    return m.Finish(HudFx, offsetDb: -16, fadeOutMs: 5, wet: 0);

                case "open": // a window is cast (0.35 s): target lock, a power-up sweep as the scan line crosses, then settled
                    m = new Mix(0.50);
                    // the brackets lock on: two square blips, A5 then E6 ("da-dit")
                    m.Synth(0.000, 0.035, new P { Wave = W.Square, Hz = A5, Amp = 0.40, Attack = 0.0005, Decay = 70, Release = 0.01, Cut = 3200, CutTo = 1400, CutTime = 0.03, Q = 1.2 });
                    m.Synth(0.050, 0.035, new P { Wave = W.Square, Hz = E6, Amp = 0.40, Attack = 0.0005, Decay = 70, Release = 0.01, Cut = 3600, CutTo = 1600, CutTime = 0.03, Q = 1.2 });
                    // the scan line crosses: detuned saws on A3, a resonant low-pass opening 300 Hz -> 3.6 kHz; a servo whoosh
                    m.Synth(0.045, 0.255, new P { Wave = W.Saw, Hz = A3, Voices = 3, Detune = 12, Amp = 0.45, Attack = 0.06, Release = 0.07, Cut = 300, CutTo = 3600, CutTime = 0.24, Q = 4.5 });
                    m.Synth(0.060, 0.220, new P { Wave = W.Noise, Amp = 0.18, Attack = 0.08, Release = 0.10, Cut = 900, CutTo = 3200, Q = 3, Filter = F.Band, Seed = 13 });
                    // data written into it: five tiny sine blips
                    Blips(m, new[] { 0.12, 0.15, 0.185, 0.215, 0.25 }, new[] { A6, E6, B6, A6, E6 }, 0.16, 0.018, 140);
                    // it settles: a soft sub thump and the system's acknowledgement, a quick E6 -> A6 chirp
                    m.Thump(0.285, 150, 65, pitchRate: 35, amp: 0.7, decay: 16);
                    m.Synth(0.285, 0.035, new P { Wave = W.Tri, Hz = E6, Amp = 0.40, Attack = 0.001, Decay = 40, Release = 0.008, Cut = 4500, Q = 0.9 });
                    m.Synth(0.318, 0.090, new P { Wave = W.Tri, Hz = A6, Amp = 0.40, Attack = 0.001, Decay = 28, Release = 0.02, Cut = 4500, Q = 0.9 });
                    return m.Finish(HudFx, offsetDb: 0, fadeOutMs: 60);

                case "close": // a window goes (0.22 s): a zap, a power-down sweep as the line runs back, the spark home
                    m = new Mix(0.34);
                    // the surge: A6 ring-modulated by 620 Hz (a bright digital zap)
                    m.Synth(0, 0.04, new P { Wave = W.Sine, Hz = A6, Amp = 0.35, Attack = 0.0005, Decay = 60, Release = 0.01, Ring = 620 });
                    // the line runs back: the saws fall a fifth (A3 -> D3) while their filter closes; the whoosh falls with them
                    m.Synth(0.02, 0.15, new P { Wave = W.Saw, Hz = A3, To = D3, Voices = 3, Detune = 12, Amp = 0.45, Attack = 0.012, Release = 0.05, Cut = 3600, CutTo = 250, Q = 4.5 });
                    m.Synth(0.02, 0.15, new P { Wave = W.Noise, Amp = 0.18, Attack = 0.02, Release = 0.07, Cut = 3200, CutTo = 800, Q = 3, Filter = F.Band, Seed = 22 });
                    // the brackets let go: a tick
                    m.Synth(0.14, 0.004, new P { Wave = W.Noise, Amp = 0.3, Attack = 0.0002, Release = 0.003, Cut = 3500, Q = 1.6, Filter = F.Band, Seed = 23 });
                    // the spark flies home: a quick sine rise E6 -> E7
                    m.Synth(0.158, 0.06, new P { Wave = W.Sine, Hz = E6, To = E7, Shape = -0.5, Amp = 0.18, Attack = 0.02, Release = 0.03 });
                    // it is taken in: a soft sub thump and a low A4 blip
                    m.Thump(0.22, 130, 60, pitchRate: 35, amp: 0.55, decay: 22);
                    m.Synth(0.22, 0.04, new P { Wave = W.Square, Hz = A4, Amp = 0.2, Attack = 0.001, Decay = 60, Release = 0.01, Cut = 1500, CutTo = 600, CutTime = 0.03, Q = 1 });
                    return m.Finish(HudFx, offsetDb: -1, fadeOutMs: 50);

                case "toast": // data in: two pulse blips, B5 then E6
                    m = new Mix(0.12);
                    m.Synth(0, 0.040, new P { Wave = W.Square, Pw = 0.35, Hz = B5, Amp = 0.4, Attack = 0.0006, Decay = 50, Release = 0.01, Cut = 3800, CutTo = 1600, CutTime = 0.035, Q = 1.2 });
                    m.Synth(0.048, 0.070, new P { Wave = W.Square, Pw = 0.35, Hz = E6, Amp = 0.4, Attack = 0.0006, Decay = 40, Release = 0.02, Cut = 4200, CutTo = 1800, CutTime = 0.05, Q = 1.2 });
                    return m.Finish(HudFx, offsetDb: -1, fadeOutMs: 15, delayScale: 0.6);

                case "ping": // here: a sonar ping on A6 after a grace chirp, its echoes running off
                    m = new Mix(0.22);
                    m.Synth(0, 0.014, new P { Wave = W.Sine, Hz = E6, To = A6, Amp = 0.25, Attack = 0.001, Release = 0.004 });
                    m.Synth(0.012, 0.208, new P { Wave = W.Sine, Hz = A6, Amp = 0.7, Attack = 0.001, Decay = 20, Release = 0.03 });
                    return m.Finish(HudFx, offsetDb: 0, fadeOutMs: 35, wet: 0.30);

                case "wake": // "Hey Whisp" heard: two soft rising triangle notes, A5 then E6 (short: the user speaks next)
                    m = new Mix(0.20);
                    m.Synth(0, 0.08, new P { Wave = W.Tri, Hz = A5, Amp = 0.6, Attack = 0.004, Decay = 18, Release = 0.02, Cut = 3000, Q = 0.8 });
                    m.Synth(0.07, 0.13, new P { Wave = W.Tri, Hz = E6, Amp = 0.6, Attack = 0.004, Decay = 16, Release = 0.04, Cut = 3400, Q = 0.8 });
                    return m.Finish(HudFx, offsetDb: -3, fadeOutMs: 30, wet: 0.10); // a little under the others: the microphone is open

                case "alert": // look at this: two firm beeps, a pulse on A5 with a triangle on E6, the second softer
                    m = new Mix(0.32);
                    for (int k = 0; k < 2; k++)
                    {
                        double t = k * 0.13, g = k == 0 ? 1.0 : 0.85;
                        m.Synth(t, 0.085, new P { Wave = W.Square, Pw = 0.3, Hz = A5, Amp = 0.38 * g, Attack = 0.001, Hold = 0.04, Decay = 30, Release = 0.02, Cut = 2600, Q = 1.4 });
                        m.Synth(t, 0.085, new P { Wave = W.Tri, Hz = E6, Amp = 0.25 * g, Attack = 0.001, Hold = 0.04, Decay = 30, Release = 0.02 });
                    }
                    return m.Finish(HudFx, offsetDb: 1, fadeOutMs: 35);

                case "error": // denied: a detuned square falling a fourth (E5, then B4), its filter closing, one note at a time
                    m = new Mix(0.22);
                    m.Synth(0, 0.095, new P { Wave = W.Square, Hz = E5, Voices = 2, Detune = 8, Amp = 0.5, Attack = 0.002, Hold = 0.03, Decay = 20, Release = 0.02, Cut = 1800, CutTo = 700, CutTime = 0.09, Q = 1.5 });
                    m.Synth(0.09, 0.13, new P { Wave = W.Square, Hz = B4, Voices = 2, Detune = 8, Amp = 0.5, Attack = 0.002, Hold = 0.03, Decay = 16, Release = 0.04, Cut = 1500, CutTo = 500, CutTime = 0.12, Q = 1.5 });
                    m.Thump(0, 110, 70, pitchRate: 30, amp: 0.25, decay: 30);
                    return m.Finish(HudFx, offsetDb: 0, fadeOutMs: 30, wet: 0.10);

                case "orb-on": // the Whisp powers up: saws on A2 + E3 + A3 spin up a tone into tune under a resonant low-pass
                    m = new Mix(0.68);  // opening; a sub thump as it ignites; the acknowledgement A5 E6 A6
                    m.Synth(0, 0.62, new P { Wave = W.Saw, Hz = A2 * 0.89, To = A2, Glide = 0.3, Shape = 1, Voices = 3, Detune = 10, Amp = 0.40, Attack = 0.12, Decay = 2.5, Release = 0.18, Cut = 180, CutTo = 2600, CutTime = 0.34, CutShape = 0.5, Q = 5 });
                    m.Synth(0, 0.62, new P { Wave = W.Saw, Hz = E3 * 0.89, To = E3, Glide = 0.3, Shape = 1, Voices = 3, Detune = 10, Amp = 0.30, Attack = 0.12, Decay = 2.5, Release = 0.18, Cut = 180, CutTo = 2600, CutTime = 0.34, CutShape = 0.5, Q = 5 });
                    m.Synth(0.02, 0.60, new P { Wave = W.Saw, Hz = A3 * 0.89, To = A3, Glide = 0.3, Shape = 1, Voices = 2, Detune = 10, Amp = 0.22, Attack = 0.12, Decay = 2.5, Release = 0.18, Cut = 180, CutTo = 2600, CutTime = 0.34, CutShape = 0.5, Q = 5 });
                    m.Synth(0, 0.32, new P { Wave = W.Noise, Amp = 0.15, Attack = 0.20, Release = 0.12, Cut = 400, CutTo = 3000, Q = 2.5, Filter = F.Band, Seed = 31 });
                    m.Thump(0.33, 150, 60, pitchRate: 30, amp: 0.7, decay: 12);
                    m.Synth(0.36, 0.030, new P { Wave = W.Tri, Hz = A5, Amp = 0.3, Attack = 0.001, Decay = 40, Release = 0.008, Cut = 4500 });
                    m.Synth(0.39, 0.030, new P { Wave = W.Tri, Hz = E6, Amp = 0.3, Attack = 0.001, Decay = 40, Release = 0.008, Cut = 4500 });
                    m.Synth(0.42, 0.120, new P { Wave = W.Tri, Hz = A6, Amp = 0.3, Attack = 0.001, Decay = 25, Release = 0.03, Cut = 4500 });
                    return m.Finish(HudFx, offsetDb: -1, fadeOutMs: 120);

                case "orb-off": // the Whisp powers down: the saws spin down a fifth while the low-pass closes, a last falling blip
                    m = new Mix(0.42);
                    m.Synth(0, 0.42, new P { Wave = W.Saw, Hz = A3, To = D3, Glide = 0.36, Shape = -0.6, Voices = 3, Detune = 10, Amp = 0.40, Attack = 0.01, Decay = 3, Release = 0.12, Cut = 2600, CutTo = 160, CutTime = 0.36, Q = 5 });
                    m.Synth(0, 0.42, new P { Wave = W.Saw, Hz = E3, To = A2, Glide = 0.36, Shape = -0.6, Voices = 2, Detune = 10, Amp = 0.28, Attack = 0.01, Decay = 3, Release = 0.12, Cut = 2600, CutTo = 160, CutTime = 0.36, Q = 5 });
                    m.Synth(0, 0.20, new P { Wave = W.Noise, Amp = 0.14, Attack = 0.01, Release = 0.15, Cut = 2500, CutTo = 500, Q = 2.5, Filter = F.Band, Seed = 41 });
                    m.Synth(0.30, 0.06, new P { Wave = W.Sine, Hz = E6, To = A5, Amp = 0.18, Attack = 0.003, Decay = 30, Release = 0.02 });
                    return m.Finish(HudFx, offsetDb: -2, fadeOutMs: 90);
            }
            return null;
        }

        // ------------------------------------------------------------------ HOLO: soft, airy, a hologram's shimmer (key of D)

        /// A longer, darker ping-pong (85 / 125 ms), no rate reduction, cut above 5.5 kHz.
        private static readonly Fx HoloFx = new Fx { DelayA = 0.085, DelayB = 0.125, Feedback = 0.38, Wet = 0.22, DelayLowPass = 3000, LowPass = 5500 };

        private static float[] Holo(string name)
        {
            Mix m;
            switch (name)
            {
                case "click": // a light tap: an FM blip on D6 (bright for a moment), a breath of air
                    m = new Mix(0.045);
                    m.Fm(0, 0.045, D6, ratio: 2, index0: 0.8, index1: 0.1, indexDecay: 120, amp: 0.6, decay: 70, attack: 0.001, release: 0.01);
                    m.Synth(0, 0.006, new P { Wave = W.Noise, Amp = 0.2, Attack = 0.001, Release = 0.004, Cut = 2600, Q = 1.2, Filter = F.Band, Seed = 101 });
                    return m.Finish(HoloFx, offsetDb: -2, fadeOutMs: 6, wet: 0.08, delayScale: 0.3);

                case "hover": // almost nothing: a breath of air with a faint D7
                    m = new Mix(0.022);
                    m.Synth(0, 0.022, new P { Wave = W.Noise, Amp = 0.7, Attack = 0.008, Release = 0.012, Cut = 1800, Q = 3, Filter = F.Band, Seed = 102 });
                    m.Synth(0.002, 0.02, new P { Wave = W.Sine, Hz = D7, Amp = 0.08, Attack = 0.002, Decay = 150 });
                    return m.Finish(HoloFx, offsetDb: -16, fadeOutMs: 5, wet: 0);

                case "open": // the hologram forms (0.35 s cast): glints, a shimmering chord opening, light settling
                    m = new Mix(0.50);
                    // the brackets lock on: two soft FM glints, A5 then D6
                    m.Fm(0, 0.04, A5, ratio: 3, index0: 0.6, index1: 0.05, indexDecay: 90, amp: 0.35, decay: 60, attack: 0.001, release: 0.01);
                    m.Fm(0.05, 0.04, D6, ratio: 3, index0: 0.6, index1: 0.05, indexDecay: 90, amp: 0.35, decay: 60, attack: 0.001, release: 0.01);
                    // the scan line crosses: a swell of air and a D + A triangle chord rising an octave, detuned, wavering, opening
                    m.Synth(0.045, 0.255, new P { Wave = W.Noise, Amp = 0.30, Attack = 0.15, Release = 0.10, Cut = 500, CutTo = 3000, Q = 1.5, Filter = F.Band, Seed = 111 });
                    m.Synth(0.045, 0.255, new P { Wave = W.Tri, Hz = D4, To = D5, Shape = 0.5, Voices = 2, Detune = 9, Vib = 12, VibHz = 7, Amp = 0.30, Attack = 0.12, Release = 0.08, Cut = 600, CutTo = 3500, Q = 2 });
                    m.Synth(0.045, 0.255, new P { Wave = W.Tri, Hz = A4, To = A5, Shape = 0.5, Voices = 2, Detune = 9, Vib = 12, VibHz = 7, Amp = 0.20, Attack = 0.12, Release = 0.08, Cut = 600, CutTo = 3500, Q = 2 });
                    // sparks of light in it
                    FmBlips(m, new[] { 0.12, 0.16, 0.20, 0.24 }, new[] { D6, E6, A6, D7 }, 0.12);
                    // it settles: a soft sub and a shimmering D6 + A6 (detuned sines beating slowly)
                    m.Thump(0.285, 110, 60, pitchRate: 25, amp: 0.45, decay: 14);
                    m.Synth(0.285, 0.215, new P { Wave = W.Sine, Hz = D6, Voices = 2, Detune = 6, Amp = 0.35, Attack = 0.004, Decay = 9, Release = 0.06, Trem = 0.15, TremHz = 9 });
                    m.Synth(0.300, 0.200, new P { Wave = W.Sine, Hz = A6, Voices = 2, Detune = 6, Amp = 0.20, Attack = 0.004, Decay = 11, Release = 0.06, Trem = 0.15, TremHz = 9 });
                    return m.Finish(HoloFx, offsetDb: 0, fadeOutMs: 60);

                case "close": // the hologram folds (0.22 s cast): a flash, the chord and the air sink, a spark of light goes home
                    m = new Mix(0.34);
                    m.Fm(0, 0.05, D6, ratio: 3, index0: 0.8, index1: 0.05, indexDecay: 70, amp: 0.30, decay: 40, attack: 0.001, release: 0.015);
                    m.Synth(0.02, 0.15, new P { Wave = W.Noise, Amp = 0.28, Attack = 0.03, Release = 0.08, Cut = 3000, CutTo = 500, Q = 1.5, Filter = F.Band, Seed = 121 });
                    m.Synth(0.02, 0.15, new P { Wave = W.Tri, Hz = D5, To = D4, Voices = 2, Detune = 9, Vib = 12, VibHz = 7, Amp = 0.30, Attack = 0.02, Release = 0.06, Cut = 3500, CutTo = 600, Q = 2 });
                    m.Synth(0.02, 0.15, new P { Wave = W.Tri, Hz = A5, To = A4, Voices = 2, Detune = 9, Vib = 12, VibHz = 7, Amp = 0.20, Attack = 0.02, Release = 0.06, Cut = 3500, CutTo = 600, Q = 2 });
                    // the spark flies home (A5 -> A6) and is taken in: a soft sub and a faint D6 glint
                    m.Synth(0.158, 0.062, new P { Wave = W.Sine, Hz = A5, To = A6, Shape = -0.5, Amp = 0.18, Attack = 0.025, Release = 0.035 });
                    m.Thump(0.22, 110, 60, pitchRate: 25, amp: 0.35, decay: 22);
                    m.Fm(0.22, 0.12, D6, ratio: 2, index0: 0.4, index1: 0.05, indexDecay: 60, amp: 0.12, decay: 30, attack: 0.002, release: 0.04);
                    return m.Finish(HoloFx, offsetDb: -1, fadeOutMs: 50);

                case "toast": // a message of light: two soft FM blips rising a fifth, D6 then A6
                    m = new Mix(0.12);
                    m.Fm(0, 0.06, D6, ratio: 2, index0: 0.7, index1: 0.05, indexDecay: 80, amp: 0.50, decay: 40, attack: 0.0015, release: 0.012);
                    m.Fm(0.05, 0.07, A6, ratio: 2, index0: 0.6, index1: 0.05, indexDecay: 80, amp: 0.45, decay: 40, attack: 0.0015, release: 0.02);
                    return m.Finish(HoloFx, offsetDb: -1, fadeOutMs: 15, delayScale: 0.5);

                case "ping": // here: a hologram ping, A6 shimmering over an FM D6, its echoes drifting
                    m = new Mix(0.22);
                    m.Synth(0, 0.22, new P { Wave = W.Sine, Hz = A6, Voices = 2, Detune = 5, Amp = 0.55, Attack = 0.002, Decay = 18, Release = 0.03 });
                    m.Fm(0, 0.22, D6, ratio: 2, index0: 1.0, index1: 0.1, indexDecay: 40, amp: 0.30, decay: 22, attack: 0.002, release: 0.03);
                    return m.Finish(HoloFx, offsetDb: 0, fadeOutMs: 35, wet: 0.30);

                case "wake": // "Hey Whisp" heard: two soft rising notes (A5, D6), shimmering sines with a breath of air
                    m = new Mix(0.20);
                    m.Synth(0, 0.09, new P { Wave = W.Sine, Hz = A5, Voices = 2, Detune = 7, Amp = 0.6, Attack = 0.006, Decay = 15, Release = 0.025 });
                    m.Synth(0.07, 0.13, new P { Wave = W.Sine, Hz = D6, Voices = 2, Detune = 7, Amp = 0.6, Attack = 0.006, Decay = 15, Release = 0.04 });
                    m.Synth(0, 0.16, new P { Wave = W.Noise, Amp = 0.10, Attack = 0.06, Release = 0.08, Cut = 2600, Q = 2.5, Filter = F.Band, Seed = 131 });
                    return m.Finish(HoloFx, offsetDb: -3, fadeOutMs: 30, wet: 0.12); // a little under the others: the microphone is open

                case "alert": // look at this: a soft pulsing D6 + A6 shimmer, twice, the second softer
                    m = new Mix(0.32);
                    for (int k = 0; k < 2; k++)
                    {
                        double t = k * 0.13, g = k == 0 ? 1.0 : 0.85;
                        m.Synth(t, 0.12, new P { Wave = W.Tri, Hz = D6, Voices = 2, Detune = 6, Amp = 0.40 * g, Attack = 0.003, Hold = 0.03, Decay = 18, Release = 0.03, Cut = 3500, Q = 1 });
                        m.Synth(t, 0.12, new P { Wave = W.Sine, Hz = A6, Amp = 0.25 * g, Attack = 0.003, Hold = 0.03, Decay = 20, Release = 0.03 });
                    }
                    return m.Finish(HoloFx, offsetDb: 1, fadeOutMs: 35, wet: 0.15);

                case "error": // did not work: a soft falling fourth (A5, then E5) that wavers and sags, one note at a time
                    m = new Mix(0.22);
                    m.Synth(0, 0.10, new P { Wave = W.Tri, Hz = A5, To = A5 * 0.97, Vib = 25, VibHz = 12, Amp = 0.55, Attack = 0.003, Hold = 0.03, Decay = 20, Release = 0.02, Cut = 2500, Q = 0.8 });
                    m.Synth(0.09, 0.13, new P { Wave = W.Tri, Hz = E5, To = E5 * 0.97, Vib = 25, VibHz = 12, Amp = 0.55, Attack = 0.003, Hold = 0.03, Decay = 16, Release = 0.05, Cut = 2200, Q = 0.8 });
                    return m.Finish(HoloFx, offsetDb: 0, fadeOutMs: 30, wet: 0.10);

                case "orb-on": // the Whisp's hologram materialises: a D chord of triangles rising into tune and opening, air
                    m = new Mix(0.68);  // round it, then an arpeggio of light (D6 A6 D7) and a soft sub
                    double[] notes = { D4, A4, D5 }, at = { 0, 0.03, 0.06 }, amp = { 0.5, 0.4, 0.3 };
                    for (int k = 0; k < notes.Length; k++)
                        m.Synth(at[k], 0.68 - at[k], new P { Wave = W.Tri, Hz = notes[k] * 0.94, To = notes[k], Glide = 0.3, Shape = 1, Voices = 2, Detune = 8, Vib = 8, VibHz = 6,
                            Amp = amp[k], Attack = 0.18, Decay = 2, Release = 0.16, Cut = 400, CutTo = 3000, CutTime = 0.35, Q = 1.5 });
                    m.Synth(0, 0.34, new P { Wave = W.Noise, Amp = 0.22, Attack = 0.22, Release = 0.12, Cut = 400, CutTo = 2800, Q = 1.4, Filter = F.Band, Seed = 141 });
                    m.Fm(0.33, 0.05, D6, ratio: 2, index0: 0.5, index1: 0.05, indexDecay: 80, amp: 0.20, decay: 40, attack: 0.001, release: 0.01);
                    m.Fm(0.36, 0.05, A6, ratio: 2, index0: 0.5, index1: 0.05, indexDecay: 80, amp: 0.20, decay: 40, attack: 0.001, release: 0.01);
                    m.Fm(0.39, 0.15, D7, ratio: 2, index0: 0.5, index1: 0.05, indexDecay: 80, amp: 0.20, decay: 25, attack: 0.001, release: 0.04);
                    m.Thump(0.33, 110, 55, pitchRate: 25, amp: 0.40, decay: 10);
                    return m.Finish(HoloFx, offsetDb: -1, fadeOutMs: 120);

                case "orb-off": // the hologram fades: the chord sinks a fourth while its filter closes, the air thins, a glint falls
                    m = new Mix(0.42);
                    m.Synth(0, 0.42, new P { Wave = W.Tri, Hz = D5, To = A4, Glide = 0.36, Shape = -0.5, Voices = 2, Detune = 8, Vib = 10, VibHz = 6, Amp = 0.50, Attack = 0.015, Decay = 3, Release = 0.14, Cut = 3000, CutTo = 300, CutTime = 0.38, Q = 1.5 });
                    m.Synth(0, 0.42, new P { Wave = W.Tri, Hz = A5, To = E5, Glide = 0.36, Shape = -0.5, Voices = 2, Detune = 8, Vib = 10, VibHz = 6, Amp = 0.35, Attack = 0.015, Decay = 3, Release = 0.14, Cut = 3000, CutTo = 300, CutTime = 0.38, Q = 1.5 });
                    m.Synth(0, 0.30, new P { Wave = W.Noise, Amp = 0.18, Attack = 0.02, Release = 0.20, Cut = 2800, CutTo = 400, Q = 1.4, Filter = F.Band, Seed = 151 });
                    m.Synth(0.28, 0.10, new P { Wave = W.Sine, Hz = A6, To = D6, Amp = 0.12, Attack = 0.003, Decay = 30, Release = 0.04 });
                    return m.Finish(HoloFx, offsetDb: -2, fadeOutMs: 90);
            }
            return null;
        }

        // ------------------------------------------------------------------ CORE: deep, a reactor and its servos (key of E)

        /// A tight slap (32 / 47 ms), dark, a little more rate reduction (11 kHz, 9 bits, mixed in at 15 %), cut above 5 kHz.
        private static readonly Fx CoreFx = new Fx { DelayA = 0.032, DelayB = 0.047, Feedback = 0.22, Wet = 0.14, DelayLowPass = 2500,
            CrushRate = 11025, CrushBits = 9, CrushMix = 0.15, LowPass = 5000 };

        private static float[] Core(string name)
        {
            Mix m;
            switch (name)
            {
                case "click": // a relay closes: a square chunk on E4 whose filter snaps shut, a metal tick, a little weight
                    m = new Mix(0.045);
                    m.Synth(0, 0.045, new P { Wave = W.Square, Hz = E4, Amp = 0.55, Attack = 0.0005, Decay = 90, Cut = 2400, CutTo = 500, CutTime = 0.02, Q = 1.6 });
                    m.Synth(0, 0.012, new P { Wave = W.Sine, Hz = E6, Amp = 0.25, Attack = 0.0003, Decay = 300, Ring = 1870 });
                    m.Synth(0, 0.005, new P { Wave = W.Noise, Amp = 0.35, Attack = 0.0003, Release = 0.004, Cut = 1800, Q = 1.4, Filter = F.Band, Seed = 201 });
                    m.Thump(0, 120, 80, pitchRate: 60, amp: 0.25, decay: 60);
                    return m.Finish(CoreFx, offsetDb: -2, fadeOutMs: 6, wet: 0.10, delayScale: 0.5);

                case "hover": // almost nothing: a faint servo tick
                    m = new Mix(0.022);
                    m.Synth(0, 0.004, new P { Wave = W.Noise, Amp = 0.5, Attack = 0.0005, Release = 0.003, Cut = 1500, Q = 2, Filter = F.Band, Seed = 202 });
                    m.Synth(0, 0.022, new P { Wave = W.Sine, Hz = B4, Amp = 0.3, Attack = 0.001, Decay = 200 });
                    return m.Finish(CoreFx, offsetDb: -16, fadeOutMs: 5, wet: 0);

                case "open": // the reactor brings a window up (0.35 s cast): latches, a servo and the reactor spinning up, a deep settle
                    m = new Mix(0.50);
                    // the brackets lock on: two latch clanks (ring-modulated blips, B4 then E5) with a tick of noise
                    m.Synth(0.000, 0.030, new P { Wave = W.Sine, Hz = B4, Amp = 0.40, Attack = 0.0005, Decay = 90, Release = 0.008, Ring = 710 });
                    m.Synth(0.000, 0.005, new P { Wave = W.Noise, Amp = 0.25, Attack = 0.0003, Release = 0.004, Cut = 1600, Q = 1.4, Filter = F.Band, Seed = 211 });
                    m.Synth(0.050, 0.030, new P { Wave = W.Sine, Hz = E5, Amp = 0.40, Attack = 0.0005, Decay = 90, Release = 0.008, Ring = 950 });
                    m.Synth(0.050, 0.005, new P { Wave = W.Noise, Amp = 0.25, Attack = 0.0003, Release = 0.004, Cut = 1900, Q = 1.4, Filter = F.Band, Seed = 212 });
                    // the scan line crosses: a servo whine (a pulse gliding 110 -> 330 Hz through a resonant band-pass that
                    // follows it) under detuned saws on E2 + B2 whose low-pass opens with a heavy resonance
                    m.Synth(0.045, 0.25, new P { Wave = W.Square, Pw = 0.25, Hz = 110, To = 330, Shape = 0.4, Amp = 0.22, Attack = 0.04, Release = 0.06, Cut = 440, CutTo = 1320, CutShape = 0.4, Q = 6, Filter = F.Band });
                    m.Synth(0.045, 0.255, new P { Wave = W.Saw, Hz = E2, Voices = 3, Detune = 14, Amp = 0.45, Attack = 0.06, Release = 0.07, Cut = 150, CutTo = 1800, CutTime = 0.24, Q = 6 });
                    m.Synth(0.045, 0.255, new P { Wave = W.Saw, Hz = B2, Voices = 2, Detune = 10, Amp = 0.30, Attack = 0.06, Release = 0.07, Cut = 150, CutTo = 1800, CutTime = 0.24, Q = 6 });
                    // it settles: a deep sub thump and a low confirmation, B3 then E4
                    m.Thump(0.285, 130, 50, pitchRate: 25, amp: 0.9, decay: 12);
                    m.Synth(0.285, 0.04, new P { Wave = W.Square, Hz = B3, Amp = 0.35, Attack = 0.001, Decay = 40, Release = 0.01, Cut = 1600, CutTo = 700, CutTime = 0.04, Q = 1.3 });
                    m.Synth(0.320, 0.12, new P { Wave = W.Square, Hz = E4, Amp = 0.35, Attack = 0.001, Decay = 25, Release = 0.03, Cut = 1800, CutTo = 700, CutTime = 0.10, Q = 1.3 });
                    return m.Finish(CoreFx, offsetDb: 0, fadeOutMs: 60);

                case "close": // the reactor takes it back (0.22 s cast): a surge, servo and saws spinning down, a latch, home
                    m = new Mix(0.34);
                    // the surge: a burst of noise through a resonant low-pass closing
                    m.Synth(0, 0.05, new P { Wave = W.Noise, Amp = 0.35, Attack = 0.002, Release = 0.03, Cut = 2500, CutTo = 600, Q = 3, Seed = 221 });
                    // the line runs back: the servo spins down 330 -> 110 Hz, the saws fall an octave (E3 -> E2) as their filter closes
                    m.Synth(0.02, 0.15, new P { Wave = W.Square, Pw = 0.25, Hz = 330, To = 110, Shape = -0.3, Amp = 0.22, Attack = 0.01, Release = 0.05, Cut = 1320, CutTo = 440, Q = 6, Filter = F.Band });
                    m.Synth(0.02, 0.15, new P { Wave = W.Saw, Hz = E3, To = E2, Voices = 3, Detune = 14, Amp = 0.45, Attack = 0.01, Release = 0.05, Cut = 1800, CutTo = 150, Q = 6 });
                    // the brackets let go: a latch clank
                    m.Synth(0.14, 0.025, new P { Wave = W.Sine, Hz = B4, Amp = 0.30, Attack = 0.0005, Decay = 100, Release = 0.006, Ring = 710 });
                    // the spark flies home (a rising whine, B4 -> E6) and is taken in (a deep thump)
                    m.Synth(0.158, 0.062, new P { Wave = W.Square, Pw = 0.3, Hz = B4, To = E6, Shape = -0.5, Amp = 0.12, Attack = 0.02, Release = 0.03, Cut = 2000, Q = 2 });
                    m.Thump(0.22, 120, 48, pitchRate: 25, amp: 0.8, decay: 18);
                    return m.Finish(CoreFx, offsetDb: -1, fadeOutMs: 50);

                case "toast": // a message: two pulse blips rising a fifth, E5 then B5
                    m = new Mix(0.12);
                    m.Synth(0, 0.045, new P { Wave = W.Square, Pw = 0.4, Hz = E5, Amp = 0.45, Attack = 0.0008, Decay = 45, Release = 0.01, Cut = 2600, CutTo = 1000, CutTime = 0.04, Q = 1.4 });
                    m.Synth(0.05, 0.07, new P { Wave = W.Square, Pw = 0.4, Hz = B5, Amp = 0.40, Attack = 0.0008, Decay = 38, Release = 0.02, Cut = 3000, CutTo = 1100, CutTime = 0.06, Q = 1.4 });
                    return m.Finish(CoreFx, offsetDb: -1, fadeOutMs: 15);

                case "ping": // here: a metal ping (E6 ring-modulated by 932 Hz: an inharmonic clang) over a low E5, slap echoes
                    m = new Mix(0.22);
                    m.Synth(0, 0.22, new P { Wave = W.Sine, Hz = E6, Amp = 0.50, Attack = 0.001, Decay = 22, Release = 0.03, Ring = 932, RingMix = 0.6 });
                    m.Synth(0, 0.22, new P { Wave = W.Sine, Hz = E5, Amp = 0.35, Attack = 0.001, Decay = 16, Release = 0.03 });
                    m.Thump(0, 110, 80, pitchRate: 40, amp: 0.20, decay: 30);
                    return m.Finish(CoreFx, offsetDb: 0, fadeOutMs: 35, wet: 0.22);

                case "wake": // "Hey Whisp" heard: two rising notes, B4 then E5, soft pulse tones through a low-pass
                    m = new Mix(0.20);
                    m.Synth(0, 0.09, new P { Wave = W.Square, Pw = 0.45, Hz = B4, Amp = 0.5, Attack = 0.004, Decay = 16, Release = 0.025, Cut = 1600, Q = 0.9 });
                    m.Synth(0.07, 0.13, new P { Wave = W.Square, Pw = 0.45, Hz = E5, Amp = 0.5, Attack = 0.004, Decay = 14, Release = 0.04, Cut = 1800, Q = 0.9 });
                    return m.Finish(CoreFx, offsetDb: -3, fadeOutMs: 30, wet: 0.10); // a little under the others: the microphone is open

                case "alert": // look at this: the reactor's alarm, softly: two pulses of E4 + B4 with a sub under each
                    m = new Mix(0.32);
                    for (int k = 0; k < 2; k++)
                    {
                        double t = k * 0.13, g = k == 0 ? 1.0 : 0.85;
                        m.Synth(t, 0.10, new P { Wave = W.Saw, Hz = E4, Voices = 2, Detune = 8, Amp = 0.40 * g, Attack = 0.002, Hold = 0.04, Decay = 25, Release = 0.02, Cut = 1600, CutTo = 900, CutTime = 0.10, Q = 2 });
                        m.Synth(t, 0.10, new P { Wave = W.Square, Hz = B4, Amp = 0.22 * g, Attack = 0.002, Hold = 0.04, Decay = 25, Release = 0.02, Cut = 1800, Q = 1 });
                        m.Thump(t, 110, 70, pitchRate: 40, amp: 0.30 * g, decay: 30);
                    }
                    return m.Finish(CoreFx, offsetDb: 1, fadeOutMs: 35);

                case "error": // denied: a deep buzz falling a fifth (B3, then E3), detuned saws through a closing low-pass, a thump
                    m = new Mix(0.22);
                    m.Synth(0, 0.10, new P { Wave = W.Saw, Hz = B3, Voices = 2, Detune = 10, Amp = 0.5, Attack = 0.002, Hold = 0.03, Decay = 18, Release = 0.02, Cut = 1400, CutTo = 500, CutTime = 0.09, Q = 2 });
                    m.Synth(0.09, 0.13, new P { Wave = W.Saw, Hz = E3, Voices = 2, Detune = 10, Amp = 0.5, Attack = 0.002, Hold = 0.03, Decay = 14, Release = 0.04, Cut = 1200, CutTo = 400, CutTime = 0.12, Q = 2 });
                    m.Thump(0, 120, 60, pitchRate: 30, amp: 0.35, decay: 20);
                    return m.Finish(CoreFx, offsetDb: 0, fadeOutMs: 30, wet: 0.10);

                case "orb-on": // the reactor ignites: saws on E2 + B2 + E3 spin up a fourth into tune, the low-pass opens
                    m = new Mix(0.68);  // with a heavy resonance, the hum pulses as it catches; a deep thump; a servo settles (B4, E5)
                    m.Synth(0, 0.62, new P { Wave = W.Saw, Hz = E2 * 0.75, To = E2, Glide = 0.32, Shape = 1, Voices = 3, Detune = 12, Amp = 0.45, Attack = 0.10, Decay = 2, Release = 0.18, Cut = 120, CutTo = 1500, CutTime = 0.34, CutShape = 0.5, Q = 6, Trem = 0.15, TremHz = 8 });
                    m.Synth(0, 0.62, new P { Wave = W.Saw, Hz = B2 * 0.75, To = B2, Glide = 0.32, Shape = 1, Voices = 3, Detune = 12, Amp = 0.30, Attack = 0.10, Decay = 2, Release = 0.18, Cut = 120, CutTo = 1500, CutTime = 0.34, CutShape = 0.5, Q = 6, Trem = 0.15, TremHz = 8 });
                    m.Synth(0.02, 0.60, new P { Wave = W.Saw, Hz = E3 * 0.75, To = E3, Glide = 0.32, Shape = 1, Voices = 2, Detune = 12, Amp = 0.22, Attack = 0.10, Decay = 2, Release = 0.18, Cut = 120, CutTo = 1500, CutTime = 0.34, CutShape = 0.5, Q = 6, Trem = 0.15, TremHz = 8 });
                    m.Synth(0, 0.32, new P { Wave = W.Noise, Amp = 0.20, Attack = 0.22, Release = 0.10, Cut = 300, CutTo = 2000, Q = 2, Seed = 241 });
                    m.Thump(0.33, 140, 45, pitchRate: 22, amp: 0.9, decay: 9);
                    m.Synth(0.36, 0.04, new P { Wave = W.Square, Hz = B4, Amp = 0.28, Attack = 0.001, Decay = 40, Release = 0.01, Cut = 1800, CutTo = 800, CutTime = 0.04, Q = 1.3 });
                    m.Synth(0.40, 0.14, new P { Wave = W.Square, Hz = E5, Amp = 0.28, Attack = 0.001, Decay = 22, Release = 0.04, Cut = 2000, CutTo = 800, CutTime = 0.12, Q = 1.3 });
                    return m.Finish(CoreFx, offsetDb: -1, fadeOutMs: 120);

                case "orb-off": // the reactor shuts down: the saws spin down an octave, faster and faster, the low-pass closes,
                    m = new Mix(0.42);  // a last low clunk
                    m.Synth(0, 0.42, new P { Wave = W.Saw, Hz = E3, To = E2, Glide = 0.38, Shape = -1, Voices = 3, Detune = 12, Amp = 0.45, Attack = 0.01, Decay = 2.5, Release = 0.12, Cut = 1500, CutTo = 120, CutTime = 0.38, Q = 6 });
                    m.Synth(0, 0.42, new P { Wave = W.Saw, Hz = B3, To = B2, Glide = 0.38, Shape = -1, Voices = 2, Detune = 12, Amp = 0.30, Attack = 0.01, Decay = 2.5, Release = 0.12, Cut = 1500, CutTo = 120, CutTime = 0.38, Q = 6 });
                    m.Thump(0.33, 100, 50, pitchRate: 30, amp: 0.50, decay: 25);
                    m.Synth(0.33, 0.006, new P { Wave = W.Noise, Amp = 0.25, Attack = 0.0005, Release = 0.004, Cut = 1200, Q = 1.4, Filter = F.Band, Seed = 251 });
                    return m.Finish(CoreFx, offsetDb: -2, fadeOutMs: 90);
            }
            return null;
        }

        /// Tiny sine blips at the given times and pitches (HUD's data being written).
        private static void Blips(Mix m, double[] at, double[] hz, double amp, double dur, double decay)
        {
            for (int k = 0; k < at.Length; k++)
                m.Synth(at[k], dur, new P { Wave = W.Sine, Hz = hz[k], Amp = amp, Attack = 0.0008, Decay = decay, Release = 0.004 });
        }

        /// Tiny FM blips at the given times and pitches (HOLO's sparks of light).
        private static void FmBlips(Mix m, double[] at, double[] hz, double amp)
        {
            for (int k = 0; k < at.Length; k++)
                m.Fm(at[k], 0.03, hz[k], ratio: 2, index0: 0.5, index1: 0.05, indexDecay: 100, amp: amp, decay: 80, attack: 0.001, release: 0.008);
        }

        // ------------------------------------------------------------------ CLASSIC: the first sounds, formula for formula

        /// The sounds of polish/base f2dcaca (UiSounds before UiSoundSynth), unchanged: plain sine chirps and decays, not
        /// mastered. Mathf stands in for UnityEngine.Mathf (the same float math), so the samples are the same as then.
        private static class Classic
        {
            private static class Mathf
            {
                public const float PI = (float)Math.PI;
                public static float Sin(float x) => (float)Math.Sin(x);
                public static float Exp(float x) => (float)Math.Exp(x);
                public static float Max(float a, float b) => a > b ? a : b;
                public static float Clamp01(float v) => v < 0f ? 0f : v > 1f ? 1f : v;
                public static float Sign(float f) => f >= 0f ? 1f : -1f;
                public static int CeilToInt(float f) => (int)Math.Ceiling(f);
            }

            public static float[] Render(string name)
            {
                switch (name)
                {
                    case "click": return Make(0.045f, (t, d) => Tone(t, 1850f, 2400f, d) * Env(t, d, 0.002f, 0.035f) * 0.55f);
                    case "hover": return Make(0.018f, (t, d) => Tone(t, 3200f, 3200f, d) * Env(t, d, 0.001f, 0.015f) * 0.12f);
                    case "open": return Make(0.66f, (t, d) =>
                        Tone(t, 3800f, 3400f, 0.02f) * Env(t, 0.02f, 0.001f, 0.018f) * 0.20f
                        + Chirp(t, 0.045f, 0.02f, 4300f, 3900f) * Decay(t, 0.045f, 90f) * 0.16f
                        + (Chirp(t, 0.09f, 0.42f, 260f, 1250f) * 0.5f + Chirp(t, 0.09f, 0.42f, 523f, 2510f) * 0.14f) * Win(t, 0.08f, 0.54f) * 0.50f
                        + (Mathf.Sin(2f * Mathf.PI * 2093f * t) + 0.7f * Mathf.Sin(2f * Mathf.PI * 3136f * t))
                          * (0.5f + 0.5f * Mathf.Sin(2f * Mathf.PI * 31f * t)) * Win(t, 0.14f, 0.52f) * 0.05f
                        + Mathf.Sin(2f * Mathf.PI * 140f * t) * Decay(t, 0.50f, 26f) * 0.30f
                        + Mathf.Sin(2f * Mathf.PI * 2637f * t) * Decay(t, 0.505f, 18f) * 0.07f);
                    case "close": return Make(0.46f, (t, d) =>
                        Tone(t, 4200f, 1800f, 0.025f) * Env(t, 0.025f, 0.001f, 0.02f) * 0.20f
                        + (Chirp(t, 0.04f, 0.29f, 1250f, 200f) * 0.5f + Chirp(t, 0.04f, 0.29f, 2510f, 400f) * 0.11f) * Win(t, 0.03f, 0.36f) * 0.48f
                        + Chirp(t, 0.32f, 0.10f, 1500f, 4200f) * Win(t, 0.32f, 0.43f) * 0.14f
                        + Mathf.Sin(2f * Mathf.PI * 196f * t) * Decay(t, 0.42f, 40f) * 0.18f);
                    case "orb-on": return Make(0.70f, (t, d) =>
                        (Mathf.Sin(2f * Mathf.PI * (392f * t + 30f * t * t)) + 0.7f * Mathf.Sin(2f * Mathf.PI * (587f * t + 45f * t * t)) + 0.5f * Mathf.Sin(2f * Mathf.PI * (784f * t + 60f * t * t)))
                        * (0.75f + 0.25f * Mathf.Sin(2f * Mathf.PI * 9f * t)) * Win(t, 0f, 0.70f) * 0.13f
                        + Mathf.Sin(2f * Mathf.PI * 1568f * t) * Decay(t, 0.42f, 9f) * 0.06f);
                    case "orb-off": return Make(0.42f, (t, d) =>
                        (Mathf.Sin(2f * Mathf.PI * (587f * t - 260f * t * t)) + 0.6f * Mathf.Sin(2f * Mathf.PI * (880f * t - 390f * t * t))) * Win(t, 0f, 0.42f) * 0.13f);
                    case "wake": return Make(0.20f, (t, d) => (Mathf.Sin(2f * Mathf.PI * 1318.5f * t) * Decay(t, 0f, 30f) + Mathf.Sin(2f * Mathf.PI * 1975.5f * t) * Decay(t, 0.07f, 24f)) * 0.2f);
                    case "ping": return Make(0.22f, (t, d) => (Chirp(t, 0f, 0.08f, 1800f, 2600f) * Decay(t, 0f, 30f) + Chirp(t, 0.09f, 0.1f, 2400f, 3200f) * Decay(t, 0.09f, 26f)) * 0.22f);
                    case "toast": return Make(0.11f, (t, d) => (t < d * 0.5f ? Tone(t, 1320f, 1320f, d) : Tone(t, 1760f, 1760f, d)) * Env(t, d, 0.004f, 0.09f) * 0.35f);
                    case "alert": return Make(0.32f, (t, d) => ((int)(t / 0.08f) % 2 == 0 ? Tone(t, 880f, 880f, d) : Tone(t, 1175f, 1175f, d)) * Env(t, d, 0.005f, 0.3f) * 0.45f);
                    case "error": return Make(0.22f, (t, d) => Mathf.Sign(Mathf.Sin(2f * Mathf.PI * 180f * t)) * Env(t, d, 0.004f, 0.2f) * 0.18f);
                    default: return null;
                }
            }

            private static float[] Make(float seconds, Func<float, float, float> f)
            {
                int n = Mathf.CeilToInt(seconds * Rate);
                var data = new float[n];
                for (int i = 0; i < n; i++) data[i] = f(i / (float)Rate, seconds);
                return data;
            }

            private static float Tone(float t, float f0, float f1, float d)
            {
                float k = (f1 - f0) / Mathf.Max(d, 1e-4f);
                return Mathf.Sin(2f * Mathf.PI * (f0 * t + 0.5f * k * t * t));
            }

            private static float Chirp(float t, float t0, float dur, float f0, float f1) => t < t0 ? 0f : Tone(t - t0, f0, f1, dur);

            private static float Win(float t, float t0, float t1) { float k = Mathf.Clamp01((t - t0) / (t1 - t0)); float s = Mathf.Sin(Mathf.PI * k); return s * s; }

            private static float Decay(float t, float t0, float rate) => t < t0 ? 0f : Mathf.Clamp01((t - t0) / 0.003f) * Mathf.Exp(-(t - t0) * rate);

            private static float Env(float t, float d, float attack, float release) =>
                Mathf.Clamp01(t / Mathf.Max(attack, 1e-4f)) * Mathf.Clamp01((d - t) / Mathf.Max(release, 1e-4f));
        }

        // ------------------------------------------------------------------ the toolkit

        private enum W { Sine, Tri, Saw, Square, Noise }
        private enum F { Low, Band, High }

        /// One synth voice. Pitch: Hz, gliding to To (0: stays) over Glide seconds (0: the whole note), Shape > 0 eases
        /// out, < 0 eases in; Voices copies spread over +-Detune cents; Vib cents of vibrato at VibHz. Envelope: a sin²
        /// Attack, Hold, an exponential Decay (per second), a cos² Release at the end, Trem of tremolo at TremHz. Filter:
        /// a resonant state-variable filter (Cut 0: none) whose cutoff glides from Cut to CutTo over CutTime (0: the whole
        /// note), CutShape like Shape. Ring: ring modulation by a sine at that many Hz, RingMix of it.
        private sealed class P
        {
            public W Wave = W.Sine;
            public double Pw = 0.5;
            public double Hz = 440, To, Glide, Shape;
            public int Voices = 1;
            public double Detune, Vib, VibHz = 6;
            public double Amp = 1, Attack = 0.001, Hold, Decay, Release = 0.005, Trem, TremHz = 8;
            public double Cut, CutTo, CutTime, CutShape, Q = 0.7071;
            public F Filter = F.Low;
            public double Ring, RingMix = 1;
            public uint Seed = 1;
        }

        /// What a style does to every clip: rate / bit reduction mixed in, a ping-pong-like delay, the low-pass.
        private sealed class Fx
        {
            public double DelayA, DelayB, Feedback, Wet, DelayLowPass = 3500;
            public double CrushRate, CrushMix;
            public int CrushBits = 10;
            public double LowPass = 6000;
        }

        /// A clip being made: voices add into it, Finish adds the style's effects and masters it.
        private sealed class Mix
        {
            public readonly float[] B;
            public readonly int N;

            public Mix(double seconds) { N = (int)Math.Ceiling(seconds * Rate); B = new float[N]; }

            private static int I(double t) => (int)Math.Round(t * Rate);

            /// One-pole step for an attack of about `sec` (63 % reached), 1 = instant.
            private static double Attack(double sec) => sec <= 0 ? 1 : 1 - Math.Exp(-1.0 / (sec * Rate));

            private static double Ease(double u, double shape) => shape > 0 ? 1 - Math.Pow(1 - u, 1 + shape) : shape < 0 ? Math.Pow(u, 1 - shape) : u;

            /// A synth voice (P). Pitch, filter and envelope are updated every 16 samples (the envelope in between is
            /// interpolated); saw and pulse are band-limited (polyBLEP).
            public void Synth(double t0, double dur, P p)
            {
                int i0 = I(t0);
                if (i0 >= N) return;
                int n = Math.Min(N - i0, I(dur)), nv = Math.Max(1, p.Voices);
                var ph = new double[nv]; var ratio = new double[nv];
                for (int k = 0; k < nv; k++)
                {
                    ph[k] = nv == 1 ? 0 : k * 0.618034 % 1.0;
                    ratio[k] = nv == 1 ? 1 : Math.Pow(2, p.Detune * (2.0 * k / (nv - 1) - 1) / 1200);
                }
                double norm = 1 / Math.Sqrt(nv), to = p.To > 0 ? p.To : p.Hz, glide = p.Glide > 0 ? p.Glide : dur, ln = Math.Log(to / p.Hz);
                double cutTo = p.CutTo > 0 ? p.CutTo : p.Cut, cutTime = p.CutTime > 0 ? p.CutTime : dur, lnc = p.Cut > 0 ? Math.Log(cutTo / p.Cut) : 0;
                var svf = new Svf(); var ring = new Osc();
                if (p.Ring > 0) ring.Set(p.Ring);
                uint s = p.Seed * 2654435761u + 0x9E3779B9u;
                double f = p.Hz, e = 0, de = 0;
                for (int i = 0; i < n; i++)
                {
                    if ((i & 15) == 0)
                    {
                        double t = i / (double)Rate;
                        f = p.Hz * Math.Exp(ln * Ease(Math.Min(1, t / glide), p.Shape));
                        if (p.Vib > 0) f *= Math.Pow(2, p.Vib / 1200 * Math.Sin(2 * Math.PI * p.VibHz * t));
                        if (p.Cut > 0) svf.Set(p.Cut * Math.Exp(lnc * Ease(Math.Min(1, t / cutTime), p.CutShape)), p.Q);
                        e = Env(p, t, dur); de = (Env(p, t + 16.0 / Rate, dur) - e) / 16;
                    }
                    double v = 0;
                    if (p.Wave == W.Noise) { s ^= s << 13; s ^= s >> 17; s ^= s << 5; v = s / 2147483648.0 - 1; }
                    else
                    {
                        for (int k = 0; k < nv; k++)
                        {
                            double dt = f * ratio[k] / Rate, x = ph[k];
                            v += Osc1(p.Wave, x, dt, p.Pw);
                            x += dt;
                            ph[k] = x >= 1 ? x - 1 : x;
                        }
                        v *= norm;
                    }
                    if (p.Cut > 0) v = svf.Run(v, p.Filter);
                    if (p.Ring > 0) { v *= 1 - p.RingMix + p.RingMix * ring.Y; ring.Step(); }
                    B[i0 + i] += (float)(p.Amp * e * v);
                    e += de;
                }
            }

            private static double Env(P p, double t, double dur)
            {
                if (t >= dur) return 0;
                double a = t < p.Attack ? Sq(Math.Sin(0.5 * Math.PI * t / p.Attack)) : 1;
                double d = p.Decay > 0 && t > p.Attack + p.Hold ? Math.Exp(-p.Decay * (t - p.Attack - p.Hold)) : 1;
                double r = p.Release > 0 && t > dur - p.Release ? Sq(Math.Cos(0.5 * Math.PI * (t - (dur - p.Release)) / p.Release)) : 1;
                double tr = p.Trem > 0 ? (1 + p.Trem * Math.Sin(2 * Math.PI * p.TremHz * t)) / (1 + p.Trem) : 1;
                return a * d * r * tr;
            }

            /// One sample of a waveform at phase x (0..1), dt = frequency / rate; saw and pulse with polyBLEP edges.
            private static double Osc1(W w, double x, double dt, double pw)
            {
                switch (w)
                {
                    case W.Sine: return Math.Sin(2 * Math.PI * x);
                    case W.Tri: return 1 - 4 * Math.Abs(x - 0.5); // its harmonics fall 12 dB an octave: no edges to smooth
                    case W.Saw: return 2 * x - 1 - Blep(x, dt);
                    default: return (x < pw ? 1 : -1) + Blep(x, dt) - Blep((x + 1 - pw) % 1.0, dt);
                }
            }

            private static double Blep(double t, double dt)
            {
                if (t < dt) { t /= dt; return t + t - t * t - 1; }
                if (t > 1 - dt) { t = (t - 1) / dt; return t * t + t + t + 1; }
                return 0;
            }

            /// A two-operator FM voice: carrier fc, modulator fc * ratio, the index falls from index0 to index1 (so it
            /// starts brighter), an exponential decay after a short attack, a cos² release over the last `release`
            /// seconds of `dur` (0: it runs to the end of the clip).
            public void Fm(double t0, double dur, double fc, double ratio, double index0, double index1, double indexDecay,
                double amp, double decay, double attack, double release = 0)
            {
                int i0 = I(t0);
                if (i0 >= N) return;
                int n = Math.Min(N - i0, I(dur)), r0 = n - I(release);
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

            /// A sub thump: a sine (and a little 2nd harmonic, so small speakers carry it) that drops from f0 to f1.
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

            /// Rate and bit reduction as a texture: the clip held at `rate` Hz and rounded to `bits` (of its own peak),
            /// `mix` of that added to (1 - mix) of the clean sound.
            private void Crush(double rate, int bits, double mix)
            {
                float pk = Peak(B);
                if (pk <= 0 || rate <= 0) return;
                double step = rate / Rate, ph = 1, held = 0, q = Math.Pow(2, bits - 1);
                for (int i = 0; i < N; i++)
                {
                    ph += step;
                    if (ph >= 1) { ph -= 1; held = Math.Round(B[i] / pk * q) / q * pk; }
                    B[i] = (float)(B[i] * (1 - mix) + held * mix);
                }
            }

            /// A ping-pong delay summed to mono: line A (`a` s) takes the sound and B's echo, line B (`b` s) takes A's,
            /// so echoes come at a, a + b, 2a + b, ...; each pass is low-passed (darker) and the send is high-passed at
            /// 250 Hz (no echoing thumps).
            private void Delay(double a, double b, double fb, double wet, double lowPass)
            {
                int na = Math.Max(1, I(a)), nb = Math.Max(1, I(b)), pa = 0, pb = 0;
                var la = new float[na]; var lb = new float[nb];
                double k = 1 - Math.Exp(-2 * Math.PI * lowPass / Rate), kh = 1 - Math.Exp(-2 * Math.PI * 250 / Rate), sa = 0, sb = 0, hl = 0;
                for (int i = 0; i < N; i++)
                {
                    double x = B[i];
                    hl += (x - hl) * kh;
                    double oa = la[pa], ob = lb[pb];
                    sa += (x - hl + fb * ob - sa) * k;
                    la[pa] = (float)sa;
                    sb += (oa - sb) * k;
                    lb[pb] = (float)sb;
                    if (++pa == na) pa = 0;
                    if (++pb == nb) pb = 0;
                    B[i] = (float)(x + wet * (oa + 0.75 * ob));
                }
            }

            /// The style's effects (rate / bit reduction, the delay: `wet` overrides its level, `delayScale` its times),
            /// then the mastering: high-pass 40 Hz, the style's 4th-order low-pass, optional soft saturation, the common
            /// loudness plus `offsetDb`, the fades, the DC left in a short clip, the peak limit.
            public float[] Finish(Fx fx, double offsetDb, double fadeOutMs, double wet = -1, double delayScale = 1, double drive = 0)
            {
                if (fx.CrushMix > 0) Crush(fx.CrushRate, fx.CrushBits, fx.CrushMix);
                double w = wet >= 0 ? wet : fx.Wet;
                if (w > 0) Delay(fx.DelayA * delayScale, fx.DelayB * delayScale, fx.Feedback, w, fx.DelayLowPass);
                var hp = Biquad.HighPass(40, 0.7071);
                var lp1 = Biquad.LowPass(fx.LowPass, 0.5412);
                var lp2 = Biquad.LowPass(fx.LowPass, 1.3066);
                for (int i = 0; i < N; i++) B[i] = (float)lp2.Run(lp1.Run(hp.Run(B[i])));
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
        }

        /// A resonant state-variable filter (topology-preserving: stable while its cutoff moves).
        private struct Svf
        {
            private double _ic1, _ic2, _a1, _a2, _a3, _k;

            public void Set(double fc, double q)
            {
                double g = Math.Tan(Math.PI * Math.Min(fc, 0.45 * Rate) / Rate);
                _k = 1 / q; _a1 = 1 / (1 + g * (g + _k)); _a2 = g * _a1; _a3 = g * _a2;
            }

            /// Low-pass (resonance peak = Q), band-pass (unity at the centre) or high-pass.
            public double Run(double v0, F mode)
            {
                double v3 = v0 - _ic2, v1 = _a1 * _ic1 + _a2 * v3, v2 = _ic2 + _a2 * _ic1 + _a3 * v3;
                _ic1 = 2 * v1 - _ic1; _ic2 = 2 * v2 - _ic2;
                return mode == F.Low ? v2 : mode == F.Band ? v1 * _k : v0 - _k * v1 - v2;
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
    }
}

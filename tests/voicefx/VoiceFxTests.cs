// The voice effect (Runtime/AI/VoiceFx.cs) without Unity: levels, edge inputs, determinism, WAV in/out, the tail per
// sentence, thread safety, allocations and the chain time on 5 s of speech-like sound.
using System;
using System.Diagnostics;
using System.Linq;
using System.Threading.Tasks;
using MetaCodex.AI;

internal static partial class Program
{
    private const int Rate = 44100;
    private static readonly float Ceiling = (float)Math.Pow(10, -1.0 / 20.0);
    private static readonly VoiceFxPreset[] All = { VoiceFxPreset.Off, VoiceFxPreset.Adjutant, VoiceFxPreset.Starship, VoiceFxPreset.Comm };

    /// Speech-like test sound: syllables of a gliding harmonic voice (110-230 Hz, three formant-ish peaks), hiss for
    /// fricatives, short pauses; the service's padding (0.4 s in front, 0.6 s behind) and its hard clipping.
    private static float[] SyntheticSpeech(double seconds, int rate = Rate, int seed = 3)
    {
        var rng = new Random(seed);
        int pad0 = (int)(0.4 * rate), pad1 = (int)(0.6 * rate), n = (int)(seconds * rate);
        var x = new float[pad0 + n + pad1];
        int i = 0;
        double ph = 0;
        while (i < n)
        {
            int syl = (int)((0.12 + 0.16 * rng.NextDouble()) * rate);
            bool hiss = rng.NextDouble() < 0.25;
            double f0a = 110 + 120 * rng.NextDouble(), f0b = f0a * (0.85 + 0.3 * rng.NextDouble());
            double fm1 = 500 + 400 * rng.NextDouble(), fm2 = 1200 + 1000 * rng.NextDouble();
            for (int k = 0; k < syl && i < n; k++, i++)
            {
                double t = (double)k / syl, env = Math.Sin(Math.PI * t);
                double v;
                if (hiss) v = (rng.NextDouble() * 2 - 1) * 0.35 * env;
                else
                {
                    double f0 = f0a + (f0b - f0a) * t;
                    ph += 2 * Math.PI * f0 / rate;
                    v = 0;
                    for (int h = 1; h <= 30; h++)
                    {
                        double fh = f0 * h;
                        if (fh > rate * 0.45) break;
                        double a = 1.0 / h + 1.2 * Math.Exp(-Math.Pow((fh - fm1) / 180, 2)) + 0.7 * Math.Exp(-Math.Pow((fh - fm2) / 260, 2)) + 0.25 * Math.Exp(-Math.Pow((fh - 2800) / 400, 2));
                        v += a * Math.Sin(h * ph);
                    }
                    v *= 0.32 * env;
                }
                x[pad0 + i] = (float)Math.Max(-1, Math.Min(1, v * 0.75)); // the service's output hard-clips on its loudest peaks
            }
            if (rng.NextDouble() < 0.3) i += (int)(0.08 * rate); // a pause between words
        }
        return x;
    }

    private static float Peak(float[] y) => y.Length == 0 ? 0 : y.Max(Math.Abs);
    private static bool Finite(float[] y) => y.All(v => !float.IsNaN(v) && !float.IsInfinity(v));
    private static int FirstLoud(float[] y) { float t = (float)Math.Pow(10, -60 / 20.0); for (int i = 0; i < y.Length; i++) if (Math.Abs(y[i]) > t) return i; return -1; }
    private static int LastLoud(float[] y) { float t = (float)Math.Pow(10, -60 / 20.0); for (int i = y.Length - 1; i >= 0; i--) if (Math.Abs(y[i]) > t) return i; return -1; }

    private static double Correlation(float[] a, float[] b)
    {
        int n = Math.Min(a.Length, b.Length);
        double sa = 0, sb = 0, sab = 0;
        for (int i = 0; i < n; i++) { sa += a[i] * a[i]; sb += b[i] * b[i]; sab += a[i] * b[i]; }
        return sab / Math.Sqrt(sa * sb + 1e-30);
    }

    private static byte[] Wav(float[] x, int rate, int channels = 1, int bits = 16)
    {
        int bps = bits / 8, n = x.Length;
        var b = new byte[44 + n * bps * channels];
        void S(int at, string s) { for (int i = 0; i < s.Length; i++) b[at + i] = (byte)s[i]; }
        void U32(int at, int v) { b[at] = (byte)v; b[at + 1] = (byte)(v >> 8); b[at + 2] = (byte)(v >> 16); b[at + 3] = (byte)(v >> 24); }
        void U16(int at, int v) { b[at] = (byte)v; b[at + 1] = (byte)(v >> 8); }
        S(0, "RIFF"); U32(4, 36 + n * bps * channels); S(8, "WAVE"); S(12, "fmt "); U32(16, 16); U16(20, 1); U16(22, channels); U32(24, rate);
        U32(28, rate * bps * channels); U16(32, bps * channels); U16(34, bits); S(36, "data"); U32(40, n * bps * channels);
        for (int i = 0; i < n; i++)
            for (int c = 0; c < channels; c++)
            {
                int o = 44 + (i * channels + c) * bps;
                if (bits == 16) { int s = (int)Math.Round(Math.Max(-1, Math.Min(1, x[i])) * 32767); b[o] = (byte)s; b[o + 1] = (byte)(s >> 8); }
                else b[o] = (byte)(128 + (int)(x[i] * 127));
            }
        return b;
    }

    private static float[] FromWav(byte[] w)
    {
        int n = BitConverter.ToInt32(w, 40) / 2;
        var f = new float[n];
        for (int i = 0; i < n; i++) f[i] = BitConverter.ToInt16(w, 44 + i * 2) / 32768f;
        return f;
    }

    /// 16-bit mono PCM of a WAV (any chunks before "data").
    private static float[] ReadWav(byte[] w, out int rate)
    {
        rate = BitConverter.ToInt32(w, 24);
        int off = 12;
        while (off + 8 <= w.Length)
        {
            int size = BitConverter.ToInt32(w, off + 4);
            if (System.Text.Encoding.ASCII.GetString(w, off, 4) == "data")
            {
                int n = Math.Min(size, w.Length - off - 8) / 2;
                var f = new float[n];
                for (int i = 0; i < n; i++) f[i] = BitConverter.ToInt16(w, off + 8 + i * 2) / 32768f;
                return f;
            }
            off += 8 + size + (size & 1);
        }
        return new float[0];
    }

    /// The service's own replies (dry-<voice>-<n>.wav, tools/voicefx): off is the dry voice, every preset as loud as
    /// off and under the ceiling, the C# chain the same sound as the prototype's render, and its time on real speech.
    private static void VoiceFxSamples(string dir)
    {
        var dry = System.IO.Directory.Exists(dir) ? System.IO.Directory.GetFiles(dir, "dry-*.wav").OrderBy(x => x).ToArray() : new string[0];
        if (dry.Length == 0) { Console.WriteLine("voicefx: no service samples in " + dir + " (skipped)"); return; }
        double worstCorr = 1, worstLu = 0, worstPeak = -99, front = 0, back = 0, speech = 0;
        int frontN = 0;
        var parity = All.Skip(1).ToDictionary(p => p, p => 1.0);
        bool finite = true;
        foreach (var path in dry)
        {
            var x = ReadWav(System.IO.File.ReadAllBytes(path), out int rate);
            var tr = VoiceFx.Trim(x, rate);
            speech += tr.Length / (double)rate;
            int cutFront = FirstLoud(x) - FirstLoud(tr); // trim starts the clip this much later in the WAV
            front += cutFront / (double)rate; back += (x.Length - tr.Length - cutFront) / (double)rate; frontN++;
            var off = VoiceFx.Process(x, rate, VoiceFxPreset.Off);
            float lOff = VoiceFx.Lufs(off, rate);
            worstCorr = Math.Min(worstCorr, Correlation(off, tr));
            foreach (var p in All)
            {
                var y = p == VoiceFxPreset.Off ? off : VoiceFx.Process(x, rate, p);
                finite &= Finite(y);
                worstPeak = Math.Max(worstPeak, 20 * Math.Log10(Peak(y) + 1e-12));
                worstLu = Math.Max(worstLu, Math.Abs(VoiceFx.Lufs(y, rate) - (p == VoiceFxPreset.Off ? VoiceFx.TargetLufs : lOff)));
                string proto = System.IO.Path.Combine(dir, VoiceFx.Name(p) + System.IO.Path.GetFileName(path).Substring(3));
                if (p != VoiceFxPreset.Off && System.IO.File.Exists(proto)) parity[p] = Math.Min(parity[p], Correlation(y, ReadWav(System.IO.File.ReadAllBytes(proto), out _)));
            }
        }
        Console.WriteLine($"voicefx: service replies: {speech / frontN:F2} s of speech on average; trimming cuts {front / frontN * 1000:F0} ms in front and {back / frontN * 1000:F0} ms behind");
        Check(worstCorr > 0.995, $"service replies ({dry.Length}): off is the dry voice (lowest correlation {worstCorr:F4})");
        Check(finite && worstPeak <= -1.0 + 1e-4 && worstLu <= 1.0, $"service replies: every preset finite, peak at most {worstPeak:F2} dBFS, loudness within {worstLu:F2} LU of off (off within it of -18 LUFS)");
        Check(parity.Values.All(c => c > 0.97), "service replies: the same sound as the prototype's render (lowest correlation " + string.Join(", ", parity.Select(kv => $"{kv.Key} {kv.Value:F3}")) + ")");
        // the chain time on the longest reply
        var longest = dry.OrderByDescending(f => new System.IO.FileInfo(f).Length).First();
        var lw = System.IO.File.ReadAllBytes(longest);
        double sec = VoiceFx.Trim(ReadWav(lw, out int lr), lr).Length / (double)lr;
        foreach (var p in All)
        {
            VoiceFx.ProcessWav(lw, p);
            var t = new double[9];
            for (int r = 0; r < t.Length; r++) { var sw = Stopwatch.StartNew(); VoiceFx.ProcessWav(lw, p); t[r] = sw.Elapsed.TotalMilliseconds; }
            Array.Sort(t);
            Console.WriteLine($"voicefx: {p,-9} {System.IO.Path.GetFileName(longest)} ({sec:F2} s of speech): median {t[4]:F1} ms = {t[4] / sec:F1} ms per s");
        }
    }

    private static void VoiceFxTests()
    {
        var speech = SyntheticSpeech(5.0);
        var trimmed = VoiceFx.Trim(speech, Rate);
        Check(trimmed.Length < speech.Length - (int)(0.9 * Rate) && trimmed.Length > 5.0 * Rate * 0.9,
            $"trim: {speech.Length / (double)Rate:F2} s -> {trimmed.Length / (double)Rate:F2} s (padding 0.4 + 0.6 s removed)");

        // off: trimmed and at a safe level, otherwise the same sound
        var off = VoiceFx.Process(speech, Rate, VoiceFxPreset.Off);
        float lOff = VoiceFx.Lufs(off, Rate);
        Check(off.Length == trimmed.Length && Peak(off) <= Ceiling + 1e-6f && Math.Abs(lOff - VoiceFx.TargetLufs) <= 0.5f,
            $"off: trimmed ({off.Length} = {trimmed.Length} samples), peak {20 * Math.Log10(Peak(off)):F2} dBFS, {lOff:F2} LUFS");
        // off = the dry voice at another gain: no filter (a 60 Hz high-pass alone turns the phase enough for 0.94-0.98)
        double cOff = Correlation(off, trimmed);
        Check(cOff > 0.995, $"off sounds like the dry voice (correlation {cOff:F4} with the trimmed input)");
        Check(FirstLoud(off) <= (int)(0.040 * Rate) && off.Length - 1 - LastLoud(off) <= (int)(0.040 * Rate),
            $"off: {FirstLoud(off) * 1000.0 / Rate:F0} ms silence in front, {(off.Length - 1 - LastLoud(off)) * 1000.0 / Rate:F0} ms behind (was 400 / 600)");
        Console.WriteLine($"voicefx: dry {20 * Math.Log10(Peak(speech)):F2} dBFS peak, {speech.Count(v => Math.Abs(v) >= 0.9999f)} samples at full scale, {VoiceFx.Lufs(speech, Rate):F1} LUFS");

        // every preset: finite, under the ceiling, the same loudness as off (+-1 LU), speech at once
        foreach (var p in All)
        {
            var y = VoiceFx.Process(speech, Rate, p);
            float l = VoiceFx.Lufs(y, Rate);
            Check(Finite(y) && Peak(y) <= Ceiling + 1e-6f && Math.Abs(l - lOff) <= 1f && FirstLoud(y) >= 0 && FirstLoud(y) <= (int)(0.040 * Rate)
                  && y.Length < trimmed.Length + Rate * 1.2,
                $"{p}: {y.Length / (double)Rate:F2} s, peak {20 * Math.Log10(Peak(y)):F2} dBFS, {l:F2} LUFS (off {lOff:F2}), first sound at {FirstLoud(y) * 1000.0 / Rate:F0} ms");
            var y2 = VoiceFx.Process(speech, Rate, p);
            Check(y.SequenceEqual(y2), $"{p}: the same input gives the same output (pooled buffers reused)");
            double c = Correlation(y, off);
            if (p != VoiceFxPreset.Off) Check(c < 0.999, $"{p}: sounds different from off (correlation {c:F3})");
        }

        // the tail: a sentence that is not the reply's last keeps a short one; the comm squelch only opens and closes
        var aLast = VoiceFx.Process(speech, Rate, VoiceFxPreset.Adjutant, true, true);
        var aInner = VoiceFx.Process(speech, Rate, VoiceFxPreset.Adjutant, false, false);
        Check(aInner.Length < aLast.Length && aInner.Length - trimmed.Length <= (int)(VoiceFx.InnerTail * Rate) + 1,
            $"adjutant: inner sentence tail {(aInner.Length - trimmed.Length) * 1000.0 / Rate:F0} ms, last {(aLast.Length - trimmed.Length) * 1000.0 / Rate:F0} ms");
        var cFirst = VoiceFx.Process(speech, Rate, VoiceFxPreset.Comm, true, false);
        var cMid = VoiceFx.Process(speech, Rate, VoiceFxPreset.Comm, false, false);
        Check(cFirst.Length > cMid.Length + (int)(0.09 * Rate), $"comm: the squelch opens the first sentence only ({(cFirst.Length - cMid.Length) * 1000.0 / Rate:F0} ms longer)");

        // edge inputs
        Check(VoiceFx.Process((float[])null, Rate, VoiceFxPreset.Adjutant) == null, "null in, null out");
        foreach (var p in All)
        {
            bool ok = true; string why = "";
            void Edge(string name, float[] x, int rate)
            {
                try
                {
                    var y = VoiceFx.Process(x, rate, p);
                    if (y == null || !Finite(y) || Peak(y) > Ceiling + 1e-6f && !ReferenceEquals(y, x)) { ok = false; why += name + " "; }
                }
                catch (Exception ex) { ok = false; why += name + " (" + ex.GetType().Name + ") "; }
            }
            Edge("empty", new float[0], Rate);
            Edge("1 sample", new[] { 0.5f }, Rate);
            Edge("63 samples", Enumerable.Repeat(0.9f, 63).ToArray(), Rate);
            Edge("1 s silence", new float[Rate], Rate);
            Edge("near silence", Enumerable.Range(0, Rate).Select(i => (float)(1e-4 * Math.Sin(i * 0.1))).ToArray(), Rate);
            Edge("DC", Enumerable.Repeat(0.5f, Rate).ToArray(), Rate);
            Edge("square", Enumerable.Range(0, Rate).Select(i => (i / 50) % 2 == 0 ? 1f : -1f).ToArray(), Rate);
            Edge("noise", Enumerable.Range(0, Rate).Select(i => (float)(new Random(i).NextDouble() * 2 - 1)).ToArray(), Rate);
            Edge("NaN", Enumerable.Range(0, 2000).Select(i => i == 1000 ? float.NaN : 0.3f * (float)Math.Sin(i * 0.05)).ToArray(), Rate);
            Edge("+inf", Enumerable.Range(0, 2000).Select(i => i == 10 ? float.PositiveInfinity : 0.1f).ToArray(), Rate);
            foreach (int r in new[] { 8000, 16000, 22050, 48000 }) Edge(r + " Hz", SyntheticSpeech(1.0, r), r);
            Edge("rate 0", SyntheticSpeech(0.5), 0);
            Check(ok, $"{p}: edge inputs give finite output under the ceiling, no exception" + (why.Length > 0 ? ": " + why : ""));
        }
        var quiet = VoiceFx.Process(Enumerable.Range(0, Rate).Select(i => (float)(2e-3 * Math.Sin(i * 0.1))).ToArray(), Rate, VoiceFxPreset.Off);
        Check(Peak(quiet) < 0.2f, $"a very quiet input is not pumped up past +24 dB (peak {Peak(quiet):F3})");

        // WAV in, WAV out
        var wav = Wav(speech, Rate);
        foreach (var p in All)
        {
            var o = VoiceFx.ProcessWav(wav, p);
            var direct = VoiceFx.Process(FromWav(wav), Rate, p);
            bool header = o.Length >= 44 && System.Text.Encoding.ASCII.GetString(o, 0, 4) == "RIFF" && System.Text.Encoding.ASCII.GetString(o, 8, 4) == "WAVE"
                          && BitConverter.ToInt16(o, 20) == 1 && BitConverter.ToInt16(o, 22) == 1 && BitConverter.ToInt32(o, 24) == Rate && BitConverter.ToInt16(o, 34) == 16
                          && System.Text.Encoding.ASCII.GetString(o, 36, 4) == "data" && BitConverter.ToInt32(o, 40) == direct.Length * 2
                          && BitConverter.ToInt32(o, 4) == 36 + direct.Length * 2 && o.Length == 44 + direct.Length * 2;
            var back = FromWav(o);
            double err = back.Zip(direct, (a, b) => Math.Abs(a - b)).Max();
            Check(header && err < 1.5 / 32768.0 * 2, $"{p}: WAV out is 16-bit mono {Rate} Hz with {direct.Length} samples (max error {err * 32768:F2} LSB)");
        }
        var stereo = Wav(speech.Take(Rate).ToArray(), Rate, 2);
        var eight = Wav(speech.Take(Rate).ToArray(), Rate, 1, 8);
        var junk = new byte[100];
        var shortWav = Wav(new float[10], Rate);
        Check(ReferenceEquals(VoiceFx.ProcessWav(stereo, VoiceFxPreset.Adjutant), stereo) && ReferenceEquals(VoiceFx.ProcessWav(eight, VoiceFxPreset.Adjutant), eight)
              && ReferenceEquals(VoiceFx.ProcessWav(junk, VoiceFxPreset.Adjutant), junk) && ReferenceEquals(VoiceFx.ProcessWav(shortWav, VoiceFxPreset.Comm), shortWav)
              && VoiceFx.ProcessWav(null, VoiceFxPreset.Off) == null, "stereo, 8-bit, not-a-WAV and tiny WAVs come back unchanged");
        var w48 = Wav(SyntheticSpeech(1.0, 48000), 48000);
        Check(BitConverter.ToInt32(VoiceFx.ProcessWav(w48, VoiceFxPreset.Starship), 24) == 48000, "the WAV keeps its rate (48 kHz)");

        // names
        Check(VoiceFx.Parse("adjutant") == VoiceFxPreset.Adjutant && VoiceFx.Parse("SHIP") == VoiceFxPreset.Starship && VoiceFx.Parse("radio") == VoiceFxPreset.Comm
              && VoiceFx.Parse("off") == VoiceFxPreset.Off && VoiceFx.Parse("banana") == VoiceFxPreset.Off && VoiceFx.Parse(null) == VoiceFxPreset.Off
              && VoiceFx.TryParse("comm", out _) && VoiceFx.TryParse("off", out _) && !VoiceFx.TryParse("banana", out _)
              && VoiceFx.Names.Length == 4 && VoiceFx.Labels.SequenceEqual(new[] { "OFF", "AI", "SHIP", "COMM" }) && VoiceFx.Name(VoiceFxPreset.Comm) == "comm",
            "preset names, labels and spoken names");

        // two sentences processed at the same time give what each gives alone (shared pool)
        var s2 = SyntheticSpeech(3.0, Rate, 9);
        var ref1 = VoiceFx.Process(speech, Rate, VoiceFxPreset.Starship);
        var ref2 = VoiceFx.Process(s2, Rate, VoiceFxPreset.Comm);
        bool same = true;
        for (int round = 0; round < 3; round++)
        {
            var t1 = Task.Run(() => VoiceFx.Process(speech, Rate, VoiceFxPreset.Starship));
            var t2 = Task.Run(() => VoiceFx.Process(s2, Rate, VoiceFxPreset.Comm));
            Task.WaitAll(t1, t2);
            same &= t1.Result.SequenceEqual(ref1) && t2.Result.SequenceEqual(ref2);
        }
        Check(same, "two threads at once: the same results as one by one");

        // allocations: the output and little else (scratch buffers pooled)
        var allocated = typeof(GC).GetMethod("GetAllocatedBytesForCurrentThread", Type.EmptyTypes);
        if (allocated != null)
        {
            VoiceFx.ProcessWav(wav, VoiceFxPreset.Adjutant); // warm the pool
            long b0 = (long)allocated.Invoke(null, null);
            var o = VoiceFx.ProcessWav(wav, VoiceFxPreset.Adjutant);
            long used = (long)allocated.Invoke(null, null) - b0;
            Check(used < o.Length + 64 * 1024, $"adjutant on 5 s: {used / 1024} KB allocated for a {o.Length / 1024} KB output (scratch pooled)");

            // a run of replies: sentences of other lengths and presets, none longer than one the pool has seen
            var lens = new[] { 2.0, 3.4, 1.1, 4.6, 2.7, 0.6, 3.9 };
            var wavs = lens.Select((s, k) => Wav(SyntheticSpeech(s, Rate, 20 + k), Rate)).ToArray();
            foreach (var p in All) VoiceFx.ProcessWav(wav, p, true, true); // the longest sentence of each preset, once
            long outs = 0, a0 = (long)allocated.Invoke(null, null);
            for (int k = 0; k < wavs.Length; k++) outs += VoiceFx.ProcessWav(wavs[k], All[k % All.Length], k % 2 == 0, k % 2 == 1).Length;
            long run = (long)allocated.Invoke(null, null) - a0;
            Check(run < outs + 64 * 1024, $"{wavs.Length} sentences of 0.6-4.6 s, all presets: {run / 1024} KB allocated for {outs / 1024} KB of output (no garbage beyond the outputs)");
        }

        // the chain time on 5 s of speech (this PC; the headset is about 4-6x slower)
        double speechSec = trimmed.Length / (double)Rate;
        foreach (var p in All)
        {
            VoiceFx.ProcessWav(wav, p); // JIT + pool
            var t = new double[9];
            for (int r = 0; r < t.Length; r++) { var sw = Stopwatch.StartNew(); VoiceFx.ProcessWav(wav, p); t[r] = sw.Elapsed.TotalMilliseconds; }
            Array.Sort(t);
            double med = t[t.Length / 2];
            Console.WriteLine($"voicefx: {p,-9} {speechSec:F2} s of speech: median {med:F1} ms (min {t[0]:F1}) = {med / speechSec:F1} ms per s");
            Check(med < 1000, $"{p}: 5 s of speech in under a second on the dev PC ({med:F0} ms)");
        }
    }
}

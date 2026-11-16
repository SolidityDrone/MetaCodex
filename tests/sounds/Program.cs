// UI sound tests without Unity: renders every sound of Runtime/UI/UiSoundSynth.cs (the file itself, compiled here) and
// the sounds of polish/base (OldSounds.cs) to 16-bit 44.1 kHz WAV files (<out>/new, <out>/old), prints a table of
// both, and checks the new ones: no NaN, peak <= -1 dBFS, no DC, faded ends, durations (about the old ones or shorter,
// wake <= 0.21 s), little energy above 6 kHz, one loudness (hover well below), the same samples on every render, and
// the time to make them all. Build + run: tests/sounds/run-sounds-tests.sh
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using MetaCodex.UI;

internal static class Program
{
    private const int Rate = UiSoundSynth.Rate;
    private static int _fails, _passes;
    private static bool _verbose;

    private static void Check(bool ok, string what)
    {
        if (ok) { _passes++; if (_verbose) Console.WriteLine("ok   " + what); return; }
        _fails++;
        Console.WriteLine("FAIL " + what);
    }

    private static int Main(string[] args)
    {
        _verbose = Array.IndexOf(args, "--verbose") >= 0;
        int oi = Array.IndexOf(args, "--out");
        string outDir = oi >= 0 && oi + 1 < args.Length ? args[oi + 1] : null;

        // time: the first render of each (cold: includes the JIT, like the first run on a device) and the best of 5
        var sw = Stopwatch.StartNew();
        var fresh = new Dictionary<string, float[]>();
        foreach (var n in UiSoundSynth.Names) fresh[n] = UiSoundSynth.Render(n);
        double coldMs = sw.Elapsed.TotalMilliseconds;
        double warmMs = double.MaxValue;
        var perSound = UiSoundSynth.Names.ToDictionary(n => n, n => double.MaxValue);
        for (int r = 0; r < 5; r++)
        {
            var all = Stopwatch.StartNew();
            foreach (var n in UiSoundSynth.Names)
            {
                var one = Stopwatch.StartNew();
                var again = UiSoundSynth.Render(n);
                perSound[n] = Math.Min(perSound[n], one.Elapsed.TotalMilliseconds);
                if (r == 0) Check(again.SequenceEqual(fresh[n]), $"{n}: the same samples on every render");
            }
            warmMs = Math.Min(warmMs, all.Elapsed.TotalMilliseconds);
        }
        sw.Restart();
        var old = OldSounds.All();
        double oldMs = sw.Elapsed.TotalMilliseconds;

        Check(UiSoundSynth.Render("nope") == null, "an unknown name gives null");
        Check(new HashSet<string>(UiSoundSynth.Names).SetEquals(old.Keys), "the same names as before: " + string.Join(" ", old.Keys));

        Console.WriteLine($"{"sound",-8} | {"dur s",11} | {"peak dBFS",11} | {"loud dB",11} | {"centroid Hz",11} | {"> 6 kHz dB",11} | {"DC",8} | ms");
        Console.WriteLine($"{"",-8} | {"old   new",11} | {"old   new",11} | {"old   new",11} | {"old   new",11} | {"old   new",11} | {"new",8} |");
        var stats = new Dictionary<string, Stats>();
        foreach (var n in UiSoundSynth.Names)
        {
            var s = Measure(fresh[n]); var o = Measure(old[n]);
            stats[n] = s;
            Console.WriteLine($"{n,-8} | {o.Dur,5:F3} {s.Dur,5:F3} | {o.PeakDb,5:F1} {s.PeakDb,5:F1} | {o.Loud,5:F1} {s.Loud,5:F1} | {o.Centroid,5:F0} {s.Centroid,5:F0} | {o.HfDb,5:F0} {s.HfDb,5:F0} | {s.Dc,8:E1} | {perSound[n]:F2}");

            var x = fresh[n];
            Check(!s.NaN, $"{n}: no NaN or infinity");
            Check(s.PeakDb <= -1.0 + 1e-6, $"{n}: peak {s.PeakDb:F2} dBFS <= -1");
            Check(Math.Abs(s.Dc) < 1e-5, $"{n}: DC {s.Dc:E1} under 1e-5");
            Check(Math.Abs(x[0]) < 1e-4 && Math.Abs(x[x.Length - 1]) < 1e-4, $"{n}: starts and ends at 0");
            Check(MaxAbs(x, 0, Rate / 2000) <= 0.25 * s.Peak, $"{n}: faded in (first 0.5 ms under a quarter of the peak)");
            Check(MaxAbs(x, x.Length - Rate / 1000, x.Length) <= 0.12 * s.Peak, $"{n}: faded out (last 1 ms under 12 % of the peak)");
            double oldDur = old[n].Length / (double)Rate;
            Check(s.Dur <= oldDur * 1.1 + 0.005, $"{n}: {s.Dur:F3} s, about the old {oldDur:F3} s or shorter");
            Check(s.HfDb < -25, $"{n}: energy above 6 kHz {s.HfDb:F1} dB < -25 dB of the whole");
            Check(s.HfTailDb < -30, $"{n}: energy above 6 kHz after the first 30 ms {s.HfTailDb:F1} dB < -30 dB of the whole");
        }
        Check(stats["wake"].Dur <= 0.21, $"wake is short: {stats["wake"].Dur:F3} s <= 0.21 s (the user speaks right after it)");
        var loud = UiSoundSynth.Names.Where(n => n != "hover").Select(n => stats[n].Loud).OrderBy(v => v).ToList();
        double median = loud[loud.Count / 2];
        foreach (var n in UiSoundSynth.Names.Where(n => n != "hover"))
            Check(Math.Abs(stats[n].Loud - median) <= 3.0, $"{n}: loudness {stats[n].Loud:F1} dB within 3 dB of the median {median:F1}");
        Check(stats["hover"].Loud <= median - 10, $"hover: {stats["hover"].Loud:F1} dB, at least 10 dB under the others (almost subliminal)");

        long samples = fresh.Values.Sum(a => (long)a.Length), oldSamples = old.Values.Sum(a => (long)a.Length);
        var oldLoud = UiSoundSynth.Names.Where(n => n != "hover").Select(n => Measure(old[n]).Loud).OrderBy(v => v).ToList();
        Console.WriteLine($"loudness median (no hover): old {oldLoud[oldLoud.Count / 2]:F1} dB, new {median:F1} dB");
        Console.WriteLine($"samples: new {samples} ({samples * 4 / 1024} KB as float, {samples / (double)Rate:F2} s), old {oldSamples} ({oldSamples * 4 / 1024} KB)");
        Console.WriteLine($"time to make all {UiSoundSynth.Names.Length}: new {coldMs:F1} ms cold (first run, with the JIT), {warmMs:F1} ms warm; old {oldMs:F1} ms cold");

        if (outDir != null)
        {
            foreach (var n in UiSoundSynth.Names)
            {
                WriteWav(Path.Combine(outDir, "new", n + ".wav"), fresh[n]);
                WriteWav(Path.Combine(outDir, "old", n + ".wav"), old[n]);
            }
            Console.WriteLine($"wav: {Path.Combine(outDir, "new")} and {Path.Combine(outDir, "old")} ({UiSoundSynth.Names.Length} each)");
        }
        Console.WriteLine(_fails == 0 ? $"sounds: ALL OK ({_passes} checks)" : $"sounds: {_fails} FAILED, {_passes} passed");
        return _fails == 0 ? 0 : 1;
    }

    private sealed class Stats
    {
        public double Dur, PeakDb, Loud, Dc, Centroid, HfDb, HfTailDb;
        public float Peak;
        public bool NaN;
    }

    private static Stats Measure(float[] x)
    {
        var s = new Stats { Dur = x.Length / (double)Rate };
        double sum = 0;
        foreach (var v in x) { if (float.IsNaN(v) || float.IsInfinity(v)) s.NaN = true; sum += v; }
        s.Dc = sum / x.Length;
        s.Peak = UiSoundSynth.Peak(x);
        s.PeakDb = 20 * Math.Log10(Math.Max(s.Peak, 1e-9));
        s.Loud = UiSoundSynth.Loudness(x);
        Spectrum(x, 0, out double all, out double hf, out double centroid);
        s.Centroid = centroid;
        s.HfDb = 10 * Math.Log10(Math.Max(hf, 1e-20) / Math.Max(all, 1e-20));
        Spectrum(x, (int)(0.03 * Rate), out _, out double hfTail, out _);
        s.HfTailDb = 10 * Math.Log10(Math.Max(hfTail, 1e-20) / Math.Max(all, 1e-20));
        return s;
    }

    /// Power spectrum summed over 1024-sample Hann frames (hop 256) from `start`: total energy, energy above 6 kHz,
    /// the spectral centroid.
    private static void Spectrum(float[] x, int start, out double all, out double hf, out double centroid)
    {
        const int F = 1024;
        var re = new double[F]; var im = new double[F];
        all = 0; hf = 0; double fsum = 0;
        for (int p = start - F + 256; p < x.Length; p += 256)
        {
            for (int i = 0; i < F; i++)
            {
                int j = p + i;
                re[i] = j >= start && j < x.Length ? x[j] * (0.5 - 0.5 * Math.Cos(2 * Math.PI * i / F)) : 0;
                im[i] = 0;
            }
            Fft(re, im);
            for (int k = 1; k < F / 2; k++)
            {
                double pw = re[k] * re[k] + im[k] * im[k], f = k * (double)Rate / F;
                all += pw; fsum += pw * f;
                if (f >= 6000) hf += pw;
            }
        }
        centroid = all > 0 ? fsum / all : 0;
    }

    private static void Fft(double[] re, double[] im)
    {
        int n = re.Length;
        for (int i = 1, j = 0; i < n; i++)
        {
            int bit = n >> 1;
            for (; (j & bit) != 0; bit >>= 1) j ^= bit;
            j ^= bit;
            if (i < j) { (re[i], re[j]) = (re[j], re[i]); (im[i], im[j]) = (im[j], im[i]); }
        }
        for (int len = 2; len <= n; len <<= 1)
        {
            double a = -2 * Math.PI / len, wr = Math.Cos(a), wi = Math.Sin(a);
            for (int i = 0; i < n; i += len)
            {
                double cr = 1, ci = 0;
                for (int k = 0; k < len / 2; k++)
                {
                    int u = i + k, v = i + k + len / 2;
                    double tr = re[v] * cr - im[v] * ci, ti = re[v] * ci + im[v] * cr;
                    re[v] = re[u] - tr; im[v] = im[u] - ti; re[u] += tr; im[u] += ti;
                    double nr = cr * wr - ci * wi; ci = cr * wi + ci * wr; cr = nr;
                }
            }
        }
    }

    private static float MaxAbs(float[] x, int from, int to)
    {
        float m = 0;
        for (int i = Math.Max(0, from); i < Math.Min(x.Length, to); i++) m = Math.Max(m, Math.Abs(x[i]));
        return m;
    }

    /// 16-bit PCM mono, triangular dither (fixed seed: the same file every run).
    private static void WriteWav(string path, float[] x)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path));
        using var w = new BinaryWriter(File.Create(path));
        int bytes = x.Length * 2;
        w.Write("RIFF".ToCharArray()); w.Write(36 + bytes); w.Write("WAVE".ToCharArray());
        w.Write("fmt ".ToCharArray()); w.Write(16); w.Write((short)1); w.Write((short)1); w.Write(Rate); w.Write(Rate * 2); w.Write((short)2); w.Write((short)16);
        w.Write("data".ToCharArray()); w.Write(bytes);
        uint s = 0x12345678;
        double R() { s ^= s << 13; s ^= s >> 17; s ^= s << 5; return s / 4294967296.0; }
        foreach (var v in x)
        {
            double d = v * 32767.0 + (R() - R());
            w.Write((short)Math.Max(-32768, Math.Min(32767, Math.Round(d))));
        }
    }
}

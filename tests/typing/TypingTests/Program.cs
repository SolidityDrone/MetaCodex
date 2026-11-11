// The table keyboard's typing engine without Unity: unit checks, then a simulated typist under hand-tracking noise.
// Build + run: tests/typing/run-typing-tests.sh
//
// The typist is a model, not a person: ten fingertips that reach, strike the table, dwell and lift on smooth
// (minimum-jerk) paths, neighbours of the striking finger dragged down with it, each press landing a little off
// its key; the tracker adds what a headset adds (a pose 30 or 60 times a second, 45 ms late, a constant offset per
// finger, jitter, now and then a finger that jumps for a moment). The numbers say whether the rules do what they
// were written for and guard them against regressions. How it types for a person is measured on the headset
// (docs/keyboard.md: the recorder).
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using MetaCodex.Typing;

internal static class Program
{
    private static int _fails, _passes;

    private static void Check(bool ok, string what)
    {
        if (ok) { _passes++; return; }
        _fails++;
        Console.WriteLine("FAIL " + what);
    }

    private static int Main(string[] args)
    {
        string table = args.Length > 0 ? args[0] : "unity/Assets/MetaCodex/Resources/Typing/letters.bytes";
        var letters = File.Exists(table) ? new LetterModel(File.ReadAllBytes(table)) : null;
        Check(letters != null, "the letter table is there: " + table);
        Layout();
        Decoder(letters);
        Detector();
        if (letters != null) Simulation(letters, args.Contains("--long") ? 12 : 5);
        Console.WriteLine(_fails == 0 ? $"ALL OK ({_passes} checks)" : $"{_fails} FAILED, {_passes} passed");
        return _fails == 0 ? 0 : 1;
    }

    // ------------------------------------------------------------------ the layout

    private static void Layout()
    {
        var l = KeyboardLayout.Ansi();
        const float u = KeyboardLayout.Unit;
        Check(l.Keys.Count == 82 && l.Keys.Select(k => k.Id).Distinct().Count() == 82, $"82 keys, each its own id ({l.Keys.Count})");
        // every row of the main block is 16 units wide without holes or overlaps
        bool rowsOk = true;
        for (int row = 1; row <= 5; row++)
        {
            float x = 0f;
            foreach (var k in l.Keys.Where(k => k.Row == row).OrderBy(k => k.UnitX))
            {
                if (Math.Abs(k.UnitX - x) > 1e-4f) rowsOk = false;
                x += k.UnitW;
            }
            // row 4 has its gap left of the up arrow (the inverted T), nothing else
            if (row != 4 && Math.Abs(x - 16f) > 1e-4f) rowsOk = false;
        }
        Check(rowsOk, "rows 1-5 tile 16 units: no hole, no overlap");
        float W(string id) => l.Find(id).UnitW;
        Check(W("tab") == 1.5f && W("caps") == 1.75f && W("lshift") == 2.25f && W("enter") == 2.25f && W("backspace") == 2f && W("space") == 6.25f && W("\\") == 1.5f && W("lctrl") == 1.25f,
            "a real board's key widths: Tab 1.5, Caps 1.75, Shift 2.25, Enter 2.25, Backspace 2, Space 6.25");
        var f = l.Find("f"); var j = l.Find("j"); var q = l.Find("q"); var a = l.Find("a"); var z = l.Find("z");
        Check(Math.Abs(j.Cx - f.Cx - 3f * u) < 1e-6f && Math.Abs(a.X - q.X - 0.25f * u) < 1e-6f && Math.Abs(z.X - a.X - 0.5f * u) < 1e-6f,
            "the stagger of a real board: A a quarter key right of Q, Z half a key right of A, F and J three keys apart");
        Check(Math.Abs(u - 0.01905f) < 1e-7f && Math.Abs(l.Width - 16f * u) < 1e-6f, "19.05 mm from key to key, 16 keys wide");
        var up = l.Find("up"); var down = l.Find("down"); var left = l.Find("left"); var right = l.Find("right");
        Check(Math.Abs(up.Cx - down.Cx) < 1e-6f && up.Row == down.Row - 1 && left.UnitX == down.UnitX - 1f && right.UnitX == down.UnitX + 1f, "the arrows are an inverted T");
        Check(l.Find("esc").UnitX == 0f && l.Find("f1").UnitX == 2f && l.Find("f5").UnitX == 6.5f && l.Find("f9").UnitX == 11f && l.Find("f12").UnitX == 14f,
            "Esc, then F1-F12 in three groups of four");
        Check(l.At(f.Cx, f.Cy) == f && l.At(-0.001f, f.Cy) == null && l.At(l.Find("space").Cx, l.Find("space").Cy).Kind == KbKind.Space, "a point finds its key");
        Check(l.Find("1").Shifted == "!" && l.Find("/").Shifted == "?" && l.Find("a").Shifted == "A" && l.Find("`").Top == "~", "US legends: 1 !, / ?, ` ~");
    }

    // ------------------------------------------------------------------ the decoder

    private static void Decoder(LetterModel letters)
    {
        var l = KeyboardLayout.Ansi();
        var d = new KeyDecoder(l) { Letters = letters };
        var f = l.Find("f"); var g = l.Find("g"); var e = l.Find("e"); var w = l.Find("w");
        Check(d.Decode(1, f.Cx, f.Cy, '\0', '\0', false, KeyContext.Text) == f && d.Choices[0].P > 0.9f, "a press on a key's centre is that key");
        // well inside a key the statistics never win: "th" + a press 4 mm from W's centre towards E stays W
        var inside = d.Decode(3, w.Cx + 0.004f, w.Cy, 't', 'h', false, KeyContext.Text);
        Check(inside == w, $"4 mm off W's centre after “th” is still W ({inside?.Id})");
        if (letters != null)
        {
            // between two keys what was typed decides: after "th", a press just on W's side of the W|E border is E;
            // just on P's side of the O|P border after "sh" it is O ("show"), just on O's side after "ap" it is P ("app")
            float border = (w.Cx + e.Cx) * 0.5f;
            var o = l.Find("o"); var pk = l.Find("p");
            float border2 = (o.Cx + pk.Cx) * 0.5f;
            var a1 = d.Decode(2, border - 0.0008f, w.Cy, 't', 'h', false, KeyContext.Text);
            var a2 = d.Decode(8, border2 + 0.0008f, o.Cy, 's', 'h', false, KeyContext.Text);
            var a3 = d.Decode(8, border2 - 0.0008f, o.Cy, 'a', 'p', false, KeyContext.Text);
            Check(a1 == e && a2 == o && a3 == pk, $"between two keys what was typed decides: “th” + W|E -> {a1?.Id}, “sh” + O|P -> {a2?.Id}, “ap” + O|P -> {a3?.Id}");
            // a secret takes the place alone
            var s1 = d.Decode(2, border - 0.0008f, w.Cy, 't', 'h', false, KeyContext.Secret);
            Check(s1 == w, $"a secret is typed by place alone ({s1?.Id})");
        }
        // Caps Lock beside A is almost never meant
        var caps = l.Find("caps"); var a = l.Find("a");
        var c1 = d.Decode(4, a.X - 0.0015f, a.Cy, 'h', 'e', false, KeyContext.Text);
        Check(c1 == a, $"a press 1.5 mm into Caps Lock from A is A ({c1?.Id})");
        Check(d.Decode(4, caps.Cx, caps.Cy, 'h', 'e', false, KeyContext.Text) == caps, "a press on Caps Lock is Caps Lock");
        // thumbs: the space bar and the keys beside it only
        var space = l.Find("space"); var v = l.Find("v");
        Check(d.Decode(0, v.Cx, v.Cy + 0.004f, 'a', 'b', false, KeyContext.Text) == space && d.Decode(5, space.Cx, space.Cy, 'a', 'b', false, KeyContext.Text) == space,
            "a thumb on the edge of the letter row presses the space bar");
        Check(d.Decode(1, -0.03f, f.Cy, 'a', 'b', false, KeyContext.Text) == null && d.Decode(1, f.Cx, l.Height + 0.03f, 'a', 'b', false, KeyContext.Text) == null, "a press off the keyboard types nothing");
        // wide keys are bars: anywhere along the space bar or Backspace
        var bs = l.Find("backspace");
        Check(d.Decode(6, space.X + 0.008f, space.Cy, 'a', 'b', false, KeyContext.Text) == space && d.Decode(9, bs.X + bs.W - 0.004f, bs.Cy, 'a', 'b', false, KeyContext.Text) == bs, "wide keys take a press anywhere along them");
        // a finger that always lands 5 mm right and 4 mm low of its key: learned, then presses near a border come out right
        var d2 = new KeyDecoder(l) { Letters = null };
        var rng = new Random(7);
        string row = "asdfghjkl";
        int wrongBefore = 0, wrongAfter = 0;
        for (int pass = 0; pass < 2; pass++)
            for (int n = 0; n < 300; n++)
            {
                var key = l.Find(row[rng.Next(row.Length)].ToString());
                float x = key.Cx + 0.005f + (float)Gauss(rng) * 0.003f, y = key.Cy + 0.004f + (float)Gauss(rng) * 0.003f;
                var got = d2.Decode(1, x, y, '\0', '\0', false, KeyContext.Secret);
                if (pass == 0 && n < 40 && got != key) wrongBefore++;
                if (pass == 1 && n >= 260 && got != key) wrongAfter++;
                d2.Learn(1, x, y, got); // what it typed, right or wrong
            }
        Check(Math.Abs(d2.OffX[1] - 0.005f) < 0.0015f && Math.Abs(d2.OffY[1] - 0.004f) < 0.0015f && wrongAfter <= wrongBefore,
            $"a finger's constant offset (5 mm, 4 mm) is learned from its own presses: {d2.OffX[1] * 1000f:F1} mm, {d2.OffY[1] * 1000f:F1} mm; wrong keys {wrongBefore}/40 at first, {wrongAfter}/40 after");
    }

    // ------------------------------------------------------------------ the detector, by hand

    private sealed class Rig
    {
        public readonly TapDetector D = new TapDetector();
        public readonly FingerSample[] F = new FingerSample[10];
        public readonly List<TapEvent> Out = new List<TapEvent>();
        public float T = 10f;
        public float PalmL = 0.05f, PalmR = 0.05f;
        public const float Contact = 0.006f;

        public Rig()
        {
            for (int i = 0; i < 10; i++) F[i] = new FingerSample { Tracked = true, X = 0.02f + i * 0.019f, Y = 0.07f, H = Contact };
            Step(20);
            for (int i = 0; i < 10; i++) D.Calibrate(i, Contact);
        }

        public void Step(int n = 1) { for (int k = 0; k < n; k++) { T += 1f / 72f; D.Step(T, F, PalmL, PalmR, Out); } }
        public void H(int i, float h) => F[i].H = h;

        /// Finger i goes from where it is to `to` over `seconds`: eased at both ends, or (Strike) faster and faster
        /// until the table stops it.
        public void Strike(int i, float to, float seconds, params (int finger, float to)[] others) { _strike = true; Move(i, to, seconds, others); _strike = false; }
        private bool _strike;
        public void Move(int i, float to, float seconds, params (int finger, float to)[] others)
        {
            int n = Math.Max(1, (int)Math.Round(seconds * 72f));
            float from = F[i].H;
            var froms = others.Select(o => F[o.finger].H).ToArray();
            for (int k = 1; k <= n; k++)
            {
                float u = k / (float)n, s = _strike ? u * u : u * u * (3f - 2f * u);
                F[i].H = from + (to - from) * s;
                for (int o = 0; o < others.Length; o++) F[others[o].finger].H = froms[o] + (others[o].to - froms[o]) * s;
                Step();
            }
        }

        public string Fingers => string.Join(",", Out.Select(e => e.Finger));
    }

    private static void Detector()
    {
        const float c = Rig.Contact;
        // resting fingers type nothing, however long
        var r = new Rig();
        r.Step(200);
        Check(r.Out.Count == 0, "ten fingers at rest on the table type nothing");

        // a lift and a strike is one press, seen within a frame or two of arriving, and only once while it stays down
        r.Move(6, c + 0.015f, 0.12f);
        r.Step(6);
        float t0 = r.T;
        r.Strike(6, c, 0.07f);
        r.Step(30);
        float seen = r.Out.Count > 0 ? r.Out[0].Time - (t0 + 0.07f) : 9f;
        Check(r.Out.Count == 1 && r.Out[0].Finger == 6 && seen <= 0.03f, $"a lift and a strike is one press ({r.Fingers}), seen {seen * 1000f:F0} ms after the tip arrives");

        // a finger that sinks slowly has come to rest
        r.Out.Clear();
        r.Move(7, c + 0.015f, 0.12f);
        r.Step(10);
        r.Move(7, c, 0.6f);
        r.Step(30);
        Check(r.Out.Count == 0 && r.D.Settled >= 1, "a finger that sinks slowly to the table has come to rest: no key");

        // two quick presses of the same finger with a small lift (a double letter)
        r.Out.Clear();
        for (int k = 0; k < 2; k++) { r.Move(6, c + 0.009f, 0.06f); r.Strike(6, c, 0.05f); r.Step(4); }
        Check(r.Out.Count == 2, $"a double letter: two presses with a 9 mm lift between ({r.Out.Count})");

        // the neighbour that follows the pressing finger down types nothing, even when it touches
        r.Out.Clear();
        r.Move(7, c + 0.014f, 0.1f, (8, c + 0.009f));
        r.Step(8);
        r.Strike(7, c, 0.07f, (8, c + 0.003f)); // the ring finger follows to 3 mm over the table
        r.Step(4);
        r.Move(8, c, 0.03f);
        r.Step(20);
        Check(r.Out.Count == 1 && r.Out[0].Finger == 7, $"the ring finger dragged down with the middle finger types nothing ({r.Fingers})");

        // two fingers landing in the same frame: the stronger stroke is the key
        r.Move(7, c + 0.016f, 0.1f, (8, c + 0.008f));
        r.Step(8);
        r.Out.Clear();
        r.Strike(7, c, 0.06f, (8, c));
        r.Step(12);
        Check(r.Out.Count == 1 && r.Out[0].Finger == 7, $"two fingers landing together: one key, the stronger stroke's ({r.Fingers})");

        // a hand put down: four fingers together, and staggered with the palm sinking
        var r2 = new Rig();
        r2.Move(1, c + 0.03f, 0.2f, (2, c + 0.03f), (3, c + 0.03f), (4, c + 0.03f));
        r2.Step(10);
        r2.Out.Clear();
        r2.Move(1, c, 0.09f, (2, c), (3, c), (4, c));
        r2.Step(15);
        Check(r2.Out.Count == 0 && r2.D.Landings >= 1, $"a hand put down (four fingers together) types nothing ({r2.Fingers})");
        r2.Move(1, c + 0.03f, 0.2f, (2, c + 0.03f), (3, c + 0.03f), (4, c + 0.03f));
        r2.Step(10);
        r2.Out.Clear();
        // staggered: 4, then 3, 2, 1 each 45 ms later, while the palm sinks 3 cm
        for (int k = 0; k < 26; k++)
        {
            float t = k / 72f;
            r2.PalmL = 0.08f - 0.03f * Math.Min(1f, t / 0.3f);
            for (int fi = 4; fi >= 1; fi--)
            {
                float start = (4 - fi) * 0.045f, u = Math.Min(1f, Math.Max(0f, (t - start) / 0.09f));
                r2.H(fi, c + 0.03f * (1f - u * u * (3f - 2f * u)));
            }
            r2.Step();
        }
        r2.Step(15);
        Check(r2.Out.Count <= 1, $"a hand put down finger after finger, the palm sinking: no row of letters, one key at most ({r2.Fingers})");

        // the thumb's space right before a letter of the same hand: both
        var r3 = new Rig();
        r3.Move(5, c + 0.012f, 0.1f, (6, c + 0.014f));
        r3.Step(6);
        r3.Out.Clear();
        r3.Strike(5, c, 0.06f);
        r3.Step(3);
        r3.Strike(6, c, 0.06f);
        r3.Step(12);
        Check(r3.Out.Count == 2 && r3.Out[0].Finger == 5 && r3.Out[1].Finger == 6, $"space with the thumb, then a letter with the index of the same hand: both, in order ({r3.Fingers})");

        // tracking drifts: every height reads 4 mm higher. Resting fingers show it: their floors follow in half a second
        var r4 = new Rig();
        for (int i = 0; i < 10; i++) r4.H(i, c + 0.004f);
        r4.Step(50);
        Check(r4.Out.Count == 0 && r4.D.Rested >= 2, $"tracked heights jump 4 mm up under resting hands: nothing is typed, the floors follow ({r4.D.Rested} hands)");
        r4.Move(6, c + 0.004f + 0.016f, 0.12f);
        r4.Step(6);
        float t1 = r4.T;
        r4.Strike(6, c + 0.004f, 0.07f);
        r4.Step(10);
        float seen2 = r4.Out.Count > 0 ? r4.Out[0].Time - (t1 + 0.07f) : 9f;
        Check(r4.Out.Count == 1 && !r4.Out[0].Late && seen2 <= 0.03f, $"and the next press is typed on arrival ({r4.Out.Count}, {seen2 * 1000f:F0} ms)");

        // the same drift with the fingers in the air: the first press stops short of the old floor; it is typed when
        // it lifts (late, once), and the next one on arrival
        var r4b = new Rig();
        for (int i = 0; i < 10; i++) r4b.H(i, c + 0.006f + 0.015f + i * 0.001f);
        r4b.Step(60);
        r4b.Strike(6, c + 0.006f, 0.07f);
        r4b.Step(8);
        int atBottom = r4b.Out.Count;
        r4b.Move(6, c + 0.006f + 0.014f, 0.09f);
        r4b.Step(3);
        bool late = r4b.Out.Count == 1 && r4b.Out[0].Late && r4b.Out[0].Finger == 6;
        r4b.Out.Clear();
        float t2 = r4b.T;
        r4b.Strike(6, c + 0.006f, 0.07f);
        r4b.Step(10);
        float seen3 = r4b.Out.Count > 0 ? r4b.Out[0].Time - (t2 + 0.07f) : 9f;
        Check(atBottom == 0 && late && r4b.Out.Count == 1 && !r4b.Out[0].Late && seen3 <= 0.03f,
            $"fingers in the air when the heights drift: the first press is typed when it lifts ({late}), the next one on arrival ({r4b.Out.Count}, {seen3 * 1000f:F0} ms)");

        // a finger that jumps for one frame (a tracking spike) types nothing
        var r5 = new Rig();
        r5.Move(6, c + 0.015f, 0.12f);
        r5.Step(10);
        r5.H(6, c); r5.Step(1);
        r5.H(6, c + 0.015f); r5.Step(20);
        Check(r5.Out.Count == 0, $"a one-frame spike down to the table types nothing ({r5.Fingers})");

        // a finger that slides in sideways at table height is not a press
        var r6 = new Rig();
        r6.Move(6, c + 0.015f, 0.12f);
        r6.Step(6);
        for (int k = 0; k < 6; k++) { r6.F[6].X += 0.004f; r6.F[6].H -= 0.0025f; r6.Step(); }
        r6.Step(10);
        Check(r6.Out.Count == 0, $"a tip sliding sideways at 29 cm/s as it comes down is not a press ({r6.Fingers})");

        // lost and found: a hand that appears low types nothing until it has lifted
        var r7 = new Rig();
        for (int i = 5; i < 10; i++) r7.F[i].Tracked = false;
        r7.Step(10);
        for (int i = 5; i < 10; i++) r7.F[i].Tracked = true;
        r7.Step(20);
        Check(r7.Out.Count == 0, "a hand that comes back into view on the table types nothing");
    }

    // ------------------------------------------------------------------ the simulated typist

    private static double Gauss(Random r)
    {
        double u1 = 1.0 - r.NextDouble(), u2 = r.NextDouble();
        return Math.Sqrt(-2.0 * Math.Log(u1)) * Math.Cos(2.0 * Math.PI * u2);
    }

    private static float Smooth(float u) { u = Math.Min(1f, Math.Max(0f, u)); return u * u * u * (10f + u * (-15f + 6f * u)); } // minimum jerk

    private sealed class Track
    {
        private readonly List<float> _t = new List<float>(), _v = new List<float>();
        private int _at;
        public void Key(float t, float v)
        {
            if (_t.Count > 0 && t <= _t[_t.Count - 1]) t = _t[_t.Count - 1] + 1e-4f;
            _t.Add(t); _v.Add(v);
        }
        public float Last => _v[_v.Count - 1];
        public float LastT => _t[_t.Count - 1];
        public float At(float t)
        {
            if (t <= _t[0]) return _v[0];
            if (t >= _t[_t.Count - 1]) return _v[_v.Count - 1];
            while (_at > 0 && _t[_at] > t) _at--;
            while (_at < _t.Count - 2 && _t[_at + 1] <= t) _at++;
            float u = (t - _t[_at]) / (_t[_at + 1] - _t[_at]);
            return _v[_at] + (_v[_at + 1] - _v[_at]) * Smooth(u);
        }
    }

    private sealed class Style
    {
        public string Name;
        public float Wpm = 40f, Hover = 0.013f, MotorSigma = 0.0025f;
        public bool Rests, Peck;
        public float Coupling = 1f;
    }

    private sealed class Noise
    {
        public string Name;
        public float Rate = 30f, Latency = 0.045f, Jitter = 0.0005f, BiasXY = 0.003f, BiasH = 0.0015f, GlitchPerSecond = 0.02f, ShiftAt = -1f, ShiftH = 0f;
    }

    private sealed class Stroke { public int Finger; public float T; public char Ch; }

    /// The true fingertips over time, for one phrase.
    private sealed class Typist
    {
        public readonly KeyboardLayout L = KeyboardLayout.Ansi();
        public readonly Track[] X = new Track[10], Y = new Track[10], H = new Track[10];
        public readonly float[] Contact = new float[10];
        public readonly List<Stroke> Strokes = new List<Stroke>();
        public readonly List<(int finger, float t0, float t1, float depth)> Dips = new List<(int, float, float, float)>();
        public float End, CalibrateAt = 1.0f;
        private readonly Style _s;
        private readonly Random _r;
        public Track PalmL = new Track(), PalmR = new Track();

        private static readonly Dictionary<char, int> FingerOf = new Dictionary<char, int>();
        static Typist()
        {
            void Put(string keys, int finger) { foreach (char c in keys) FingerOf[c] = finger; }
            Put("`1qaz", 4); Put("2wsx", 3); Put("3edc", 2); Put("45rtfgvb", 1);
            Put("67yuhjnm", 6); Put("8ik,", 7); Put("9ol.", 8); Put("0-=p[];'/", 9);
            FingerOf[' '] = 5;
        }

        private static readonly string[] Home = { "space", "f", "d", "s", "a", "space", "j", "k", "l", ";" };

        public Typist(Style s, string text, int seed)
        {
            _s = s;
            _r = new Random(seed);
            const float u = KeyboardLayout.Unit;
            for (int i = 0; i < 10; i++)
            {
                X[i] = new Track(); Y[i] = new Track(); H[i] = new Track();
                Contact[i] = 0.005f + (float)_r.NextDouble() * 0.003f + (i % 5 == 0 ? 0.004f : 0f); // where the tip joint is when the finger touches
                var home = L.Find(Home[i]);
                float hx = i % 5 == 0 ? home.Cx + (i == 0 ? -1.4f : 1.4f) * u : home.Cx, hy = i % 5 == 0 ? home.Cy + 0.003f : home.Cy;
                X[i].Key(0f, hx); Y[i].Key(0f, hy);
                // flat on the table for the calibration (a flat finger's tip joint sits a little higher), then lifted to type
                H[i].Key(0f, Contact[i] + 0.0025f);
                H[i].Key(1.3f, Contact[i] + 0.0025f);
                H[i].Key(1.75f, Idle(i));
            }
            PalmL.Key(0f, 0.03f); PalmL.Key(1.3f, 0.03f); PalmL.Key(1.75f, 0.05f);
            PalmR.Key(0f, 0.03f); PalmR.Key(1.3f, 0.03f); PalmR.Key(1.75f, 0.05f);
            float t = 2.4f, gap = 60f / (5f * s.Wpm);
            int lastFinger = -1;
            float[] busyUntil = new float[10];
            foreach (char ch in text)
            {
                if (!FingerOf.TryGetValue(ch, out int finger)) continue;
                var key = ch == ' ' ? L.Find("space") : L.Find(ch.ToString());
                if (s.Peck) finger = ch == ' ' ? 5 : key.Cx < L.Find("t").Cx + 0.5f * u ? 1 : 6;
                else if (ch == ' ') finger = lastFinger >= 0 && lastFinger < 5 ? 5 : 0; // the thumb of the other hand
                float dt = gap * (float)Math.Exp(Gauss(_r) * 0.22);
                if (finger == lastFinger) dt *= 1.25f;
                t = Math.Max(t + dt, busyUntil[finger] + 0.11f); // the same finger again: it has to lift and strike once more
                Press(finger, key, ch, t, out busyUntil[finger]);
                lastFinger = finger;
            }
            End = t + 1.0f;
            for (int i = 0; i < 10; i++) GoHome(i, End + 1f);
        }

        private float Idle(int i) => _s.Peck && i % 5 != 1 && i % 5 != 0 ? Contact[i] + 0.028f : _s.Rests ? Contact[i] : Contact[i] + _s.Hover;

        private readonly float[] _freeAt = new float[10];

        /// A finger that has pressed goes back over its home key when it has the time (a typist who rests lets it sink
        /// onto the table there; one who pecks keeps the hand where it is).
        private void GoHome(int f, float before)
        {
            float t0 = _freeAt[f];
            if (t0 <= 0f || _s.Peck) return;
            _freeAt[f] = 0f;
            if (before - t0 < 0.26f) return; // the next press of this finger comes first
            var home = L.Find(Home[f]);
            float hx = f % 5 == 0 ? X[f].Last : home.Cx, hy = f % 5 == 0 ? Y[f].Last : home.Cy;
            X[f].Key(t0, X[f].Last); Y[f].Key(t0, Y[f].Last);
            X[f].Key(t0 + 0.22f, hx); Y[f].Key(t0 + 0.22f, hy);
            if (_s.Rests) { H[f].Key(t0 + 0.02f, H[f].Last); H[f].Key(t0 + 0.24f, Contact[f]); } // a small hop, then down again
            else H[f].Key(t0 + 0.22f, Contact[f] + _s.Hover);
        }

        private void Press(int f, KeyDef key, char ch, float T, out float busyUntil)
        {
            float down = 0.065f + (float)_r.NextDouble() * 0.02f, dwell = 0.06f + (float)_r.NextDouble() * 0.05f, upT = 0.09f;
            float lift = Math.Max(_s.Hover, 0.010f) * (0.85f + 0.4f * (float)_r.NextDouble());
            float tx = key.Kind == KbKind.Space ? X[f].Last + (float)Gauss(_r) * 0.004f : key.Cx + (float)Gauss(_r) * _s.MotorSigma;
            float ty = key.Cy + (float)Gauss(_r) * _s.MotorSigma;
            GoHome(f, T - down - 0.16f);
            float start = Math.Max(H[f].LastT + 0.005f, T - down - 0.16f);
            float top = Math.Max(start + 0.04f, T - down);
            // reach over the key (lifting when it rests), strike, dwell, lift
            X[f].Key(start, X[f].Last); Y[f].Key(start, Y[f].Last); H[f].Key(start, H[f].Last);
            X[f].Key(top, tx); Y[f].Key(top, ty - 0.0015f); H[f].Key(top, Contact[f] + lift);
            X[f].Key(T, tx); Y[f].Key(T, ty); H[f].Key(T, Contact[f]);
            X[f].Key(T + dwell, tx); Y[f].Key(T + dwell, ty); H[f].Key(T + dwell, Contact[f]);
            // a typist who rests lifts only a little after a press
            float after = _s.Rests ? 0.004f : lift;
            H[f].Key(T + dwell + upT, Contact[f] + after);
            Dips.Add((f, top, T + dwell + upT, lift));
            busyUntil = T + dwell + upT;
            _freeAt[f] = busyUntil;
            if (_s.Peck)
            {
                // the whole hand bobs with a pecking finger
                var palm = f < 5 ? PalmL : PalmR;
                palm.Key(top, 0.05f); palm.Key(T, 0.05f - lift * 0.8f); palm.Key(T + dwell, 0.05f - lift * 0.8f); palm.Key(T + dwell + upT, 0.05f);
            }
            Strokes.Add(new Stroke { Finger = f, T = T, Ch = ch });
        }

        private static float Couple(int a, int b)
        {
            if (a / 5 != b / 5 || a % 5 == 0 || b % 5 == 0) return 0f;
            int lo = Math.Min(a % 5, b % 5), d = Math.Abs(a - b);
            return d == 1 ? (lo == 3 ? 0.55f : lo == 2 ? 0.45f : 0.30f) : d == 2 ? 0.15f : 0.05f;
        }

        public void At(float t, int i, out float x, out float y, out float h)
        {
            x = X[i].At(t); y = Y[i].At(t);
            h = H[i].At(t);
            // dragged down by a neighbour's stroke (it cannot go through the table)
            float drag = 0f;
            foreach (var d in Dips)
            {
                if (d.finger == i || t < d.t0 || t > d.t1) continue;
                float c = Couple(d.finger, i) * _s.Coupling;
                if (c <= 0f) continue;
                float sunk = Contact[d.finger] + d.depth - H[d.finger].At(t - 0.01f);
                if (sunk > 0f) drag = Math.Max(drag, c * sunk);
            }
            if (_s.Peck && i % 5 != 0 && i % 5 != 1) h += (i < 5 ? PalmL : PalmR).At(t) - 0.05f; // curled fingers ride on the hand
            h = Math.Max(Contact[i], h - drag);
        }
    }

    /// What the headset reports: sampled, late, offset, jittery, now and then wrong.
    private sealed class Tracker
    {
        private readonly Typist _ty;
        private readonly Noise _n;
        private readonly Random _r;
        private readonly float[] _bx = new float[10], _by = new float[10], _bh = new float[10];
        private readonly float[] _gx = new float[10], _gy = new float[10], _gh = new float[10];
        private readonly int[] _glitchLeft = new int[10];
        private int _k = -1;
        private readonly FingerSample[] _a = new FingerSample[10], _b = new FingerSample[10];
        private float _ta, _tb, _palmLa, _palmLb, _palmRa, _palmRb;

        public Tracker(Typist ty, Noise n, int seed)
        {
            _ty = ty; _n = n; _r = new Random(seed * 7919 + 13);
            for (int i = 0; i < 10; i++)
            {
                // a hand's fingers share most of their offset; ring and little fingers are tracked worst
                float worse = i % 5 >= 3 ? 1.4f : 1f;
                _bx[i] = (float)Gauss(_r) * n.BiasXY * worse; _by[i] = (float)Gauss(_r) * n.BiasXY * worse; _bh[i] = (float)Gauss(_r) * n.BiasH;
            }
        }

        private void Sample(int k, FingerSample[] into, out float palmL, out float palmR)
        {
            float t = k / _n.Rate;
            float shift = _n.ShiftAt >= 0f && t >= _n.ShiftAt ? _n.ShiftH : 0f;
            for (int i = 0; i < 10; i++)
            {
                _ty.At(t, i, out float x, out float y, out float h);
                if (_glitchLeft[i] == 0 && _r.NextDouble() < _n.GlitchPerSecond / _n.Rate)
                {
                    _glitchLeft[i] = 2 + _r.Next(3);
                    _gx[i] = (float)Gauss(_r) * 0.007f; _gy[i] = (float)Gauss(_r) * 0.007f; _gh[i] = (float)Gauss(_r) * 0.008f;
                }
                bool g = _glitchLeft[i] > 0;
                if (g) _glitchLeft[i]--;
                into[i] = new FingerSample
                {
                    Tracked = true,
                    X = x + _bx[i] + (float)Gauss(_r) * _n.Jitter + (g ? _gx[i] : 0f),
                    Y = y + _by[i] + (float)Gauss(_r) * _n.Jitter + (g ? _gy[i] : 0f),
                    H = h + _bh[i] + shift + (float)Gauss(_r) * _n.Jitter + (g ? _gh[i] : 0f),
                };
            }
            palmL = _ty.PalmL.At(t) + shift; palmR = _ty.PalmR.At(t) + shift;
        }

        /// The pose the app gets at time t: between the two newest samples that are old enough.
        public void Get(float t, FingerSample[] into, out float palmL, out float palmR)
        {
            float want = t - _n.Latency;
            int k = (int)Math.Floor(want * _n.Rate);
            while (_k < k + 1)
            {
                _k++;
                Array.Copy(_b, _a, 10); _ta = _tb; _palmLa = _palmLb; _palmRa = _palmRb;
                Sample(_k, _b, out _palmLb, out _palmRb); _tb = _k / _n.Rate;
            }
            float u = _tb > _ta ? Math.Min(1f, Math.Max(0f, (want - _ta) / (_tb - _ta))) : 1f;
            for (int i = 0; i < 10; i++)
                into[i] = new FingerSample { Tracked = true, X = _a[i].X + (_b[i].X - _a[i].X) * u, Y = _a[i].Y + (_b[i].Y - _a[i].Y) * u, H = _a[i].H + (_b[i].H - _a[i].H) * u };
            palmL = _palmLa + (_palmLb - _palmLa) * u; palmR = _palmRa + (_palmRb - _palmRa) * u;
        }
    }

    /// The keyboard before this work: one height per hand, a threshold, 55 ms of waiting, the key under the tip.
    private sealed class HeightThreshold
    {
        private const float PressHeight = 0.0055f, ReleaseHeight = 0.013f, MinTapSpeed = 0.045f;
        private readonly float[] _restY = new float[2], _smooth = new float[10];
        private readonly bool[] _up = new bool[10], _seen = new bool[10];
        private readonly List<(int tip, KeyDef key, float at)> _pending = new List<(int, KeyDef, float)>();
        private readonly KeyboardLayout _l;
        public HeightThreshold(KeyboardLayout l) { _l = l; }
        public void Calibrate(FingerSample[] f)
        {
            for (int h = 0; h < 2; h++) { float s = 0f; for (int i = 1; i < 5; i++) s += f[h * 5 + i].H; _restY[h] = s / 4f; }
        }
        public void Step(float now, float dt, FingerSample[] f, List<(float t, KeyDef key)> output)
        {
            for (int idx = 0; idx < 10; idx++)
            {
                float h = f[idx].H - _restY[idx / 5];
                float prev = _seen[idx] ? _smooth[idx] : h;
                float sm = _seen[idx] ? prev + (h - prev) * 0.6f : h;
                float speed = (prev - sm) / dt;
                bool was = _seen[idx];
                _seen[idx] = true; _smooth[idx] = sm;
                if (!was) { _up[idx] = sm > ReleaseHeight; continue; }
                if (sm > ReleaseHeight) _up[idx] = true;
                if (!_up[idx] || sm > PressHeight || speed < MinTapSpeed) continue;
                _up[idx] = false;
                var key = _l.At(f[idx].X, f[idx].Y);
                if (key == null) continue;
                if (idx % 5 == 0 && key.Row < 5) continue;
                _pending.Add((idx, key, now));
            }
            for (int i = _pending.Count - 1; i >= 0; i--)
            {
                var p = _pending[i];
                if (now - p.at < 0.055f) continue;
                int together = 0;
                foreach (var q in _pending) if (q.tip / 5 == p.tip / 5 && Math.Abs(q.at - p.at) < 0.08f) together++;
                _pending.RemoveAt(i);
                if (together >= 3) continue;
                output.Add((now, p.key));
            }
            if (_pending.Count >= 3) { int h0 = _pending[0].tip / 5; if (_pending.TrueForAll(q => q.tip / 5 == h0)) _pending.Clear(); }
        }
    }

    private struct Score
    {
        public int Chars, Wrong, Extra, Missing;
        public List<float> Delays;
        public float Errors => Chars > 0 ? 100f * (Wrong + Extra + Missing) / Chars : 0f;
        public void Add(Score o) { Chars += o.Chars; Wrong += o.Wrong; Extra += o.Extra; Missing += o.Missing; (Delays ??= new List<float>()).AddRange(o.Delays ?? new List<float>()); }
        public float MedianDelay { get { if (Delays == null || Delays.Count == 0) return 0f; var d = Delays.OrderBy(x => x).ToList(); return d[d.Count / 2]; } }
    }

    /// Typed against meant: wrong keys, extra keys, missing keys (the cheapest alignment).
    private static Score Compare(string meant, string typed)
    {
        int n = meant.Length, m = typed.Length;
        var d = new int[n + 1, m + 1];
        for (int i = 0; i <= n; i++) d[i, 0] = i;
        for (int j = 0; j <= m; j++) d[0, j] = j;
        for (int i = 1; i <= n; i++)
            for (int j = 1; j <= m; j++)
                d[i, j] = Math.Min(Math.Min(d[i - 1, j] + 1, d[i, j - 1] + 1), d[i - 1, j - 1] + (meant[i - 1] == typed[j - 1] ? 0 : 1));
        var s = new Score { Chars = n };
        int a = n, b = m;
        while (a > 0 || b > 0)
        {
            if (a > 0 && b > 0 && d[a, b] == d[a - 1, b - 1] + (meant[a - 1] == typed[b - 1] ? 0 : 1)) { if (meant[a - 1] != typed[b - 1]) s.Wrong++; a--; b--; }
            else if (a > 0 && d[a, b] == d[a - 1, b] + 1) { s.Missing++; a--; }
            else { s.Extra++; b--; }
        }
        return s;
    }

    private static readonly string[] Phrases =
    {
        "the quick brown fox jumps over the lazy dog",
        "please refactor this function and run the tests again",
        "git commit -m fix the keyboard and push it",
        "open the terminal and show me the last build log",
        "what is the status of the thread about hand tracking",
        "npm install and then start the server on port 8080",
        "add a new panel for the settings with a slider",
        "it still looks wrong when i move my hands fast",
    };

    private static (Score now, Score before, int lateOnly) Run(Style style, Noise noise, LetterModel letters, int seeds, bool statistics = true, bool learn = true, Action<TapDetector, KeyDecoder> tune = null)
    {
        var now = new Score { Delays = new List<float>() };
        var before = new Score { Delays = new List<float>() };
        int late = 0;
        for (int seed = 1; seed <= seeds; seed++)
        {
            // a typist keeps their hands, their habits and the headset for a whole sitting: one detector and one
            // decoder across the phrases of a seed
            var layout = KeyboardLayout.Ansi();
            var det = new TapDetector();
            var dec = new KeyDecoder(layout) { Letters = statistics ? letters : null };
            if (!learn) dec.LearnRate = 0f;
            tune?.Invoke(det, dec);
            for (int p = 0; p < Phrases.Length; p++)
            {
                string text = Phrases[p];
                var ty = new Typist(style, text, seed * 100 + p);
                var tr = new Tracker(ty, noise, seed); // the same offsets for the whole sitting
                var old = new HeightThreshold(layout);
                det.Reset();
                var f = new FingerSample[10];
                var taps = new List<TapEvent>();
                var oldOut = new List<(float t, KeyDef key)>();
                var typed = new System.Text.StringBuilder();
                var typedOld = new System.Text.StringBuilder();
                var times = new List<float>();
                var timesOld = new List<float>();
                bool calibrated = false;
                var sum = new float[10]; int sumN = 0;
                char p2 = '\0', p1 = '\0';
                const float dt = 1f / 72f;
                for (float t = 0.2f; t < ty.End; t += dt)
                {
                    tr.Get(t, f, out float pl, out float pr);
                    if (!calibrated)
                    {
                        if (t > 0.6f) { for (int i = 0; i < 10; i++) sum[i] += f[i].H; sumN++; }
                        if (t >= ty.CalibrateAt)
                        {
                            calibrated = true;
                            var mean = new FingerSample[10];
                            for (int i = 0; i < 10; i++) { mean[i].H = sum[i] / sumN; det.Calibrate(i, mean[i].H); }
                            old.Calibrate(mean);
                        }
                        det.Step(t, f, pl, pr, taps);
                        taps.Clear();
                        continue;
                    }
                    taps.Clear();
                    det.Step(t, f, pl, pr, taps);
                    foreach (var e in taps)
                    {
                        var key = dec.Decode(e.Finger, e.X, e.Y, p2, p1, false, KeyContext.Text);
                        if (key == null) continue;
                        if (e.Late) late++;
                        if (Environment.GetEnvironmentVariable("MCX_TYPING_SHOW") == "2" && seed == 1 && Shown(style) && p.ToString() == (Environment.GetEnvironmentVariable("MCX_TYPING_PHRASE") ?? "0")) Console.WriteLine($"      t={e.Time:F3} finger {e.Finger} {(e.Late ? "LATE " : "")}speed {e.Speed:F2} lift {e.Lift * 1000f:F1} mm -> {key.Id}   (next meant: {string.Join(" ", ty.Strokes.Where(s => Math.Abs(s.T - e.Time) < 0.12f).Select(s => $"{s.Ch}@{s.T:F3} f{s.Finger}"))})");
                        char c = key.Letter != '\0' ? key.Letter : '#';
                        typed.Append(c); times.Add(e.Time);
                        p2 = p1; p1 = c == '#' ? '\0' : c;
                        dec.Learn(e.Finger, e.X, e.Y, key);
                    }
                    oldOut.Clear();
                    old.Step(t, dt, f, oldOut);
                    foreach (var o in oldOut) { typedOld.Append(o.key.Letter != '\0' ? o.key.Letter : '#'); timesOld.Add(o.t); }
                }
                string meant = new string(ty.Strokes.Select(s => s.Ch).ToArray());
                if (Environment.GetEnvironmentVariable("MCX_TYPING_SHOW") == "1" && seed == 1 && Shown(style)) Console.WriteLine($"    meant  {meant}\n    typed  {typed}\n    before {typedOld}");
                var a = Compare(meant, typed.ToString()); a.Delays = Delays(ty, times); now.Add(a);
                var b = Compare(meant, typedOld.ToString()); b.Delays = Delays(ty, timesOld); before.Add(b);
            }
        }
        return (now, before, late);
    }

    private static bool Shown(Style s) { string w = Environment.GetEnvironmentVariable("MCX_TYPING_STYLE"); return string.IsNullOrEmpty(w) || s.Name.Contains(w); }

    /// For every key typed: how long after the finger really touched (the nearest touch within 0.2 s).
    private static List<float> Delays(Typist ty, List<float> times)
    {
        var d = new List<float>();
        foreach (float t in times)
        {
            float best = float.MaxValue;
            foreach (var s in ty.Strokes) if (Math.Abs(t - s.T) < Math.Abs(best)) best = t - s.T;
            if (Math.Abs(best) < 0.2f) d.Add(best);
        }
        return d;
    }

    private static void Simulation(LetterModel letters, int seeds)
    {
        var hover = new Style { Name = "ten fingers, hovering 13 mm" };
        var low = new Style { Name = "ten fingers, hovering 8 mm", Hover = 0.008f };
        var rest = new Style { Name = "ten fingers, resting on the table", Rests = true };
        var fast = new Style { Name = "ten fingers, 60 words a minute", Wpm = 60f };
        var peck = new Style { Name = "two index fingers, the hand bobbing", Peck = true, Wpm = 28f };
        var high = new Style { Name = "ten fingers, lifting 20 mm", Hover = 0.020f };
        var normal = new Noise { Name = "default tracking (30 Hz)" };
        var fmm = new Noise { Name = "Fast Motion Mode (60 Hz, more jitter)", Rate = 60f, Latency = 0.035f, Jitter = 0.0013f };
        var drift = new Noise { Name = "default tracking, heights drift 5 mm up", ShiftAt = 6f, ShiftH = 0.005f };
        var clean = new Noise { Name = "perfect tracking", Jitter = 0f, BiasXY = 0f, BiasH = 0f, GlitchPerSecond = 0f, Latency = 0.02f, Rate = 72f };

        Console.WriteLine();
        Console.WriteLine("simulated typist, " + seeds + " sittings of " + Phrases.Sum(p => p.Length) + " keys each; errors in % of the keys meant (wrong / extra / missing), delay = median from the real touch");
        Console.WriteLine($"{"typist",-38} {"tracking",-40} {"height threshold (before)",-34} {"this engine",-34}");
        string Cell(Score s) => $"{s.Errors,5:F1} % ({100f * s.Wrong / s.Chars:F1}/{100f * s.Extra / s.Chars:F1}/{100f * s.Missing / s.Chars:F1}) {s.MedianDelay * 1000f,4:F0} ms";
        var rows = new List<(Style s, Noise n)> { (hover, clean), (high, normal), (hover, normal), (low, normal), (rest, normal), (fast, normal), (peck, normal), (hover, fmm), (low, fmm), (hover, drift), (rest, drift) };
        var results = new Dictionary<string, (Score now, Score before, int late)>();
        foreach (var (s, n) in rows)
        {
            var r = Run(s, n, letters, seeds);
            results[s.Name + "|" + n.Name] = r;
            Console.WriteLine($"{s.Name,-38} {n.Name,-40} {Cell(r.before),-34} {Cell(r.now),-34}");
        }
        Console.WriteLine();
        Console.WriteLine("what each part is worth (ten fingers hovering 8 mm, default tracking):");
        var full = results[low.Name + "|" + normal.Name].now;
        var noStats = Run(low, normal, letters, seeds, statistics: false).now;
        var noLearn = Run(low, normal, letters, seeds, learn: false).now;
        var neither = Run(low, normal, letters, seeds, statistics: false, learn: false).now;
        var noHold = Run(low, normal, letters, seeds, tune: (d, _) => { d.HoldTime = 0f; d.InhibitTime = 0f; }).now;
        Console.WriteLine($"  the whole engine                              {Cell(full)}");
        Console.WriteLine($"  without the letter statistics                 {Cell(noStats)}");
        Console.WriteLine($"  without the per-finger offsets                {Cell(noLearn)}");
        Console.WriteLine($"  without both (the key under the tip)          {Cell(neither)}");
        Console.WriteLine($"  without the wait and the hand's quiet time    {Cell(noHold)}");
        Console.WriteLine();

        var perfect = results[hover.Name + "|" + clean.Name].now;
        Check(perfect.Errors < 1.0f, $"perfect tracking, a careful typist: under 1 % errors ({perfect.Errors:F1} %)");
        foreach (var (s, n) in rows)
        {
            var r = results[s.Name + "|" + n.Name];
            if (n == clean) continue;
            Check(r.now.Errors <= r.before.Errors + 0.5f, $"{s.Name}, {n.Name}: no worse than the height threshold ({r.now.Errors:F1} % against {r.before.Errors:F1} %)");
            float most = n == fmm ? 3.5f : 2.0f;
            Check(100f * r.now.Extra / r.now.Chars < most, $"{s.Name}, {n.Name}: under {most:F1} % keys nobody pressed ({100f * r.now.Extra / r.now.Chars:F1} %)");
        }
        var main = results[hover.Name + "|" + normal.Name];
        Check(main.now.Errors < 4f, $"ten fingers hovering, default tracking: under 4 % errors ({main.now.Errors:F1} %)");
        var rd = results[rest.Name + "|" + drift.Name];
        // the weakest case: until the resting fingers have shown the new height (0.4 s of rest) presses are missed
        Check(rd.now.Errors < 14f, $"a typist who rests, tracked heights drifting 5 mm: under 14 % errors ({rd.now.Errors:F1} %)");
        Check(main.now.MedianDelay < main.before.MedianDelay - 0.02f, $"keys come at least 20 ms sooner than before ({main.now.MedianDelay * 1000f:F0} ms against {main.before.MedianDelay * 1000f:F0} ms)");
        var d = results[hover.Name + "|" + drift.Name];
        Check(d.now.Errors < main.now.Errors + 4f, $"tracked heights drifting 5 mm cost under 4 points ({d.now.Errors:F1} % against {main.now.Errors:F1} %)");
    }
}

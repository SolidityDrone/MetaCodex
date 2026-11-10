using System;
using System.Collections.Generic;
using UnityEngine;

namespace MetaCodex.UI
{
    /// Line icons drawn by code into one texture at first use (no image files to ship or license): each icon is a few
    /// strokes, arcs and shapes on a canvas from -1 to 1 (y up), stored as a signed distance field, so a shader draws it
    /// crisp at any size and gives it a glow from the same texel (the wrist menu, WristArt). One stroke weight for all,
    /// round caps and joins, like a hand-drawn instrument panel.
    public static class UiIconAtlas
    {
        public const int Cell = 96, Cols = 8, Rows = 4;
        /// Canvas units from a cell's centre to its edge: the icon (-1..1) plus room for its glow.
        public const float Extent = 1.32f;
        /// Distance stored (canvas units): 0.5 at a stroke's edge, 1 this far inside, 0 this far outside.
        public const float Range = 0.42f;
        /// Half the stroke width (canvas units).
        public const float Stroke = 0.075f;

        private static Texture2D _tex;
        private static readonly Dictionary<string, int> Index = new Dictionary<string, int>();

        public static Texture2D Texture { get { Ensure(); return _tex; } }
        /// The atlas's bytes (one per texel, rows from the bottom), kept for tests: the texture itself is GPU only.
        public static byte[] Pixels { get { Ensure(); return _pixels; } }
        private static byte[] _pixels;
        public static IEnumerable<string> Names => Icons.Keys;

        /// An icon's rectangle in the atlas (uv offset in xy, size in zw); false when there is none by that name.
        public static bool TryGet(string name, out Vector4 rect)
        {
            Ensure();
            rect = default;
            if (string.IsNullOrEmpty(name) || !Index.TryGetValue(name, out int i)) return false;
            rect = new Vector4((i % Cols) / (float)Cols, 1f - (i / Cols + 1) / (float)Rows, 1f / Cols, 1f / Rows);
            return true;
        }

        /// The signed distance (canvas units) of an icon at a point: tests, and the atlas itself.
        public static float Distance(string name, Vector2 p) => Icons.TryGetValue(name, out var ic) ? ic.Eval(p) : 1e3f;

        private static void Ensure()
        {
            if (_tex != null) return;
            int w = Cell * Cols, h = Cell * Rows;
            var px = new byte[w * h];
            int i = 0;
            Index.Clear();
            foreach (var kv in Icons)
            {
                if (i >= Cols * Rows) break;
                Index[kv.Key] = i;
                int cx = (i % Cols) * Cell, cy = (Rows - 1 - i / Cols) * Cell; // row 0 at the top (uv y down the rows)
                for (int y = 0; y < Cell; y++)
                    for (int x = 0; x < Cell; x++)
                    {
                        var p = new Vector2(((x + 0.5f) / Cell * 2f - 1f) * Extent, ((y + 0.5f) / Cell * 2f - 1f) * Extent);
                        float d = kv.Value.Eval(p);
                        px[(cy + y) * w + cx + x] = (byte)Mathf.RoundToInt(Mathf.Clamp01(0.5f - d / (2f * Range)) * 255f);
                    }
                i++;
            }
            _tex = new Texture2D(w, h, TextureFormat.R8, false, true)
            {
                name = "mcx icon atlas", wrapMode = TextureWrapMode.Clamp, filterMode = FilterMode.Bilinear, anisoLevel = 2,
                hideFlags = HideFlags.DontSave,
            };
            _tex.LoadRawTextureData(px);
            _tex.Apply(false, true);
            _pixels = px;
        }

        // ------------------------------------------------------------------ the drawing language

        private sealed class Icon
        {
            // each step takes the distance so far and returns the new one: strokes are a union, Cut takes a filled
            // shape out of what is drawn before it (the part of a back window hidden by the front one)
            private readonly List<Func<Vector2, float, float>> _ops = new List<Func<Vector2, float, float>>();

            public float Eval(Vector2 p) { float d = 1e3f; foreach (var op in _ops) d = op(p, d); return d; }

            private Icon Add(Func<Vector2, float> f) { _ops.Add((p, d) => Mathf.Min(d, f(p))); return this; }

            public Icon Line(float ax, float ay, float bx, float by)
            {
                var a = new Vector2(ax, ay); var b = new Vector2(bx, by);
                return Add(p => SegDist(p, a, b) - Stroke);
            }

            /// A polyline through x0, y0, x1, y1, ... (closed: back to the first point).
            public Icon Poly(bool closed, params float[] xy)
            {
                int n = xy.Length / 2;
                for (int k = 0; k < n - 1; k++) Line(xy[2 * k], xy[2 * k + 1], xy[2 * k + 2], xy[2 * k + 3]);
                if (closed && n > 2) Line(xy[2 * n - 2], xy[2 * n - 1], xy[0], xy[1]);
                return this;
            }

            public Icon Circle(float cx, float cy, float r) { var c = new Vector2(cx, cy); return Add(p => Mathf.Abs((p - c).magnitude - r) - Stroke); }

            public Icon Disc(float cx, float cy, float r) { var c = new Vector2(cx, cy); return Add(p => (p - c).magnitude - r); }

            /// An arc from a0 to a1 degrees, counter-clockwise from +x (a1 > a0; may pass 360).
            public Icon Arc(float cx, float cy, float r, float a0, float a1)
            {
                var c = new Vector2(cx, cy);
                float s = a0 * Mathf.Deg2Rad, span = (a1 - a0) * Mathf.Deg2Rad;
                var e0 = c + new Vector2(Mathf.Cos(s), Mathf.Sin(s)) * r;
                var e1 = c + new Vector2(Mathf.Cos(s + span), Mathf.Sin(s + span)) * r;
                return Add(p =>
                {
                    var q = p - c;
                    float a = Mathf.Atan2(q.y, q.x) - s;
                    a = Mathf.Repeat(a, Mathf.PI * 2f);
                    float d = a <= span ? Mathf.Abs(q.magnitude - r) : Mathf.Min((p - e0).magnitude, (p - e1).magnitude);
                    return d - Stroke;
                });
            }

            /// A rounded rectangle's outline: centre, half size, corner radius.
            public Icon Box(float cx, float cy, float hx, float hy, float rad)
            {
                var c = new Vector2(cx, cy); var hh = new Vector2(hx, hy);
                return Add(p => Mathf.Abs(BoxDist(p - c, hh, rad)) - Stroke);
            }

            /// An ellipse's outline, as a closed polyline (an exact stroke: the scaled-circle distance swells at the ends).
            public Icon Ellipse(float cx, float cy, float rx, float ry, int steps = 40)
            {
                var xy = new float[steps * 2];
                for (int k = 0; k < steps; k++) { float a = Mathf.PI * 2f * k / steps; xy[2 * k] = cx + Mathf.Cos(a) * rx; xy[2 * k + 1] = cy + Mathf.Sin(a) * ry; }
                return Poly(true, xy);
            }

            /// A quadratic curve from a to b bent towards c, as a polyline.
            public Icon Curve(float ax, float ay, float cx, float cy, float bx, float by, int steps = 10)
            {
                var pts = new float[(steps + 1) * 2];
                for (int k = 0; k <= steps; k++)
                {
                    float t = k / (float)steps, u = 1f - t;
                    pts[2 * k] = u * u * ax + 2f * u * t * cx + t * t * bx;
                    pts[2 * k + 1] = u * u * ay + 2f * u * t * cy + t * t * by;
                }
                return Poly(false, pts);
            }

            /// Takes a filled rounded rectangle (grown by a gap) out of everything drawn so far.
            public Icon CutBox(float cx, float cy, float hx, float hy, float rad, float gap)
            {
                var c = new Vector2(cx, cy); var hh = new Vector2(hx, hy);
                _ops.Add((p, d) => Mathf.Max(d, -(BoxDist(p - c, hh, rad) - gap)));
                return this;
            }

            /// A regular polygon's outline: n corners on radius r, the first at `rot` degrees.
            public Icon Ngon(float cx, float cy, float r, int n, float rot)
            {
                var xy = new float[n * 2];
                for (int k = 0; k < n; k++) { float a = (rot + 360f * k / n) * Mathf.Deg2Rad; xy[2 * k] = cx + Mathf.Cos(a) * r; xy[2 * k + 1] = cy + Mathf.Sin(a) * r; }
                return Poly(true, xy);
            }

            /// A four-pointed spark: tips on radius r, its sides curved in towards the centre.
            public Icon Spark(float cx, float cy, float r, float pinch)
            {
                for (int k = 0; k < 4; k++)
                {
                    float a0 = (90f + 90f * k) * Mathf.Deg2Rad, a1 = a0 + Mathf.PI * 0.5f, am = a0 + Mathf.PI * 0.25f;
                    Curve(cx + Mathf.Cos(a0) * r, cy + Mathf.Sin(a0) * r, cx + Mathf.Cos(am) * r * pinch, cy + Mathf.Sin(am) * r * pinch,
                        cx + Mathf.Cos(a1) * r, cy + Mathf.Sin(a1) * r, 8);
                }
                return this;
            }

            /// A gear: n teeth between radii r0 (root) and r1 (tip), a hole of radius hole.
            public Icon Gear(float r0, float r1, int n, float hole)
            {
                var xy = new List<float>();
                float step = 360f / n;
                for (int k = 0; k < n; k++)
                {
                    float c = 90f + k * step;
                    foreach (var (a, r) in new[] { (c - step * 0.5f, r0), (c - step * 0.30f, r0), (c - step * 0.18f, r1), (c + step * 0.18f, r1), (c + step * 0.30f, r0) })
                    {
                        xy.Add(Mathf.Cos(a * Mathf.Deg2Rad) * r);
                        xy.Add(Mathf.Sin(a * Mathf.Deg2Rad) * r);
                    }
                }
                Poly(true, xy.ToArray());
                return Circle(0f, 0f, hole);
            }
        }

        private static Vector2 Rot(Vector2 v, float deg)
        {
            float r = deg * Mathf.Deg2Rad, c = Mathf.Cos(r), s = Mathf.Sin(r);
            return new Vector2(v.x * c - v.y * s, v.x * s + v.y * c);
        }

        private static float SegDist(Vector2 p, Vector2 a, Vector2 b)
        {
            var pa = p - a; var ba = b - a;
            float h = Mathf.Clamp01(Vector2.Dot(pa, ba) / Mathf.Max(1e-8f, Vector2.Dot(ba, ba)));
            return (pa - ba * h).magnitude;
        }

        private static float BoxDist(Vector2 p, Vector2 h, float r)
        {
            var q = new Vector2(Mathf.Abs(p.x), Mathf.Abs(p.y)) - h + new Vector2(r, r);
            return new Vector2(Mathf.Max(q.x, 0f), Mathf.Max(q.y, 0f)).magnitude + Mathf.Min(Mathf.Max(q.x, q.y), 0f) - r;
        }

        // ------------------------------------------------------------------ the icons (canvas -1..1, y up)

        private static readonly Dictionary<string, Icon> Icons = Build();

        private static Dictionary<string, Icon> Build()
        {
            var d = new Dictionary<string, Icon>();
            // talk: a microphone in its holder
            d["mic"] = new Icon().Box(0f, 0.30f, 0.27f, 0.50f, 0.27f).Arc(0f, 0.22f, 0.54f, 200f, 340f).Line(0f, -0.34f, 0f, -0.64f).Line(-0.30f, -0.66f, 0.30f, -0.66f);
            // a terminal: a window with a prompt and a cursor
            d["terminal"] = new Icon().Box(0f, 0f, 0.90f, 0.70f, 0.16f).Poly(false, -0.50f, 0.26f, -0.20f, 0.0f, -0.50f, -0.26f).Line(0.0f, -0.28f, 0.42f, -0.28f);
            // files: a folder with its tab and front flap
            d["folder"] = new Icon().Poly(true, -0.88f, -0.62f, -0.88f, 0.56f, -0.30f, 0.56f, -0.14f, 0.36f, 0.88f, 0.36f, 0.88f, -0.62f).Line(-0.88f, 0.12f, 0.88f, 0.12f);
            // threads: two speech bubbles, the front one with text
            d["threads"] = new Icon().Box(0.30f, -0.18f, 0.56f, 0.40f, 0.22f).Poly(false, 0.52f, -0.58f, 0.66f, -0.84f, 0.30f, -0.58f)
                .CutBox(-0.22f, 0.22f, 0.66f, 0.44f, 0.24f, 0.17f)
                .Box(-0.22f, 0.22f, 0.66f, 0.44f, 0.24f).Poly(false, -0.56f, -0.22f, -0.70f, -0.50f, -0.30f, -0.22f).Line(-0.56f, 0.34f, 0.10f, 0.34f).Line(-0.56f, 0.10f, -0.10f, 0.10f);
            // agents: a spark with a small one beside it
            d["spark"] = new Icon().Spark(-0.14f, -0.12f, 0.80f, 0.22f).Spark(0.62f, 0.62f, 0.30f, 0.24f);
            // focus: a camera's aperture
            var ap = new Icon().Circle(0f, 0f, 0.84f);
            for (int k = 0; k < 6; k++)
            {
                float a = (90f + 60f * k) * Mathf.Deg2Rad, b = (90f + 60f * k + 105f) * Mathf.Deg2Rad;
                ap.Line(Mathf.Cos(a) * 0.84f, Mathf.Sin(a) * 0.84f, Mathf.Cos(b) * 0.34f, Mathf.Sin(b) * 0.34f);
            }
            d["aperture"] = ap;
            // web: a globe
            d["globe"] = new Icon().Circle(0f, 0f, 0.84f).Ellipse(0f, 0f, 0.36f, 0.84f).Line(-0.84f, 0f, 0.84f, 0f).Line(-0.70f, 0.44f, 0.70f, 0.44f).Line(-0.70f, -0.44f, 0.70f, -0.44f);
            // MQH: a package, an isometric cube
            d["cube"] = new Icon().Ngon(0f, 0f, 0.86f, 6, 90f).Line(0f, 0f, 0f, -0.86f).Line(0f, 0f, Mathf.Cos(30f * Mathf.Deg2Rad) * 0.86f, 0.43f).Line(0f, 0f, -Mathf.Cos(30f * Mathf.Deg2Rad) * 0.86f, 0.43f);
            // captures: two pictures, a mountain and a sun in the front one
            d["photos"] = new Icon().Box(0.20f, 0.22f, 0.62f, 0.48f, 0.12f).CutBox(-0.14f, -0.16f, 0.62f, 0.48f, 0.12f, 0.17f)
                .Box(-0.14f, -0.16f, 0.62f, 0.48f, 0.12f).Poly(false, -0.62f, -0.52f, -0.30f, -0.14f, -0.08f, -0.38f, 0.10f, -0.22f, 0.36f, -0.52f).Disc(0.18f, 0.06f, 0.10f);
            // system: a chip with its pins
            var chip = new Icon().Box(0f, 0f, 0.52f, 0.52f, 0.10f).Box(0f, 0f, 0.20f, 0.20f, 0.04f);
            foreach (float o in new[] { -0.26f, 0f, 0.26f })
                chip.Line(o, 0.62f, o, 0.84f).Line(o, -0.62f, o, -0.84f).Line(0.62f, o, 0.84f, o).Line(-0.62f, o, -0.84f, o);
            d["chip"] = chip;
            // voice: a sound wave
            var wave = new Icon();
            float[] hs = { 0.22f, 0.52f, 0.80f, 0.46f, 0.20f };
            for (int k = 0; k < 5; k++) wave.Line(-0.64f + 0.32f * k, -hs[k], -0.64f + 0.32f * k, hs[k]);
            d["wave"] = wave;
            // vault: a padlock
            d["lock"] = new Icon().Arc(0f, 0.16f, 0.36f, 0f, 180f).Line(-0.36f, 0.16f, -0.36f, 0.02f).Line(0.36f, 0.16f, 0.36f, 0.02f)
                .Box(0f, -0.38f, 0.62f, 0.42f, 0.12f).Disc(0f, -0.30f, 0.11f).Line(0f, -0.32f, 0f, -0.54f);
            // git: a branch
            d["branch"] = new Icon().Circle(-0.42f, -0.62f, 0.17f).Circle(-0.42f, 0.62f, 0.17f).Circle(0.46f, 0.32f, 0.17f)
                .Line(-0.42f, -0.45f, -0.42f, 0.45f).Curve(0.46f, 0.15f, 0.46f, -0.26f, -0.42f, -0.24f, 10);
            // select: corner marks around a cross
            var sel = new Icon().Line(-0.24f, 0f, 0.24f, 0f).Line(0f, -0.24f, 0f, 0.24f);
            foreach (var (sx, sy) in new[] { (-1f, 1f), (1f, 1f), (1f, -1f), (-1f, -1f) })
                sel.Poly(false, sx * 0.84f, sy * 0.46f, sx * 0.84f, sy * 0.84f, sx * 0.46f, sy * 0.84f);
            d["select"] = sel;
            // keys: a keyboard
            var kb = new Icon().Box(0f, 0f, 0.92f, 0.58f, 0.14f).Line(-0.40f, -0.28f, 0.40f, -0.28f);
            for (int k = 0; k < 5; k++) kb.Disc(-0.56f + 0.28f * k, 0.24f, 0.075f);
            for (int k = 0; k < 4; k++) kb.Disc(-0.42f + 0.28f * k, 0f, 0.075f);
            d["keyboard"] = kb;
            // table keys: a keyboard lying flat on a table (the system's Surface Keyboard), its edge nearer and wider
            var tk = new Icon().Poly(true, -0.80f, -0.24f, 0.80f, -0.24f, 0.50f, 0.32f, -0.50f, 0.32f).Line(-0.94f, -0.50f, 0.94f, -0.50f);
            for (int k = 0; k < 4; k++) tk.Disc(-0.34f + 0.227f * k, 0.15f, 0.06f);
            for (int k = 0; k < 5; k++) tk.Disc(-0.48f + 0.24f * k, -0.06f, 0.065f);
            d["tablekeys"] = tk;
            // center: a turning arrow around a point
            float ea = 60f * Mathf.Deg2Rad;
            var tip = new Vector2(Mathf.Cos(ea), Mathf.Sin(ea)) * 0.66f;
            var tan = new Vector2(-Mathf.Sin(ea), Mathf.Cos(ea));
            var back1 = tip - Rot(tan, 38f) * 0.30f;
            var back2 = tip - Rot(tan, -38f) * 0.30f;
            d["recenter"] = new Icon().Arc(0f, 0f, 0.66f, 105f, 420f).Line(tip.x, tip.y, back1.x, back1.y).Line(tip.x, tip.y, back2.x, back2.y).Disc(0f, 0f, 0.13f);
            // setup: a gear
            d["gear"] = new Icon().Gear(0.60f, 0.84f, 8, 0.24f);
            // terminals: two windows, a prompt in the front one
            d["windows"] = new Icon().Box(0.20f, 0.22f, 0.66f, 0.50f, 0.13f).CutBox(-0.14f, -0.14f, 0.66f, 0.50f, 0.13f, 0.17f)
                .Box(-0.14f, -0.14f, 0.66f, 0.50f, 0.13f).Poly(false, -0.48f, 0.04f, -0.26f, -0.14f, -0.48f, -0.32f).Line(-0.14f, -0.34f, 0.18f, -0.34f);
            // the core: MetaCodex's mark, a hexagon around a turned one and a point
            d["mark"] = new Icon().Ngon(0f, 0f, 0.86f, 6, 90f).Ngon(0f, 0f, 0.44f, 6, 0f).Disc(0f, 0f, 0.10f);
            // close
            d["close"] = new Icon().Line(-0.46f, -0.46f, 0.46f, 0.46f).Line(-0.46f, 0.46f, 0.46f, -0.46f);
            return d;
        }
    }
}

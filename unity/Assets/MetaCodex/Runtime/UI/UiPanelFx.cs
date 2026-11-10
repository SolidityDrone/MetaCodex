using System;
using System.Collections.Generic;
using UnityEngine;

namespace MetaCodex.UI
{
    /// How a panel comes and goes: it is CAST, by the orb when there is one (UiOrb), like a hologram printed by a
    /// line of light (an original design):
    /// OPEN (0.64 s) four corner brackets fly in and lock on where the panel will be, a fan of light opens from the
    /// orb to one edge, and a hot line sweeps across between the brackets; behind the line the panel exists: first a
    /// glowing hexagon lattice whose cells close one by one and cool into glass, the frame, then the controls and
    /// the texts coming online hot and flickering; a last pulse through the frame settles it.
    /// CLOSE (0.44 s) a surge, the line runs back and takes the panel with it, the brackets let go, and a spark of
    /// what is left flies home to the orb.
    /// Nothing moves or is scaled: the panel's shaders (McxPanelFx.hlsl) read one plane, the scan line, from
    /// _PanelScan (UiPanel) and draw only what lies behind it. This component draws the light itself, in the panel's
    /// own space (a child mesh, bent like the panel) with MetaCodex/WristArt: the brackets while they fly, the scan
    /// line, the fan and its two rays from the caster, the spark.
    public sealed class UiPanelFx : MonoBehaviour
    {
        public struct Timeline
        {
            /// How far the scan line has crossed the panel: 0 nothing is built, 1 all of it.
            public float Scan;
            /// The panel: a flash of light 0..1, everything hidden 0..1, its own corner brackets hidden 0..1.
            public float Flash, Hide, BracketsOwn;
            /// The overlay: the flying brackets (how far outside the corners, metres, and how bright), the scan line
            /// and the caster's fan (how bright), the spark on its way home (0 at the panel .. 1 at the caster, how bright).
            public float BracketOut, Bracket, Line, Fan, Spark, SparkA;
            /// The fan lights the whole panel, up to its far edge, wherever the line is (a ping).
            public bool FanWhole;
        }

        public const float OpenTime = 0.64f, CloseTime = 0.44f;

        /// Who casts the panels: the orb's place in the world, or null when there is none (then the line simply
        /// crosses from the left).
        public static Func<Vector3?> Caster;
        /// Where the user's eyes are (the rays from the caster face them).
        public static Func<Vector3?> Eye;
        /// A panel starts to be cast (true) or taken back (false): the orb turns to it.
        public static event Action<UiPanel, bool> Casting;
        /// A spark from a closed panel reaches the caster.
        public static event Action<UiPanel> Absorbed;

        internal static void RaiseCasting(UiPanel p, bool opening) { try { Casting?.Invoke(p, opening); } catch (Exception) { } }
        internal static void RaiseAbsorbed(UiPanel p) { try { Absorbed?.Invoke(p); } catch (Exception) { } }

        /// Seconds for the opening or the closing; `light` = a small passing panel (a callout, the voice HUD).
        public static float Duration(bool opening, bool light) => light ? (opening ? 0.30f : 0.22f) : opening ? OpenTime : CloseTime;

        private static float Seg(float x, float a, float b) => Mathf.Clamp01((x - a) / (b - a));
        private static float Bump(float x, float a, float b) => Mathf.Sin(Mathf.PI * Seg(x, a, b));
        private static float EaseOut(float t) { t = Mathf.Clamp01(t); float u = 1f - t; return 1f - u * u * u; }
        private static float EaseIn(float t) { t = Mathf.Clamp01(t); return t * t * t; }
        /// A sweep that starts and stops softly but runs at an even speed in between.
        private static float Sweep(float t) { t = Mathf.Clamp01(t); return Mathf.Lerp(t, t * t * (3f - 2f * t), 0.55f); }

        /// The state of the panel at a moment of its opening or closing (t 0..1).
        public static Timeline Evaluate(bool opening, float t, bool light)
        {
            var o = new Timeline { Spark = -1f };
            if (light)
            {
                // a passing panel: the line crosses it once, no brackets fly
                if (opening)
                {
                    o.Hide = 1f - Seg(t, 0f, 0.15f);
                    o.Scan = Sweep(Seg(t, 0.04f, 0.86f));
                    o.Line = Seg(t, 0.02f, 0.12f) * (1f - Seg(t, 0.82f, 0.94f));
                    o.Fan = 0.7f * Seg(t, 0f, 0.12f) * (1f - Seg(t, 0.80f, 1f));
                    o.BracketsOwn = 1f - Seg(t, 0.80f, 1f);
                    o.Flash = 0.30f * Bump(t, 0.70f, 0.96f);
                }
                else
                {
                    o.Scan = 1f - Sweep(Seg(t, 0.04f, 0.86f));
                    o.Line = Seg(t, 0.02f, 0.12f) * (1f - Seg(t, 0.82f, 0.94f));
                    o.Fan = 0.7f * Seg(t, 0f, 0.12f) * (1f - Seg(t, 0.80f, 1f));
                    o.BracketsOwn = Seg(t, 0f, 0.15f);
                    o.Hide = Seg(t, 0.82f, 1f);
                }
                return o;
            }
            if (opening)
            {
                o.Hide = 1f - Seg(t, 0f, 0.12f);                                     // the footprint: a faint outline of what is coming
                o.BracketOut = 0.016f * (1f - EaseOut(Seg(t, 0f, 0.26f)));
                // the flying brackets hand over to the frame's own (SciFiUI, the same shape and light) once locked on
                o.Bracket = Seg(t, 0f, 0.10f) * (1f - Seg(t, 0.26f, 0.34f));
                o.BracketsOwn = 1f - Seg(t, 0.26f, 0.34f);
                o.Fan = Seg(t, 0.04f, 0.16f) * (1f - Seg(t, 0.80f, 0.96f));
                o.Scan = Sweep(Seg(t, 0.14f, 0.80f));
                o.Line = Seg(t, 0.12f, 0.18f) * (1f - Seg(t, 0.78f, 0.86f));
                o.Flash = 0.55f * Bump(t, 0.74f, 0.96f); // the last frames are the panel at rest
            }
            else
            {
                o.Flash = 0.5f * Bump(t, 0f, 0.22f);
                o.Scan = 1f - Sweep(Seg(t, 0.10f, 0.74f));
                o.Line = Seg(t, 0.08f, 0.14f) * (1f - Seg(t, 0.72f, 0.80f));
                o.Fan = Seg(t, 0.04f, 0.14f) * (1f - Seg(t, 0.74f, 0.86f));
                // the flying brackets take over from the frame's own and let go
                o.BracketsOwn = Seg(t, 0.62f, 0.70f);
                o.Bracket = Seg(t, 0.62f, 0.70f) * (1f - Seg(t, 0.80f, 1f));
                o.BracketOut = 0.012f * EaseIn(Seg(t, 0.70f, 1f));
                o.Hide = Seg(t, 0.78f, 0.98f);
                if (t > 0.72f) { o.Spark = EaseIn(Seg(t, 0.72f, 1f)) * 0.9f + Seg(t, 0.72f, 1f) * 0.1f; o.SparkA = Bump(t, 0.70f, 1.02f); }
            }
            return o;
        }

        /// An open panel asked to open again: the caster lights it up, its frame flashes and the brackets close in round it once.
        public static Timeline Ping(float t, bool light)
        {
            return new Timeline
            {
                Scan = 1f, Spark = -1f, FanWhole = true,
                Flash = 0.42f * Bump(t, 0f, 1f),
                BracketOut = 0.010f * (1f - EaseOut(t)),
                Bracket = light ? 0f : Bump(t, 0f, 1f),
                Fan = Bump(t, 0f, 0.9f),
            };
        }

        // ------------------------------------------------------------------ the plan: which way the line crosses

        /// 0: the line is vertical and moves along the panel's x; 1: horizontal, along its y.
        public int Axis { get; private set; }
        /// +1: it starts at the negative edge (left, or bottom) and moves to the positive one; -1 the other way.
        public float Sign { get; private set; } = 1f;
        // the line starts this far outside the frame (its halo and brackets), and runs on this far past the far edge:
        // what it built is hot for a few centimetres behind it, and must have cooled when it stops (else the end pops)
        private const float Pad = 0.024f, Tail = 0.16f;

        /// Picks the edge nearest to the caster (the line leaves from the caster's side and comes back to it).
        public void Plan(UiPanel p)
        {
            Axis = 0; Sign = 1f;
            var c = Caster?.Invoke();
            if (!c.HasValue) return;
            var l = p.transform.InverseTransformPoint(c.Value);
            float dx = l.x / Mathf.Max(p.Width * 0.5f, 1e-3f), dy = l.y / Mathf.Max(p.Height * 0.5f, 1e-3f);
            if (Mathf.Abs(dx) >= Mathf.Abs(dy) * 0.8f) { Axis = 0; Sign = dx <= 0f ? 1f : -1f; }
            else { Axis = 1; Sign = dy <= 0f ? 1f : -1f; }
        }

        /// How far the line starts from the centre, along its axis: half the panel (a chord across a bent one) and the pad.
        private float Reach(UiPanel p)
        {
            float half = Axis == 0 ? p.Width * 0.5f : p.Height * 0.5f;
            float r = p.BendNow;
            if (Axis == 0 && r > 0.01f) half = r * Mathf.Sin(Mathf.Min(half / r, Mathf.PI * 0.5f));
            return half + Pad;
        }

        /// The line's place along its axis for a scan value: panel-local metres from the centre (a chord across a bent
        /// panel). Scan 0: at the near edge; 1: past the far edge by Tail.
        public float LinePlace(UiPanel p, float scan)
        {
            float ext = Reach(p);
            return Sign * Mathf.Lerp(-ext, ext + Tail, scan);
        }

        /// The scan plane for the shaders (world space): xyz = its normal, the way the line moves while the panel
        /// opens; w = its offset. Built: dot(p, xyz) &lt; w.
        public Vector4 ScanPlane(UiPanel p, float scan)
        {
            var tr = p.transform;
            var axis = Axis == 0 ? tr.right : tr.up;
            float scale = Axis == 0 ? tr.lossyScale.x : tr.lossyScale.y;
            var n = axis * Sign;
            float w = Vector3.Dot(tr.position, n) + Sign * LinePlace(p, scan) * scale;
            return new Vector4(n.x, n.y, n.z, w);
        }

        // ------------------------------------------------------------------ the overlay

        private Mesh _mesh;
        private MeshRenderer _mr;
        private static Material _mat;
        private readonly List<Vector3> _v = new List<Vector3>();
        // reused every frame of a cast (no garbage while a window is drawn)
        private readonly List<Vector3> _line = new List<Vector3>(), _fan = new List<Vector3>();
        private static readonly (float sx, float sy)[] Corners = { (-1f, 1f), (1f, 1f), (1f, -1f), (-1f, -1f) };
        private readonly List<Color> _c = new List<Color>();
        private readonly List<Vector2> _uv = new List<Vector2>();
        private readonly List<Vector4> _info = new List<Vector4>(), _extra = new List<Vector4>();
        private readonly List<int> _tri = new List<int>();
        private float _bend;
        /// The light of the scan line and of the caster's rays: a warm white, the orb's own gold in it.
        public static readonly Color Hot = new Color(1f, 0.93f, 0.78f);

        public static UiPanelFx Create(UiPanel p)
        {
            var go = new GameObject("transition light", typeof(MeshFilter), typeof(MeshRenderer));
            go.transform.SetParent(p.transform, false);
            var fx = go.AddComponent<UiPanelFx>();
            fx._mesh = new Mesh { name = "transition light" };
            fx._mesh.MarkDynamic();
            go.GetComponent<MeshFilter>().sharedMesh = fx._mesh;
            fx._mr = go.GetComponent<MeshRenderer>();
            fx._mr.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            fx._mr.receiveShadows = false;
            if (_mat == null)
            {
                var sh = Resources.Load<Shader>("Shaders/WristArt") ?? Shader.Find("MetaCodex/WristArt");
                if (sh != null) _mat = new Material(sh) { name = "panel transition light", renderQueue = 3010 }; // over the panels
            }
            fx._mr.sharedMaterial = _mat;
            fx._mr.enabled = false;
            return fx;
        }

        public MeshRenderer Renderer => _mr;

        public void Hide()
        {
            if (_mr != null && _mr.enabled) _mr.enabled = false;
        }

        /// Draws the light for this moment, in the panel's own space.
        public void Draw(UiPanel p, Timeline tl)
        {
            if (_mesh == null || _mat == null) return;
            transform.localPosition = Vector3.zero;
            transform.localRotation = Quaternion.identity;
            transform.localScale = Vector3.one;
            _bend = p.BendNow;
            _v.Clear(); _c.Clear(); _uv.Clear(); _info.Clear(); _extra.Clear(); _tri.Clear();
            var t = UiTheme.Current;
            Color accent = t.Accent, hot = Color.Lerp(t.Accent, Color.white, 0.55f);
            float hw = p.Width * 0.5f, hh = p.Height * 0.5f, z = -0.003f;
            var caster = Caster?.Invoke();
            Vector3 eye = p.transform.InverseTransformPoint(Eye?.Invoke() ?? p.transform.position - p.transform.forward * 0.8f);
            if (tl.Bracket > 0.002f && Mathf.Min(hw, hh) >= 0.055f) // (SciFiUI draws none on a small card either)
            {
                // the brackets of the frame (SciFiUI draws them at rest): its chamfered outline offset outwards, near
                // each corner; they come in from further out and lock on 4 mm off the frame
                float o = 0.004f + tl.BracketOut, w = 0.0012f, k = 0.41421356f; // tan(22.5): the chamfer's offset
                foreach (var (sx, sy) in Corners)
                {
                    float c = sx * sy < 0f ? t.CutBig : t.CutSmall; // as UiPanel's body: big cuts top-left and bottom-right
                    float arm = Mathf.Clamp(Mathf.Max(Mathf.Min(0.030f, Mathf.Min(hw, hh) * 0.32f), c + 0.006f), 0f, Mathf.Min(hw, hh));
                    var a0 = new Vector2(sx * (hw + o), sy * (hh - arm));
                    var a1 = new Vector2(sx * (hw + o), sy * (hh - c + o * k));
                    var b1 = new Vector2(sx * (hw - c + o * k), sy * (hh + o));
                    var b0 = new Vector2(sx * (hw - arm), sy * (hh + o));
                    Segment(a0, a1, z, w, accent, tl.Bracket, 0.9f);
                    Segment(a1, b1, z, w, accent, tl.Bracket, 0.9f);
                    Segment(b1, b0, z, w, accent, tl.Bracket, 0.9f);
                    // a node of light in the middle of the cut: the lock
                    var m = (a1 + b1) * 0.5f;
                    Segment(m - (b1 - a1).normalized * 0.0015f, m + (b1 - a1).normalized * 0.0015f, z, 0.0028f, hot, tl.Bracket, 0.7f);
                }
            }
            // the scan line: where it is now, as a row of points on the panel's surface (an arc when it runs along a bent
            // panel). Past the far edge it is not drawn (only its heat still leaves the panel), and the fan lets go there.
            float ext = Reach(p), place = LinePlace(p, tl.Scan);
            float over = Mathf.Max(0f, place * Sign - ext);
            if (tl.FanWhole) over = 0f;
            float gone = Mathf.Clamp01(over / 0.03f), onPanel = 1f - gone * gone * (3f - 2f * gone); // (Mathf.SmoothStep is an interpolation, not this)
            var line = LinePoints(p, Mathf.Clamp(tl.FanWhole ? Sign * ext : place, -ext, ext));
            tl.Line *= onPanel;
            tl.Fan *= onPanel;
            if (tl.Line > 0.002f) ScanLine(line, z - 0.0006f, tl.Line);
            if (caster.HasValue)
            {
                var from = p.transform.InverseTransformPoint(caster.Value);
                if (tl.Fan > 0.002f)
                {
                    // the fan between the caster and the line, and a ray along each of its edges
                    var fan = _fan;
                    fan.Clear();
                    foreach (var q in line) fan.Add(new Vector3(q.x, q.y, q.z + z));
                    Fan(from, fan, accent, tl.Fan);
                    Ray(from, fan[0], eye, 0.0034f, Hot, tl.Fan);
                    Ray(from, fan[fan.Count - 1], eye, 0.0034f, Hot, tl.Fan);
                }
                if (tl.Spark >= 0f && tl.SparkA > 0.002f)
                {
                    // what is left of the panel flies home: a short streak from the edge it closed at to the caster
                    var start = (line[0] + line[line.Count - 1]) * 0.5f;
                    var head = Vector3.Lerp(start, from, tl.Spark);
                    var tail = Vector3.Lerp(start, from, Mathf.Max(0f, tl.Spark - 0.30f));
                    Ray(tail, head, eye, 0.0080f, Hot, tl.SparkA, 0f);
                    Ray(Vector3.Lerp(tail, head, 0.75f), head, eye, 0.016f, Color.white, tl.SparkA, 0f);
                }
            }
            _mesh.Clear();
            _mesh.SetVertices(_v);
            _mesh.SetColors(_c);
            _mesh.SetUVs(0, _uv);
            _mesh.SetUVs(1, _info);
            _mesh.SetUVs(2, _extra);
            _mesh.SetTriangles(_tri, 0);
            _mesh.RecalculateBounds();
            if (!_mr.enabled) _mr.enabled = _v.Count > 0;
            else if (_v.Count == 0) _mr.enabled = false;
        }

        /// The scan line at `place` (LinePlace) as points on the panel's surface, panel-local: two for a straight
        /// line, a row along the arc when it lies across a bent panel.
        private List<Vector3> LinePoints(UiPanel p, float place)
        {
            var pts = _line;
            pts.Clear();
            float hw = p.Width * 0.5f, hh = p.Height * 0.5f, over = 0.007f, r = _bend;
            if (Axis == 0)
            {
                // vertical: straight even on a bent panel (the bend is round a vertical axis); `place` is a chord
                float zz = r > 0.01f ? -r + Mathf.Sqrt(Mathf.Max(r * r - place * place, 0f)) : 0f;
                pts.Add(new Vector3(place, -hh - over, zz));
                pts.Add(new Vector3(place, hh + over, zz));
                return pts;
            }
            int n = r > 0.01f ? Mathf.Clamp(Mathf.CeilToInt(p.Width / 0.03f), 2, 40) : 1;
            for (int i = 0; i <= n; i++) pts.Add(Bent(new Vector2(Mathf.Lerp(-hw - over, hw + over, i / (float)n), place), 0f));
            return pts;
        }

        // the panel's bend (UiPanel / SciFiUI: around a vertical axis in front of it)
        private Vector3 Bent(Vector2 q, float z)
        {
            float r = _bend;
            if (r <= 0.01f) return new Vector3(q.x, q.y, z);
            float a = q.x / r;
            return new Vector3(r * Mathf.Sin(a), q.y, z - r * (1f - Mathf.Cos(a)));
        }

        private void Quad(Vector3 a0, Vector3 a1, Vector3 b0, Vector3 b1, Color lin, Vector4 info, Vector4 extra, float v0, float v1)
        {
            int i0 = _v.Count;
            _v.Add(a0); _c.Add(lin); _uv.Add(new Vector2(0f, v0)); _info.Add(info); _extra.Add(extra);
            _v.Add(a1); _c.Add(lin); _uv.Add(new Vector2(1f, v0)); _info.Add(info); _extra.Add(extra);
            _v.Add(b0); _c.Add(lin); _uv.Add(new Vector2(0f, v1)); _info.Add(info); _extra.Add(extra);
            _v.Add(b1); _c.Add(lin); _uv.Add(new Vector2(1f, v1)); _info.Add(info); _extra.Add(extra);
            _tri.Add(i0); _tri.Add(i0 + 1); _tri.Add(i0 + 3);
            _tri.Add(i0); _tri.Add(i0 + 3); _tri.Add(i0 + 2);
        }

        /// A straight line of light (WristArt kind 1) from a to b in the panel's plane, split so it follows a bent panel.
        private void Segment(Vector2 a, Vector2 b, float z, float w, Color col, float strength, float glow)
        {
            if (strength <= 0.002f) return;
            float len = (b - a).magnitude;
            if (len < 1e-5f) return;
            var dir = (b - a) / len;
            float qw = w + 2f * Mathf.Max(0.0012f, w * 2.4f) * Mathf.Clamp01(glow + 0.2f);
            var side = new Vector2(-dir.y, dir.x) * (qw * 0.5f);
            var lin = col.linear;
            lin.a = Mathf.Clamp01(strength);
            var info = new Vector4(w, qw, 1f, 0f);
            var ex = new Vector4(glow, 0f, 0.5f, 0f);
            int segs = _bend > 0.01f ? Mathf.Clamp(Mathf.CeilToInt(Mathf.Abs(b.x - a.x) / 0.03f), 1, 32) : 1;
            for (int i = 0; i < segs; i++)
            {
                float k0 = i / (float)segs, k1 = (i + 1) / (float)segs;
                var m0 = Vector2.Lerp(a, b, k0); var m1 = Vector2.Lerp(a, b, k1);
                Quad(Bent(m0 - side, z), Bent(m0 + side, z), Bent(m1 - side, z), Bent(m1 + side, z), lin, info, ex, k0, k1);
            }
        }

        /// The scan line (WristArt kind 7): a hot core, a glow on both sides, beads of light running along it.
        private void ScanLine(List<Vector3> pts, float z, float strength)
        {
            const float qw = 0.11f; // the quad: the glow reaches 5 cm to each side
            var lin = Hot.linear;
            lin.a = Mathf.Clamp01(strength);
            float total = 0f;
            for (int i = 1; i < pts.Count; i++) total += Vector3.Distance(pts[i - 1], pts[i]);
            var info = new Vector4(0.0014f, qw, 7f, 0f);
            var ex = new Vector4(total / 0.035f, 0f, 0f, 0f); // a bead every 3.5 cm
            // across the line: along the panel's surface, at right angles to it
            float run = 0f;
            for (int i = 1; i < pts.Count; i++)
            {
                Vector3 a = pts[i - 1], b = pts[i];
                var along = (b - a).normalized;
                var normal = SurfaceNormal(a.x);
                var side = Vector3.Cross(along, normal).normalized * (qw * 0.5f);
                var off = normal * z;
                float len = Vector3.Distance(a, b);
                Quad(a - side - off, a + side - off, b - side - off, b + side - off, lin, info, ex, run / Mathf.Max(total, 1e-4f), (run + len) / Mathf.Max(total, 1e-4f));
                run += len;
            }
        }

        /// The panel's normal towards the user (panel-local) at the chord place x.
        private Vector3 SurfaceNormal(float x)
        {
            float r = _bend;
            if (r <= 0.01f) return new Vector3(0f, 0f, -1f);
            float s = Mathf.Clamp(x / r, -1f, 1f);
            return new Vector3(-s, 0f, -Mathf.Sqrt(Mathf.Max(1f - s * s, 0f)));
        }

        /// The fan of light from the caster to the line (WristArt kind 6): thin triangles from one point.
        private void Fan(Vector3 from, List<Vector3> pts, Color col, float strength)
        {
            var lin = col.linear;
            lin.a = Mathf.Clamp01(strength);
            int n = pts.Count - 1;
            for (int i = 0; i < n; i++)
            {
                // uv: (0,0) at the caster, (1,0) and (0,1) at the two ends on the line; info.xy: where across the whole fan
                var info = new Vector4(i / (float)n, (i + 1) / (float)n, 6f, 0f);
                int i0 = _v.Count;
                _v.Add(from); _c.Add(lin); _uv.Add(new Vector2(0f, 0f)); _info.Add(info); _extra.Add(Vector4.zero);
                _v.Add(pts[i]); _c.Add(lin); _uv.Add(new Vector2(1f, 0f)); _info.Add(info); _extra.Add(Vector4.zero);
                _v.Add(pts[i + 1]); _c.Add(lin); _uv.Add(new Vector2(0f, 1f)); _info.Add(info); _extra.Add(Vector4.zero);
                _tri.Add(i0); _tri.Add(i0 + 1); _tri.Add(i0 + 2);
            }
        }

        /// A ray of light through the air from a to b (WristArt kind 4, a ray), turned to face the eyes.
        private void Ray(Vector3 a, Vector3 b, Vector3 eye, float w, Color col, float strength, float pulse = 1f)
        {
            if (strength <= 0.002f || (b - a).sqrMagnitude < 1e-8f) return;
            var dir = (b - a).normalized;
            var side = Vector3.Cross(dir, ((a + b) * 0.5f - eye).normalized);
            if (side.sqrMagnitude < 1e-8f) side = Vector3.up;
            side = side.normalized * (w * 0.5f);
            var lin = col.linear;
            lin.a = Mathf.Clamp01(strength);
            Quad(a - side, a + side, b - side, b + side, lin, new Vector4(0f, 1f - pulse, 4f, 0f), Vector4.zero, 0f, 1f);
        }
    }
}

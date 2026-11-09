using System.Collections.Generic;
using UnityEngine;

namespace MetaCodex.UI
{
    /// How a panel opens and closes, in the spirit of an RTS command console coming online (an original design):
    /// OPEN (0.52 s) a spark at the panel's centre runs out into a line of light the width of the panel, four corner
    /// brackets lock on from outside, the line unfolds into the panel (a slight overshoot), the glass materialises from
    /// its bright hex lattice, the texts come in flickering line by line, and a last pulse settles it.
    /// CLOSE (0.34 s) a surge of light, the panel collapses into a bright line as an old screen powers off, the line
    /// shrinks to a point, a flare, gone; the brackets let go.
    /// The panel's transform carries the squash (Sx, Sy); its shaders read Flash, Ghost and Hide from _PanelFx
    /// (UiPanel); this component draws the brackets, the line and the flare at the panel's real size (a child mesh,
    /// counter-scaled against the squash, bent like the panel), with MetaCodex/WristArt lines over the panels.
    public sealed class UiPanelFx : MonoBehaviour
    {
        public struct Timeline
        {
            /// The panel: squash of its width and height, a flash of light 0..1, ghost 0..1 (the glass and controls
            /// not yet there: only the lattice and the frame) and 1..2 (fading out at the end), texts hidden 0..1.
            public float Sx, Sy, Flash, Ghost, Hide;
            /// The overlay: brackets (how far outside the corners, metres, and how bright), the line through the
            /// centre (how bright; its half length follows Sx), the flare at the centre (how bright, its radius).
            public float BracketOut, Bracket, Line, Flare, FlareR;
        }

        public const float OpenTime = 0.52f, CloseTime = 0.34f;

        /// Seconds for the opening or the closing; `light` = a small passing panel (toasts, the voice HUD).
        public static float Duration(bool opening, bool light) => light ? (opening ? 0.30f : 0.22f) : opening ? OpenTime : CloseTime;

        private static float Seg(float x, float a, float b) => Mathf.Clamp01((x - a) / (b - a));
        private static float Bump(float x, float a, float b) => Mathf.Sin(Mathf.PI * Seg(x, a, b));
        private static float EaseOut(float t) { t = Mathf.Clamp01(t); float u = 1f - t; return 1f - u * u * u; }
        private static float EaseIn(float t) { t = Mathf.Clamp01(t); return t * t * t; }
        private static float EaseInOut(float t) { t = Mathf.Clamp01(t); return t < 0.5f ? 4f * t * t * t : 1f - Mathf.Pow(-2f * t + 2f, 3f) * 0.5f; }
        private static float EaseOutBack(float t, float c1) { float c3 = c1 + 1f; t = Mathf.Clamp01(t); return 1f + c3 * Mathf.Pow(t - 1f, 3f) + c1 * Mathf.Pow(t - 1f, 2f); }

        /// The state of the panel at a moment of its opening or closing (t 0..1).
        public static Timeline Evaluate(bool opening, float t, bool light)
        {
            var o = new Timeline();
            if (opening)
            {
                float p = t;
                o.Sx = Mathf.Lerp(0.03f, 1f, EaseOut(Seg(p, 0f, 0.20f)));
                o.Sy = Mathf.Lerp(0.02f, 1f, EaseOutBack(Seg(p, 0.14f, 0.58f), 0.9f));
                o.Flash = 0.85f * Bump(p, 0f, 0.26f) + 0.22f * Bump(p, 0.56f, 0.80f);
                o.Ghost = 1f - EaseInOut(Seg(p, 0.30f, 0.86f));
                o.Hide = 1f - Seg(p, 0.42f, 0.92f);
                o.BracketOut = 0.014f * (1f - EaseOut(Seg(p, 0.08f, 0.50f)));
                o.Bracket = light ? 0f : Seg(p, 0f, 0.10f) * (1f - Seg(p, 0.70f, 1f));
                o.Line = Bump(p, 0f, 0.32f);
                o.Flare = 0.9f * Bump(p, 0f, 0.15f);
                o.FlareR = 0.005f + 0.012f * Seg(p, 0f, 0.15f);
            }
            else
            {
                float q = t;
                o.Flash = 0.75f * Bump(q, 0f, 0.26f) + 0.7f * Seg(q, 0.20f, 0.62f) * (1f - Seg(q, 0.86f, 1f));
                o.Sy = Mathf.Lerp(1f, 0.02f, EaseIn(Seg(q, 0.06f, 0.56f)));
                o.Sx = Mathf.Lerp(1f, 0.03f, EaseIn(Seg(q, 0.52f, 0.88f)));
                o.Ghost = 0.6f * Seg(q, 0f, 0.5f) + 0.4f * Seg(q, 0.5f, 0.84f) + Seg(q, 0.84f, 1f);
                o.Hide = Seg(q, 0f, 0.28f);
                o.BracketOut = 0.010f * EaseIn(Seg(q, 0.10f, 0.60f));
                o.Bracket = light ? 0f : 0.8f * Bump(q, 0f, 0.62f);
                o.Line = Seg(q, 0.42f, 0.70f) * (1f - Seg(q, 0.88f, 1f));
                o.Flare = Bump(q, 0.76f, 1f);
                o.FlareR = 0.004f + 0.014f * Seg(q, 0.76f, 1f);
            }
            return o;
        }

        /// An open panel asked to open again: its frame flashes and the brackets close in round it once.
        public static Timeline Ping(float t, bool light)
        {
            return new Timeline
            {
                Sx = 1f, Sy = 1f,
                Flash = 0.42f * Bump(t, 0f, 1f),
                BracketOut = 0.010f * (1f - EaseOut(t)),
                Bracket = light ? 0f : Bump(t, 0f, 1f),
            };
        }

        // ------------------------------------------------------------------ the overlay

        private Mesh _mesh;
        private MeshRenderer _mr;
        private static Material _mat;
        private readonly List<Vector3> _v = new List<Vector3>();
        private readonly List<Color> _c = new List<Color>();
        private readonly List<Vector2> _uv = new List<Vector2>();
        private readonly List<Vector4> _info = new List<Vector4>(), _extra = new List<Vector4>();
        private readonly List<int> _tri = new List<int>();
        private float _bend;

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

        public void Hide()
        {
            if (_mr != null && _mr.enabled) _mr.enabled = false;
        }

        /// Draws the light for this moment (in the panel's own space, at its rest size).
        public void Draw(UiPanel p, Timeline tl)
        {
            if (_mesh == null || _mat == null) return;
            // counter the squash: this child is drawn at the panel's real size
            transform.localPosition = Vector3.zero;
            transform.localRotation = Quaternion.identity;
            transform.localScale = new Vector3(1f / Mathf.Max(tl.Sx, 1e-3f), 1f / Mathf.Max(tl.Sy, 1e-3f), 1f);
            _bend = p.BendNow;
            _v.Clear(); _c.Clear(); _uv.Clear(); _info.Clear(); _extra.Clear(); _tri.Clear();
            var t = UiTheme.Current;
            Color accent = t.Accent, hot = Color.Lerp(t.Accent, Color.white, 0.55f);
            float hw = p.Width * 0.5f, hh = p.Height * 0.5f, z = -0.003f;
            if (tl.Bracket > 0.002f)
            {
                // the brackets of the frame (SciFiUI draws them at rest): its chamfered outline offset outwards, near
                // each corner; they come in from further out and lock on 4 mm off the frame
                float o = 0.004f + tl.BracketOut, w = 0.0012f, k = 0.41421356f; // tan(22.5): the chamfer's offset
                foreach (var (sx, sy) in new[] { (-1f, 1f), (1f, 1f), (1f, -1f), (-1f, -1f) })
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
            if (tl.Line > 0.002f)
            {
                // the line of light: one through the centre while the panel is a line, then two scan lines riding its
                // top and bottom edges as it unfolds (or closes), gone when it is whole
                float half = hw * tl.Sx, edge = hh * tl.Sy;
                float k = tl.Line * (1f - Mathf.Clamp01((tl.Sy - 0.85f) / 0.15f));
                if (edge < 0.004f)
                {
                    Segment(new Vector2(-half, 0f), new Vector2(half, 0f), z, 0.0016f, hot, k, 1f);
                    Segment(new Vector2(-half * 1.04f, 0f), new Vector2(half * 1.04f, 0f), z, 0.0005f, Color.white, k * 0.7f, 0.6f);
                }
                else
                    foreach (float y in new[] { edge, -edge })
                    {
                        Segment(new Vector2(-half, y), new Vector2(half, y), z, 0.0013f, hot, k, 1f);
                        Segment(new Vector2(-half * 1.02f, y), new Vector2(half * 1.02f, y), z, 0.0004f, Color.white, k * 0.6f, 0.5f);
                    }
            }
            if (tl.Flare > 0.002f)
            {
                // a spark: a hot core, a horizontal streak, a ring
                Segment(new Vector2(-tl.FlareR * 0.35f, 0f), new Vector2(tl.FlareR * 0.35f, 0f), z - 0.0005f, tl.FlareR * 0.5f, Color.white, tl.Flare, 1f);
                Segment(new Vector2(-tl.FlareR * 3.2f, 0f), new Vector2(tl.FlareR * 3.2f, 0f), z - 0.0005f, 0.0009f, hot, tl.Flare * 0.8f, 1f);
                Ring(Vector2.zero, tl.FlareR, 0.0007f, z - 0.0005f, accent, tl.Flare * 0.7f, 0.8f);
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

        // the panel's bend (UiPanel / SciFiUI: around a vertical axis in front of it)
        private Vector3 Bent(Vector2 q, float z)
        {
            float r = _bend;
            if (r <= 0.01f) return new Vector3(q.x, q.y, z);
            float a = q.x / r;
            return new Vector3(r * Mathf.Sin(a), q.y, z - r * (1f - Mathf.Cos(a)));
        }

        /// A straight line of light (WristArt kind 1) from a to b, split so it follows a bent panel.
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
            int i0 = _v.Count;
            for (int i = 0; i <= segs; i++)
            {
                float k = i / (float)segs;
                var m = Vector2.Lerp(a, b, k);
                _v.Add(Bent(m - side, z)); _c.Add(lin); _uv.Add(new Vector2(0f, k)); _info.Add(info); _extra.Add(ex);
                _v.Add(Bent(m + side, z)); _c.Add(lin); _uv.Add(new Vector2(1f, k)); _info.Add(info); _extra.Add(ex);
            }
            for (int i = 0; i < segs; i++)
            {
                int q = i0 + i * 2;
                _tri.Add(q); _tri.Add(q + 1); _tri.Add(q + 3);
                _tri.Add(q); _tri.Add(q + 3); _tri.Add(q + 2);
            }
        }

        private void Ring(Vector2 c, float r, float w, float z, Color col, float strength, float glow)
        {
            if (strength <= 0.002f) return;
            float qw = w + 2f * Mathf.Max(0.0012f, w * 2.4f) * Mathf.Clamp01(glow + 0.2f);
            var lin = col.linear;
            lin.a = Mathf.Clamp01(strength);
            var info = new Vector4(w, qw, 1f, 0f);
            var ex = new Vector4(glow, 0f, 0.5f, 0f);
            const int segs = 40;
            int i0 = _v.Count;
            for (int i = 0; i <= segs; i++)
            {
                float a = Mathf.PI * 2f * i / segs;
                var d = new Vector2(Mathf.Cos(a), Mathf.Sin(a));
                _v.Add(Bent(c + d * Mathf.Max(0f, r - qw * 0.5f), z)); _c.Add(lin); _uv.Add(new Vector2(0f, i / (float)segs)); _info.Add(info); _extra.Add(ex);
                _v.Add(Bent(c + d * (r + qw * 0.5f), z)); _c.Add(lin); _uv.Add(new Vector2(1f, i / (float)segs)); _info.Add(info); _extra.Add(ex);
            }
            for (int i = 0; i < segs; i++)
            {
                int q = i0 + i * 2;
                _tri.Add(q); _tri.Add(q + 1); _tri.Add(q + 3);
                _tri.Add(q); _tri.Add(q + 3); _tri.Add(q + 2);
            }
        }
    }
}

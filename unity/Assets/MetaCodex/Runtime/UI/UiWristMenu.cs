using System;
using System.Collections.Generic;
using MetaCodex.Hands;
using TMPro;
using UnityEngine;

namespace MetaCodex.UI
{
    /// The main menu on the wrist (Settings > DISPLAY > MAIN MENU: WRIST): a holographic core floating over the back of
    /// the left wrist, shown when the user looks at the wrist as at a watch (palm turned away, so it never meets the
    /// Horizon OS palm-up menu gesture). Touch the core with the right index fingertip and it unfolds: an inner ring of
    /// six blades (the most used actions) and an outer ring of twelve, chamfered glass with energy rims, under a ticking
    /// ring, corner brackets and a radar sweep. Touching a blade runs it with a flash, a shockwave and a burst of
    /// sparks. No pointing or pinching: the fingertip presses through the surface (and must come back out before the
    /// next press). Idle seven seconds, or the wrist turned away, and it folds back to the core.
    public sealed class UiWristMenu : MonoBehaviour
    {
        public sealed class Item { public string Glyph, Label; public Action Act; public Func<bool> Lit; public Color LitTint; }

        public readonly List<Item> Inner = new List<Item>(), Outer = new List<Item>();
        /// The hands: left carries the menu, the right index fingertip presses.
        public Func<Handedness, (bool ok, HandFrame frame)> Hand;
        public Transform Head;
        public UiTheme Theme;
        /// Overall size (1 = 15 cm across the outer ring).
        public float Scale = 0.85f;

        public bool Visible => _shown > 0.01f;
        public bool Expanded => _expandTarget;
        public float Shown01 => _shown;
        public float Expand01 => _expand;
        /// What the fingertip is over: -2 the core, 0.. a blade (inner ring first, then outer), -1 nothing.
        public int Hover { get; private set; } = -1;
        public event Action<int> Pressed;

        // geometry, metres before Scale: the core, its dashed ring, the two rings of blades, ticks and brackets
        private const float CoreR0 = 0.0042f, CoreR1 = 0.0150f, DashR = 0.0189f;
        private const float InR0 = 0.0236f, InR1 = 0.0470f, OutR0 = 0.0500f, OutR1 = 0.0690f;
        private const float TickR0 = 0.0728f, TickR1 = 0.0752f, BracketR = 0.0800f, MidRingR = 0.0485f;
        private const float InGapDeg = 3.4f, OutGapDeg = 2.6f, Chamfer = 0.0042f;
        private const float InStart = 90f, OutStart = 75f; // the first blade's centre (degrees, counter-clockwise from +x)

        private Mesh _mesh;
        private Material _mat, _sparkMat;
        private ParticleSystem _burst, _motes;
        private TextMeshPro _coreGlyph, _hint;
        private readonly List<TextMeshPro> _labels = new List<TextMeshPro>();
        private float[] _hoverAmt = new float[0], _flash = new float[0];
        private bool _expandTarget, _wantShow, _posed, _armed;
        private float _shown, _expand, _watchFor, _awayFor, _idle, _cooldownUntil, _time;
        private float _wave = 1f;
        private Vector2 _waveCenter;
        private readonly List<Vector3> _v = new List<Vector3>();
        private readonly List<Color> _c = new List<Color>();
        private readonly List<Vector2> _uv = new List<Vector2>();
        private readonly List<Vector4> _info = new List<Vector4>();
        private readonly List<int> _tri = new List<int>();

        // ------------------------------------------------------------------ set up

        public static UiWristMenu Create(Transform parent, UiTheme theme)
        {
            var go = new GameObject("wrist menu");
            go.transform.SetParent(parent, false);
            var m = go.AddComponent<UiWristMenu>();
            m.Theme = theme;
            return m;
        }

        /// Call after filling Inner / Outer: makes the labels, the mesh and the particle systems.
        public void Build()
        {
            var sh = Resources.Load<Shader>("Shaders/WristHolo") ?? Shader.Find("MetaCodex/WristHolo");
            var ssh = Resources.Load<Shader>("Shaders/WristSpark") ?? Shader.Find("MetaCodex/WristSpark");
            _mat = new Material(sh) { name = "wrist menu" };
            _sparkMat = ssh != null ? new Material(ssh) { name = "wrist sparks" } : null;
            _mesh = new Mesh { name = "wrist menu" };
            _mesh.MarkDynamic();
            gameObject.AddComponent<MeshFilter>().sharedMesh = _mesh;
            var mr = gameObject.AddComponent<MeshRenderer>();
            mr.sharedMaterial = _mat;
            mr.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            mr.receiveShadows = false;
            int n = Inner.Count + Outer.Count;
            _hoverAmt = new float[n];
            _flash = new float[n];
            for (int i = 0; i < n; i++)
            {
                var it = ItemAt(i);
                bool inner = i < Inner.Count;
                _labels.Add(Label("blade " + it.Label, $"<size=165%>{it.Glyph}</size>\n{it.Label}", inner ? 0.0047f : 0.0040f));
            }
            _coreGlyph = Label("core", "◆", 0.0105f);
            _hint = Label("hint", "MENU", 0.0040f);
            if (_sparkMat != null)
            {
                _burst = Sparks("sparks", false);
                _motes = Sparks("motes", true);
            }
            SetVisible(false);
        }

        private TextMeshPro Label(string name, string text, float lineHeight)
        {
            var go = new GameObject(name);
            go.transform.SetParent(transform, false);
            var t = go.AddComponent<TextMeshPro>();
            if (Theme?.Display != null) { t.font = Theme.Display; t.fontSharedMaterial = Theme.Display.material; }
            t.fontSize = (Theme ?? UiTheme.Current).FontSizeFor(lineHeight) * 1.12f;
            t.characterSpacing = 3f;
            t.alignment = TextAlignmentOptions.Center;
            t.textWrappingMode = TextWrappingModes.NoWrap;
            t.richText = true;
            t.lineSpacing = -18f;
            t.rectTransform.sizeDelta = new Vector2(0.03f, 0.02f);
            t.rectTransform.pivot = new Vector2(0.5f, 0.5f);
            t.text = text;
            var r = go.GetComponent<MeshRenderer>();
            if (r != null) r.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            return t;
        }

        private ParticleSystem Sparks(string name, bool motes)
        {
            var go = new GameObject(name);
            go.transform.SetParent(transform, false);
            var ps = go.AddComponent<ParticleSystem>();
            ps.Stop(true, ParticleSystemStopBehavior.StopEmittingAndClear);
            var main = ps.main;
            main.playOnAwake = false;
            main.loop = motes;
            main.maxParticles = motes ? 24 : 72;
            main.simulationSpace = motes ? ParticleSystemSimulationSpace.Local : ParticleSystemSimulationSpace.World;
            main.scalingMode = ParticleSystemScalingMode.Hierarchy;
            main.startLifetime = motes ? new ParticleSystem.MinMaxCurve(0.9f, 1.6f) : new ParticleSystem.MinMaxCurve(0.22f, 0.5f);
            main.startSpeed = motes ? new ParticleSystem.MinMaxCurve(0.004f, 0.012f) : new ParticleSystem.MinMaxCurve(0.05f, 0.16f);
            main.startSize = motes ? new ParticleSystem.MinMaxCurve(0.0007f, 0.0016f) : new ParticleSystem.MinMaxCurve(0.0012f, 0.0034f);
            var accent = (Theme ?? UiTheme.Current).Accent;
            main.startColor = new ParticleSystem.MinMaxGradient(accent, Color.Lerp(accent, Color.white, 0.6f));
            var em = ps.emission;
            em.enabled = motes;
            if (motes) em.rateOverTime = 6f;
            var shape = ps.shape;
            shape.enabled = true;
            shape.shapeType = motes ? ParticleSystemShapeType.Circle : ParticleSystemShapeType.Sphere;
            shape.radius = motes ? CoreR1 * 1.2f : 0.003f;
            if (motes) shape.rotation = new Vector3(0f, 0f, 0f);
            var col = ps.colorOverLifetime;
            col.enabled = true;
            var g = new Gradient();
            g.SetKeys(new[] { new GradientColorKey(Color.white, 0f), new GradientColorKey(Color.white, 1f) },
                new[] { new GradientAlphaKey(0f, 0f), new GradientAlphaKey(motes ? 0.55f : 1f, 0.12f), new GradientAlphaKey(0f, 1f) });
            col.color = g;
            var size = ps.sizeOverLifetime;
            size.enabled = true;
            size.size = new ParticleSystem.MinMaxCurve(1f, AnimationCurve.Linear(0f, 1f, 1f, 0.15f));
            var r = go.GetComponent<ParticleSystemRenderer>();
            r.sharedMaterial = _sparkMat;
            r.renderMode = motes ? ParticleSystemRenderMode.Billboard : ParticleSystemRenderMode.Stretch;
            if (!motes) { r.velocityScale = 0.06f; r.lengthScale = 1.2f; }
            r.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            return ps;
        }

        private Item ItemAt(int part) => part < Inner.Count ? Inner[part] : Outer[part - Inner.Count];

        private void SetVisible(bool on)
        {
            var mr = GetComponent<MeshRenderer>();
            if (mr != null && mr.enabled != on) mr.enabled = on;
            foreach (var l in _labels) if (l.gameObject.activeSelf != on) l.gameObject.SetActive(on);
            if (_coreGlyph != null && _coreGlyph.gameObject.activeSelf != on) _coreGlyph.gameObject.SetActive(on);
            if (_hint != null && _hint.gameObject.activeSelf != on) _hint.gameObject.SetActive(on);
            if (_motes != null) { if (on && !_motes.isPlaying) _motes.Play(); else if (!on && _motes.isPlaying) _motes.Stop(true, ParticleSystemStopBehavior.StopEmittingAndClear); }
        }

        // ------------------------------------------------------------------ every frame

        private void Update() => Tick(Time.unscaledDeltaTime);

        /// One step: watch pose, placement, the fingertip, animation, the mesh (tests call it with their own dt).
        public void Tick(float dt)
        {
            if (_mesh == null) return;
            _time += dt;
            (bool okL, HandFrame L) = Hand != null ? Hand(Handedness.Left) : (false, default);
            (bool okR, HandFrame R) = Hand != null ? Hand(Handedness.Right) : (false, default);
            bool watching = false;
            Vector3 anchor = default;
            Quaternion rot = Quaternion.identity;
            if (okL && Head != null && !L.SystemGesture)
            {
                var back = -L.PalmNormal(Handedness.Left);
                var fwd = (L.MiddleKnuckle - L.Wrist).normalized;
                var toHead = Head.position - L.Wrist;
                float dist = toHead.magnitude;
                toHead /= Mathf.Max(dist, 1e-4f);
                // the back of the wrist turned to the eyes, and looked at (within ~44 degrees of the gaze)
                watching = Vector3.Dot(back, toHead) > 0.45f && Vector3.Dot(Head.forward, -toHead) > 0.72f && dist > 0.12f && dist < 0.75f;
                anchor = L.Wrist - fwd * 0.012f + back * 0.040f + toHead * 0.025f;
                var toEye = (Head.position - anchor).normalized;
                var up = Vector3.ProjectOnPlane(Head.up, toEye);
                rot = Quaternion.LookRotation(-toEye, up.sqrMagnitude > 1e-4f ? up.normalized : Vector3.up);
            }
            if (watching) { _watchFor += dt; _awayFor = 0f; } else { _awayFor += dt; _watchFor = 0f; }
            _wantShow = _wantShow ? _awayFor < 0.35f : _watchFor > 0.08f;
            if (!_wantShow) _expandTarget = false;
            _shown = Mathf.MoveTowards(_shown, _wantShow ? 1f : 0f, dt / (_wantShow ? 0.18f : 0.25f));
            if (watching)
            {
                // follows the wrist smoothly; nearly still while a fingertip is about to press it
                float near = okR && _posed ? Mathf.Abs(transform.InverseTransformPoint(R.IndexTip).z) : 1f;
                float rate = near < 0.04f ? 3f : 16f;
                if (!_posed || _shown < 0.02f) { transform.SetPositionAndRotation(anchor, rot); _posed = true; }
                else
                {
                    float k = 1f - Mathf.Exp(-rate * dt);
                    transform.SetPositionAndRotation(Vector3.Lerp(transform.position, anchor, k), Quaternion.Slerp(transform.rotation, rot, k));
                }
            }
            transform.localScale = Vector3.one * Scale;
            _expand = Mathf.MoveTowards(_expand, _expandTarget ? 1f : 0f, dt / (_expandTarget ? 0.34f : 0.2f));
            // the fingertip: over what, and pressed through the surface
            Hover = -1;
            if (okR && _shown > 0.5f)
            {
                var p = transform.InverseTransformPoint(R.IndexTip); // +z is away from the eyes: the surface is z = 0
                int part = HitTest(new Vector2(p.x, p.y));
                if (p.z > -0.03f && p.z < 0.012f) Hover = part;
                if (p.z < -0.006f) _armed = true;
                if (_armed && p.z >= -0.0008f && p.z < 0.02f && part != -1 && _time >= _cooldownUntil)
                {
                    _armed = false;
                    _cooldownUntil = _time + 0.3f;
                    Press(part, R.IndexTip, new Vector2(p.x, p.y));
                }
            }
            if (_expandTarget && Hover == -1) { _idle += dt; if (_idle > 7f) _expandTarget = false; } else _idle = 0f;
            for (int i = 0; i < _hoverAmt.Length; i++)
            {
                _hoverAmt[i] = Mathf.MoveTowards(_hoverAmt[i], Hover == i ? 1f : 0f, dt * 9f);
                _flash[i] = Mathf.Max(0f, _flash[i] - dt / 0.35f);
            }
            if (_wave < 1f) _wave = Mathf.Min(1f, _wave + dt / 0.38f);
            bool on = _shown > 0.001f;
            SetVisible(on);
            if (on) Rebuild();
        }

        /// What a point of the menu's surface is over (menu-local, metres before Scale).
        public int HitTest(Vector2 q)
        {
            float r = q.magnitude, a = Mathf.Atan2(q.y, q.x) * Mathf.Rad2Deg;
            if (r < CoreR1 * 1.15f) return -2;
            if (_expand < 0.6f) return -1;
            if (r >= InR0 - 0.001f && r <= InR1 + 0.0015f) { int i = BladeAt(a, Inner.Count, InStart, InGapDeg); if (i >= 0) return i; }
            if (r >= OutR0 - 0.0015f && r <= OutR1 + 0.002f) { int i = BladeAt(a, Outer.Count, OutStart, OutGapDeg); if (i >= 0) return Inner.Count + i; }
            return -1;
        }

        private static int BladeAt(float a, int n, float start, float gap)
        {
            if (n == 0) return -1;
            float step = 360f / n;
            int i = Mathf.RoundToInt(-Mathf.DeltaAngle(start, a) / step);
            i = ((i % n) + n) % n;
            float centre = start - i * step;
            return Mathf.Abs(Mathf.DeltaAngle(centre, a)) <= step * 0.5f - gap * 0.5f ? i : -1;
        }

        private void Press(int part, Vector3 tipWorld, Vector2 local)
        {
            if (part == -2)
            {
                _expandTarget = !_expandTarget;
                UiSounds.Fire(_expandTarget ? "open" : "close");
                Burst(tipWorld, 10);
                _wave = 0f;
                _waveCenter = Vector2.zero;
                return;
            }
            _flash[part] = 1f;
            _idle = 0f;
            UiSounds.Fire("click");
            Burst(tipWorld, 18);
            _wave = 0f;
            _waveCenter = local;
            Pressed?.Invoke(part);
            try { ItemAt(part).Act?.Invoke(); }
            catch (Exception ex) { Debug.LogWarning("[MetaCodex] wrist menu " + ItemAt(part).Label + ": " + ex.Message); }
        }

        private void Burst(Vector3 at, int count)
        {
            if (_burst == null) return;
            _burst.transform.position = at;
            _burst.Emit(count);
        }

        /// Tests and hints: the world position of a part's centre (core: -2, blades: 0..).
        public Vector3 WorldOf(int part)
        {
            if (part == -2) return transform.position;
            bool inner = part < Inner.Count;
            int i = inner ? part : part - Inner.Count, n = inner ? Inner.Count : Outer.Count;
            float a = ((inner ? InStart : OutStart) - i * 360f / n) * Mathf.Deg2Rad;
            float r = inner ? (InR0 + InR1) * 0.5f : (OutR0 + OutR1) * 0.5f;
            return transform.TransformPoint(new Vector3(Mathf.Cos(a) * r, Mathf.Sin(a) * r, 0f));
        }

        /// Out of sight at once (the DOCK mode took over).
        public void HideNow()
        {
            _shown = 0f; _expand = 0f; _expandTarget = false; _wantShow = false; _posed = false;
            SetVisible(false);
        }

        /// Tests: fold or unfold without a finger.
        public void SetExpandedForTest(bool on) { _expandTarget = on; _expand = on ? 1f : 0f; }

        // ------------------------------------------------------------------ the mesh

        private static float EaseOutBack(float t) { const float c1 = 1.70158f, c3 = c1 + 1f; t = Mathf.Clamp01(t); return 1f + c3 * Mathf.Pow(t - 1f, 3f) + c1 * Mathf.Pow(t - 1f, 2f); }

        private void Rebuild()
        {
            _v.Clear(); _c.Clear(); _uv.Clear(); _info.Clear(); _tri.Clear();
            var t = Theme ?? UiTheme.Current;
            Color accent = t.Accent;
            float shown = _shown, ex = _expand * shown;
            float pulse = 0.5f + 0.5f * Mathf.Sin(_time * 2.4f);
            float uiHide = Mathf.Clamp01(Shader.GetGlobalFloat("_McxUiHide"));

            // the core: a hexagonal ring of glass, a dashed ring turning around it
            bool coreHover = Hover == -2;
            Band(Vector2.zero, CoreR0, CoreR1, 30f, 390f, 6, accent, shown * (0.85f + 0.15f * pulse), coreHover ? 0.8f : 0.12f + 0.12f * pulse, 0f, 0f, true);
            for (int k = 0; k < 24; k++)
            {
                float a0 = -_time * 22f + k * 15f;
                Line(Vector2.zero, DashR, 0.0009f, a0, a0 + 8f, 2, accent, shown * 0.75f, coreHover ? 0.6f : 0f);
            }
            if (ex > 0.01f)
            {
                // the rings of blades: they swing out of the core one after the other
                int n = Inner.Count + Outer.Count;
                for (int i = 0; i < n; i++)
                {
                    bool inner = i < Inner.Count;
                    int j = inner ? i : i - Inner.Count, cnt = inner ? Inner.Count : Outer.Count;
                    float step = 360f / cnt, gap = inner ? InGapDeg : OutGapDeg;
                    float delay = (inner ? 0f : 0.28f) + 0.32f * j / Mathf.Max(1, cnt);
                    float e = EaseOutBack((_expand * 1.6f - delay) / 0.62f);
                    if (e <= 0.001f) continue;
                    float centre = (inner ? InStart : OutStart) - j * step + (1f - e) * 32f;
                    float h = _hoverAmt[i], f = _flash[i];
                    var item = ItemAt(i);
                    bool lit = item.Lit != null && item.Lit();
                    Color col = lit ? item.LitTint : accent;
                    if (f > 0f) col = Color.Lerp(col, Color.white, f * 0.7f);
                    float push = (1f - e) * -0.014f + h * 0.0024f + f * 0.0015f;
                    float r0 = (inner ? InR0 : OutR0) + push, r1 = (inner ? InR1 : OutR1) + push;
                    float state = Mathf.Clamp01(h * 0.65f + f + (lit ? 0.45f : 0f));
                    Band(Vector2.zero, r0, r1, centre - step * 0.5f + gap * 0.5f, centre + step * 0.5f - gap * 0.5f, inner ? 10 : 6, col, ex * Mathf.Clamp01(e) * (inner ? 1f : 0.88f), state, Chamfer, -h * 0.002f, false);
                    var label = _labels[i];
                    float ar = centre * Mathf.Deg2Rad, rm = (r0 + r1) * 0.5f;
                    label.rectTransform.localPosition = new Vector3(Mathf.Cos(ar) * rm, Mathf.Sin(ar) * rm, -0.0009f - h * 0.002f);
                    var lc = lit ? Color.Lerp(item.LitTint, Color.white, 0.55f) : Color.Lerp(t.Text, Color.white, h * 0.6f);
                    lc.a = ex * Mathf.Clamp01(e) * (0.82f + 0.18f * h) * (1f - uiHide);
                    if (label.color != lc) label.color = lc;
                }
                // a thin ring between the two, the ticking ring, the brackets and the radar sweep
                Line(Vector2.zero, MidRingR, 0.0006f, 0f, 360f, 96, accent, ex * 0.35f, 0f);
                for (int k = 0; k < 48; k++)
                {
                    float a = -_time * 6f + k * 7.5f;
                    bool major = k % 4 == 0;
                    Tick(TickR0, major ? TickR1 + 0.0016f : TickR1, a, major ? 0.0009f : 0.0006f, accent, ex * (major ? 0.9f : 0.5f));
                }
                for (int k = 0; k < 4; k++)
                {
                    float a = 45f + k * 90f + _time * 3f;
                    Line(Vector2.zero, BracketR, 0.0011f, a - 11f, a + 11f, 8, accent, ex * 0.8f, 0f);
                    Tick(BracketR - 0.0035f, BracketR + 0.0006f, a - 11f, 0.0011f, accent, ex * 0.8f);
                    Tick(BracketR - 0.0035f, BracketR + 0.0006f, a + 11f, 0.0011f, accent, ex * 0.8f);
                }
                float sweep = _time * 75f;
                Glow(Vector2.zero, InR0, OutR1, sweep - 42f, sweep, 14, accent, ex * 0.16f);
            }
            if (_wave < 1f)
            {
                float w = 1f - Mathf.Pow(1f - _wave, 3f);
                Line(_waveCenter, Mathf.Lerp(0.004f, 0.034f, w), 0.0016f * (1f - _wave) + 0.0004f, 0f, 360f, 48, Color.Lerp(accent, Color.white, 0.4f), shown * (1f - _wave) * 0.9f, 1f);
            }
            _mesh.Clear();
            _mesh.SetVertices(_v);
            _mesh.SetColors(_c);
            _mesh.SetUVs(0, _uv);
            _mesh.SetUVs(1, _info);
            _mesh.SetTriangles(_tri, 0);
            _mesh.RecalculateBounds();
            // the core's glyph and hint, the labels of a folded menu
            _coreGlyph.text = _expandTarget ? "✕" : "◆";
            _coreGlyph.rectTransform.localPosition = new Vector3(0f, 0f, -0.0009f);
            var cg = Color.Lerp(t.TextAccent, Color.white, coreHover ? 0.7f : 0.2f * pulse);
            cg.a = shown * (1f - uiHide);
            _coreGlyph.color = cg;
            _hint.rectTransform.localPosition = new Vector3(0f, -(DashR + 0.0058f), -0.0009f);
            var hc = t.TextDim;
            hc.a = shown * (1f - _expand) * 0.85f * (1f - uiHide);
            _hint.color = hc;
            if (ex <= 0.01f) foreach (var l in _labels) { var c = l.color; if (c.a != 0f) { c.a = 0f; l.color = c; } }
        }

        /// A ring segment of glass (kind 0) from r0 to r1 between two angles; the outer corners cut by `chamfer`;
        /// `closed`: a whole ring (the hexagonal core), whose start and end meet: no rim across the seam.
        private void Band(Vector2 centre, float r0, float r1, float a0, float a1, int segs, Color col, float strength, float state, float chamfer, float z, bool closed)
        {
            if (strength <= 0.001f) return;
            int b = _v.Count;
            float span = (a1 - a0) * Mathf.Deg2Rad, lenOuter = Mathf.Abs(span) * r1, lenMid = Mathf.Abs(span) * (r0 + r1) * 0.5f;
            var lin = col.linear;
            lin.a = strength;
            for (int i = 0; i <= segs; i++)
            {
                float k = i / (float)segs, a = (a0 + (a1 - a0) * k) * Mathf.Deg2Rad;
                float sOut = lenOuter * k, cut = Mathf.Max(0f, chamfer - Mathf.Min(sOut, lenOuter - sOut));
                var dir = new Vector2(Mathf.Cos(a), Mathf.Sin(a));
                var pin = centre + dir * r0;
                var pout = centre + dir * (r1 - cut);
                // a closed ring: "along" stays far from both ends (no rim across the seam)
                float along = closed ? 0.25f + 0.5f * k : k;
                _v.Add(new Vector3(pin.x, pin.y, z)); _uv.Add(new Vector2(0f, along));
                _v.Add(new Vector3(pout.x, pout.y, z)); _uv.Add(new Vector2(1f, along));
                _c.Add(lin); _c.Add(lin);
                var info = new Vector4(r1 - r0, closed ? 1000f : lenMid, 0f, state);
                _info.Add(info); _info.Add(info);
            }
            for (int i = 0; i < segs; i++)
            {
                int q = b + i * 2;
                _tri.Add(q); _tri.Add(q + 1); _tri.Add(q + 3);
                _tri.Add(q); _tri.Add(q + 3); _tri.Add(q + 2);
            }
        }

        /// An arc of light (kind 1) of width w around radius r.
        private void Line(Vector2 centre, float r, float w, float a0, float a1, int segs, Color col, float strength, float state)
        {
            if (strength <= 0.001f) return;
            int b = _v.Count;
            var lin = col.linear;
            lin.a = strength;
            for (int i = 0; i <= segs; i++)
            {
                float k = i / (float)segs, a = (a0 + (a1 - a0) * k) * Mathf.Deg2Rad;
                var dir = new Vector2(Mathf.Cos(a), Mathf.Sin(a));
                var p0 = centre + dir * (r - w * 0.5f);
                var p1 = centre + dir * (r + w * 0.5f);
                _v.Add(new Vector3(p0.x, p0.y, -0.0003f)); _uv.Add(new Vector2(0f, k));
                _v.Add(new Vector3(p1.x, p1.y, -0.0003f)); _uv.Add(new Vector2(1f, k));
                _c.Add(lin); _c.Add(lin);
                var info = new Vector4(w, 0f, 1f, state);
                _info.Add(info); _info.Add(info);
            }
            for (int i = 0; i < segs; i++)
            {
                int q = b + i * 2;
                _tri.Add(q); _tri.Add(q + 1); _tri.Add(q + 3);
                _tri.Add(q); _tri.Add(q + 3); _tri.Add(q + 2);
            }
        }

        /// A radial tick of light (kind 1) from r0 to r1 at angle a.
        private void Tick(float r0, float r1, float aDeg, float w, Color col, float strength)
        {
            if (strength <= 0.001f) return;
            float a = aDeg * Mathf.Deg2Rad;
            var dir = new Vector2(Mathf.Cos(a), Mathf.Sin(a));
            var side = new Vector2(-dir.y, dir.x) * (w * 0.5f);
            var lin = col.linear;
            lin.a = strength;
            int b = _v.Count;
            Vector2 p0 = dir * r0 - side, p1 = dir * r0 + side, p2 = dir * r1 + side, p3 = dir * r1 - side;
            _v.Add(new Vector3(p0.x, p0.y, -0.0003f)); _uv.Add(new Vector2(0f, 0f));
            _v.Add(new Vector3(p1.x, p1.y, -0.0003f)); _uv.Add(new Vector2(1f, 0f));
            _v.Add(new Vector3(p2.x, p2.y, -0.0003f)); _uv.Add(new Vector2(1f, 1f));
            _v.Add(new Vector3(p3.x, p3.y, -0.0003f)); _uv.Add(new Vector2(0f, 1f));
            for (int k = 0; k < 4; k++) { _c.Add(lin); _info.Add(new Vector4(w, r1 - r0, 1f, 0f)); }
            _tri.Add(b); _tri.Add(b + 1); _tri.Add(b + 2);
            _tri.Add(b); _tri.Add(b + 2); _tri.Add(b + 3);
        }

        /// A soft glow (kind 2) over a ring sector, brightest at its leading edge (a1): the radar sweep.
        private void Glow(Vector2 centre, float r0, float r1, float a0, float a1, int segs, Color col, float strength)
        {
            if (strength <= 0.001f) return;
            int b = _v.Count;
            var lin = col.linear;
            lin.a = strength;
            for (int i = 0; i <= segs; i++)
            {
                float k = i / (float)segs, a = (a0 + (a1 - a0) * k) * Mathf.Deg2Rad;
                var dir = new Vector2(Mathf.Cos(a), Mathf.Sin(a));
                var p0 = centre + dir * r0;
                var p1 = centre + dir * r1;
                _v.Add(new Vector3(p0.x, p0.y, -0.0002f)); _uv.Add(new Vector2(0f, k));
                _v.Add(new Vector3(p1.x, p1.y, -0.0002f)); _uv.Add(new Vector2(1f, k));
                _c.Add(lin); _c.Add(lin);
                var info = new Vector4(r1 - r0, 0f, 2f, 0f);
                _info.Add(info); _info.Add(info);
            }
            for (int i = 0; i < segs; i++)
            {
                int q = b + i * 2;
                _tri.Add(q); _tri.Add(q + 1); _tri.Add(q + 3);
                _tri.Add(q); _tri.Add(q + 3); _tri.Add(q + 2);
            }
        }
    }
}

using System.Collections.Generic;
using UnityEngine;

namespace MetaCodex.UI
{
    /// The Whisp: a small prismatic crystal wisp that floats by the user. It casts the panels (UiPanelFx: rays of light
    /// from it draw them), it is where the voice comes from (its inner light pulses with every syllable and sends ripples
    /// out), and what the app has to say appears beside it. An original design: a faceted crystal cluster with a light
    /// inside that breathes, four small shards in slow orbit, a soft halo, a short tail when it moves; its facets shift in
    /// hue with the angle they are seen from (a cheap fresnel over the app's cyan).
    /// Its states, each a smooth change from the last: idle (calm cyan, a slow turn), flare (the wake word: a burst of
    /// white light, the shards thrown out), listening (bright ice, the shards in a ring that faces the user, the light
    /// follows the voice), thinking (violet, it spins up), speaking (the light pulses with the words, ripples leave it),
    /// needs you (amber, a slow heartbeat), busy (dim steel, the shards turn round it like a spinner).
    /// This component is its body: the look for each state, the eased motion towards `Home`, and the uniforms of
    /// MetaCodex/Orb (one small mesh made once, about a hundred triangles, shaped and moved in the vertex shader). Where
    /// it floats and what it shows is the shell's (ShellOrb). It never appears or goes at once: Show(true/false) plays
    /// its ignition.
    public sealed class UiOrb : MonoBehaviour
    {
        public enum Mood { Idle, Listening, Thinking, Speaking }

        /// The bounds' half size in radii: the halo, the shards' orbits and the tail fit inside.
        public const float Extent = 2.6f;
        /// The halo's half size in radii (a square turned to each eye; the old orb's quad was 2.3).
        public const float HaloExtent = 1.7f;
        public static readonly Color Gold = new Color(1f, 0.80f, 0.50f);

        public Transform Head;
        public float Radius = 0.032f;
        public Mood State { get; private set; } = Mood.Idle;
        /// The voice's loudness now, 0..1 (speaking), or the microphone's (listening).
        public float Loudness;
        /// Where it wants to float (world); it eases there and bobs a little round it.
        public Vector3 Home;
        /// Messages the user has not looked at yet (0..4): each makes one of its shards larger and gold.
        public int Unread;
        /// 0..1: a hand is near it or points at it (it brightens and leans to that hand, at `AttendTo`).
        public float Attention;
        public Vector3 AttendTo;
        /// Something waits for the user (a thread asks for an approval or an answer): amber, a slow heartbeat.
        public bool NeedsYou;
        /// Locked or busy (the first start's setup runs and it cannot listen yet): dim steel, the shards turn round it.
        public bool Busy;
        /// Shown, as far as the app is concerned (it may still be fading).
        public bool Visible => _appearTarget > 0.5f;
        /// 0 gone .. 1 there.
        public float Appear => _appear;
        public Vector3 Position => transform.position;
        /// Where it attends (world, unit): the user, the panel it casts, the place it points at.
        public Vector3 Look => _look;
        public float Energy => _energy;
        public float Pulse => _pulse;
        public Color Tint => _tint;
        public float Aim => _aim;
        public int Ripples { get { int n = 0; for (int i = 0; i < 4; i++) if (_ripples[i] >= 0f) n++; return n; } }
        /// How fast it may fly (m/s) and how soon it settles (s).
        public float MaxSpeed = 2.4f, SettleTime = 0.14f;

        private Mesh _mesh;
        private MeshRenderer _mr;
        private Material _mat;
        private Vector3 _pos, _vel, _look = Vector3.back, _aimDir = Vector3.back, _pointDir = Vector3.forward, _lag1, _lag2;
        private float _appear, _appearTarget, _energy = 0.35f, _pulse, _kick, _aim, _aimHold, _pointHold, _point, _time, _lastRipple = -10f, _loud;
        private float _flashT = 1f, _flashLen = 1f, _flareT = 1f, _flareAt = -10f, _wave = 1f, _waveStrength;
        private float _spin, _spinSpeed = 0.35f, _heart, _prismPhase, _prism = 0.65f, _orbitSpeed = 1f, _orbitR = 1.35f, _ring, _ringPhase, _stream;
        private Color _tint = Color.cyan, _flash = Color.white;
        private bool _placed;
        private readonly float[] _shardPhase = { 0f, 1.7f, 3.4f, 5.1f }, _tumble = { 0f, 2f, 4f, 1f };
        private readonly float[] _ripples = { -1f, -1f, -1f, -1f };
        // every input of MetaCodex/Orb is an ordinary material property (no arrays: see the shader's header)
        private static readonly int CenterId = Shader.PropertyToID("_OrbCenter"), StateId = Shader.PropertyToID("_OrbState"), TintId = Shader.PropertyToID("_OrbTint"),
            DeepId = Shader.PropertyToID("_OrbDeep"), GoldId = Shader.PropertyToID("_OrbGold"), PhaseAId = Shader.PropertyToID("_OrbPhaseA"), PhaseBId = Shader.PropertyToID("_OrbPhaseB"),
            LookId = Shader.PropertyToID("_OrbLook"), ShieldId = Shader.PropertyToID("_OrbShield"), RipplesId = Shader.PropertyToID("_OrbRipples"),
            TailAId = Shader.PropertyToID("_OrbRing0N"), TailBId = Shader.PropertyToID("_OrbRing0U"), TumbleId = Shader.PropertyToID("_OrbRing1N"),
            MoteWId = Shader.PropertyToID("_OrbMoteW"), MoteGoldId = Shader.PropertyToID("_OrbMoteGold");
        private static readonly int[] RotAIds = { Shader.PropertyToID("_OrbRotA0"), Shader.PropertyToID("_OrbRotA1"), Shader.PropertyToID("_OrbRotA2") };
        private static readonly int[] RotBIds = { Shader.PropertyToID("_OrbRotB0"), Shader.PropertyToID("_OrbRotB1"), Shader.PropertyToID("_OrbRotB2") };
        private static readonly int[] MoteIds = { Shader.PropertyToID("_OrbMote0"), Shader.PropertyToID("_OrbMote1"), Shader.PropertyToID("_OrbMote2"), Shader.PropertyToID("_OrbMote3") };

        public static UiOrb Create(Transform parent)
        {
            var go = new GameObject("Orb", typeof(MeshFilter), typeof(MeshRenderer));
            go.transform.SetParent(parent, false);
            var orb = go.AddComponent<UiOrb>();
            orb.Init();
            return orb;
        }

        private void Init()
        {
            _mesh = BuildMesh();
            GetComponent<MeshFilter>().sharedMesh = _mesh;
            _mr = GetComponent<MeshRenderer>();
            _mr.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            _mr.receiveShadows = false;
            _mr.lightProbeUsage = UnityEngine.Rendering.LightProbeUsage.Off;
            var sh = Resources.Load<Shader>("Shaders/Orb") ?? Shader.Find("MetaCodex/Orb");
            if (sh != null) _mat = new Material(sh) { name = "whisp" };
            _mr.sharedMaterial = _mat;
            _mr.enabled = false;
            if (UiTheme.Current != null) _tint = UiTheme.Current.Accent;
            FitBounds();
        }

        private void FitBounds()
        {
            // the shader places every part round the transform's place: bounds that hold it from any side
            if (_mesh != null) _mesh.bounds = new Bounds(Vector3.zero, Vector3.one * (2f * Extent * Radius + 0.02f));
        }

        private void OnDestroy()
        {
            if (_mesh != null) { if (Application.isPlaying) Destroy(_mesh); else DestroyImmediate(_mesh); }
            if (_mat != null) { if (Application.isPlaying) Destroy(_mat); else DestroyImmediate(_mat); }
        }

        // ------------------------------------------------------------------ the mesh (made once)

        // the parts, in draw order (one draw call: the order of the triangles is the order they blend in)
        private const float KindCrystal = 0f, KindHeart = 1f, KindShard = 2f, KindHalo = 3f, KindTail = 4f;
        private const int TailSegs = 6;
        private List<Vector3> _bv, _bn;
        private List<Vector4> _b0, _b1, _b2;
        private List<int> _bt;

        /// The halo (2 triangles), the tail (12), the heart (8), the crystal cluster (44), the shards (32): 98 triangles.
        /// Positions are in radii; the vertex shader turns, scales and places them from the uniforms.
        private Mesh BuildMesh()
        {
            _bv = new List<Vector3>(); _bn = new List<Vector3>(); _b0 = new List<Vector4>(); _b1 = new List<Vector4>(); _b2 = new List<Vector4>(); _bt = new List<int>();
            // the halo: a square turned to each eye, corners -1..1
            int h = _bv.Count;
            for (int k = 0; k < 4; k++) Vert(new Vector3(k % 2 == 0 ? -1f : 1f, k < 2 ? -1f : 1f, 0f), Vector3.zero, KindHalo, 0f, 0f, Vector3.zero, Vector3.zero);
            _bt.Add(h); _bt.Add(h + 2); _bt.Add(h + 1); _bt.Add(h + 1); _bt.Add(h + 2); _bt.Add(h + 3);
            // the tail: a ribbon of six segments (x: how far along it, y: which side)
            int tl = _bv.Count;
            for (int s = 0; s <= TailSegs; s++)
            {
                Vert(new Vector3(s / (float)TailSegs, -1f, 0f), Vector3.zero, KindTail, 0f, 0f, Vector3.zero, Vector3.zero);
                Vert(new Vector3(s / (float)TailSegs, 1f, 0f), Vector3.zero, KindTail, 0f, 0f, Vector3.zero, Vector3.zero);
            }
            for (int s = 0; s < TailSegs; s++) { int i0 = tl + s * 2; _bt.Add(i0); _bt.Add(i0 + 2); _bt.Add(i0 + 1); _bt.Add(i0 + 1); _bt.Add(i0 + 2); _bt.Add(i0 + 3); }
            var rnd = new System.Random(11);
            // the heart: a small octahedron of light inside the crystal
            Gem(Vector3.zero, Vector3.up, 0.26f, 0f, 0.17f, 0f, 0.17f, 0.26f, 4, 0f, KindHeart, rnd);
            // the crystal: a six-sided point, its foot a little narrower, and two smaller points grown from its side
            Gem(Vector3.zero, Vector3.up, 1.00f, 0.40f, 0.36f, -0.30f, 0.33f, 0.80f, 6, 0.12f, KindCrystal, rnd);
            Gem(new Vector3(0.16f, -0.30f, 0.06f), new Vector3(0.62f, 0.78f, 0.15f), 0.62f, 0.12f, 0.15f, 0.12f, 0.15f, 0.16f, 5, 0f, KindCrystal, rnd);
            Gem(new Vector3(-0.14f, -0.36f, -0.05f), new Vector3(-0.55f, 0.80f, -0.25f), 0.52f, 0.10f, 0.13f, 0.10f, 0.13f, 0.14f, 5, 0f, KindCrystal, rnd);
            // the shards: a thin octahedron each (local units; the shader scales it), each tumbling round its own axis
            for (int i = 0; i < 4; i++)
            {
                var axis = new Vector3((float)rnd.NextDouble() - 0.5f, (float)rnd.NextDouble() - 0.5f, (float)rnd.NextDouble() - 0.5f);
                if (axis.sqrMagnitude < 1e-3f) axis = Vector3.up;
                axis.Normalize();
                Vector3 top = Vector3.up, bottom = Vector3.down;
                for (int k = 0; k < 4; k++)
                {
                    float a0 = k * Mathf.PI * 0.5f, a1 = (k + 1) * Mathf.PI * 0.5f;
                    var c0 = new Vector3(Mathf.Cos(a0), 0f, Mathf.Sin(a0)) * 0.34f;
                    var c1 = new Vector3(Mathf.Cos(a1), 0f, Mathf.Sin(a1)) * 0.34f;
                    float hue = (float)rnd.NextDouble();
                    Tri(top, c0, c1, Vector3.zero, KindShard, i, hue, true, true, true, axis);
                    Tri(bottom, c1, c0, Vector3.zero, KindShard, i, hue, true, true, true, axis);
                }
            }
            var mesh = new Mesh { name = "whisp" };
            mesh.SetVertices(_bv);
            mesh.SetNormals(_bn);
            mesh.SetUVs(0, _b0);
            mesh.SetUVs(1, _b1);
            mesh.SetUVs(2, _b2);
            mesh.SetTriangles(_bt, 0);
            _bv = null; _bn = null; _b0 = null; _b1 = null; _b2 = null; _bt = null;
            return mesh;
        }

        /// A crystal along `axis` from `at`: a point `top` along it, an upper ring of `sides` corners at `upY` (radius
        /// `upR`), a lower ring at `lowY` (radius `lowR`, turned `twist` radians against the upper), a point `bottom`
        /// below `at`. Equal rings make a bipyramid (no sides between them).
        private void Gem(Vector3 at, Vector3 axis, float top, float upY, float upR, float lowY, float lowR, float bottom, int sides, float twist, float kind, System.Random rnd)
        {
            axis.Normalize();
            var e1 = Vector3.Cross(axis, Mathf.Abs(axis.y) < 0.9f ? Vector3.up : Vector3.right).normalized;
            var e2 = Vector3.Cross(axis, e1);
            var inside = at + axis * ((upY + lowY) * 0.5f);
            var apexT = at + axis * top;
            var apexB = at - axis * bottom;
            bool prism = upY - lowY > 1e-4f;
            for (int i = 0; i < sides; i++)
            {
                float a0 = i * Mathf.PI * 2f / sides, a1 = (i + 1) * Mathf.PI * 2f / sides;
                Vector3 u0 = at + axis * upY + (e1 * Mathf.Cos(a0) + e2 * Mathf.Sin(a0)) * upR;
                Vector3 u1 = at + axis * upY + (e1 * Mathf.Cos(a1) + e2 * Mathf.Sin(a1)) * upR;
                Vector3 l0 = at + axis * lowY + (e1 * Mathf.Cos(a0 + twist) + e2 * Mathf.Sin(a0 + twist)) * lowR;
                Vector3 l1 = at + axis * lowY + (e1 * Mathf.Cos(a1 + twist) + e2 * Mathf.Sin(a1 + twist)) * lowR;
                Tri(apexT, u0, u1, inside, kind, 0f, (float)rnd.NextDouble(), true, true, true, Vector3.zero);
                if (prism)
                {
                    // a side: two triangles, their shared diagonal is no edge of the crystal (it does not glow)
                    float hue = (float)rnd.NextDouble();
                    Tri(u0, l0, l1, inside, kind, 0f, hue, true, true, false, Vector3.zero);
                    Tri(u0, l1, u1, inside, kind, 0f, hue, false, true, true, Vector3.zero);
                    Tri(apexB, l1, l0, inside, kind, 0f, (float)rnd.NextDouble(), true, true, true, Vector3.zero);
                }
                else Tri(apexB, u1, u0, inside, kind, 0f, (float)rnd.NextDouble(), true, true, true, Vector3.zero);
            }
        }

        /// One flat facet: its own three corners (so it has its own normal, outwards from `inside`) and their barycentric
        /// places, so the shader can light the facet's edges; `ab`, `bc`, `ca` say which edges are real crystal edges.
        private void Tri(Vector3 a, Vector3 b, Vector3 c, Vector3 inside, float kind, float index, float hue, bool ab, bool bc, bool ca, Vector3 axis)
        {
            var n = Vector3.Cross(b - a, c - a).normalized;
            if (Vector3.Dot(n, (a + b + c) / 3f - inside) < 0f) n = -n;
            // a barycentric part is 0 on the edge across from its corner; an edge that must not glow keeps its part >= 1
            float nx = bc ? 0f : 1f, ny = ca ? 0f : 1f, nz = ab ? 0f : 1f;
            int i0 = _bv.Count;
            Vert(a, n, kind, index, hue, new Vector3(1f + nx, ny, nz), axis);
            Vert(b, n, kind, index, hue, new Vector3(nx, 1f + ny, nz), axis);
            Vert(c, n, kind, index, hue, new Vector3(nx, ny, 1f + nz), axis);
            _bt.Add(i0); _bt.Add(i0 + 1); _bt.Add(i0 + 2);
        }

        private void Vert(Vector3 p, Vector3 n, float kind, float index, float hue, Vector3 bary, Vector3 axis)
        {
            _bv.Add(p);
            _bn.Add(n);
            _b0.Add(new Vector4(kind, index, hue, 0f));
            _b1.Add(new Vector4(bary.x, bary.y, bary.z, 0f));
            _b2.Add(new Vector4(axis.x, axis.y, axis.z, 0f));
        }

        // ------------------------------------------------------------------ what the shell asks of it

        /// Comes (a spark, the shards open, the light ignites) or goes; never at once.
        public void Show(bool on)
        {
            if (on && _appearTarget < 0.5f) { _kick = 1f; _wave = 0f; _waveStrength = 1f; Ripple(); } // it ignites: a flash, a ripple, a wave of light
            _appearTarget = on ? 1f : 0f;
        }

        /// Puts it at a place at once (the first frame, a recenter): no flight across the room, no tail.
        public void Teleport(Vector3 at) { Home = at; _pos = at; _vel = Vector3.zero; _placed = true; transform.position = at; _lag1 = at; _lag2 = at; }

        public void SetMood(Mood m) { State = m; }

        /// A moment of colour: a notification (its kind's colour), an answer, an error.
        public void Flash(Color c, float seconds = 1.1f) { _flash = c; _flashT = 0f; _flashLen = Mathf.Max(0.2f, seconds); _kick = Mathf.Max(_kick, 0.7f); _wave = 0f; _waveStrength = 0.8f; }

        /// It heard its name ("Hey Whisp", or TALK): a quick flare, a burst of light and its shards thrown out, then
        /// whatever comes next (listening). A second call within 0.8 s is the same flare.
        public void WhispFlare()
        {
            if (_time - _flareAt < 0.8f) return;
            _flareAt = _time;
            _flareT = 0f;
            _kick = 1f;
            Ripple();
        }

        /// It turns to a point it casts a panel at: it leans to it, a kick of light, a wave of light runs along it from there.
        public void Cast(Vector3 target)
        {
            var d = target - transform.position;
            if (d.sqrMagnitude > 1e-6f) _aimDir = d.normalized;
            _aimHold = 0.75f;
            _kick = 1f;
            _wave = 0f; _waveStrength = 1f;
        }

        /// It shows the user where to look (a window that had to open out of view): its tip turns to the place and its
        /// shards stream that way for `seconds`.
        public void PointAt(Vector3 target, float seconds = 2.5f)
        {
            var d = target - transform.position;
            if (d.sqrMagnitude < 1e-6f) return;
            _pointDir = d.normalized;
            _pointHold = Mathf.Max(_pointHold, seconds);
            _kick = Mathf.Max(_kick, 0.6f);
        }

        /// What is left of a closed panel comes home: a small kick, the wave of light once more.
        public void Absorb() { _kick = Mathf.Max(_kick, 0.8f); _wave = 0f; _waveStrength = Mathf.Max(_waveStrength, 0.8f); }

        private void Update() { Tick(Time.unscaledDeltaTime); }

        // ------------------------------------------------------------------ every frame

        /// One frame (self-tests step it: the Editor has no frame loop).
        public void Tick(float dt)
        {
            dt = Mathf.Clamp(dt, 0f, 0.1f);
            _time += dt;
            var t = UiTheme.Current;
            // ---- there or not: it ignites in 0.7 s, goes in 0.4 s
            _appear = Mathf.MoveTowards(_appear, _appearTarget, dt / (_appearTarget > _appear ? 0.7f : 0.4f));
            // ---- flight: eased towards home, then a slow bob round it; two points trail it (the tail when it moves)
            if (!_placed) Teleport(Home);
            _pos = Vector3.SmoothDamp(_pos, Home, ref _vel, SettleTime, MaxSpeed, Mathf.Max(dt, 1e-4f));
            var bob = new Vector3(Mathf.Sin(_time * 1.45f) * 0.003f, Mathf.Sin(_time * 2.2f) * 0.006f, Mathf.Cos(_time * 1.1f) * 0.003f);
            transform.position = _pos + bob;
            _lag1 = Vector3.Lerp(_lag1, transform.position, UiTheme.Approach(dt, 0.16f));
            _lag2 = Vector3.Lerp(_lag2, _lag1, UiTheme.Approach(dt, 0.16f));

            // ---- the state: how bright the light inside is, the colour, how fast it turns, where the shards go
            _loud = Mathf.Lerp(_loud, Mathf.Clamp01(Loudness), 1f - Mathf.Exp(-dt * (Loudness > _loud ? 40f : 9f))); // fast up, slower down
            Color accent = t != null ? t.Accent : new Color(0.21f, 0.82f, 1f);
            float breath = 0.5f + 0.5f * Mathf.Sin(_time * 1.6f); // about four seconds a breath
            float energy = 0.30f + 0.12f * breath, pulse = 0f, aim = 0f, spin = 0.35f, orbit = 1f, orbitR = 1.35f, ring = 0f, prism = 0.65f;
            Color tint = accent;
            Vector3 toHead = Head != null ? (Head.position - transform.position).normalized : Vector3.back;
            Vector3 aimDir = toHead;
            switch (State)
            {
                case Mood.Listening: // bright ice; the shards gather in a ring that faces the user; the light follows the voice
                    energy = 0.62f + 0.38f * _loud; pulse = _loud * 0.8f; aim = 0.9f; spin = 0.2f; orbit = 0.6f; ring = 1f; orbitR = 1.22f + 0.18f * _loud; prism = 0.5f;
                    tint = Color.Lerp(accent, Color.white, 0.35f);
                    break;
                case Mood.Thinking: // violet; it spins up, the shards race round it, the colours run
                    energy = 0.85f + 0.08f * Mathf.Sin(_time * 9f); pulse = 0.25f + 0.15f * Mathf.Sin(_time * 9f); spin = 2.6f; orbit = 4f; prism = 1f;
                    if (t != null) tint = Color.Lerp(accent, t.Violet, 0.7f);
                    break;
                case Mood.Speaking: // the light pulses with every syllable, ripples leave it, the shards breathe out with the voice
                    energy = 0.55f + 0.5f * _loud; pulse = _loud; spin = 0.6f; orbit = 1.4f; orbitR = 1.35f + 0.22f * _loud; prism = 0.8f;
                    tint = Color.Lerp(accent, Color.white, 0.12f);
                    break;
                default:
                    if (Busy)
                    {
                        // locked or busy: dim steel, the shards turn round it evenly, like a spinner
                        energy = 0.20f + 0.05f * breath; spin = 0.15f; ring = 1f; orbit = 2.2f; orbitR = 1.15f; prism = 0.2f;
                        if (t != null) tint = Color.Lerp(accent, t.Frame, 0.55f);
                    }
                    else if (NeedsYou)
                    {
                        // needs you: amber, a slow heartbeat (two beats, a rest)
                        float ph = Mathf.Repeat(_time / 1.4f, 1f), b1 = (ph - 0.10f) / 0.05f, b2 = (ph - 0.28f) / 0.05f;
                        float beat = Mathf.Exp(-b1 * b1) + 0.6f * Mathf.Exp(-b2 * b2);
                        energy = 0.40f + 0.45f * beat; pulse = 0.35f * beat; prism = 0.45f;
                        tint = Color.Lerp(accent, t != null ? t.Warn : Gold, 0.85f);
                    }
                    break;
            }
            // the wake flare on top: a burst of white light, the shards thrown out, a quick turn
            if (_flareT < 1f)
            {
                _flareT = Mathf.Min(1f, _flareT + dt / 0.65f);
                float f = 1f - _flareT; f *= f;
                energy = Mathf.Max(energy, 0.5f + 0.9f * f);
                pulse = Mathf.Max(pulse, f);
                tint = Color.Lerp(tint, Color.white, 0.55f * f);
                orbitR += 0.55f * f;
                spin += 3f * f;
            }
            // a flash of colour on top (a notification), fading
            if (_flashT < 1f)
            {
                _flashT = Mathf.Min(1f, _flashT + dt / _flashLen);
                float k = Mathf.Sin(Mathf.PI * Mathf.Sqrt(_flashT));
                tint = Color.Lerp(tint, _flash, 0.85f * k);
                energy = Mathf.Max(energy, 0.35f + 0.5f * k);
            }
            // a hand near it or pointing at it: it brightens and leans to the hand
            if (Attention > 0.01f)
            {
                var toHand = AttendTo - transform.position;
                if (toHand.sqrMagnitude > 1e-6f) { aim = Mathf.Max(aim, 0.8f * Attention); aimDir = toHand.normalized; }
                energy = Mathf.Max(energy, 0.35f + 0.35f * Attention);
            }
            // pointing the user somewhere, or casting: it turns to the target for a moment
            if (_pointHold > 0f) { _pointHold -= dt; aim = 1f; aimDir = _pointDir; energy = Mathf.Max(energy, 0.7f); }
            if (_aimHold > 0f) { _aimHold -= dt; aim = 1f; aimDir = _aimDir; energy = Mathf.Max(energy, 0.95f); }
            _kick = Mathf.Max(0f, _kick - dt * 2.6f);
            _energy = Mathf.Lerp(_energy, energy, 1f - Mathf.Exp(-dt * 7f));
            _spinSpeed = Mathf.Lerp(_spinSpeed, spin, UiTheme.Approach(dt, 0.8f));
            _orbitSpeed = Mathf.Lerp(_orbitSpeed, orbit, UiTheme.Approach(dt, 0.8f));
            _orbitR = Mathf.Lerp(_orbitR, orbitR, UiTheme.Approach(dt, 0.3f));
            _ring = Mathf.Lerp(_ring, ring, UiTheme.Approach(dt, 0.45f));
            _point = Mathf.Lerp(_point, _pointHold > 0f ? 1f : 0f, UiTheme.Approach(dt, 0.35f));
            _prism = Mathf.Lerp(_prism, prism, UiTheme.Approach(dt, 0.6f));
            _aim = Mathf.Lerp(_aim, aim, 1f - Mathf.Exp(-dt * (aim > _aim ? 14f : 4f)));
            _tint = Color.Lerp(_tint, tint, 1f - Mathf.Exp(-dt * 8f));
            _pulse = Mathf.Clamp01(Mathf.Max(pulse, _kick));
            _look = Vector3.Slerp(_look, aimDir, 1f - Mathf.Exp(-dt * 10f)).normalized;
            if (_wave < 1f) { _wave = Mathf.Min(1f, _wave + dt / 0.55f); if (_wave >= 1f) _waveStrength = 0f; }

            // ---- ripples: one leaves at each rise of the voice, at most every 0.2 s
            for (int i = 0; i < 4; i++) if (_ripples[i] >= 0f) { _ripples[i] += dt / 0.9f; if (_ripples[i] >= 1f) _ripples[i] = -1f; }
            if (State == Mood.Speaking && _loud > 0.32f && _time - _lastRipple > 0.2f) Ripple();

            // ---- phases (each wrapped: a sine of a huge number is not smooth on a phone GPU)
            _spin = Mathf.Repeat(_spin + dt * _spinSpeed, Mathf.PI * 2f);
            _heart = Mathf.Repeat(_heart + dt * (0.6f + 1.4f * _spinSpeed), Mathf.PI * 2f);
            _prismPhase = Mathf.Repeat(_prismPhase + dt * (0.04f + 0.05f * _spinSpeed), 1f);
            _ringPhase = Mathf.Repeat(_ringPhase + dt * 0.8f * _orbitSpeed, Mathf.PI * 2f);
            _stream = Mathf.Repeat(_stream + dt * 0.9f, 1f);
            for (int i = 0; i < 4; i++)
            {
                _shardPhase[i] = Mathf.Repeat(_shardPhase[i] + dt * _orbitSpeed * (0.55f + 0.12f * i), Mathf.PI * 2f);
                _tumble[i] = Mathf.Repeat(_tumble[i] + dt * (0.9f + 0.35f * i) * (0.5f + 0.5f * _orbitSpeed), Mathf.PI * 2f);
            }
            Apply(toHead);
        }

        /// A ripple leaves it now (the voice; tests).
        public void Ripple()
        {
            int slot = 0;
            for (int i = 0; i < 4; i++) { if (_ripples[i] < 0f) { slot = i; break; } if (_ripples[i] > _ripples[slot]) slot = i; }
            _ripples[slot] = 0f;
            _lastRipple = _time;
        }

        private void Rows(Quaternion q, int[] ids)
        {
            var m = Matrix4x4.Rotate(q);
            for (int r = 0; r < 3; r++) _mat.SetVector(ids[r], m.GetRow(r));
        }

        private static readonly Vector3 HeartAxis = new Vector3(0.3f, 1f, -0.2f).normalized;
        // the shards' own orbits, tilted apart, in a frame that faces the user (so it composes the same from anywhere)
        private static readonly Quaternion[] ShardOrbits =
        {
            Quaternion.AngleAxis(64f, new Vector3(1f, 0.2f, 0f).normalized),
            Quaternion.AngleAxis(-52f, new Vector3(0.3f, 0.1f, 1f).normalized),
            Quaternion.AngleAxis(78f, new Vector3(-0.6f, 0.3f, 0.7f).normalized),
            Quaternion.AngleAxis(-35f, new Vector3(0.8f, -0.2f, -0.5f).normalized),
        };

        private void Apply(Vector3 toHead)
        {
            if (_mat == null) return;
            bool show = _appear > 0.002f;
            if (_mr.enabled != show) _mr.enabled = show;
            if (!show) return;
            // it comes as a point that opens: the radius overshoots a little as it ignites
            float a = _appear, back = 1f + 2.2f * Mathf.Pow(a - 1f, 3f) + 1.2f * Mathf.Pow(a - 1f, 2f);
            float radius = Radius * Mathf.Max(0.02f, back);
            Color lin = _tint.linear, gold = Gold.linear;
            var c = transform.position;
            _mat.SetVector(CenterId, new Vector4(c.x, c.y, c.z, radius));
            _mat.SetVector(StateId, new Vector4(_energy, _pulse, Mathf.Clamp01(a * 1.6f), 1.5f));
            _mat.SetVector(TintId, new Vector4(lin.r, lin.g, lin.b, 1f));
            float m = Mathf.Max(Mathf.Max(lin.r, Mathf.Max(lin.g, lin.b)), 1e-3f); // (three arguments would be the params overload: an array a frame)
            _mat.SetVector(DeepId, new Vector4(0.010f * (lin.r / m * 0.5f + 0.5f), 0.030f * (lin.g / m * 0.5f + 0.5f), 0.075f * (lin.b / m * 0.5f + 0.5f), 1f));
            _mat.SetVector(GoldId, new Vector4(gold.r, gold.g, gold.b, 1f));
            _mat.SetVector(PhaseAId, new Vector4(_prismPhase, _flareT, 0f, 0f));
            _mat.SetVector(PhaseBId, new Vector4(0f, 0f, 0f, _prism));
            // the crystal turns about its long axis, sways a little and leans its tip to what it attends to (all the way
            // when it points the user somewhere); the heart turns the other way
            var sway = Quaternion.Euler(7f * Mathf.Sin(_time * 0.53f), 0f, 9f * Mathf.Sin(_time * 0.41f + 1f));
            var spinQ = Quaternion.AngleAxis(_spin * Mathf.Rad2Deg, Vector3.up);
            float lean = Mathf.Clamp01(_aim * 0.35f + _point * 0.65f);
            var leanQ = Quaternion.Slerp(Quaternion.identity, Quaternion.FromToRotation(Vector3.up, _look), lean);
            Rows(leanQ * sway * spinQ, RotAIds);
            Rows(Quaternion.AngleAxis(-_heart * Mathf.Rad2Deg, HeartAxis) * Quaternion.AngleAxis(Mathf.Repeat(_time * 23f, 360f), Vector3.right), RotBIds);
            _mat.SetVector(LookId, new Vector4(_look.x, _look.y, _look.z, _waveStrength * (1f - _wave * _wave)));
            _mat.SetVector(ShieldId, new Vector4(_wave, HaloExtent, 0f, 0f));
            _mat.SetVector(RipplesId, new Vector4(_ripples[0], _ripples[1], _ripples[2], _ripples[3]));
            // the shards: each on its own orbit; gathered on one ring that faces the user while it listens or is busy;
            // streaming in a line towards a place it points at
            var fwd = -toHead; fwd.y *= 0.35f;
            if (fwd.sqrMagnitude < 1e-6f) fwd = Vector3.forward;
            var frame = Quaternion.LookRotation(fwd.normalized, Vector3.up);
            Vector3 fr = frame * Vector3.right, fu = frame * Vector3.up;
            float open = Mathf.Clamp01(a * 1.4f - 0.3f); // the shards open as it ignites
            Vector4 weights = Vector4.zero, golden = Vector4.zero, tumble = Vector4.zero;
            for (int i = 0; i < 4; i++)
            {
                var q = frame * ShardOrbits[i];
                Vector3 n = q * Vector3.up, u = q * Vector3.right, v = Vector3.Cross(n, u);
                float rho = _orbitR * (1f + 0.07f * i) * Mathf.Lerp(0.5f, 1f, open);
                var p = rho * (u * Mathf.Cos(_shardPhase[i]) + v * Mathf.Sin(_shardPhase[i]));
                float ra = _ringPhase + i * Mathf.PI * 0.5f;
                p = Vector3.Lerp(p, _orbitR * (fr * Mathf.Cos(ra) + fu * Mathf.Sin(ra)), _ring);
                float bright = 1f;
                if (_point > 0.001f)
                {
                    float s = Mathf.Repeat(_stream + i * 0.25f, 1f);
                    p = Vector3.Lerp(p, _look * (1.25f + 1.1f * s), _point);
                    bright = Mathf.Lerp(1f, Mathf.Sin(Mathf.PI * s), _point); // each fades in and out along the line: no jump when it starts again
                }
                bool unread = i < Unread;
                float size = (unread ? 0.19f : 0.14f) * Mathf.Lerp(0.3f, 1f, open);
                _mat.SetVector(MoteIds[i], new Vector4(p.x, p.y, p.z, size));
                weights[i] = open * bright * (unread ? 1.2f : 1f);
                golden[i] = unread ? 1f : 0f;
                tumble[i] = _tumble[i];
            }
            _mat.SetVector(MoteWId, weights);
            _mat.SetVector(MoteGoldId, golden);
            _mat.SetVector(TumbleId, tumble);
            // the tail: a curve from the centre through the two trailing points (radii), at most 2.2 radii long
            float inv = 1f / Mathf.Max(radius, 1e-4f);
            Vector3 p1 = (_lag1 - c) * inv, p2 = (_lag2 - c) * inv;
            float len = p2.magnitude;
            if (len > 2.2f) { float k = 2.2f / len; p1 *= k; p2 *= k; len = 2.2f; }
            float tail = Mathf.Clamp01((len - 0.15f) / 0.9f) * open;
            _mat.SetVector(TailAId, new Vector4(p1.x, p1.y, p1.z, 0.30f));
            _mat.SetVector(TailBId, new Vector4(p2.x, p2.y, p2.z, tail));
            FitBounds();
        }

        /// Self-tests: the material's shader compiled and the renderer shows.
        public bool Drawn => _mr != null && _mr.enabled && _mat != null && _mat.shader != null && _mat.shader.isSupported;
    }
}

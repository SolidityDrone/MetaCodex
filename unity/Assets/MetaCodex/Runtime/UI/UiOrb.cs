using System;
using UnityEngine;

namespace MetaCodex.UI
{
    /// The orb: a sphere of energy that floats by the user. It casts the panels (UiPanelFx: a fan of light from it
    /// draws them), it is where the voice comes from (it pulses with every syllable and sends ripples out), and
    /// what the app has to say appears beside it. An original design, in the spirit of an RTS's energy spheres: a
    /// hot core, two shells of plasma turning against each other inside dark glass, three thin rings (two of warm
    /// gold) that drift like a gyroscope, turn to face what it attends to, and spin up while it thinks.
    /// This component is its body: the look for each mood, the eased motion towards `Home`, and the uniforms of
    /// MetaCodex/Orb (one quad that the shader turns to each eye). Where it floats and what it shows is the shell's
    /// (ShellOrb). It never appears or goes at once: Show(true/false) plays its own ignition.
    public sealed class UiOrb : MonoBehaviour
    {
        public enum Mood { Idle, Listening, Thinking, Speaking }

        /// The quad's half size in radii: room for the halo, the rings and the ripples (2.7 until 0.7.9: 27 % more pixels).
        public const float Extent = 2.3f;
        public static readonly Color Gold = new Color(1f, 0.80f, 0.50f);

        public Transform Head;
        public float Radius = 0.032f;
        public Mood State { get; private set; } = Mood.Idle;
        /// The voice's loudness now, 0..1 (speaking), or the microphone's (listening).
        public float Loudness;
        /// Where it wants to float (world); it eases there and bobs a little round it.
        public Vector3 Home;
        /// Messages the user has not looked at yet (0..4): each rides round the orb as a larger, gold spark.
        public int Unread;
        /// 0..1: a hand is near it or points at it (it brightens and its rings turn to that hand, at `AttendTo`).
        public float Attention;
        public Vector3 AttendTo;
        /// Shown, as far as the app is concerned (it may still be fading).
        public bool Visible => _appearTarget > 0.5f;
        /// 0 gone .. 1 there.
        public float Appear => _appear;
        public Vector3 Position => transform.position;
        /// Where it attends (world, unit): the user, or the panel it casts.
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
        private Vector3 _pos, _vel, _look = Vector3.back, _aimDir = Vector3.back;
        private float _appear, _appearTarget, _energy = 0.35f, _pulse, _kick, _spin = 1f, _aim, _aimHold, _shield, _shieldAt = 1f, _flashT = 1f, _flashLen = 1f, _time, _lastRipple = -10f, _loud;
        private Color _tint = Color.cyan, _flash = Color.white;
        private bool _placed;
        private readonly float[] _phA = new float[3], _phB = new float[3], _ringTilt = new float[3], _ringPhase = new float[3], _mote = new float[4];
        private float _rotA, _rotB;
        private readonly float[] _ripples = { -1f, -1f, -1f, -1f };
        // every input of MetaCodex/Orb is an ordinary material property (no arrays: see the shader's header)
        private static readonly int CenterId = Shader.PropertyToID("_OrbCenter"), StateId = Shader.PropertyToID("_OrbState"), TintId = Shader.PropertyToID("_OrbTint"),
            DeepId = Shader.PropertyToID("_OrbDeep"), GoldId = Shader.PropertyToID("_OrbGold"), PhaseAId = Shader.PropertyToID("_OrbPhaseA"), PhaseBId = Shader.PropertyToID("_OrbPhaseB"),
            LookId = Shader.PropertyToID("_OrbLook"), ShieldId = Shader.PropertyToID("_OrbShield"), RipplesId = Shader.PropertyToID("_OrbRipples"),
            MoteWId = Shader.PropertyToID("_OrbMoteW"), MoteGoldId = Shader.PropertyToID("_OrbMoteGold");
        private static readonly int[] RotAIds = { Shader.PropertyToID("_OrbRotA0"), Shader.PropertyToID("_OrbRotA1"), Shader.PropertyToID("_OrbRotA2") };
        private static readonly int[] RotBIds = { Shader.PropertyToID("_OrbRotB0"), Shader.PropertyToID("_OrbRotB1"), Shader.PropertyToID("_OrbRotB2") };
        private static readonly int[] RingNIds = { Shader.PropertyToID("_OrbRing0N"), Shader.PropertyToID("_OrbRing1N"), Shader.PropertyToID("_OrbRing2N") };
        private static readonly int[] RingUIds = { Shader.PropertyToID("_OrbRing0U"), Shader.PropertyToID("_OrbRing1U"), Shader.PropertyToID("_OrbRing2U") };
        private static readonly int[] RingPIds = { Shader.PropertyToID("_OrbRing0P"), Shader.PropertyToID("_OrbRing1P"), Shader.PropertyToID("_OrbRing2P") };
        private static readonly int[] RingQIds = { Shader.PropertyToID("_OrbRing0Q"), Shader.PropertyToID("_OrbRing1Q"), Shader.PropertyToID("_OrbRing2Q") };
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
            _mesh = new Mesh { name = "orb" };
            _mesh.SetVertices(new[] { new Vector3(-0.5f, -0.5f, 0f), new Vector3(0.5f, -0.5f, 0f), new Vector3(-0.5f, 0.5f, 0f), new Vector3(0.5f, 0.5f, 0f) });
            _mesh.SetTriangles(new[] { 0, 2, 1, 1, 2, 3 }, 0);
            GetComponent<MeshFilter>().sharedMesh = _mesh;
            _mr = GetComponent<MeshRenderer>();
            _mr.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            _mr.receiveShadows = false;
            _mr.lightProbeUsage = UnityEngine.Rendering.LightProbeUsage.Off;
            var sh = Resources.Load<Shader>("Shaders/Orb") ?? Shader.Find("MetaCodex/Orb");
            if (sh != null) _mat = new Material(sh) { name = "orb" };
            _mr.sharedMaterial = _mat;
            _mr.enabled = false;
            for (int i = 0; i < 3; i++) { _ringTilt[i] = i * 1.0f; _ringPhase[i] = 0f; }
            for (int i = 0; i < 4; i++) _mote[i] = i * 1.7f;
            if (UiTheme.Current != null) _tint = UiTheme.Current.Accent;
            FitBounds();
        }

        private void FitBounds()
        {
            // the shader turns the quad to each eye round the transform's place: bounds that hold it from any side
            if (_mesh != null) _mesh.bounds = new Bounds(Vector3.zero, Vector3.one * (2f * Extent * Radius + 0.02f));
        }

        private void OnDestroy()
        {
            if (_mesh != null) { if (Application.isPlaying) Destroy(_mesh); else DestroyImmediate(_mesh); }
            if (_mat != null) { if (Application.isPlaying) Destroy(_mat); else DestroyImmediate(_mat); }
        }

        /// Comes (a spark, the rings open, the sphere ignites) or goes; never at once.
        public void Show(bool on)
        {
            if (on && _appearTarget < 0.5f) { _kick = 1f; _shield = 1f; _shieldAt = 0f; Ripple(); } // it ignites: a flash, a ripple, the shield's wave
            _appearTarget = on ? 1f : 0f;
        }

        /// Puts it at a place at once (the first frame, a recenter): no flight across the room.
        public void Teleport(Vector3 at) { Home = at; _pos = at; _vel = Vector3.zero; _placed = true; transform.position = at; }

        public void SetMood(Mood m) { State = m; }

        /// A moment of colour: a notification (its kind's colour), an answer, an error.
        public void Flash(Color c, float seconds = 1.1f) { _flash = c; _flashT = 0f; _flashLen = Mathf.Max(0.2f, seconds); _kick = Mathf.Max(_kick, 0.7f); _shield = 1f; _shieldAt = 0f; }

        /// It turns to a point it casts a panel at: the rings aim, a kick of light, the shield's wave runs back from there.
        public void Cast(Vector3 target)
        {
            var d = target - transform.position;
            if (d.sqrMagnitude > 1e-6f) _aimDir = d.normalized;
            _aimHold = 0.75f;
            _kick = 1f;
            _shield = 1f; _shieldAt = 0f;
        }

        /// What is left of a closed panel comes home: a small kick, the shield's wave once more.
        public void Absorb() { _kick = Mathf.Max(_kick, 0.8f); _shield = Mathf.Max(_shield, 0.8f); _shieldAt = 0f; }

        private void Update() { Tick(Time.unscaledDeltaTime); }

        /// One frame (self-tests step it: the Editor has no frame loop).
        public void Tick(float dt)
        {
            dt = Mathf.Clamp(dt, 0f, 0.1f);
            _time += dt;
            var t = UiTheme.Current;
            // ---- there or not: it ignites in 0.7 s, goes in 0.4 s
            _appear = Mathf.MoveTowards(_appear, _appearTarget, dt / (_appearTarget > _appear ? 0.7f : 0.4f));
            // ---- flight: eased towards home, then a slow bob round it
            if (!_placed) Teleport(Home);
            _pos = Vector3.SmoothDamp(_pos, Home, ref _vel, SettleTime, MaxSpeed, Mathf.Max(dt, 1e-4f));
            var bob = new Vector3(Mathf.Sin(_time * 1.45f) * 0.003f, Mathf.Sin(_time * 2.2f) * 0.006f, Mathf.Cos(_time * 1.1f) * 0.003f);
            transform.position = _pos + bob;

            // ---- the mood: energy, colour, how fast the plasma and the rings turn, where the rings face
            _loud = Mathf.Lerp(_loud, Mathf.Clamp01(Loudness), 1f - Mathf.Exp(-dt * (Loudness > _loud ? 40f : 9f))); // fast up, slower down
            float energy = 0.35f + 0.05f * Mathf.Sin(_time * 0.9f), spin = 1f, aim = 0f, pulse = 0f; // at rest it breathes
            Color tint = t != null ? t.Accent : Color.cyan;
            Vector3 toHead = Head != null ? (Head.position - transform.position).normalized : Vector3.back;
            Vector3 aimDir = toHead;
            switch (State)
            {
                case Mood.Listening: energy = 0.60f + 0.3f * _loud; spin = 0.6f; aim = 0.9f; pulse = _loud * 0.8f; tint = Color.Lerp(tint, Gold, 0.85f); break; // it turns gold while it listens
                case Mood.Thinking: energy = 0.90f; spin = 3.2f; pulse = 0.25f + 0.15f * Mathf.Sin(_time * 9f); if (t != null) tint = Color.Lerp(tint, t.Violet, 0.7f); break;
                case Mood.Speaking: energy = 0.55f + 0.5f * _loud; spin = 1.5f; pulse = _loud; tint = Color.Lerp(tint, Color.white, 0.12f); break;
            }
            // a flash of colour on top (a notification), fading
            if (_flashT < 1f)
            {
                _flashT = Mathf.Min(1f, _flashT + dt / _flashLen);
                float k = Mathf.Sin(Mathf.PI * Mathf.Sqrt(_flashT));
                tint = Color.Lerp(tint, _flash, 0.85f * k);
                energy = Mathf.Max(energy, 0.35f + 0.5f * k);
            }
            // a hand near it or pointing at it: it brightens and turns its rings to the hand
            if (Attention > 0.01f)
            {
                var toHand = AttendTo - transform.position;
                if (toHand.sqrMagnitude > 1e-6f) { aim = Mathf.Max(aim, 0.8f * Attention); aimDir = toHand.normalized; }
                energy = Mathf.Max(energy, 0.35f + 0.35f * Attention);
            }
            // casting: the rings aim at the target for a moment
            if (_aimHold > 0f) { _aimHold -= dt; aim = 1f; aimDir = _aimDir; energy = Mathf.Max(energy, 0.95f); }
            _kick = Mathf.Max(0f, _kick - dt * 2.6f);
            float ease = 1f - Mathf.Exp(-dt * 7f);
            _energy = Mathf.Lerp(_energy, energy, ease);
            _spin = Mathf.Lerp(_spin, spin, 1f - Mathf.Exp(-dt * 3f));
            _aim = Mathf.Lerp(_aim, aim, 1f - Mathf.Exp(-dt * (aim > _aim ? 14f : 4f)));
            _tint = Color.Lerp(_tint, tint, 1f - Mathf.Exp(-dt * 8f));
            _pulse = Mathf.Clamp01(Mathf.Max(pulse, _kick));
            _look = Vector3.Slerp(_look, aimDir, 1f - Mathf.Exp(-dt * 10f)).normalized;
            if (_shieldAt < 1f) { _shieldAt = Mathf.Min(1f, _shieldAt + dt / 0.8f); if (_shieldAt >= 1f) _shield = 0f; }

            // ---- ripples: one leaves at each rise of the voice, at most every 0.2 s
            for (int i = 0; i < 4; i++) if (_ripples[i] >= 0f) { _ripples[i] += dt / 0.9f; if (_ripples[i] >= 1f) _ripples[i] = -1f; }
            if (State == Mood.Speaking && _loud > 0.32f && _time - _lastRipple > 0.2f) Ripple();

            // ---- phases (each wrapped: a sine of a huge number is not smooth on a phone GPU)
            float w = dt * _spin;
            Wrap(ref _phA[0], w * 0.9f); Wrap(ref _phA[1], w * 0.7f); Wrap(ref _phA[2], w * 1.1f);
            Wrap(ref _phB[0], w * 0.6f); Wrap(ref _phB[1], w * 0.5f); Wrap(ref _phB[2], w * 0.8f);
            Wrap(ref _rotA, w * 0.45f); Wrap(ref _rotB, -w * 0.26f);
            Wrap(ref _ringTilt[0], w * 0.31f); Wrap(ref _ringTilt[1], w * 0.23f); Wrap(ref _ringTilt[2], w * 0.19f);
            float rs = dt * (1f + (_spin - 1f) * 2.2f);
            _ringPhase[0] = Mathf.Repeat(_ringPhase[0] + rs * 0.05f, 1f); _ringPhase[1] = Mathf.Repeat(_ringPhase[1] - rs * 0.08f, 1f); _ringPhase[2] = Mathf.Repeat(_ringPhase[2] + rs * 0.03f, 1f);
            for (int i = 0; i < 4; i++) Wrap(ref _mote[i], w * (0.9f + 0.23f * i));
            Apply(toHead);
        }

        private static void Wrap(ref float a, float by) { a = Mathf.Repeat(a + by, Mathf.PI * 2f); }

        /// A ripple leaves the orb now (the voice; tests).
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

        private static readonly Vector3 AxisA = new Vector3(0.2f, 1f, 0.1f).normalized, AxisB = new Vector3(-0.4f, 1f, 0.3f).normalized;
        private static readonly Quaternion[] MoteOrbits =
        {
            Quaternion.AngleAxis(0.6f * Mathf.Rad2Deg, new Vector3(1f, 0.3f, 0f).normalized),
            Quaternion.AngleAxis(1.1f * Mathf.Rad2Deg, new Vector3(Mathf.Cos(2.1f), 0.3f, Mathf.Sin(2.1f)).normalized),
            Quaternion.AngleAxis(1.6f * Mathf.Rad2Deg, new Vector3(Mathf.Cos(4.2f), 0.3f, Mathf.Sin(4.2f)).normalized),
            Quaternion.AngleAxis(2.1f * Mathf.Rad2Deg, new Vector3(Mathf.Cos(6.3f), 0.3f, Mathf.Sin(6.3f)).normalized),
        };

        // (tilt axis, base tilt, sway, radius, tube width, gold, dashes, duty, brightness): ring 0 is the halo ring that
        // faces the user, ring 1 a fine dashed ring across it, ring 2 a faint wide one with a single bright third
        private static readonly (Vector3 axis, float tilt, float sway, float rho, float width, float gold, float segs, float duty, float bright)[] RingDefs =
        {
            (new Vector3(1f, 0f, 0.3f), 1.15f, 0.25f, 1.30f, 0.020f, 1f, 3f, 0.86f, 1.15f),
            (new Vector3(0.2f, 0f, 1f), 0.55f, 0.30f, 1.46f, 0.012f, 0f, 36f, 0.50f, 0.85f),
            (new Vector3(1f, 0f, -1f), -0.75f, 0.20f, 1.64f, 0.009f, 1f, 1f, 0.42f, 0.72f),
        };

        private void Apply(Vector3 toHead)
        {
            if (_mat == null) return;
            bool show = _appear > 0.002f;
            if (_mr.enabled != show) _mr.enabled = show;
            if (!show) return;
            var t = UiTheme.Current;
            // it comes as a point that opens: the radius overshoots a little as it ignites
            float a = _appear, back = 1f + 2.2f * Mathf.Pow(a - 1f, 3f) + 1.2f * Mathf.Pow(a - 1f, 2f);
            float radius = Radius * Mathf.Max(0.02f, back);
            Color lin = _tint.linear, gold = Gold.linear;
            var c = transform.position;
            _mat.SetVector(CenterId, new Vector4(c.x, c.y, c.z, radius));
            _mat.SetVector(StateId, new Vector4(_energy, _pulse, Mathf.Clamp01(a * 1.6f), 1.5f));
            _mat.SetVector(TintId, new Vector4(lin.r, lin.g, lin.b, 1f));
            float m = Mathf.Max(lin.r, Mathf.Max(lin.g, lin.b), 1e-3f);
            _mat.SetVector(DeepId, new Vector4(0.010f * (lin.r / m * 0.5f + 0.5f), 0.030f * (lin.g / m * 0.5f + 0.5f), 0.075f * (lin.b / m * 0.5f + 0.5f), 1f));
            _mat.SetVector(GoldId, new Vector4(gold.r, gold.g, gold.b, 1f));
            _mat.SetVector(PhaseAId, new Vector4(_phA[0], _phA[1], _phA[2], 1.4f));
            _mat.SetVector(PhaseBId, new Vector4(_phB[0] + 2f, _phB[1] + 1f, _phB[2], -0.9f));
            Rows(Quaternion.AngleAxis(_rotA * Mathf.Rad2Deg, AxisA), RotAIds);
            Rows(Quaternion.AngleAxis(_rotB * Mathf.Rad2Deg, AxisB), RotBIds);
            _mat.SetVector(LookId, new Vector4(_look.x, _look.y, _look.z, _shield * (1f - _shieldAt * _shieldAt)));
            _mat.SetVector(ShieldId, new Vector4(_shieldAt, Extent, 0f, 0f));
            _mat.SetVector(RipplesId, new Vector4(_ripples[0], _ripples[1], _ripples[2], _ripples[3]));
            // the rings live in a frame that faces the user (z from the user to the orb), so they compose the same from
            // wherever the user stands; aiming turns their normals to what the orb attends to
            var fwd = -toHead; fwd.y *= 0.35f;
            if (fwd.sqrMagnitude < 1e-6f) fwd = Vector3.forward;
            var frame = Quaternion.LookRotation(fwd.normalized, Vector3.up);
            float open = Mathf.Clamp01(a * 1.4f - 0.3f); // the rings open as it ignites
            for (int i = 0; i < 3; i++)
            {
                var d = RingDefs[i];
                var q = frame * Quaternion.AngleAxis((d.tilt + d.sway * Mathf.Sin(_ringTilt[i] + i)) * Mathf.Rad2Deg, d.axis.normalized);
                Vector3 n = q * Vector3.up, u = q * Vector3.right;
                if (_aim > 0.001f)
                {
                    // the nearer of the two ways a ring can face the target
                    var target = Vector3.Dot(n, _look) >= 0f ? _look : -_look;
                    n = Vector3.Slerp(n, target, _aim).normalized;
                    u = (u - n * Vector3.Dot(u, n));
                    u = u.sqrMagnitude < 1e-6f ? Vector3.Cross(n, Vector3.up).normalized : u.normalized;
                }
                float rho = d.rho * (1f + 0.05f * _pulse) * Mathf.Lerp(0.55f, 1f, open) - (i == 0 ? 0.06f : i == 2 ? -0.10f : 0f) * _aim;
                if (u.sqrMagnitude < 1e-6f) u = Mathf.Abs(n.x) < 0.9f ? Vector3.Cross(n, Vector3.right).normalized : Vector3.Cross(n, Vector3.forward).normalized; // never zero (the dashes' angle)
                _mat.SetVector(RingNIds[i], n);
                _mat.SetVector(RingUIds[i], u);
                _mat.SetVector(RingPIds[i], new Vector4(rho, d.width, _ringPhase[i], d.bright * open * (1f + 0.6f * _pulse)));
                _mat.SetVector(RingQIds[i], new Vector4(d.segs, d.duty, d.gold, 0f));
            }
            // four motes on their own orbits; a message not read yet makes its mote larger and gold
            Vector4 weights = Vector4.zero, golden = Vector4.zero;
            for (int i = 0; i < 4; i++)
            {
                var q = MoteOrbits[i];
                Vector3 n = q * Vector3.up, u = q * Vector3.right, v = Vector3.Cross(n, u);
                float rho = 1.16f + 0.13f * i;
                bool unread = i < Unread;
                float size = unread ? 0.046f : 0.030f;
                var p = rho * (u * Mathf.Cos(_mote[i]) + v * Mathf.Sin(_mote[i]));
                _mat.SetVector(MoteIds[i], new Vector4(p.x, p.y, p.z, size * size));
                weights[i] = open * (unread ? 1f : 1.25f);
                golden[i] = unread ? 1f : 0f;
            }
            _mat.SetVector(MoteWId, weights);
            _mat.SetVector(MoteGoldId, golden);
            FitBounds();
        }

        /// Self-tests: the material's shader compiled and the renderer shows.
        public bool Drawn => _mr != null && _mr.enabled && _mat != null && _mat.shader != null && _mat.shader.isSupported;
    }
}

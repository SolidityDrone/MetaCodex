using System;
using System.Collections.Generic;
using System.Globalization;
using MetaCodex.UI;
using Newtonsoft.Json.Linq;
using UnityEngine;

namespace MetaCodex.Shell
{
    /// A small physics stage under an agent view (component "scene3d"): Unity rigid bodies on a plate in front
    /// of the view panel, pushed by your index fingertips. The stage follows its panel (bodies are carried along
    /// when the panel moves), gravity is per stage (world down), and RUN / PAUSE / RESET come from the panel.
    ///
    /// Spec: {"type":"scene3d", "size":[0.5,0.32], "gravity":[0,-9.81,0], "walls":true, "timeScale":1,
    ///        "bodies":[{"shape":"sphere|box|capsule|cylinder", "size":0.04 | [x,y,z], "pos":[x,y,z], "vel":[..],
    ///                   "spin":[..], "mass":1, "color":"#3987E5", "bounce":0.6, "friction":0.4, "static":false,
    ///                   "count":1, "spread":0.1, "label":"ball"}]}
    /// Positions are metres from the plate centre (y up, z away from you).
    public sealed class ViewStage : MonoBehaviour
    {
        public const int MaxBodies = 160;
        public bool Running { get; private set; } = true;
        public int Count => _bodies.Count;
        public float Width { get; private set; } = 0.5f;
        public float Depth { get; private set; } = 0.32f;
        /// World positions of the index fingertips (null when that hand is not tracked).
        public Func<int, Vector3?> Fingertip;

        private sealed class Body { public Rigidbody Rb; public Vector3 P0, V0, W0; public Quaternion R0; public Vector3 SavedV, SavedW; }
        private readonly List<Body> _bodies = new List<Body>();
        private readonly Rigidbody[] _tips = new Rigidbody[2];
        private Vector3 _gravity = new Vector3(0f, -9.81f, 0f);
        private float _timeScale = 1f;
        private JObject _spec;
        private Transform _content;
        private Vector3 _lastPos;
        private Quaternion _lastRot;
        private static Material _mat;
        private MaterialPropertyBlock _mpb;

        public static Material SharedMaterial()
        {
            if (_mat != null) return _mat;
            var sh = Resources.Load<Shader>("Shaders/StageLit") ?? Shader.Find("MetaCodex/StageLit");
            _mat = new Material(sh != null ? sh : Shader.Find("Universal Render Pipeline/Unlit")) { name = "stage bodies", enableInstancing = true };
            return _mat;
        }

        public void Build(JObject spec)
        {
            _spec = spec ?? new JObject();
            Clear();
            _mpb = _mpb ?? new MaterialPropertyBlock();
            var size = Floats(_spec["size"], 2);
            Width = size != null ? Mathf.Clamp(size[0], 0.2f, 1.2f) : 0.5f;
            Depth = size != null && size.Length > 1 ? Mathf.Clamp(size[1], 0.15f, 0.8f) : 0.32f;
            var g = Floats(_spec["gravity"], 3);
            _gravity = g != null ? new Vector3(g[0], g[1], g[2]) : new Vector3(0f, -9.81f, 0f);
            _timeScale = Mathf.Clamp(F(_spec["timeScale"], 1f), 0.05f, 4f);
            _content = new GameObject("stage-content").transform;
            _content.SetParent(transform, false);

            // the plate (a thin, dark slab) and invisible walls so bodies stay on it
            var plate = Prim(PrimitiveType.Cube, "plate", new Vector3(0f, -0.004f, 0f), new Vector3(Width, 0.008f, Depth), UiTheme.Hex(0x23262E));
            plate.GetComponent<Collider>().material = PhysMat(0.3f, 0.6f);
            if (_spec["walls"]?.Type != JTokenType.Boolean || (bool)_spec["walls"])
            {
                float h = 0.14f, t = 0.02f;
                Wall(new Vector3(0f, h / 2, Depth / 2 + t / 2), new Vector3(Width + 2 * t, h, t));
                Wall(new Vector3(0f, h / 2, -Depth / 2 - t / 2), new Vector3(Width + 2 * t, h, t));
                Wall(new Vector3(Width / 2 + t / 2, h / 2, 0f), new Vector3(t, h, Depth));
                Wall(new Vector3(-Width / 2 - t / 2, h / 2, 0f), new Vector3(t, h, Depth));
                // faint rim so the edge of the stage is visible
                Prim(PrimitiveType.Cube, "rim-front", new Vector3(0f, 0.001f, -Depth / 2), new Vector3(Width, 0.003f, 0.003f), UiTheme.Hex(0x5B9BFF), false);
            }
            int colorIndex = 0;
            foreach (var b in _spec["bodies"] as JArray ?? new JArray())
            {
                if (!(b is JObject bo)) continue;
                int count = Mathf.Clamp((int)F(bo["count"], 1f), 1, MaxBodies);
                float spread = F(bo["spread"], 0.06f);
                for (int k = 0; k < count && _bodies.Count < MaxBodies; k++)
                {
                    var jitter = count > 1 ? new Vector3(Hash(k * 3 + 1) - 0.5f, Hash(k * 3 + 2) * 0.5f, Hash(k * 3 + 3) - 0.5f) * spread : Vector3.zero;
                    AddBody(bo, jitter, colorIndex);
                }
                colorIndex++;
            }
            for (int h = 0; h < 2; h++)
            {
                var tip = new GameObject("fingertip-" + h);
                tip.transform.SetParent(transform, false);
                var sc = tip.AddComponent<SphereCollider>();
                sc.radius = 0.011f;
                var rb = tip.AddComponent<Rigidbody>();
                rb.isKinematic = true;
                rb.interpolation = RigidbodyInterpolation.None;
                _tips[h] = rb;
                tip.SetActive(false);
            }
            _lastPos = transform.position;
            _lastRot = transform.rotation;
            SetRunning(true);
        }

        private static float Hash(int i) { float x = Mathf.Sin(i * 12.9898f) * 43758.5453f; return x - Mathf.Floor(x); }

        private void AddBody(JObject b, Vector3 jitter, int colorIndex)
        {
            string shape = ((string)b["shape"] ?? "sphere").ToLowerInvariant();
            var type = shape == "box" || shape == "cube" ? PrimitiveType.Cube : shape == "capsule" ? PrimitiveType.Capsule : shape == "cylinder" ? PrimitiveType.Cylinder : PrimitiveType.Sphere;
            Vector3 size;
            var sz = Floats(b["size"], 3);
            if (sz != null && sz.Length >= 3) size = new Vector3(sz[0], sz[1], sz[2]);
            else { float s = F(b["size"], 0.04f); size = type == PrimitiveType.Capsule || type == PrimitiveType.Cylinder ? new Vector3(s, s, s) : Vector3.one * s; }
            size = Vector3.Max(Vector3.one * 0.006f, Vector3.Min(size, Vector3.one * 0.4f));
            var pos = Floats(b["pos"], 3);
            var p = (pos != null ? new Vector3(pos[0], pos[1], pos[2]) : new Vector3(0f, 0.1f, 0f)) + jitter;
            var color = UiViz.Tone((string)b["color"], UiViz.Series(colorIndex));
            var go = Prim(type, (string)b["label"] ?? shape, p, size, color);
            go.GetComponent<Collider>().material = PhysMat(F(b["bounce"], 0.5f), F(b["friction"], 0.4f));
            var rb = go.AddComponent<Rigidbody>();
            rb.mass = Mathf.Clamp(F(b["mass"], 1f), 0.01f, 1000f);
            rb.useGravity = false; // per-stage gravity, applied in FixedUpdate
            rb.interpolation = RigidbodyInterpolation.Interpolate;
            rb.collisionDetectionMode = CollisionDetectionMode.ContinuousSpeculative;
            rb.linearDamping = F(b["drag"], 0.05f);
            rb.angularDamping = 0.1f;
            bool isStatic = b["static"]?.Type == JTokenType.Boolean && (bool)b["static"];
            rb.isKinematic = isStatic;
            var v = Floats(b["vel"], 3);
            var w = Floats(b["spin"], 3);
            var body = new Body
            {
                Rb = rb,
                P0 = go.transform.localPosition,
                R0 = go.transform.localRotation,
                V0 = v != null ? new Vector3(v[0], v[1], v[2]) : Vector3.zero,
                W0 = w != null ? new Vector3(w[0], w[1], w[2]) : Vector3.zero,
            };
            if (!isStatic) { rb.linearVelocity = transform.rotation * body.V0; rb.angularVelocity = transform.rotation * body.W0; }
            _bodies.Add(body);
        }

        private GameObject Prim(PrimitiveType type, string name, Vector3 localPos, Vector3 size, Color color, bool collider = true)
        {
            var go = GameObject.CreatePrimitive(type);
            go.name = name;
            go.transform.SetParent(_content, false);
            go.transform.localPosition = localPos;
            go.transform.localRotation = Quaternion.identity;
            // primitives: sphere/cube 1 unit; capsule 1 wide x 2 tall; cylinder 1 wide x 2 tall
            go.transform.localScale = type == PrimitiveType.Capsule || type == PrimitiveType.Cylinder ? new Vector3(size.x, size.y * 0.5f, size.z) : size;
            if (!collider) { var c = go.GetComponent<Collider>(); if (c != null) Kill(c); }
            var r = go.GetComponent<MeshRenderer>();
            r.sharedMaterial = SharedMaterial();
            r.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            r.receiveShadows = false;
            _mpb.SetColor("_Color", color);
            r.SetPropertyBlock(_mpb);
            return go;
        }

        private void Wall(Vector3 localPos, Vector3 size)
        {
            var go = new GameObject("wall");
            go.transform.SetParent(_content, false);
            go.transform.localPosition = localPos;
            var bc = go.AddComponent<BoxCollider>();
            bc.size = size;
            bc.material = PhysMat(0.5f, 0.2f);
        }

        private static readonly Dictionary<(float, float), PhysicsMaterial> Mats = new Dictionary<(float, float), PhysicsMaterial>();
        private static PhysicsMaterial PhysMat(float bounce, float friction)
        {
            var key = (Mathf.Round(Mathf.Clamp01(bounce) * 20f) / 20f, Mathf.Round(Mathf.Clamp(friction, 0f, 2f) * 20f) / 20f);
            if (Mats.TryGetValue(key, out var m) && m != null) return m;
            m = new PhysicsMaterial("stage") { bounciness = key.Item1, dynamicFriction = key.Item2, staticFriction = key.Item2, bounceCombine = PhysicsMaterialCombine.Maximum, frictionCombine = PhysicsMaterialCombine.Average };
            Mats[key] = m;
            return m;
        }

        public void SetRunning(bool on)
        {
            Running = on;
            if (!on)
            {
                foreach (var b in _bodies)
                {
                    if (b.Rb == null || b.Rb.isKinematic) continue; // static bodies stay as they are
                    b.SavedV = b.Rb.linearVelocity;
                    b.SavedW = b.Rb.angularVelocity;
                    b.Rb.isKinematic = true;
                    _paused.Add(b);
                }
                return;
            }
            foreach (var b in _paused)
            {
                if (b.Rb == null) continue;
                b.Rb.isKinematic = false;
                b.Rb.linearVelocity = b.SavedV;
                b.Rb.angularVelocity = b.SavedW;
            }
            _paused.Clear();
        }

        private readonly List<Body> _paused = new List<Body>();

        public void ResetScene() => Build(_spec);

        private void Clear()
        {
            _bodies.Clear();
            _paused.Clear();
            for (int i = transform.childCount - 1; i >= 0; i--) Kill(transform.GetChild(i).gameObject);
            _tips[0] = _tips[1] = null;
        }

        private static void Kill(UnityEngine.Object o)
        {
            if (o == null) return;
            if (Application.isPlaying) Destroy(o); else DestroyImmediate(o);
        }

        private void FixedUpdate() => Step(Time.fixedDeltaTime);

        /// One physics step's own work: carry bodies along when the panel moved, gravity, fingertips.
        public void Step(float dt)
        {
            // the panel moved (dragged, carousel, opened): every body keeps where it is on the stage. Its transform, a child
            // of the stage, moved with it already: put the physics body there. Adding the stage's move to Rb.position
            // moved a body twice whenever its physics pose had been synced since (it then fell off the plate); the
            // transform is right either way.
            if (transform.position != _lastPos || transform.rotation != _lastRot)
            {
                var dq = transform.rotation * Quaternion.Inverse(_lastRot);
                foreach (var b in _bodies)
                {
                    if (b.Rb == null) continue;
                    var tr = b.Rb.transform;
                    b.Rb.position = tr.position;
                    b.Rb.rotation = tr.rotation;
                    if (!b.Rb.isKinematic) { b.Rb.linearVelocity = dq * b.Rb.linearVelocity; b.Rb.angularVelocity = dq * b.Rb.angularVelocity; }
                }
                _lastPos = transform.position;
                _lastRot = transform.rotation;
            }
            if (!Running) return;
            foreach (var b in _bodies)
                if (b.Rb != null && !b.Rb.isKinematic) b.Rb.AddForce(_gravity * _timeScale * _timeScale, ForceMode.Acceleration);
            for (int h = 0; h < 2; h++)
            {
                var tip = _tips[h];
                if (tip == null) continue;
                var p = Fingertip?.Invoke(h);
                bool near = p.HasValue && Vector3.Distance(p.Value, transform.position) < Mathf.Max(Width, Depth);
                if (tip.gameObject.activeSelf != near) { tip.gameObject.SetActive(near); if (near) tip.position = p.Value; }
                if (near) tip.MovePosition(p.Value);
            }
        }

        private static float F(JToken t, float def)
        {
            if (t == null) return def;
            if (t.Type == JTokenType.Float || t.Type == JTokenType.Integer) return (float)t;
            if (t.Type == JTokenType.String && float.TryParse((string)t, NumberStyles.Float, CultureInfo.InvariantCulture, out var f)) return f;
            if (t is JArray a && a.Count > 0) return F(a[0], def);
            return def;
        }

        private static float[] Floats(JToken t, int n)
        {
            if (!(t is JArray a) || a.Count == 0) return null;
            var r = new float[Math.Min(n, a.Count)];
            for (int i = 0; i < r.Length; i++) r[i] = F(a[i], 0f);
            return r;
        }
    }
}

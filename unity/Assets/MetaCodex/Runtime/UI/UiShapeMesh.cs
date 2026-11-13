using System.Collections.Generic;
using TMPro;
using UnityEngine;
using UnityEngine.TextCore.LowLevel;

namespace MetaCodex.UI
{
    /// All shapes of one panel in one mesh (one draw call). Elements keep the index of their shapes and
    /// change state/tint in place; the mesh is re-uploaded once per frame at most, only when something changed.
    /// A change of colour or state only (hover, press, selection, the flash after a click) rewrites that shape's
    /// colours and states and uploads those two streams; only a change of size, place, kind or visibility rebuilds
    /// the geometry (0.7.10: every hover and every frame of a click's flash rebuilt the whole panel, thousands of
    /// vertices on a big one). Setting a shape, a rect or a state to what it already is changes nothing.
    [RequireComponent(typeof(MeshFilter), typeof(MeshRenderer))]
    public sealed class UiShapeMesh : MonoBehaviour
    {
        private readonly List<UiShape> _shapes = new List<UiShape>();
        private Mesh _mesh;
        private bool _dirty;
        private readonly List<Vector3> _v = new List<Vector3>();
        private readonly List<Color> _c = new List<Color>();
        private readonly List<Vector2> _uv = new List<Vector2>();
        private readonly List<Vector4> _shape = new List<Vector4>();
        private readonly List<Vector4> _style = new List<Vector4>();
        private readonly List<int> _tris = new List<int>();
        // where each shape's vertices are in the lists (-1: not drawn), and the shapes whose colour or state changed
        private readonly List<int> _first = new List<int>(), _count = new List<int>();
        private readonly List<int> _restyle = new List<int>();
        private bool _layout = true;

        public int Count => _shapes.Count;

        /// Panel-local point -> its place on the bend cylinder (identity when flat). The shaders bend the same way
        /// (UiPanel: the bend is drawn on the GPU); this is for pointers and placement.
        public static Vector3 Bend(Vector3 v, float radius)
        {
            if (radius <= 0f) return v;
            float a = v.x / radius;
            return new Vector3(radius * Mathf.Sin(a), v.y, v.z - radius * (1f - Mathf.Cos(a)));
        }

        /// `material` is the theme's SciFiUI material. `bodyDepth`: this mesh holds a panel body (UiPanel with a frame),
        /// so it also draws the body's depth before any UI colour (DepthFirst); reticles and rails do not.
        public void Init(Material material, bool bodyDepth = true)
        {
            _mesh = new Mesh { name = "ui shapes" };
            _mesh.MarkDynamic();
            GetComponent<MeshFilter>().sharedMesh = _mesh;
            var r = GetComponent<MeshRenderer>();
            EnsureShaderTextures();
            var mats = MaterialsFor(material);
            if (mats == null) r.sharedMaterial = material;
            else if (bodyDepth) r.sharedMaterials = new[] { mats.Colour, mats.Depth }; // one sub-mesh: drawn once per material
            else r.sharedMaterial = mats.Colour;
            r.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            r.receiveShadows = false;
        }

        // ------------------------------------------------------------------ the shader's shared data

        // Panel bodies hide what is behind them (SciFiUI's PanelDepth pass writes their depth where they are opaque). The
        // pass ran right before each panel's colour, and transparent panels draw back to front, so it hid only what
        // came later (texts, icons) and the shapes of windows behind still ran the whole shader under it. Now each
        // renderer has two copies of the theme material: Colour (the colour pass only, the shader's queue) and Depth
        // (the depth pass only, DepthFirstQueue: before every SciFiUI colour draw), so the GPU's depth test rejects the
        // hidden pixels of windows behind before their fragment shader runs. The copies follow the theme material's
        // values (PANEL OPACITY, accent) before each camera renders.
        public const int DepthFirstQueue = 2989; // Transparent-11: after the wrist menu (2980, 2981), before SciFiUI (2990)

        public sealed class Materials { public Material Source, Colour, Depth; }
        private static readonly List<Materials> MaterialSets = new List<Materials>();
        private static readonly int OpacityId = Shader.PropertyToID("_Opacity"), AccentId = Shader.PropertyToID("_Accent");
        private static bool _syncHooked;

        /// The colour and depth-first copies of a SciFiUI material (made once per source material; null for none).
        public static Materials MaterialsFor(Material source)
        {
            if (source == null || source.shader == null) return null;
            foreach (var m in MaterialSets) if (m.Source == source && m.Colour != null && m.Depth != null) return m;
            var set = new Materials
            {
                Source = source,
                Colour = new Material(source) { name = source.name + " (colour)", hideFlags = HideFlags.DontSave },
                Depth = new Material(source) { name = source.name + " (body depth)", hideFlags = HideFlags.DontSave, renderQueue = DepthFirstQueue },
            };
            // SetShaderPassEnabled takes the pass's LightMode tag: PanelDepth = SRPDefaultUnlit, the colour = UniversalForward
            set.Colour.SetShaderPassEnabled("SRPDefaultUnlit", false);
            set.Depth.SetShaderPassEnabled("UniversalForward", false);
            MaterialSets.RemoveAll(m => m.Source == null || m.Source == source);
            MaterialSets.Add(set);
            if (!_syncHooked)
            {
                _syncHooked = true;
                UnityEngine.Rendering.RenderPipelineManager.beginCameraRendering += (_, __) => SyncMaterials();
            }
            return set;
        }

        /// Copies the values the shader reads per material from each theme material to its copies (only what changed).
        public static void SyncMaterials()
        {
            foreach (var m in MaterialSets)
            {
                if (m.Source == null || m.Colour == null || m.Depth == null) continue;
                float o = m.Source.GetFloat(OpacityId);
                if (m.Colour.GetFloat(OpacityId) != o) { m.Colour.SetFloat(OpacityId, o); m.Depth.SetFloat(OpacityId, o); }
                var acc = m.Source.GetVector(AccentId);
                if (m.Colour.GetVector(AccentId) != acc) { m.Colour.SetVector(AccentId, acc); m.Depth.SetVector(AccentId, acc); }
            }
        }

        // Fields the shader used to compute per pixel, made once at the first Init (about 350 KB with mips + 28 KB) and
        // set as globals:
        //   _McxStoneNoise  the stone grain: 0.5 x value noise of 6.0 mm cells + 0.3 x 2.2 mm + 0.2 x 0.9 mm, tiling every
        //                   0.132 m (22, 60 and 147 cells), 512 x 512, mipmapped (SciFiUI StoneGrain, kStoneTile)
        //   _McxHexField    one period of the glass's hexagon lattice (1 x sqrt(3) cells): the distance to the cell's
        //                   centre x 2 (0 centre .. 1 border), 128 x 222, bilinear (SciFiUI HexDist; HexCell is the formula)
        private static Texture2D _stoneNoise, _hexField;
        private static readonly int StoneNoiseId = Shader.PropertyToID("_McxStoneNoise"), HexFieldId = Shader.PropertyToID("_McxHexField");
        public const int StoneNoiseSize = 512, HexFieldWidth = 128, HexFieldHeight = 222;

        public static void EnsureShaderTextures()
        {
            if (_stoneNoise == null) _stoneNoise = MakeStoneNoise();
            if (_hexField == null) _hexField = MakeHexField();
            Shader.SetGlobalTexture(StoneNoiseId, _stoneNoise);
            Shader.SetGlobalTexture(HexFieldId, _hexField);
        }

        private static Texture2D MakeStoneNoise()
        {
            const int n = StoneNoiseSize;
            int[] cells = { 22, 60, 147 };
            float[] weights = { 0.5f, 0.3f, 0.2f };
            var sum = new float[n * n];
            var x0 = new int[n]; var x1 = new int[n]; var tx = new float[n];
            for (int o = 0; o < cells.Length; o++)
            {
                int c = cells[o];
                var lattice = new float[c * c];
                for (int j = 0; j < c; j++) for (int i = 0; i < c; i++) lattice[j * c + i] = Hash01(i, j, o);
                for (int x = 0; x < n; x++)
                {
                    float f = (x + 0.5f) * c / n;
                    int i0 = (int)f;
                    float u = f - i0;
                    x0[x] = i0 % c; x1[x] = (i0 + 1) % c; tx[x] = u * u * (3f - 2f * u);
                }
                float w = weights[o];
                for (int y = 0; y < n; y++)
                {
                    float f = (y + 0.5f) * c / n;
                    int j0 = (int)f;
                    float v = f - j0;
                    float ty = v * v * (3f - 2f * v);
                    int r0 = (j0 % c) * c, r1 = ((j0 + 1) % c) * c;
                    int row = y * n;
                    for (int x = 0; x < n; x++)
                    {
                        float a = lattice[r0 + x0[x]], b = lattice[r0 + x1[x]], cc = lattice[r1 + x0[x]], d = lattice[r1 + x1[x]];
                        float top = a + (b - a) * tx[x], bottom = cc + (d - cc) * tx[x];
                        sum[row + x] += w * (top + (bottom - top) * ty);
                    }
                }
            }
            var bytes = new byte[n * n];
            for (int k = 0; k < bytes.Length; k++) bytes[k] = (byte)Mathf.Clamp(Mathf.RoundToInt(sum[k] * 255f), 0, 255);
            var tex = new Texture2D(n, n, TextureFormat.R8, true, true)
            {
                name = "ui stone grain", wrapMode = TextureWrapMode.Repeat, filterMode = FilterMode.Trilinear, anisoLevel = 0, hideFlags = HideFlags.DontSave,
            };
            tex.SetPixelData(bytes, 0);
            tex.Apply(true, true);
            return tex;
        }

        private static Texture2D MakeHexField()
        {
            const int w = HexFieldWidth, h = HexFieldHeight;
            const float sy = 1.7320508f;
            var bytes = new byte[w * h];
            for (int y = 0; y < h; y++)
            {
                float v = (y + 0.5f) / h;
                for (int x = 0; x < w; x++)
                {
                    float u = (x + 0.5f) / w;
                    // SciFiUI HexCell with q / s = (u, v): a = (frac(q / s) - 0.5) s, b = (frac(q / s - 0.5) - 0.5) s
                    float ax = u - 0.5f, ay = (v - 0.5f) * sy;
                    float bx = Frac(u - 0.5f) - 0.5f, by = (Frac(v - 0.5f) - 0.5f) * sy;
                    bool useA = ax * ax + ay * ay < bx * bx + by * by;
                    float gx = Mathf.Abs(useA ? ax : bx), gy = Mathf.Abs(useA ? ay : by);
                    float hd = Mathf.Max(gx * 0.5f + gy * 0.8660254f, gx); // 0 at the centre .. 0.5 at the border
                    bytes[y * w + x] = (byte)Mathf.Clamp(Mathf.RoundToInt(hd * 2f * 255f), 0, 255);
                }
            }
            var tex = new Texture2D(w, h, TextureFormat.R8, false, true)
            {
                name = "ui hex field", wrapMode = TextureWrapMode.Repeat, filterMode = FilterMode.Bilinear, anisoLevel = 0, hideFlags = HideFlags.DontSave,
            };
            tex.SetPixelData(bytes, 0);
            tex.Apply(false, true);
            return tex;
        }

        private static float Frac(float x) => x - Mathf.Floor(x);

        // a fixed random value 0..1 per lattice point and octave (an integer hash: the same on every device)
        private static float Hash01(int i, int j, int octave)
        {
            unchecked
            {
                uint hsh = (uint)i * 73856093u ^ (uint)j * 19349663u ^ (uint)(octave + 1) * 83492791u;
                hsh ^= hsh >> 13; hsh *= 0x5bd1e995u; hsh ^= hsh >> 15; hsh *= 0x27d4eb2du; hsh ^= hsh >> 16;
                return (hsh & 0xFFFFFF) / 16777216f;
            }
        }

        /// The quad's margin around a shape. Shapes that draw no halo get no room for one: lines (kind 5: they fade to 0
        /// at their edges) and solid or empty pills (kind 4 strokes and dots: no edge, no glow) keep only AaMargin, the
        /// room the edge's anti-aliasing and 4x MSAA coverage need (a quad that ends exactly at the shape trims the
        /// outer half pixel: thinner lines). Every other shape keeps its margin (headers draw their bar into it); flat
        /// boxes (kind 9) already ask for 0.4-0.5 mm. A pill with a partial fill keeps its margin: the fill's end is
        /// measured over the quad.
        public const float AaMargin = 0.0008f;

        public static float QuadMargin(in UiShape s)
        {
            if (s.Kind == UiShapeKind.Line) return Mathf.Min(s.Margin, AaMargin);
            if (s.Kind == UiShapeKind.Bar && (s.State >= 0.9999f || s.State <= 0.0001f))
            {
                float hx = s.Size.x * 0.5f, hy = s.Size.y * 0.5f;
                bool square = s.CutBig < 0.00005f;
                bool pill = !square && !(hy > 0.0022f && hx > hy * 4f); // as in the shader
                if (pill) return Mathf.Min(s.Margin, AaMargin);
            }
            return s.Margin;
        }

        public int Add(UiShape s)
        {
            _shapes.Add(s);
            _dirty = _layout = true;
            return _shapes.Count - 1;
        }

        public UiShape Get(int id) => _shapes[id];

        private static bool SameLayout(in UiShape a, in UiShape b) =>
            a.Center == b.Center && a.Size == b.Size && a.Kind == b.Kind && a.CutBig == b.CutBig && a.CutSmall == b.CutSmall
            && a.Border == b.Border && a.Margin == b.Margin && a.Rotation == b.Rotation && a.Visible == b.Visible;

        public void Set(int id, UiShape s)
        {
            var old = _shapes[id];
            if (!SameLayout(old, s) || QuadMargin(old) != QuadMargin(s)) { _shapes[id] = s; _dirty = _layout = true; return; }
            if (old.Tint == s.Tint && Mathf.Abs(old.State - s.State) < 0.002f) return; // nothing changed
            _shapes[id] = s;
            Restyle(id);
        }

        public void SetState(int id, float state)
        {
            var s = _shapes[id];
            if (Mathf.Abs(s.State - state) < 0.002f) return;
            float m0 = QuadMargin(s);
            s.State = state;
            _shapes[id] = s;
            if (QuadMargin(s) != m0) { _dirty = _layout = true; return; } // a pill's fill became partial (or full): new quad
            Restyle(id);
        }

        public void SetTint(int id, Color tint)
        {
            var s = _shapes[id];
            if (s.Tint == tint) return;
            s.Tint = tint;
            _shapes[id] = s;
            Restyle(id);
        }

        // only this shape's colour or state: its vertices are rewritten in place at the next Flush
        private void Restyle(int id)
        {
            _dirty = true;
            if (_layout || id >= _first.Count || _first[id] < 0) { _layout |= _shapes[id].Visible; return; } // not in the mesh yet
            _restyle.Add(id);
        }

        public void SetVisible(int id, bool visible)
        {
            var s = _shapes[id];
            if (s.Visible == visible) return;
            s.Visible = visible;
            _shapes[id] = s;
            _dirty = _layout = true;
        }

        public void SetRect(int id, Vector3 center, Vector2 size)
        {
            var s = _shapes[id];
            if (s.Center == center && s.Size == size) return;
            s.Center = center;
            s.Size = size;
            _shapes[id] = s;
            _dirty = _layout = true;
        }

        public void Clear() { _shapes.Clear(); _dirty = _layout = true; }

        /// Self-tests: how many times the geometry was rebuilt and how many times only colours and states were.
        public int RebuildsForTest { get; private set; }
        public int RestylesForTest { get; private set; }

        private void LateUpdate() => Flush();

        /// Uploads the mesh now if anything changed (also used by Editor self-tests that render without a frame loop).
        public void Flush()
        {
            if (!_dirty || _mesh == null) return;
            _dirty = false;
            if (!_layout)
            {
                // colours and states only: rewrite those shapes' vertices, upload the two streams
                foreach (int id in _restyle)
                {
                    var s = _shapes[id];
                    int f = _first[id], n = _count[id];
                    if (f < 0) continue;
                    var lin = UiTheme.Lin(s.Tint);
                    var style = new Vector4(s.CutBig, s.CutSmall, s.Border, s.State);
                    for (int k = f; k < f + n; k++) { _c[k] = lin; _style[k] = style; }
                }
                _restyle.Clear();
                _mesh.SetColors(_c);
                _mesh.SetUVs(2, _style);
                RestylesForTest++;
                return;
            }
            _layout = false;
            _restyle.Clear();
            RebuildsForTest++;
            _v.Clear(); _c.Clear(); _uv.Clear(); _shape.Clear(); _style.Clear(); _tris.Clear(); _first.Clear(); _count.Clear();
            foreach (var s in _shapes)
            {
                _first.Add(-1); _count.Add(0);
                if (!s.Visible) continue;
                float margin = QuadMargin(s);
                float hw = s.Size.x * 0.5f + margin, hh = s.Size.y * 0.5f + margin;
                var lin = UiTheme.Lin(s.Tint);
                var shape = new Vector4(s.Size.x, s.Size.y, margin, (float)s.Kind);
                var style = new Vector4(s.CutBig, s.CutSmall, s.Border, s.State);
                // columns of about 1.5 cm, so the shader can bend wide shapes smoothly
                int cols = Mathf.Clamp(Mathf.CeilToInt(hw * 2f / 0.015f), 1, 64);
                int i0 = _v.Count;
                _first[_first.Count - 1] = i0;
                _count[_count.Count - 1] = (cols + 1) * 2;
                float cs = 1f, sn = 0f; // rotated shapes (diagram edges, chart lines): around the centre, in the panel plane
                if (s.Rotation != 0f) { cs = Mathf.Cos(s.Rotation); sn = Mathf.Sin(s.Rotation); }
                for (int k = 0; k <= cols; k++)
                {
                    float u = (float)k / cols;
                    float x = -hw + u * hw * 2f;
                    _v.Add(s.Center + new Vector3(x * cs + hh * sn, x * sn - hh * cs, 0f));
                    _v.Add(s.Center + new Vector3(x * cs - hh * sn, x * sn + hh * cs, 0f));
                    _uv.Add(new Vector2(u, 0f)); _uv.Add(new Vector2(u, 1f));
                    _c.Add(lin); _c.Add(lin); _shape.Add(shape); _shape.Add(shape); _style.Add(style); _style.Add(style);
                }
                for (int k = 0; k < cols; k++)
                {
                    int a = i0 + k * 2;
                    _tris.Add(a); _tris.Add(a + 1); _tris.Add(a + 3);
                    _tris.Add(a); _tris.Add(a + 3); _tris.Add(a + 2);
                }
            }
            _mesh.Clear();
            _mesh.SetVertices(_v);
            _mesh.SetColors(_c);
            _mesh.SetUVs(0, _uv);
            _mesh.SetUVs(1, _shape);
            _mesh.SetUVs(2, _style);
            _mesh.SetTriangles(_tris, 0);
            _mesh.RecalculateBounds();
            // bent in the vertex shader: grow the bounds so the panel is not culled while it curves towards you
            var bb = _mesh.bounds;
            bb.Expand(new Vector3(0f, 0f, Mathf.Max(0.3f, bb.size.x)));
            _mesh.bounds = bb;
        }

        private void OnDestroy()
        {
            if (_mesh == null) return;
            if (Application.isPlaying) Destroy(_mesh); else DestroyImmediate(_mesh);
        }
    }
}

using System.Collections.Generic;
using TMPro;
using UnityEngine;
using UnityEngine.TextCore.LowLevel;

namespace MetaCodex.UI
{
    /// All shapes of one panel in one mesh (one draw call). Elements keep the index of their shapes and
    /// change state/tint in place; the mesh is re-uploaded once per frame at most, only when something changed.
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

        public int Count => _shapes.Count;

        /// 0 = flat. Otherwise the shapes curve around a vertical axis this far in front of the panel (towards
        /// the user), like a curved monitor: quads are split into narrow columns and bent.
        public float BendRadius
        {
            get => _bend;
            set { if (Mathf.Abs(_bend - value) < 1e-4f) return; _bend = value; _dirty = true; }
        }
        private float _bend;

        /// Panel-local point -> its place on the bend cylinder (identity when flat).
        public static Vector3 Bend(Vector3 v, float radius)
        {
            if (radius <= 0f) return v;
            float a = v.x / radius;
            return new Vector3(radius * Mathf.Sin(a), v.y, v.z - radius * (1f - Mathf.Cos(a)));
        }

        public void Init(Material material)
        {
            _mesh = new Mesh { name = "ui shapes" };
            _mesh.MarkDynamic();
            GetComponent<MeshFilter>().sharedMesh = _mesh;
            var r = GetComponent<MeshRenderer>();
            r.sharedMaterial = material;
            r.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            r.receiveShadows = false;
        }

        public int Add(UiShape s)
        {
            _shapes.Add(s);
            _dirty = true;
            return _shapes.Count - 1;
        }

        public UiShape Get(int id) => _shapes[id];

        public void Set(int id, UiShape s) { _shapes[id] = s; _dirty = true; }

        public void SetState(int id, float state)
        {
            var s = _shapes[id];
            if (Mathf.Abs(s.State - state) < 0.002f) return;
            s.State = state;
            _shapes[id] = s;
            _dirty = true;
        }

        public void SetTint(int id, Color tint)
        {
            var s = _shapes[id];
            if (s.Tint == tint) return;
            s.Tint = tint;
            _shapes[id] = s;
            _dirty = true;
        }

        public void SetVisible(int id, bool visible)
        {
            var s = _shapes[id];
            if (s.Visible == visible) return;
            s.Visible = visible;
            _shapes[id] = s;
            _dirty = true;
        }

        public void SetRect(int id, Vector3 center, Vector2 size)
        {
            var s = _shapes[id];
            s.Center = center;
            s.Size = size;
            _shapes[id] = s;
            _dirty = true;
        }

        public void Clear() { _shapes.Clear(); _dirty = true; }

        private void LateUpdate() => Flush();

        /// Uploads the mesh now if anything changed (also used by Editor self-tests that render without a frame loop).
        public void Flush()
        {
            if (!_dirty || _mesh == null) return;
            _dirty = false;
            _v.Clear(); _c.Clear(); _uv.Clear(); _shape.Clear(); _style.Clear(); _tris.Clear();
            foreach (var s in _shapes)
            {
                if (!s.Visible) continue;
                float hw = s.Size.x * 0.5f + s.Margin, hh = s.Size.y * 0.5f + s.Margin;
                var lin = UiTheme.Lin(s.Tint);
                var shape = new Vector4(s.Size.x, s.Size.y, s.Margin, (float)s.Kind);
                var style = new Vector4(s.CutBig, s.CutSmall, s.Border, s.State);
                // flat: one quad; bent: columns of about 1.5 cm so the curve stays smooth
                int cols = _bend > 0f ? Mathf.Clamp(Mathf.CeilToInt(hw * 2f / 0.015f), 1, 64) : 1;
                int i0 = _v.Count;
                for (int k = 0; k <= cols; k++)
                {
                    float u = (float)k / cols;
                    float x = -hw + u * hw * 2f;
                    _v.Add(Bend(s.Center + new Vector3(x, -hh, 0f), _bend));
                    _v.Add(Bend(s.Center + new Vector3(x, hh, 0f), _bend));
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
        }

        private void OnDestroy()
        {
            if (_mesh == null) return;
            if (Application.isPlaying) Destroy(_mesh); else DestroyImmediate(_mesh);
        }
    }
}

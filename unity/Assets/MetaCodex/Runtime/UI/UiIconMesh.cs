using System.Collections.Generic;
using Newtonsoft.Json.Linq;
using UnityEngine;

namespace MetaCodex.UI
{
    /// The tool logos (Resources/ToolIcons: Simple Icons, CC0 SVG data; each logo is its owner's trademark, shown
    /// only to name the tool), white in one atlas, with their brand colours.
    public static class UiIcons
    {
        private static Texture2D _atlas;
        private static JObject _index;
        private static bool _loaded;

        public static Texture2D Atlas { get { Load(); return _atlas; } }

        private static void Load()
        {
            if (_loaded) return;
            _loaded = true;
            _atlas = Resources.Load<Texture2D>("ToolIcons/tool-icons");
            var json = Resources.Load<TextAsset>("ToolIcons/tool-icons-index");
            try { if (json != null) _index = JObject.Parse(json.text); } catch (System.Exception) { _index = null; }
        }

        /// One icon: the atlas rect of its drawn content (not the whole cell), that content's width / height, its brand
        /// colour (logos; pictograms have none) and whether it is the tool's own logo or a pictogram of what it does.
        public struct Info { public Rect Uv; public float Aspect; public Color Brand; public bool HasBrand; public bool Logo; public string Title; }

        /// The atlas rect (uv) of a tool's icon and its brand colour; false when the tool has none.
        public static bool TryGet(string tool, out Rect uv, out Color brand)
        {
            bool ok = TryGetIcon(tool, out var info);
            uv = info.Uv;
            brand = info.Brand;
            return ok;
        }

        /// The icon of a tool (tools/make-tool-icons.js): uv of the measured content box, computed from the atlas size
        /// the index records (the imported texture may be resized; fractions of the recorded size stay right).
        public static bool TryGetIcon(string tool, out Info info)
        {
            info = new Info { Brand = Color.white, Aspect = 1f };
            Load();
            if (_atlas == null || _index == null || string.IsNullOrEmpty(tool)) return false;
            string slug = (string)_index["tools"]?[tool] ?? tool;
            var icon = _index["icons"]?[slug];
            if (icon == null) return false;
            int cell = (int)icon["cell"], cols = (int?)_index["columns"] ?? 8, size = (int?)_index["cell"] ?? 128;
            float aw = (float?)_index["width"] ?? _atlas.width, ah = (float?)_index["height"] ?? _atlas.height;
            int cx = cell % cols, cy = cell / cols;
            // the content box in cell units, y down (whole cell for an index without boxes)
            float bx0 = 0f, by0 = 0f, bx1 = 1f, by1 = 1f;
            if (icon["box"] is JArray b && b.Count == 4) { bx0 = (float)b[0]; by0 = (float)b[1]; bx1 = (float)b[2]; by1 = (float)b[3]; }
            float px0 = (cx + bx0) * size, px1 = (cx + bx1) * size, py0 = (cy + by0) * size, py1 = (cy + by1) * size;
            info.Uv = new Rect(px0 / aw, 1f - py1 / ah, (px1 - px0) / aw, (py1 - py0) / ah);
            info.Aspect = Mathf.Max(0.05f, (bx1 - bx0) / Mathf.Max(0.001f, by1 - by0));
            info.Logo = (string)icon["kind"] != "pictogram";
            info.Title = (string)icon["title"];
            string hex = (string)icon["hex"];
            info.HasBrand = !string.IsNullOrEmpty(hex) && ColorUtility.TryParseHtmlString("#" + hex, out info.Brand);
            if (!info.HasBrand) info.Brand = Color.white;
            return true;
        }
    }

    /// All icons of one panel in one mesh (like UiShapeMesh): textured quads from the atlas, tinted, bent on the
    /// GPU with the panel (shader MetaCodex/UiIcon). Elements keep the index of their icons.
    [RequireComponent(typeof(MeshFilter), typeof(MeshRenderer))]
    public sealed class UiIconMesh : MonoBehaviour
    {
        private struct Icon { public Vector3 Center; public Vector2 Size; public Rect Uv; public Color Tint; public bool Visible; }
        private readonly List<Icon> _icons = new List<Icon>();
        private readonly List<Vector3> _v = new List<Vector3>();
        private readonly List<Color> _c = new List<Color>();
        private readonly List<Vector2> _uv = new List<Vector2>();
        private readonly List<int> _t = new List<int>();
        private Mesh _mesh;
        private bool _dirty;
        private static Material _material;

        public static Material SharedMaterial()
        {
            if (_material != null) return _material;
            var sh = Resources.Load<Shader>("Shaders/UiIcon") ?? Shader.Find("MetaCodex/UiIcon");
            if (sh == null) return null;
            _material = new Material(sh) { name = "ui icons", mainTexture = UiIcons.Atlas };
            return _material;
        }

        public void Init()
        {
            _mesh = new Mesh { name = "ui icons" };
            _mesh.MarkDynamic();
            GetComponent<MeshFilter>().sharedMesh = _mesh;
            var r = GetComponent<MeshRenderer>();
            r.sharedMaterial = SharedMaterial();
            r.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            r.receiveShadows = false;
        }

        public int Add(Vector3 center, Vector2 size, Rect uv, Color tint)
        {
            _icons.Add(new Icon { Center = center, Size = size, Uv = uv, Tint = tint, Visible = true });
            _dirty = true;
            return _icons.Count - 1;
        }

        public void Set(int id, Vector3 center, Vector2 size, Rect uv, Color tint)
        {
            _icons[id] = new Icon { Center = center, Size = size, Uv = uv, Tint = tint, Visible = true };
            _dirty = true;
        }

        public void SetVisible(int id, bool v)
        {
            var i = _icons[id];
            if (i.Visible == v) return;
            i.Visible = v;
            _icons[id] = i;
            _dirty = true;
        }

        private void LateUpdate() => Flush();

        public void Flush()
        {
            if (!_dirty || _mesh == null) return;
            _dirty = false;
            _v.Clear(); _c.Clear(); _uv.Clear(); _t.Clear();
            foreach (var i in _icons)
            {
                if (!i.Visible) continue;
                int b = _v.Count;
                float hx = i.Size.x * 0.5f, hy = i.Size.y * 0.5f;
                var lin = UiTheme.Lin(i.Tint);
                _v.Add(i.Center + new Vector3(-hx, -hy, 0f)); _uv.Add(new Vector2(i.Uv.xMin, i.Uv.yMin));
                _v.Add(i.Center + new Vector3(-hx, hy, 0f)); _uv.Add(new Vector2(i.Uv.xMin, i.Uv.yMax));
                _v.Add(i.Center + new Vector3(hx, hy, 0f)); _uv.Add(new Vector2(i.Uv.xMax, i.Uv.yMax));
                _v.Add(i.Center + new Vector3(hx, -hy, 0f)); _uv.Add(new Vector2(i.Uv.xMax, i.Uv.yMin));
                for (int k = 0; k < 4; k++) _c.Add(lin);
                _t.Add(b); _t.Add(b + 1); _t.Add(b + 2);
                _t.Add(b); _t.Add(b + 2); _t.Add(b + 3);
            }
            _mesh.Clear();
            _mesh.SetVertices(_v);
            _mesh.SetColors(_c);
            _mesh.SetUVs(0, _uv);
            _mesh.SetTriangles(_t, 0);
            _mesh.RecalculateBounds();
            var bb = _mesh.bounds;
            bb.Expand(new Vector3(0f, 0f, 0.3f));
            _mesh.bounds = bb;
        }

        private void OnDestroy()
        {
            if (_mesh == null) return;
            if (Application.isPlaying) Destroy(_mesh); else DestroyImmediate(_mesh);
        }
    }
}

using System;
using System.Collections.Generic;
using TMPro;
using UnityEngine;

namespace MetaCodex.UI
{
    /// A world-space panel: frame, header with title and close button, and a flat list of elements laid out in
    /// panel coordinates (metres from the top-left corner, y down). The transform is the panel centre; the
    /// panel faces the user along -z (its +z points away from them), like the terminal.
    public class UiPanel : MonoBehaviour
    {
        public string Id { get; private set; }
        public string Title { get; private set; }
        public float Width { get; private set; }
        public float Height { get; private set; }
        public bool Closable { get; set; } = true;
        public bool Draggable { get; set; } = true;
        public bool HasHeader { get; private set; } = true;
        public bool IsOpen => gameObject.activeSelf;
        public UiTheme Theme => UiTheme.Current;
        public UiShapeMesh Shapes { get; private set; }
        public readonly List<UiElement> Elements = new List<UiElement>();
        public event Action<UiPanel> Closed;
        public event Action<UiPanel> Opened;
        /// Called every frame while open (panels refresh their data here).
        public event Action<UiPanel> Tick;

        private int _body, _header;
        private UiText _title;
        private UiButton _close;
        private float _openT = 1f;
        private Vector3 _baseScale = Vector3.one;

        public static T Create<T>(string id, string title, float width, float height, Transform parent = null, bool header = true) where T : UiPanel
        {
            var go = new GameObject("panel-" + id);
            if (parent != null) go.transform.SetParent(parent, false);
            var p = go.AddComponent<T>();
            p.Init(id, title, width, height, header);
            return p;
        }

        protected virtual void Init(string id, string title, float width, float height, bool header)
        {
            Id = id; Title = title; Width = width; Height = height; HasHeader = header;
            var shapesGo = new GameObject("shapes", typeof(MeshFilter), typeof(MeshRenderer));
            shapesGo.transform.SetParent(transform, false);
            Shapes = shapesGo.AddComponent<UiShapeMesh>();
            Shapes.Init(Theme.ShapeMaterial);
            Hook();
            ClaimSlot();
            TagRenderer(shapesGo.GetComponent<MeshRenderer>(), Vector3.zero);
            _body = Shapes.Add(Shape(UiShapeKind.Panel, new Rect(0, 0, width, height), Theme.Frame, Theme.CutBig, Theme.CutSmall));
            if (header)
            {
                var hr = new Rect(0.004f, 0.004f, width - 0.008f, Theme.HeaderHeight - 0.004f);
                _header = Shapes.Add(Shape(UiShapeKind.Header, hr, Theme.Accent, Theme.CutBig * 0.8f, 0.002f, margin: 0.002f));
                _title = UiText.Create(this, new Rect(Theme.Padding + 0.006f, 0.006f, width - 0.1f, Theme.HeaderHeight - 0.010f),
                    ConsoleTitle(title), Theme.Display, Theme.TitleSize, Theme.TextAccent, TextAlignmentOptions.MidlineLeft);
                _title.Tmp.characterSpacing = 5f;
                _close = Add(new UiButton("✕", () => Close(), UiButtonStyle.Ghost)
                {
                    Rect = new Rect(width - 0.044f, 0.006f, 0.036f, Theme.HeaderHeight - 0.010f),
                });
            }
        }

        public UiShape Shape(UiShapeKind kind, Rect r, Color tint, float cutBig = -1f, float cutSmall = -1f, float margin = -1f, float state = 0f)
        {
            return new UiShape
            {
                Center = PanelToLocal(r.center),
                Size = r.size,
                Kind = kind,
                Tint = tint,
                CutBig = cutBig < 0 ? Theme.CutBig * 0.6f : cutBig,
                CutSmall = cutSmall < 0 ? Theme.CutSmall : cutSmall,
                Border = Theme.Border,
                Margin = margin < 0 ? Theme.GlowMargin : margin,
                State = state,
                Visible = true,
            };
        }

        /// A straight stroke from a to b (panel coordinates): a rotated pill (Bar kind, full) or line.
        public UiShape Segment(Vector2 a, Vector2 b, float thickness, Color tint, UiShapeKind kind = UiShapeKind.Bar)
        {
            var d = b - a;
            float len = Mathf.Max(d.magnitude, 1e-4f);
            var s = Shape(kind, new Rect((a + b) * 0.5f - new Vector2(len, thickness) * 0.5f, new Vector2(len, thickness)), tint, thickness * 0.5f, 0f, 0.0015f, 1f);
            s.Rotation = Mathf.Atan2(-d.y, d.x); // panel y points down, local y up
            return s;
        }

        /// Panel coordinates (top-left origin, y down, metres) -> local position (centre origin, y up).
        public Vector3 PanelToLocal(Vector2 p, float z = 0f) => new Vector3(p.x - Width * 0.5f, Height * 0.5f - p.y, z);

        public Vector2 LocalToPanel(Vector3 local) => new Vector2(local.x + Width * 0.5f, Height * 0.5f - local.y);

        public T Add<T>(T element) where T : UiElement
        {
            element.Attach(this);
            Elements.Add(element);
            return element;
        }

        public void Remove(UiElement element)
        {
            element.Detach();
            Elements.Remove(element);
        }

        /// Content area below the header.
        public Rect Content => new Rect(Theme.Padding, (HasHeader ? Theme.HeaderHeight : 0f) + Theme.Padding,
            Width - 2 * Theme.Padding, Height - (HasHeader ? Theme.HeaderHeight : 0f) - 2 * Theme.Padding);

        public bool InHeader(Vector2 p) => (HasHeader ? p.y >= 0f && p.y <= Theme.HeaderHeight : HandleHeight > 0f && p.y >= 0f && p.y <= HandleHeight) && p.x >= 0f && p.x <= Width;

        /// Panels without a title bar (the dock) can still be moved: pinch the strip at the top (a small bar shows it).
        public float HandleHeight { get; private set; }

        public void AddGrabHandle(float height)
        {
            HandleHeight = height;
            float w = Mathf.Min(0.07f, Width * 0.3f);
            Shapes.Add(Shape(UiShapeKind.Bar, new Rect((Width - w) * 0.5f, height * 0.5f - 0.0022f, w, 0.0044f), Theme.Frame, 0.003f, 0f, 0.003f, 0f));
        }

        public void SetTitle(string title)
        {
            Title = title;
            if (_title != null) _title.Text = ConsoleTitle(title);
        }

        /// Panel titles in capitals (the console look), except what must keep its case (paths in titles are rare).
        private static string ConsoleTitle(string title) => UiTheme.TitleCase ? UiTheme.Pretty(title) : (title ?? "").ToUpperInvariant();

        /// Hides the title bar's own close button (terminal frames have their own buttons there).
        public void HideCloseButton() => _close?.SetVisible(false);

        /// Ray against the panel's drawn surface (its plane, or its cylinder when bent); the panel point when the
        /// ray hits inside the frame. Pointers land where the panel is drawn, not on its flat projection.
        public bool Raycast(Ray ray, out Vector2 panelPoint, out float distance)
        {
            panelPoint = default;
            distance = 0f;
            if (!IsOpen || !Intersect(ray, out var local, out distance)) return false;
            panelPoint = SurfaceToPanel(local);
            return panelPoint.x >= -0.004f && panelPoint.y >= -0.004f && panelPoint.x <= Width + 0.004f && panelPoint.y <= Height + 0.004f;
        }

        /// The same without the bounds check (dragging continues outside the panel).
        public bool ProjectRay(Ray ray, out Vector2 panelPoint)
        {
            panelPoint = default;
            if (!Intersect(ray, out var local, out _)) return false;
            panelPoint = SurfaceToPanel(local);
            return true;
        }

        /// Where a ray meets the drawn surface, in panel-local space. Bent: the cylinder x² + (z + r)² = r², on the
        /// side away from its axis (z + r > 0), which is where the panel is.
        private bool Intersect(Ray ray, out Vector3 local, out float distance)
        {
            local = default;
            distance = 0f;
            float r = _bendCurrent;
            if (r <= 0f)
            {
                var n = transform.forward;
                float denom = Vector3.Dot(n, ray.direction);
                if (Mathf.Abs(denom) < 1e-5f) return false;
                float t = Vector3.Dot(transform.position - ray.origin, n) / denom;
                if (t < 0f) return false;
                var hit = ray.origin + ray.direction * t;
                local = transform.InverseTransformPoint(hit);
                distance = t;
                return true;
            }
            var o = transform.InverseTransformPoint(ray.origin);
            var d = transform.InverseTransformVector(ray.direction);
            float ox = o.x, oz = o.z + r, dx = d.x, dz = d.z;
            float A = dx * dx + dz * dz;
            if (A < 1e-9f) return false;
            float B = 2f * (ox * dx + oz * dz), C = ox * ox + oz * oz - r * r;
            float disc = B * B - 4f * A * C;
            if (disc < 0f) return false;
            float sq = Mathf.Sqrt(disc);
            float t0 = (-B - sq) / (2f * A), t1 = (-B + sq) / (2f * A), th = -1f;
            if (t0 >= 0f && oz + t0 * dz > 0f) th = t0;
            if (t1 >= 0f && oz + t1 * dz > 0f && (th < 0f || t1 < th)) th = t1;
            if (th < 0f) return false;
            local = o + d * th;
            distance = Vector3.Distance(ray.origin, transform.TransformPoint(local));
            return true;
        }

        /// A point on the drawn surface (panel-local) -> panel coordinates (the arc length along a bent panel).
        private Vector2 SurfaceToPanel(Vector3 local)
        {
            float r = _bendCurrent;
            if (r <= 0f) return LocalToPanel(local);
            float a = Mathf.Atan2(local.x, local.z + r);
            return new Vector2(a * r + Width * 0.5f, Height * 0.5f - local.y);
        }

        /// A world point (a fingertip) -> panel coordinates and its depth off the drawn surface (< 0: in front,
        /// towards the user), on a bent panel too.
        public void SurfaceOf(Vector3 world, out Vector2 panelPoint, out float depth)
        {
            var local = transform.InverseTransformPoint(world);
            float r = _bendCurrent;
            if (r <= 0f) { panelPoint = LocalToPanel(local); depth = local.z; return; }
            float zr = local.z + r;
            panelPoint = new Vector2(Mathf.Atan2(local.x, zr) * r + Width * 0.5f, Height * 0.5f - local.y);
            depth = Mathf.Sqrt(local.x * local.x + zr * zr) - r;
        }

        /// Panel coordinates -> the world point where that spot is drawn (bent or flat), `depth` off the surface
        /// along its normal (< 0: towards the user).
        public Vector3 SurfacePoint(Vector2 panelPoint, float depth = 0f)
        {
            float r = _bendCurrent;
            var flat = PanelToLocal(panelPoint, depth);
            if (r <= 0f) return transform.TransformPoint(flat);
            float a = flat.x / r, rr = r + depth;
            return transform.TransformPoint(new Vector3(rr * Mathf.Sin(a), flat.y, -r + rr * Mathf.Cos(a)));
        }

        public UiElement HitTest(Vector2 p)
        {
            for (int i = Elements.Count - 1; i >= 0; i--)
            {
                var e = Elements[i];
                if (e.Visible && e.Interactive && e.Enabled && e.Rect.Contains(p)) return e;
            }
            return null;
        }

        public void Open()
        {
            if (!gameObject.activeSelf) gameObject.SetActive(true);
            _openT = 0f;
            if (Id != "toast") UiSounds.Fire("open");
            Opened?.Invoke(this);
        }

        public void Close()
        {
            if (!gameObject.activeSelf) return;
            gameObject.SetActive(false);
            if (Id != "toast") UiSounds.Fire("close");
            Closed?.Invoke(this);
        }

        public void Toggle() { if (IsOpen) Close(); else Open(); }

        /// Places the panel in front of a head pose: level, at the given distance and height offset.
        public void PlaceInFront(Transform head, float distance, float down = 0f, float sideways = 0f)
        {
            Vector3 eye = head != null ? head.position : new Vector3(0f, 1.3f, 0f);
            Vector3 fwd = head != null ? head.forward : Vector3.forward;
            fwd.y = 0f;
            if (fwd.sqrMagnitude < 1e-4f) fwd = Vector3.forward;
            fwd.Normalize();
            var right = Vector3.Cross(Vector3.up, fwd);
            transform.SetPositionAndRotation(eye + fwd * distance + Vector3.down * down + right * sideways, Quaternion.LookRotation(fwd, Vector3.up));
        }

        /// Turns the panel to face a point (yaw only), keeping it upright.
        public void FaceTowards(Vector3 eye)
        {
            var dir = transform.position - eye;
            dir.y = 0f;
            if (dir.sqrMagnitude < 1e-4f) return;
            transform.rotation = Quaternion.LookRotation(dir.normalized, Vector3.up);
        }

        public void SetHighlight(float state) => Shapes.SetState(_body, state);

        // ------------------------------------------------------------------ bending (GPU)

        // A bent panel curves around a vertical axis `radius` in front of it (towards the user). The vertex
        // shaders of its shapes (MetaCodex/SciFiUI) and texts (MetaCodex/TextBend) bend every vertex from one global
        // array, _PanelBend[slot].x = the radius drawn now; each renderer carries its panel's slot (and a text its
        // offset in the panel) in a property block. Changing the bend costs nothing on the CPU, eases smoothly (no
        // 5 cm ticks), and a text that changes every frame is never drawn flat for a frame.
        public const int MaxSlots = 128;
        private static readonly UiPanel[] Slots = new UiPanel[MaxSlots];
        private static readonly Vector4[] BendData = new Vector4[MaxSlots];
        private static int _pushedFrame = -1;
        private static readonly int BendArrayId = Shader.PropertyToID("_PanelBend");
        private static readonly int IndexId = Shader.PropertyToID("_PanelIndex");
        private static readonly int OffsetId = Shader.PropertyToID("_TextOffset");
        private static MaterialPropertyBlock _mpb;
        private static bool _hooked;
        public int Slot { get; private set; } = -1;
        private float _bendTarget, _bendCurrent;

        /// 0 = flat. The radius the layout asked for; BendNow is what is drawn (it eases towards this).
        public float BendRadius => _bendTarget;
        public float BendNow => _bendCurrent;

        private void ClaimSlot()
        {
            for (int i = 0; i < MaxSlots; i++)
                if (Slots[i] == null) { Slots[i] = this; Slot = i; BendData[i] = Vector4.zero; return; }
            Slot = -1; // out of slots: this panel stays flat
        }

        protected virtual void OnDestroy()
        {
            if (Slot >= 0 && Slots[Slot] == this) { Slots[Slot] = null; BendData[Slot] = Vector4.zero; }
        }

        public void SetBend(float radius)
        {
            _bendTarget = radius < 0.05f ? 0f : radius;
            if (!Application.isPlaying) SnapBend(); // Editor self-tests: no frame loop to ease it
        }

        /// Jump to the target bend now (tests, rebuilt panels).
        public void SnapBend()
        {
            _bendCurrent = _bendTarget;
            if (Slot >= 0) BendData[Slot] = new Vector4(_bendCurrent, 0f, 0f, 0f);
        }

        private void EaseBend(float dt)
        {
            if (Mathf.Approximately(_bendCurrent, _bendTarget)) return;
            // ease the curvature (1/r): flat <-> bent is then as smooth as one radius to another
            float k0 = _bendCurrent > 0f ? 1f / _bendCurrent : 0f, k1 = _bendTarget > 0f ? 1f / _bendTarget : 0f;
            float k = Mathf.Lerp(k0, k1, 1f - Mathf.Exp(-dt * 9f));
            if (Mathf.Abs(k - k1) < 0.002f) k = k1;
            _bendCurrent = k > 1e-3f ? 1f / k : 0f;
            if (k == k1) _bendCurrent = _bendTarget;
            if (Slot >= 0) BendData[Slot] = new Vector4(_bendCurrent, 0f, 0f, 0f);
        }

        /// The bend of every panel to the shaders (once a frame; self-tests call it before rendering).
        public static void PushBendGlobals()
        {
            Shader.SetGlobalVectorArray(BendArrayId, BendData);
            _pushedFrame = Time.frameCount;
        }

        /// Tags a renderer of this panel (shapes, a text, a text's fallback sub-mesh) with the panel's slot and, for
        /// a text, where its origin sits in the panel.
        public void TagRenderer(Renderer r, Vector3 offset)
        {
            if (r == null) return;
            _mpb ??= new MaterialPropertyBlock();
            r.GetPropertyBlock(_mpb);
            _mpb.SetFloat(IndexId, Slot);
            _mpb.SetVector(OffsetId, offset);
            r.SetPropertyBlock(_mpb);
        }

        private static void Hook()
        {
            if (_hooked) return;
            _hooked = true;
            TMPro_EventManager.TEXT_CHANGED_EVENT.Add(OnAnyTextChanged);
        }

        /// Glyphs from fallback fonts live in TMP sub-meshes created on the fly: tag them like their text.
        private static void OnAnyTextChanged(UnityEngine.Object obj)
        {
            if (!(obj is TextMeshPro t) || t == null || t.transform.childCount == 0) return;
            var panel = t.GetComponentInParent<UiPanel>();
            if (panel == null) return;
            foreach (var sub in t.GetComponentsInChildren<TMP_SubMesh>(true))
                panel.TagRenderer(sub.renderer, t.rectTransform.localPosition);
        }

        /// Self-tests: one frame of easing towards the target bend.
        public void EaseBendForTest(float dt) { EaseBend(dt); }

        /// Kept for callers of the old CPU bending: texts are bent on the GPU now.
        public void ApplyBendNow() { SnapBend(); PushBendGlobals(); }

        private void LateUpdate()
        {
            if (_pushedFrame != Time.frameCount) PushBendGlobals();
        }

        protected virtual void Update()
        {
            EaseBend(Time.unscaledDeltaTime);
            if (_openT < 1f)
            {
                _openT = Mathf.Min(1f, _openT + Time.unscaledDeltaTime / 0.18f);
                float k = 1f - Mathf.Pow(1f - _openT, 3f);
                transform.localScale = new Vector3(_baseScale.x * Mathf.Lerp(0.85f, 1f, k), _baseScale.y * Mathf.Lerp(0.04f, 1f, k), _baseScale.z);
                Shapes.SetState(_body, 1f - k);
            }
            Tick?.Invoke(this);
            foreach (var e in Elements) if (e.Visible) e.Update();
        }

        /// Editor/self-test: lay out and upload everything without a frame loop.
        public void ForceRefresh()
        {
            _openT = 1f;
            SnapBend();
            PushBendGlobals();
            transform.localScale = _baseScale;
            Tick?.Invoke(this);
            foreach (var e in Elements) if (e.Visible) e.Update();
            Shapes.Flush();
            foreach (var t in GetComponentsInChildren<TextMeshPro>(true)) t.ForceMeshUpdate();
        }
    }

    /// A TextMeshPro label owned by a panel, positioned by a panel rect.
    public sealed class UiText
    {
        public TextMeshPro Tmp { get; private set; }
        public Rect Rect { get; private set; }
        private UiPanel _panel;

        public static UiText Create(UiPanel panel, Rect r, string text, TMP_FontAsset font, float lineHeight, Color color,
            TextAlignmentOptions align = TextAlignmentOptions.MidlineLeft, bool wrap = false)
        {
            var go = new GameObject("text");
            go.transform.SetParent(panel.transform, false);
            var t = go.AddComponent<TextMeshPro>();
            ApplyFont(t, font);
            bool display = font != null && font == panel.Theme.Display;
            // the display face (Rajdhani) is narrow with a small x-height: a little larger and letter-spaced
            t.fontSize = panel.Theme.FontSizeFor(lineHeight) * (display ? 1.12f : 1f);
            if (display) t.characterSpacing = 3f;
            t.color = color;
            t.alignment = align;
            t.textWrappingMode = wrap ? TextWrappingModes.Normal : TextWrappingModes.NoWrap;
            t.overflowMode = TextOverflowModes.Ellipsis;
            t.richText = true;
            t.rectTransform.pivot = new Vector2(0f, 1f);
            t.text = text ?? "";
            var ut = new UiText { Tmp = t, _panel = panel };
            ut.SetRect(r);
            var mr = go.GetComponent<MeshRenderer>();
            if (mr != null) mr.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            panel.TagRenderer(mr, t.rectTransform.localPosition);
            return ut;
        }

        public void SetRect(Rect r)
        {
            bool moved = Rect.position != r.position;
            Rect = r;
            Tmp.rectTransform.sizeDelta = r.size;
            Tmp.rectTransform.localPosition = _panel.PanelToLocal(r.position, -0.0012f);
            if (moved) _panel.TagRenderer(Tmp.GetComponent<MeshRenderer>(), Tmp.rectTransform.localPosition);
        }

        /// Sets the font and makes sure its material bends with the panel (the terminal's font asset keeps TMP's
        /// own shader for the terminal itself; panel texts get a bending copy of its material).
        public static void ApplyFont(TextMeshPro t, TMP_FontAsset font)
        {
            if (font == null) return;
            if (t.font != font) t.font = font;
            var m = font.material;
            if (m != null && m.shader != null && m.shader.name != "MetaCodex/TextBend") t.fontSharedMaterial = UiTheme.BendMaterialFor(font);
        }

        public string Text { get => Tmp.text; set { if (Tmp.text != value) Tmp.text = value ?? ""; } }
        public Color Color { get => Tmp.color; set => Tmp.color = value; }
        public void SetVisible(bool v) { if (Tmp.gameObject.activeSelf != v) Tmp.gameObject.SetActive(v); }
        public void Destroy() { if (Tmp != null) UiToasts.Kill(Tmp.gameObject); }
    }
}

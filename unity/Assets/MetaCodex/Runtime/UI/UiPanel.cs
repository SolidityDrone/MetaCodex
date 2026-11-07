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
            _body = Shapes.Add(Shape(UiShapeKind.Panel, new Rect(0, 0, width, height), Theme.Frame, Theme.CutBig, Theme.CutSmall));
            if (header)
            {
                var hr = new Rect(0.004f, 0.004f, width - 0.008f, Theme.HeaderHeight - 0.004f);
                _header = Shapes.Add(Shape(UiShapeKind.Header, hr, Theme.Accent, Theme.CutBig * 0.8f, 0.002f, margin: 0.002f));
                _title = UiText.Create(this, new Rect(Theme.Padding + 0.006f, 0.006f, width - 0.1f, Theme.HeaderHeight - 0.008f),
                    title.ToUpperInvariant(), Theme.Display, Theme.TitleSize, Theme.Text, TextAlignmentOptions.MidlineLeft);
                _title.Tmp.characterSpacing = 6f;
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

        public bool InHeader(Vector2 p) => HasHeader && p.y >= 0f && p.y <= Theme.HeaderHeight && p.x >= 0f && p.x <= Width;

        /// Ray against the panel plane; panel point when it hits inside the frame.
        public bool Raycast(Ray ray, out Vector2 panelPoint, out float distance)
        {
            panelPoint = default;
            distance = 0f;
            if (!IsOpen) return false;
            var n = transform.forward;
            float denom = Vector3.Dot(n, ray.direction);
            if (Mathf.Abs(denom) < 1e-5f) return false;
            float t = Vector3.Dot(transform.position - ray.origin, n) / denom;
            if (t < 0f) return false;
            var hit = ray.origin + ray.direction * t;
            panelPoint = LocalToPanel(transform.InverseTransformPoint(hit));
            distance = t;
            return panelPoint.x >= -0.004f && panelPoint.y >= -0.004f && panelPoint.x <= Width + 0.004f && panelPoint.y <= Height + 0.004f;
        }

        /// Plane intersection without the bounds check (dragging continues outside the panel).
        public bool ProjectRay(Ray ray, out Vector2 panelPoint)
        {
            panelPoint = default;
            var n = transform.forward;
            float denom = Vector3.Dot(n, ray.direction);
            if (Mathf.Abs(denom) < 1e-5f) return false;
            float t = Vector3.Dot(transform.position - ray.origin, n) / denom;
            if (t < 0f) return false;
            panelPoint = LocalToPanel(transform.InverseTransformPoint(ray.origin + ray.direction * t));
            return true;
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
            Opened?.Invoke(this);
        }

        public void Close()
        {
            if (!gameObject.activeSelf) return;
            gameObject.SetActive(false);
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

        // ------------------------------------------------------------------ bending (float layout)

        /// 0 = flat. Otherwise shapes and texts curve around a vertical axis `radius` in front of the panel.
        public float BendRadius => Shapes != null ? Shapes.BendRadius : 0f;
        private readonly HashSet<TMP_Text> _bendDirty = new HashSet<TMP_Text>();
        private static bool _hooked;

        public void SetBend(float radius)
        {
            radius = radius < 0.05f ? 0f : radius;
            if (Mathf.Abs(BendRadius - radius) < 1e-3f) return;
            Hook();
            Shapes.BendRadius = radius;
            // regenerate flat geometry; the change event marks each text for bending
            foreach (var t in GetComponentsInChildren<TextMeshPro>(true)) { t.ForceMeshUpdate(); _bendDirty.Add(t); }
        }

        private static void Hook()
        {
            if (_hooked) return;
            _hooked = true;
            TMPro_EventManager.TEXT_CHANGED_EVENT.Add(OnAnyTextChanged);
        }

        private static void OnAnyTextChanged(UnityEngine.Object obj)
        {
            if (!(obj is TextMeshPro t) || t == null) return;
            var panel = t.GetComponentInParent<UiPanel>();
            if (panel != null && panel.BendRadius > 0f) panel._bendDirty.Add(t);
        }

        private void LateUpdate() => ApplyBendNow();

        /// Bends the texts that changed since the last call (LateUpdate; self-tests call it directly).
        public void ApplyBendNow()
        {
            if (_bendDirty.Count == 0) return;
            float r = BendRadius;
            var list = new List<TMP_Text>(_bendDirty);
            _bendDirty.Clear();
            foreach (var t in list)
            {
                if (t == null || r <= 0f) continue;
                var ti = t.textInfo;
                if (ti == null || ti.meshInfo == null) continue;
                var tt = t.transform;
                for (int m = 0; m < ti.meshInfo.Length; m++)
                {
                    var verts = ti.meshInfo[m].vertices;
                    if (verts == null) continue;
                    int n = Mathf.Min(ti.meshInfo[m].vertexCount, verts.Length);
                    for (int k = 0; k < n; k++)
                    {
                        var pl = transform.InverseTransformPoint(tt.TransformPoint(verts[k]));
                        verts[k] = tt.InverseTransformPoint(transform.TransformPoint(UiShapeMesh.Bend(pl, r)));
                    }
                }
                t.UpdateVertexData(TMP_VertexDataUpdateFlags.Vertices);
            }
        }

        protected virtual void Update()
        {
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
            if (font != null) t.font = font;
            t.fontSize = panel.Theme.FontSizeFor(lineHeight);
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
            return ut;
        }

        public void SetRect(Rect r)
        {
            Rect = r;
            Tmp.rectTransform.sizeDelta = r.size;
            Tmp.rectTransform.localPosition = _panel.PanelToLocal(r.position, -0.0012f);
        }

        public string Text { get => Tmp.text; set { if (Tmp.text != value) Tmp.text = value ?? ""; } }
        public Color Color { get => Tmp.color; set => Tmp.color = value; }
        public void SetVisible(bool v) { if (Tmp.gameObject.activeSelf != v) Tmp.gameObject.SetActive(v); }
        public void Destroy() { if (Tmp != null) UiToasts.Kill(Tmp.gameObject); }
    }
}

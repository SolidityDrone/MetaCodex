using System;
using System.Collections.Generic;
using MetaCodex.Hands;
using UnityEngine;

namespace MetaCodex.UI
{
    /// Short messages. With the orb they are its callouts: cards in a column that rises beside it from its own level
    /// (a pinned panel first, the voice HUD; then the newest message), one flat sheet turned to the user, joined to
    /// the orb by a rail of light; the orb casts each card (UiPanelFx, the light variant). Without the orb they float
    /// in the upper field of view. Every message is also kept in `History` (the orb's menu lists them).
    public sealed class UiToasts : MonoBehaviour
    {
        public struct Note { public DateTime At; public string Kind, Text; }

        /// Where the column starts: the orb's place, the side it grows to (a unit vector across the user's view) and
        /// the size of the cards there. Null: no orb, the old place in the upper field of view.
        public struct Anchor { public Vector3 Orb; public Vector3 Side; public float Radius, Scale; }

        public Transform Head;
        public Func<Anchor?> Where;
        /// A panel that takes the first place of the column while it is shown (the voice HUD).
        public UiPanel Pinned;
        public readonly List<Note> History = new List<Note>();
        public const int MaxHistory = 40;
        /// Raised for every message (the orb flashes in its colour).
        public event Action<Note> Shown;
        private sealed class Toast { public UiPanel Panel; public float Until; }
        private readonly List<Toast> _toasts = new List<Toast>();
        private UiShapeMesh _rail;
        public string Last { get; private set; }
        public int Count => _toasts.Count;
        public IEnumerable<UiPanel> Panels { get { foreach (var t in _toasts) if (t.Panel != null) yield return t.Panel; } }

        public void Show(string text, string kind = "info", float seconds = 3.2f)
        {
            Last = text;
            var note = new Note { At = DateTime.Now, Kind = kind ?? "info", Text = text ?? "" };
            History.Insert(0, note);
            while (History.Count > MaxHistory) History.RemoveAt(History.Count - 1);
            var t = UiTheme.Current;
            // a long message takes two lines
            bool two = (text ?? "").Length > 54;
            var panel = UiPanel.Create<UiPanel>("toast", "", 0.42f, two ? 0.074f : 0.052f, transform, header: false);
            panel.Closable = false;
            panel.Draggable = false;
            var tint = kind == "error" ? t.Danger : kind == "warn" ? t.Warn : kind == "ok" ? t.Ok : t.Accent;
            panel.Add(new UiLabel((kind == "error" ? "⚠ " : kind == "ok" ? "✓ " : "◆ ") + text, t.BodySize)
                { Rect = new Rect(0.016f, 0.006f, 0.39f, panel.Height - 0.012f), Color = Color.Lerp(tint, Color.white, 0.5f), Wrap = two });
            panel.SetHighlight(0.6f);
            _toasts.Insert(0, new Toast { Panel = panel, Until = Time.unscaledTime + seconds + (two ? 1.2f : 0f) + (kind == "error" ? 1.5f : 0f) });
            while (_toasts.Count > 4) { _toasts[_toasts.Count - 1].Panel.CloseAndDestroy(); _toasts.RemoveAt(_toasts.Count - 1); }
            Layout(true);
            panel.PlayOpen(); // cast by the orb from where it is (after it has its place: the scan line leaves from the orb's side)
            UiSounds.Fire(kind == "error" ? "error" : "toast");
            try { Shown?.Invoke(note); } catch (Exception) { }
        }

        private void Update() { Tick(Time.unscaledTime); }

        /// One frame (self-tests step it).
        public void Tick(float now)
        {
            for (int i = _toasts.Count - 1; i >= 0; i--)
            {
                if (now < _toasts[i].Until) continue;
                if (_toasts[i].Panel != null) _toasts[i].Panel.CloseAndDestroy(); // and goes with it
                _toasts.RemoveAt(i);
            }
            Layout(false);
        }

        internal static void Kill(GameObject go)
        {
            if (go == null) return;
            if (Application.isPlaying) Destroy(go); else DestroyImmediate(go);
        }

        /// The gap between two cards (metres).
        public const float Gap = 0.016f;

        /// Puts every card (and the pinned panel) at its place now, without easing (before the pinned panel opens).
        public void LayoutNow() => Layout(true, true);

        private void Layout(bool snapNew, bool snapAll = false)
        {
            var a = Where?.Invoke();
            if (!a.HasValue) { LayoutFree(snapNew); Rail(false, default, default, 0f, 0f, 0f); return; }
            var w = a.Value;
            Vector3 eye = Head != null ? Head.position : w.Orb - Vector3.forward;
            // the column: one flat sheet turned to the user (every card parallel to the next), its near edge two orb
            // radii off the orb, the first card level with the orb, the others above it
            var edge = w.Orb + w.Side * (w.Radius * 2.15f);
            var rot = Quaternion.LookRotation((edge + w.Side * (0.21f * w.Scale) - eye).normalized, Vector3.up);
            Vector3 up = rot * Vector3.up, across = rot * Vector3.right;
            float grows = Vector3.Dot(across, w.Side) >= 0f ? 1f : -1f; // +1: the column grows to the sheet's right
            across *= grows;                                             // along the sheet, away from the orb
            float y = 0f, bottom = 0f, top = 0f;
            bool first = true;
            void Place(UiPanel p, bool isToast, bool snap)
            {
                float sx = isToast ? w.Scale : p.RestLossyScale.x, sy = isToast ? w.Scale : p.RestLossyScale.y;
                float h = p.Height * sy, wd = p.Width * sx;
                if (first) { y = -h * 0.5f; bottom = y; first = false; }
                var target = edge + across * (wd * 0.5f + 0.014f) + up * (y + h * 0.5f);
                y += h + Gap;
                top = y - Gap;
                if (isToast) p.BaseScale = Vector3.one * w.Scale;
                var tr = p.transform;
                tr.position = snap ? target : Vector3.Lerp(tr.position, target, 0.16f);
                tr.rotation = rot;
            }
            bool pinned = Pinned != null && Pinned.gameObject.activeSelf;
            if (pinned) Place(Pinned, false, snapAll);
            int placed = 0;
            for (int i = 0; i < _toasts.Count; i++)
            {
                if (_toasts[i].Panel == null) continue;
                // a new card takes its place at once; the others slide up to make room
                Place(_toasts[i].Panel, true, snapAll || (snapNew && i == 0));
                placed++;
            }
            // the link starts at the orb's outer ring: that far back from the edge, along the sheet
            float link = Vector3.Dot(edge - (w.Orb + w.Side * (w.Radius * 1.72f)), across);
            Rail(placed > 0 || pinned, edge, rot, link * grows, bottom, top);
        }

        /// The rail of light that joins the cards to the orb, in the column's sheet: a link out of the orb's rings to
        /// the column's edge (`link` metres long; negative: the orb is to the sheet's right), a line up along that
        /// edge, a node of light where they meet. Panel shapes: two lines and a dot.
        private void Rail(bool on, Vector3 edge, Quaternion rot, float link, float bottom, float top)
        {
            if (!on) { if (_rail != null && _rail.gameObject.activeSelf) _rail.gameObject.SetActive(false); return; }
            var t = UiTheme.Current;
            if (_rail == null)
            {
                if (t == null || t.ShapeMaterial == null) return;
                var go = new GameObject("callout rail", typeof(MeshFilter), typeof(MeshRenderer));
                go.transform.SetParent(transform, false);
                _rail = go.AddComponent<UiShapeMesh>();
                _rail.Init(t.ShapeMaterial);
                var faint = t.Accent; faint.a = 0.55f;
                for (int i = 0; i < 2; i++) _rail.Add(new UiShape { Kind = UiShapeKind.Line, Tint = faint, Margin = 0.002f, State = 0.3f, Visible = true });
                _rail.Add(new UiShape { Kind = UiShapeKind.Dot, Size = new Vector2(0.006f, 0.006f), Margin = 0.003f, Tint = t.Accent, State = 0.6f, Visible = true });
            }
            if (!_rail.gameObject.activeSelf) _rail.gameObject.SetActive(true);
            _rail.transform.SetPositionAndRotation(edge, rot);
            // in the sheet's own axes (x to its right, y up): the link lies on the orb's side of the edge
            _rail.SetRect(0, new Vector3(-link * 0.5f, 0f, 0f), new Vector2(Mathf.Abs(link), 0.0020f));
            _rail.SetRect(1, new Vector3(0f, (bottom + top) * 0.5f, 0f), new Vector2(0.0020f, Mathf.Max(0.004f, top - bottom)));
            _rail.SetRect(2, Vector3.zero, new Vector2(0.006f, 0.006f));
            if (!Application.isPlaying) _rail.Flush();
        }

        public bool RailShown => _rail != null && _rail.gameObject.activeSelf;

        // no orb: in the upper field of view, newest lowest
        private void LayoutFree(bool snap)
        {
            if (Head == null) return;
            var fwd = Head.forward; fwd.y = 0f;
            if (fwd.sqrMagnitude < 1e-4f) fwd = Vector3.forward;
            fwd.Normalize();
            float y = 0.20f;
            for (int i = 0; i < _toasts.Count; i++)
            {
                var p = _toasts[i].Panel;
                if (p == null) continue;
                var target = Head.position + fwd * 0.75f + Vector3.up * (y + p.Height * 0.5f);
                y += p.Height + 0.006f;
                var tr = p.transform;
                tr.position = snap ? target : Vector3.Lerp(tr.position, target, 0.08f);
                tr.rotation = Quaternion.LookRotation(fwd, Vector3.up);
            }
        }
    }
}

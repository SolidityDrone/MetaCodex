using System;
using System.Collections.Generic;
using MetaCodex.Hands;
using UnityEngine;

namespace MetaCodex.UI
{
    /// Short messages. With the orb they are its callouts: cards on an arc round its right side, concentric with it,
    /// centred on its level (a pinned panel lowest, the voice HUD; then the newest message), each turned to the user,
    /// joined to the orb by a rail of light along the arc; the orb casts each card (UiPanelFx, the light variant). Without the orb they float
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

        /// Pointer input for callouts that do something when tapped (the shell gives its router).
        public Action<UiPanel> Register, Unregister;

        public void Show(string text, string kind = "info", float seconds = 3.2f) => Show(text, kind, seconds, null);

        /// A callout; with onTap the whole card is a button (a thread that answered: tap opens it).
        public void Show(string text, string kind, float seconds, Action onTap)
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
            if (onTap != null && Register != null)
            {
                panel.Add(new UiButton("", onTap, UiButtonStyle.Ghost) { Rect = new Rect(0.004f, 0.004f, panel.Width - 0.008f, panel.Height - 0.008f) });
                Register(panel);
            }
            panel.SetHighlight(0.6f);
            _toasts.Insert(0, new Toast { Panel = panel, Until = Time.unscaledTime + seconds + (two ? 1.2f : 0f) + (kind == "error" ? 1.5f : 0f) });
            while (_toasts.Count > 4) { Unregister?.Invoke(_toasts[_toasts.Count - 1].Panel); _toasts[_toasts.Count - 1].Panel.CloseAndDestroy(); _toasts.RemoveAt(_toasts.Count - 1); }
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
                if (_toasts[i].Panel != null) { Unregister?.Invoke(_toasts[i].Panel); _toasts[i].Panel.CloseAndDestroy(); } // and goes with it
                _toasts.RemoveAt(i);
            }
            // nothing shown and nothing left to put away (the rail, the extent): no layout this frame
            if (_toasts.Count == 0 && (Pinned == null || !Pinned.gameObject.activeSelf) && !RailShown && Extent == default) return;
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

        /// What the callouts cover now in the orb's plane (metres; x to the user's right, y up; zero when none): the
        /// shell keeps it off the window the user is watching.
        public Rect Extent { get; private set; }

        private const int ArcSegments = 10;
        private readonly List<UiPanel> _items = new List<UiPanel>();

        private void Layout(bool snapNew, bool snapAll = false)
        {
            var a = Where?.Invoke();
            if (!a.HasValue) { LayoutFree(snapNew); Rail(false, default, default, 0f, 0f, 0f, 0f); Extent = default; return; }
            var w = a.Value;
            Vector3 eye = Head != null ? Head.position : w.Orb - Vector3.forward;
            // owner 2026-11-11: "wrapped around the circle of the orb": the column is an arc round the orb's right side,
            // in the plane through the orb that faces the user; the stack is centred on the orb's level (the newest lowest),
            // each card's corner nearest the orb on one circle round it, every card turned to the user
            var fwd = w.Orb - eye;
            if (fwd.sqrMagnitude < 1e-6f) fwd = Vector3.forward;
            fwd.Normalize();
            var right = w.Side.sqrMagnitude > 1e-6f ? w.Side.normalized : Vector3.right;
            var up = Vector3.Cross(fwd, right).normalized;
            var plane = Quaternion.LookRotation(fwd, up);
            _items.Clear();
            bool pinned = Pinned != null && Pinned.gameObject.activeSelf;
            if (pinned) _items.Add(Pinned);
            foreach (var t in _toasts) if (t.Panel != null) _items.Add(t.Panel);
            if (_items.Count == 0) { Rail(false, default, default, 0f, 0f, 0f, 0f); Extent = default; return; }
            float H(UiPanel p) => p.Height * (p == Pinned ? p.RestLossyScale.y : w.Scale);
            float Wd(UiPanel p) => p.Width * (p == Pinned ? p.RestLossyScale.x : w.Scale);
            float total = -Gap;
            foreach (var p in _items) total += H(p) + Gap;
            float ring = w.Radius * 1.75f;
            // the circle: clear of the orb's rings by a little more than the old straight column, wide enough that the
            // top and bottom cards still bow round it
            float y = -total * 0.5f, maxNear = 0f;
            foreach (var p in _items) { float h = H(p); maxNear = Mathf.Max(maxNear, Mathf.Max(0f, Mathf.Abs(y + h * 0.5f) - h * 0.5f)); y += h + Gap; }
            float rc = Mathf.Max(w.Radius * 2.15f + 0.014f, maxNear * 1.2f);
            y = -total * 0.5f;
            float xMax = 0f, aLow = 0f, aHigh = 0f;
            for (int i = 0; i < _items.Count; i++)
            {
                var p = _items[i];
                float h = H(p), wd = Wd(p), cy = y + h * 0.5f;
                float yNear = Mathf.Sign(cy) * Mathf.Max(0f, Mathf.Abs(cy) - h * 0.5f);
                float xNear = Mathf.Sqrt(Mathf.Max(rc * rc - yNear * yNear, rc * rc * 0.25f));
                float ang = Mathf.Atan2(yNear, xNear);
                if (i == 0) { aLow = ang; aHigh = ang; } else { aLow = Mathf.Min(aLow, ang); aHigh = Mathf.Max(aHigh, ang); }
                xMax = Mathf.Max(xMax, xNear + wd);
                var target = w.Orb + right * (xNear + wd * 0.5f) + up * cy;
                bool isToast = p != Pinned;
                if (isToast) p.BaseScale = Vector3.one * w.Scale;
                // a new card takes its place at once; the others slide to make room
                bool snap = snapAll || (snapNew && isToast && _toasts.Count > 0 && p == _toasts[0].Panel);
                var tr = p.transform;
                var pos = snap ? target : Vector3.Lerp(tr.position, target, 0.16f);
                var look = pos - eye;
                tr.SetPositionAndRotation(pos, look.sqrMagnitude > 1e-6f ? Quaternion.LookRotation(look.normalized, Vector3.up) : plane);
                y += h + Gap;
            }
            Extent = new Rect(ring, -total * 0.5f, xMax - ring, total);
            // the rail: out of the orb's rings to the arc, then along the arc past the cards' near corners
            float rr = rc - 0.005f;
            float pad = Mathf.Max(0.12f, (_items.Count == 1 ? H(_items[0]) * 0.5f / rr : 0f));
            Rail(true, w.Orb, plane, ring, rr, aLow - pad, aHigh + pad);
        }

        /// The rail of light that joins the cards to the orb, in the orb's plane: a link out of its rings to the arc
        /// (`from` to `radius` along the user's right), an arc of `radius` from angle `a0` to `a1` (radians, 0 = the
        /// user's right, up positive), a node of light where they meet. Panel shapes: lines and a dot.
        private void Rail(bool on, Vector3 orb, Quaternion rot, float from, float radius, float a0, float a1)
        {
            if (!on) { if (_rail != null && _rail.gameObject.activeSelf) _rail.gameObject.SetActive(false); return; }
            var t = UiTheme.Current;
            if (_rail == null)
            {
                if (t == null || t.ShapeMaterial == null) return;
                var go = new GameObject("callout rail", typeof(MeshFilter), typeof(MeshRenderer));
                go.transform.SetParent(transform, false);
                _rail = go.AddComponent<UiShapeMesh>();
                _rail.Init(t.ShapeMaterial, false);
                var faint = t.Accent; faint.a = 0.55f;
                for (int i = 0; i < 1 + ArcSegments; i++) _rail.Add(new UiShape { Kind = UiShapeKind.Line, Tint = faint, Margin = 0.002f, State = 0.3f, Visible = true });
                _rail.Add(new UiShape { Kind = UiShapeKind.Dot, Size = new Vector2(0.006f, 0.006f), Margin = 0.003f, Tint = t.Accent, State = 0.6f, Visible = true });
            }
            if (!_rail.gameObject.activeSelf) _rail.gameObject.SetActive(true);
            _rail.transform.SetPositionAndRotation(orb, rot);
            // in the plane's own axes (x the user's right, y up)
            float link = Mathf.Max(0.002f, radius - from);
            _rail.SetRect(0, new Vector3(from + link * 0.5f, 0f, 0f), new Vector2(link, 0.0020f));
            float lo = Mathf.Min(a0, 0f), hi = Mathf.Max(a1, 0f), step = (hi - lo) / ArcSegments;
            for (int i = 0; i < ArcSegments; i++)
            {
                float m = lo + step * (i + 0.5f);
                var s = _rail.Get(1 + i);
                s.Center = new Vector3(Mathf.Cos(m) * radius, Mathf.Sin(m) * radius, 0f);
                s.Size = new Vector2(2f * radius * Mathf.Sin(step * 0.5f) + 0.0012f, 0.0020f);
                s.Rotation = m + Mathf.PI * 0.5f; // along the arc
                _rail.Set(1 + i, s);
            }
            _rail.SetRect(1 + ArcSegments, new Vector3(radius, 0f, 0f), new Vector2(0.006f, 0.006f));
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

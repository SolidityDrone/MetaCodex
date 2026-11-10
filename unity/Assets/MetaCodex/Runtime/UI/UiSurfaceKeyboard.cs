using System;
using System.Collections.Generic;
using MetaCodex.Hands;
using TMPro;
using UnityEngine;

namespace MetaCodex.UI
{
    public enum KbKind { Char, Backspace, Enter, Tab, Escape, Shift, Ctrl, Alt, Space, Left, Right, Up, Down, Paste, Hide }

    /// Where the keys go: a UI text field (UiKeyboard), the focused terminal, a web page.
    public interface IKeySink
    {
        /// Shown on the keyboard: what is being typed into.
        string Title { get; }
        /// The text so far (a field; masked when secure), null when there is none (a terminal).
        string Preview { get; }
        void Type(string text, bool ctrl, bool alt);
        void Key(KbKind key, bool ctrl, bool alt);
        /// A field ends with Enter / Escape (the keyboard goes); a terminal or a page keep it until ⌨↓.
        bool KeepsKeyboard { get; }
    }

    /// MetaCodex's own keyboard (owner 2026-11-10: "I don't ever wanna see the default one"; "detect hands on a table,
    /// spawn the keyboard and lock it on the flat surface, a touchpad on the right, like the Experimental one"):
    /// rest both hands flat on a table for a moment and the keyboard is laid on it under them, F under the left index,
    /// J under the right, keys scaled to the hands' spread, locked there; a fingertip that lifts and taps the table
    /// types the key under it (resting fingers type nothing), with the touchpad on the right (one finger moves a
    /// cursor over the window you look at, a tap clicks, two fingers scroll). Until hands rest on a table it floats in
    /// front of you (poke or pinch its keys). Horizon OS's own keyboard is never opened (UiKeyboard routes here).
    public sealed class UiSurfaceKeyboard : MonoBehaviour
    {
        public enum KbMode { Hidden, Air, Surface }

        // ---- geometry (metres, the nominal key pitch; the panel is scaled to the hands)
        public const float Pitch = 0.019f, KeyGap = 0.0022f, PadGapU = 0.55f, PadWU = 5.2f, StripU = 0.95f, KeysWU = 15f;
        // ---- typing thresholds: fingertip joint height above where that hand rests (m), speeds (m/s)
        public float PressHeight = 0.0055f, ReleaseHeight = 0.013f, HoverHeight = 0.030f, MinTapSpeed = 0.045f, TouchHeight = 0.009f;

        public KbMode Mode { get; private set; } = KbMode.Hidden;
        public bool Visible => Mode != KbMode.Hidden;
        public IKeySink Sink { get; private set; }
        public UiPanel Panel => _panel;
        public bool Shift => _shift || _caps;
        public bool Caps => _caps;
        public bool CtrlOn => _ctrl;
        public bool AltOn => _alt;
        /// The touchpad's cursor: on this panel, at this point (null: none yet).
        public UiPanel CursorPanel { get; private set; }
        public Vector2 CursorPoint { get; private set; }
        /// Self-tests and the shell: a key was sent (kind, text).
        public event Action<KbKind, string> Sent;

        private sealed class KeyDef
        {
            public string Label, Shifted;
            public KbKind Kind;
            public float W, X; // units
            public int Row;
            public UiButton Button;
            public Rect Rect;  // panel coordinates
        }

        private readonly List<KeyDef> _keys = new List<KeyDef>();
        private UiPanel _panel;
        private UiLabel _strip, _padLabel;
        private int _padShape = -1;
        private Rect _padRect;
        private UiInputRouter _router;
        private Func<Transform> _head;
        private Func<IHandSource> _hands;
        private Transform _root;
        private bool _shift, _caps, _ctrl, _alt, _registered;
        private float _shiftAt = -10f;
        private string _hint = "";

        // surface state: the plane and where each hand rests
        private float[] _restY = new float[2];
        private Vector3 _fjMid; // where F and J's midpoint lies on the table (re-placement only for a real move)
        private float _restSince = -1f, _restLeftAt = -10f;
        private Vector3[] _prevTip = new Vector3[10];
        private float[] _smoothH = new float[10];
        private bool[] _up = new bool[10], _seen = new bool[10];
        private float _prevTime, _dt = 1f / 72f;
        // a press waits a moment: three fingers of one hand landing together is a hand put down, not typing
        private readonly List<(int tip, KeyDef key, float at)> _pending = new List<(int, KeyDef, float)>();
        private KeyDef _repeatKey;
        private int _repeatTip = -1;
        private float _repeatAt;
        // the touchpad
        private int _padTip = -1, _padTip2 = -1;
        private Vector2 _padStart, _padLast;
        private float _padDownAt;
        private bool _padMoved;
        private GameObject _cursorGo;

        public void Init(UiInputRouter router, Func<Transform> head, Func<IHandSource> hands, Transform root)
        {
            _router = router; _head = head; _hands = hands; _root = root;
            Build();
            _panel.gameObject.SetActive(false);
        }

        // ------------------------------------------------------------------ layout

        private void Row(int row, params (string label, string shifted, KbKind kind, float w)[] keys)
        {
            float x = 0f;
            foreach (var k in keys)
            {
                _keys.Add(new KeyDef { Label = k.label, Shifted = k.shifted ?? k.label, Kind = k.kind, W = k.w, X = x, Row = row });
                x += k.w;
            }
        }

        private static (string, string, KbKind, float) C(string normal, string shifted) => (normal, shifted, KbKind.Char, 1f);

        private void BuildLayout()
        {
            _keys.Clear();
            Row(0, C("`", "~"), C("1", "!"), C("2", "@"), C("3", "#"), C("4", "$"), C("5", "%"), C("6", "^"), C("7", "&"), C("8", "*"), C("9", "("), C("0", ")"),
                C("-", "_"), C("=", "+"), ("⌫", null, KbKind.Backspace, 2f));
            Row(1, ("TAB", null, KbKind.Tab, 1.5f), C("q", "Q"), C("w", "W"), C("e", "E"), C("r", "R"), C("t", "T"), C("y", "Y"), C("u", "U"), C("i", "I"),
                C("o", "O"), C("p", "P"), C("[", "{"), C("]", "}"), C("\\", "|") );
            _keys[_keys.Count - 1].W = 1.5f;
            Row(2, ("CTRL", null, KbKind.Ctrl, 1.75f), C("a", "A"), C("s", "S"), C("d", "D"), C("f", "F"), C("g", "G"), C("h", "H"), C("j", "J"), C("k", "K"),
                C("l", "L"), C(";", ":"), C("'", "\""), ("⏎", null, KbKind.Enter, 2.25f));
            Row(3, ("⇧", null, KbKind.Shift, 2.25f), C("z", "Z"), C("x", "X"), C("c", "C"), C("v", "V"), C("b", "B"), C("n", "N"), C("m", "M"), C(",", "<"),
                C(".", ">"), C("/", "?"), ("⇧", null, KbKind.Shift, 2.75f));
            Row(4, ("ESC", null, KbKind.Escape, 1.25f), ("ALT", null, KbKind.Alt, 1.25f), ("⎘", null, KbKind.Paste, 1.25f), (" ", null, KbKind.Space, 6.25f),
                ("←", null, KbKind.Left, 1f), ("↑", null, KbKind.Up, 1f), ("↓", null, KbKind.Down, 1f), ("→", null, KbKind.Right, 1f), ("⌨↓", null, KbKind.Hide, 1f));
        }

        public static float WidthU => KeysWU + PadGapU + PadWU;
        private static float PanelW => WidthU * Pitch + 2f * 0.008f;
        private static float PanelH => (5f + StripU) * Pitch + 2f * 0.008f;
        private const float Pad = 0.008f;

        private Rect KeyRect(KeyDef k) => new Rect(Pad + k.X * Pitch + KeyGap * 0.5f, Pad + (StripU + k.Row) * Pitch + KeyGap * 0.5f, k.W * Pitch - KeyGap, Pitch - KeyGap);

        /// F's and J's centres in panel coordinates (the keyboard is placed so the index fingers rest on them).
        private Vector2 KeyCentre(string label) { foreach (var k in _keys) if (k.Label == label) return KeyRect(k).center; return Vector2.zero; }

        private void Build()
        {
            BuildLayout();
            var t = UiTheme.Current;
            _panel = UiPanel.Create<UiPanel>("keyboard", "", PanelW, PanelH, _root, header: false);
            _panel.Closable = false;
            _panel.Draggable = false;
            _panel.LightTransition = true;
            foreach (var k in _keys)
            {
                var kk = k;
                k.Rect = KeyRect(k);
                string label = k.Kind == KbKind.Space ? "" : k.Label;
                k.Button = _panel.Add(new UiButton(label, () => Press(kk)) { Rect = k.Rect, TextSize = k.Kind == KbKind.Char ? 0.0105f : 0.0082f });
            }
            _padRect = new Rect(Pad + (KeysWU + PadGapU) * Pitch, Pad + StripU * Pitch + KeyGap * 0.5f, PadWU * Pitch - KeyGap, 5f * Pitch - KeyGap);
            _padShape = _panel.Shapes.Add(_panel.Shape(UiShapeKind.Button, _padRect, t.Frame, 0.006f, 0f, 0.004f));
            _padLabel = _panel.Add(new UiLabel("TOUCHPAD", 0.0075f) { Rect = new Rect(_padRect.x, _padRect.yMax - 0.016f, _padRect.width, 0.014f), Align = TextAlignmentOptions.Center, Color = t.TextDim, UseDisplay = true });
            _strip = _panel.Add(new UiLabel("", 0.0085f) { Rect = new Rect(Pad, Pad * 0.5f, PanelW - 2f * Pad, StripU * Pitch), Color = t.TextAccent, UseDisplay = true });
            _cursorGo = GameObject.CreatePrimitive(PrimitiveType.Sphere);
            _cursorGo.name = "touchpad cursor";
            var col = _cursorGo.GetComponent<Collider>();
            if (col != null) { if (Application.isPlaying) Destroy(col); else DestroyImmediate(col); }
            _cursorGo.transform.SetParent(_root, false);
            _cursorGo.transform.localScale = Vector3.one * 0.009f;
            var sh = Shader.Find("Universal Render Pipeline/Unlit");
            if (sh != null) _cursorGo.GetComponent<MeshRenderer>().sharedMaterial = new Material(sh) { color = new Color(0.55f, 0.92f, 1f) };
            _cursorGo.SetActive(false);
        }

        // ------------------------------------------------------------------ showing

        /// Opens the keyboard for a sink (a field, the terminal, a page). Already on a table: it stays there.
        public void Show(IKeySink sink)
        {
            Sink = sink;
            if (Mode == KbMode.Hidden)
            {
                if (_lastSurfaceUsable && LastSurfaceNear()) { ApplySurface(); SetMode(KbMode.Surface); }
                else { PlaceInAir(); SetMode(KbMode.Air); }
            }
            RefreshStrip();
        }

        public void Hide()
        {
            if (Mode == KbMode.Hidden) return;
            SetMode(KbMode.Hidden);
            Sink = null;
            _pending.Clear();
            _repeatKey = null;
            if (_cursorGo != null) _cursorGo.SetActive(false);
        }

        private void SetMode(KbMode m)
        {
            Mode = m;
            bool on = m != KbMode.Hidden;
            if (on && !_panel.gameObject.activeSelf) { _panel.gameObject.SetActive(true); _panel.Open(); }
            else if (!on && _panel.gameObject.activeSelf) _panel.Close();
            // in the air its keys are poked or pinched like any button; on a table the fingertips' height decides
            bool wantRouter = m == KbMode.Air;
            if (_router != null && wantRouter != _registered)
            {
                if (wantRouter) _router.Register(_panel); else _router.Unregister(_panel);
                _registered = wantRouter;
            }
            _padLabel?.SetVisible(m == KbMode.Surface);
            _hint = m == KbMode.Air ? "rest both hands flat on a table to type on it" : "";
            for (int i = 0; i < 10; i++) { _up[i] = false; _seen[i] = false; }
            RefreshStrip();
        }

        private void PlaceInAir()
        {
            var head = _head?.Invoke();
            Vector3 eye = head != null ? head.position : new Vector3(0f, 1.4f, 0f);
            Vector3 fwd = head != null ? head.forward : Vector3.forward;
            fwd.y = 0f;
            if (fwd.sqrMagnitude < 1e-4f) fwd = Vector3.forward;
            fwd.Normalize();
            var pos = eye + fwd * 0.42f + Vector3.down * 0.30f;
            // tilted towards the eyes like a lectern
            var tilt = Quaternion.LookRotation(Quaternion.AngleAxis(48f, Vector3.Cross(Vector3.up, fwd)) * fwd, Vector3.up);
            _panel.transform.SetPositionAndRotation(pos, tilt);
            _panel.BaseScale = Vector3.one;
        }

        // the surface the keyboard lies on, kept for the next time it is asked for
        private bool _lastSurfaceUsable;
        private Vector3 _surfPos;
        private Quaternion _surfRot;
        private float _surfScale = 1f;

        private bool LastSurfaceNear()
        {
            var head = _head?.Invoke();
            return head == null || Vector3.Distance(head.position, _surfPos) < 1.0f;
        }

        private void ApplySurface()
        {
            _panel.transform.SetPositionAndRotation(_surfPos, _surfRot);
            _panel.BaseScale = Vector3.one * _surfScale;
        }

        /// Lays the keyboard on the table under two resting hands: F under the left index tip, J under the right.
        private void PlaceOnSurface(HandFrame l, HandFrame r)
        {
            Vector3 li = l.IndexTip, ri = r.IndexTip;
            float plane = Mathf.Min(_restY[0], _restY[1]) - 0.006f; // the drawn keys sit on the table, under the joints
            var across = ri - li; across.y = 0f;
            float spread = across.magnitude;
            if (spread < 0.04f) return;
            across /= spread;
            var away = Vector3.Cross(across, Vector3.up); // to the user's front
            Vector2 f = KeyCentre("f"), j = KeyCentre("j");
            float nominal = Mathf.Abs(j.x - f.x);
            float scale = Mathf.Clamp(spread / Mathf.Max(nominal, 1e-4f), 16f / 19f, 25f / 19f);
            var rot = Quaternion.LookRotation(Vector3.down, away);
            var mid = (li + ri) * 0.5f; mid.y = plane;
            var midLocal = _panel.PanelToLocal((f + j) * 0.5f) * scale;
            _surfPos = mid - rot * midLocal;
            _surfPos.y = plane;
            _surfRot = rot;
            _surfScale = scale;
            _fjMid = mid;
            _lastSurfaceUsable = true;
            ApplySurface();
            SetMode(KbMode.Surface);
        }

        // ------------------------------------------------------------------ every frame

        private static Vector3 Tip(HandFrame f, int i) => i == 0 ? f.ThumbTip : i == 1 ? f.IndexTip : i == 2 ? f.MiddleTip : i == 3 ? f.RingTip : f.LittleTip;

        private void Update() => Tick(Time.unscaledTime);

        public void Tick(float now)
        {
            if (Mode == KbMode.Hidden) return;
            var src = _hands?.Invoke();
            HandFrame l = default, r = default;
            bool hl = src != null && src.TryGet(Handedness.Left, out l) && l.Tracked;
            bool hr = src != null && src.TryGet(Handedness.Right, out r) && r.Tracked;
            float dt = Mathf.Clamp(now - _prevTime, 1e-3f, 0.1f);
            _prevTime = now;
            _dt = dt;

            // both hands at rest, flat, on one level: a table under them
            bool resting = hl && hr && Resting(l, Handedness.Left) && Resting(r, Handedness.Right);
            if (resting)
            {
                float yl = RestHeight(l), yr = RestHeight(r);
                if (Mathf.Abs(yl - yr) > 0.035f) resting = false;
                else if (_restSince < 0f) _restSince = now;
                if (resting && now - _restSince > 0.40f)
                {
                    var mid = (l.IndexTip + r.IndexTip) * 0.5f;
                    bool far = Mode != KbMode.Surface || Vector2.Distance(new Vector2(mid.x, mid.z), new Vector2(_fjMid.x, _fjMid.z)) > 0.09f || Mathf.Abs(Mathf.Min(yl, yr) - 0.006f - _surfPos.y) > 0.03f;
                    if (far) { _restY[0] = yl; _restY[1] = yr; PlaceOnSurface(l, r); }
                    else { _restY[0] = Mathf.Lerp(_restY[0], yl, 0.05f); _restY[1] = Mathf.Lerp(_restY[1], yr, 0.05f); } // tracking drifts a little
                }
            }
            else _restSince = -1f;

            if (Mode == KbMode.Surface)
            {
                for (int h = 0; h < 2; h++)
                {
                    bool ok = h == 0 ? hl : hr;
                    var f = h == 0 ? l : r;
                    for (int i = 0; i < 5; i++) TickTip(h * 5 + i, ok, ok ? Tip(f, i) : Vector3.zero, h, i, dt, now);
                }
                FlushPending(now);
                TickRepeat(now);
            }
            RefreshHover();
        }

        /// Flat, palm down, fingers out and still, below the eyes.
        private bool Resting(HandFrame f, Handedness h)
        {
            if (Vector3.Dot(f.PalmNormal(h), Vector3.down) < 0.72f) return false;
            float lo = float.MaxValue, hi = float.MinValue;
            for (int i = 1; i < 5; i++) { float y = Tip(f, i).y; lo = Mathf.Min(lo, y); hi = Mathf.Max(hi, y); }
            if (hi - lo > 0.028f) return false;
            if (f.Palm.y - lo > 0.075f || f.Palm.y - lo < -0.01f) return false; // a flat hand: the palm just over the tips
            var head = _head?.Invoke();
            if (head != null && head.position.y - lo < 0.22f) return false;
            int hi0 = h == Handedness.Left ? 0 : 5;
            for (int i = 1; i < 5; i++)
                if (_seen[hi0 + i] && (Tip(f, i) - _prevTip[hi0 + i]).magnitude / _dt > 0.08f) return false;
            return true;
        }

        private static float RestHeight(HandFrame f)
        {
            float s = 0f;
            for (int i = 1; i < 5; i++) s += Tip(f, i).y;
            return s / 4f;
        }

        private void TickTip(int idx, bool tracked, Vector3 tip, int hand, int finger, float dt, float now)
        {
            if (!tracked) { _seen[idx] = false; _up[idx] = false; return; }
            float h = tip.y - _restY[hand];
            float prev = _seen[idx] ? _smoothH[idx] : h;
            float sm = _seen[idx] ? Mathf.Lerp(prev, h, 0.6f) : h;
            float speed = (prev - sm) / dt; // > 0: going down
            var lastTip = _prevTip[idx];
            _prevTip[idx] = tip;
            bool wasSeen = _seen[idx];
            _seen[idx] = true;
            _smoothH[idx] = sm;
            if (!wasSeen) { _up[idx] = sm > ReleaseHeight; return; }
            if (sm > ReleaseHeight) _up[idx] = true;
            if (_repeatTip == idx && sm > PressHeight * 1.6f) { _repeatKey = null; _repeatTip = -1; }
            _panel.SurfaceOf(tip, out var pp, out _);
            // the touchpad: index or middle fingers only
            if (_padRect.Contains(pp) && finger >= 1 && finger <= 2) { TickPad(idx, pp, sm, now); return; }
            if (_padTip == idx && sm > TouchHeight * 1.5f) EndPad(now);
            if (_padTip2 == idx && sm > TouchHeight * 1.5f) _padTip2 = -1;
            if (!_up[idx] || sm > PressHeight || speed < MinTapSpeed) return;
            _up[idx] = false;
            var key = KeyAt(pp);
            if (key == null) return;
            if (finger == 0 && key.Row < 4) return; // thumbs type the bottom row only (space)
            _pending.Add((idx, key, now));
        }

        private KeyDef KeyAt(Vector2 pp)
        {
            KeyDef best = null;
            float bestD = float.MaxValue;
            foreach (var k in _keys)
            {
                var r = k.Rect;
                // a little slack between keys: the nearest one within half a gap more
                var grown = new Rect(r.x - KeyGap, r.y - KeyGap, r.width + 2f * KeyGap, r.height + 2f * KeyGap);
                if (!grown.Contains(pp)) continue;
                float d = (r.center - pp).sqrMagnitude;
                if (d < bestD) { bestD = d; best = k; }
            }
            return best;
        }

        private void FlushPending(float now)
        {
            for (int i = _pending.Count - 1; i >= 0; i--)
            {
                var p = _pending[i];
                if (now - p.at < 0.055f) continue;
                int hand = p.tip / 5;
                int together = 0;
                foreach (var q in _pending) if (q.tip / 5 == hand && Mathf.Abs(q.at - p.at) < 0.08f) together++;
                _pending.RemoveAt(i);
                if (together >= 3) continue; // a hand landing
                Press(p.key);
                if (p.key.Kind == KbKind.Backspace || p.key.Kind == KbKind.Left || p.key.Kind == KbKind.Right || p.key.Kind == KbKind.Up || p.key.Kind == KbKind.Down)
                { _repeatKey = p.key; _repeatTip = p.tip; _repeatAt = now + 0.45f; }
            }
            // drop what is left of a landing hand
            if (_pending.Count >= 3) { var h0 = _pending[0].tip / 5; if (_pending.TrueForAll(q => q.tip / 5 == h0)) _pending.Clear(); }
        }

        private void TickRepeat(float now)
        {
            if (_repeatKey == null || now < _repeatAt) return;
            _repeatAt = now + 0.07f;
            Press(_repeatKey, repeat: true);
        }

        // ------------------------------------------------------------------ the touchpad

        private void TickPad(int idx, Vector2 pp, float h, float now)
        {
            bool touching = h < TouchHeight;
            if (!touching) { if (_padTip == idx) EndPad(now); if (_padTip2 == idx) _padTip2 = -1; return; }
            if (_padTip < 0) { _padTip = idx; _padStart = _padLast = pp; _padDownAt = now; _padMoved = false; EnsureCursorPanel(); return; }
            if (_padTip != idx) { if (_padTip2 < 0) _padTip2 = idx; return; }
            var d = pp - _padLast;
            _padLast = pp;
            if ((pp - _padStart).magnitude > 0.008f) _padMoved = true;
            if (CursorPanel == null) return;
            if (_padTip2 >= 0)
            {
                // two fingers: scroll what is under the cursor
                var el = CursorPanel.HitTest(CursorPoint);
                if (el is UiList list && Mathf.Abs(d.y) > 1e-4f) list.ScrollBy(-d.y * 160f);
                return;
            }
            float speed = d.magnitude / _dt;
            float gain = Mathf.Lerp(2.2f, 5.5f, Mathf.InverseLerp(0.02f, 0.25f, speed)); // pointer acceleration
            var c = CursorPoint + d * gain;
            CursorPoint = new Vector2(Mathf.Clamp(c.x, 0f, CursorPanel.Width), Mathf.Clamp(c.y, 0f, CursorPanel.Height));
            PlaceCursor();
        }

        private void EndPad(float now)
        {
            if (_padTip >= 0 && !_padMoved && now - _padDownAt < 0.30f && CursorPanel != null) ClickAtCursor();
            _padTip = -1;
            _padTip2 = -1;
        }

        /// The cursor lives on the window you look at when a touch starts (the active one if you look at none).
        private void EnsureCursorPanel()
        {
            var head = _head?.Invoke();
            UiPanel best = null;
            float bestDot = 0.90f;
            if (head != null && _router != null)
                foreach (var p in _router.Panels)
                {
                    if (p == null || p == _panel || !p.IsOpen) continue;
                    float dot = Vector3.Dot(head.forward, (p.transform.position - head.position).normalized);
                    if (dot > bestDot) { bestDot = dot; best = p; }
                }
            best ??= _router?.ActivePanel;
            if (best == null) return;
            if (best != CursorPanel) { CursorPanel = best; CursorPoint = new Vector2(best.Width * 0.5f, best.Height * 0.5f); }
            PlaceCursor();
        }

        private void PlaceCursor()
        {
            if (_cursorGo == null || CursorPanel == null) return;
            _cursorGo.SetActive(true);
            _cursorGo.transform.position = CursorPanel.SurfacePoint(CursorPoint, -0.004f);
        }

        private void ClickAtCursor()
        {
            var el = CursorPanel.HitTest(CursorPoint);
            if (el == null) return;
            var e = new UiPointerEvent { Point = CursorPoint, PressPoint = CursorPoint };
            el.OnPress(e);
            el.OnRelease(e, true);
            UiSounds.Fire("click");
        }

        // ------------------------------------------------------------------ keys

        private void Press(KeyDef k, bool repeat = false)
        {
            if (Sink == null) return;
            bool ctrl = _ctrl, alt = _alt;
            switch (k.Kind)
            {
                case KbKind.Shift:
                    if (_caps) { _caps = false; _shift = false; }
                    else if (_shift && Time.unscaledTime - _shiftAt < 0.45f) { _caps = true; _shift = false; }
                    else { _shift = !_shift; _shiftAt = Time.unscaledTime; }
                    RefreshLabels();
                    break;
                case KbKind.Ctrl: _ctrl = !_ctrl; RefreshLabels(); break;
                case KbKind.Alt: _alt = !_alt; RefreshLabels(); break;
                case KbKind.Hide: Hide(); return;
                case KbKind.Char:
                case KbKind.Space:
                {
                    string s = k.Kind == KbKind.Space ? " " : Shift ? k.Shifted : k.Label;
                    Sink.Type(s, ctrl, alt);
                    Sent?.Invoke(k.Kind, s);
                    if (_shift && !_caps) { _shift = false; RefreshLabels(); }
                    if (ctrl || alt) { _ctrl = _alt = false; RefreshLabels(); }
                    break;
                }
                default:
                    Sink.Key(k.Kind, ctrl, alt);
                    Sent?.Invoke(k.Kind, null);
                    if (ctrl || alt) { _ctrl = _alt = false; RefreshLabels(); }
                    if (!Sink.KeepsKeyboard && (k.Kind == KbKind.Enter || k.Kind == KbKind.Escape)) { Hide(); return; }
                    break;
            }
            if (!repeat) { k.Button?.Flash(); UiSounds.Fire("key"); }
            RefreshStrip();
        }

        private void RefreshLabels()
        {
            foreach (var k in _keys)
            {
                if (k.Button == null) continue;
                if (k.Kind == KbKind.Char) k.Button.SetText(Shift ? k.Shifted : k.Label);
                else if (k.Kind == KbKind.Shift) { k.Button.SetSelected(_shift || _caps); k.Button.SetText(_caps ? "⇪" : "⇧"); }
                else if (k.Kind == KbKind.Ctrl) k.Button.SetSelected(_ctrl);
                else if (k.Kind == KbKind.Alt) k.Button.SetSelected(_alt);
            }
            RefreshStrip();
        }

        /// The fingertips near the keys light the key under them.
        private readonly HashSet<UiButton> _hovered = new HashSet<UiButton>(), _hoverNow = new HashSet<UiButton>();

        private void RefreshHover()
        {
            _hoverNow.Clear();
            if (Mode == KbMode.Surface)
                for (int i = 0; i < 10; i++)
                {
                    if (!_seen[i] || _smoothH[i] > HoverHeight) continue;
                    _panel.SurfaceOf(_prevTip[i], out var pp, out _);
                    var k = KeyAt(pp);
                    if (k?.Button != null) _hoverNow.Add(k.Button);
                }
            foreach (var b in _hovered) if (!_hoverNow.Contains(b)) b.SetHover(false);
            foreach (var b in _hoverNow) if (!_hovered.Contains(b)) b.SetHover(true);
            _hovered.Clear();
            foreach (var b in _hoverNow) _hovered.Add(b);
        }

        public void RefreshStrip()
        {
            if (_strip == null) return;
            string mods = (_caps ? "⇪ " : _shift ? "⇧ " : "") + (_ctrl ? "CTRL " : "") + (_alt ? "ALT " : "");
            string target = Sink != null ? Sink.Title : "";
            string preview = Sink?.Preview;
            string text = "⌨ " + target + (preview != null ? "  ▸ " + Tail(preview, 46) + "▏" : "") + (mods.Length > 0 ? "   " + mods : "") + (_hint.Length > 0 ? "   · " + _hint : "");
            _strip.Set(text);
        }

        private static string Tail(string s, int n) => s.Length <= n ? s : "…" + s.Substring(s.Length - n);

        // ------------------------------------------------------------------ self-tests

        public Rect KeyRectForTest(string label) { foreach (var k in _keys) if (k.Label == label) return k.Rect; return default; }
        public Vector3 KeyWorldForTest(string label, float height = 0f)
        {
            var r = KeyRectForTest(label);
            return _panel.transform.TransformPoint(_panel.PanelToLocal(r.center)) + Vector3.up * height;
        }
        public Vector3 PadWorldForTest(Vector2 uv) => _panel.transform.TransformPoint(_panel.PanelToLocal(new Vector2(_padRect.x + uv.x * _padRect.width, _padRect.y + uv.y * _padRect.height)));
        public float RestHeightForTest(int hand) => _restY[hand];
        public int KeyCountForTest => _keys.Count;
        public void PressForTest(string label) { foreach (var k in _keys) if (k.Label == label) { Press(k); return; } }
        public string StripTextForTest => _strip != null ? _strip.Text : "";
        public float SurfaceScaleForTest => _surfScale;
        public void SetCursorForTest(UiPanel p, Vector2 point) { CursorPanel = p; CursorPoint = point; PlaceCursor(); }
        public void ForgetSurfaceForTest() { _lastSurfaceUsable = false; }
    }
}

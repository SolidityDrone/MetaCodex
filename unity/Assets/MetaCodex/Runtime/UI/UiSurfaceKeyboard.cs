using System;
using System.Collections.Generic;
using MetaCodex.Hands;
using MetaCodex.Typing;
using TMPro;
using UnityEngine;

namespace MetaCodex.UI
{
    /// Where the keys go: a UI text field (UiKeyboard), the focused terminal, a web page.
    public interface IKeySink
    {
        /// Shown on the keyboard: what is being typed into.
        string Title { get; }
        /// The text so far (a field; masked when secure), null when there is none (a terminal).
        string Preview { get; }
        void Type(string text, bool ctrl, bool alt);
        /// A key that is not a character; `fn` is 1..12 for a function key.
        void Key(KbKind key, bool ctrl, bool alt, bool shift, int fn);
        /// A field ends with Enter / Escape (the keyboard goes); a terminal or a page keep it until it is hidden.
        bool KeepsKeyboard { get; }
        /// Sentences, commands, or a secret: how much the letter statistics may help to choose a key (KeyDecoder).
        KeyContext Context { get; }
    }

    /// One key of the keyboard: a plain rounded cap with its legend, like a real one. In the air it is poked or
    /// pinched like any button; on a table the keyboard lights and presses it itself.
    public sealed class UiKeycap : UiElement
    {
        public readonly KeyDef Key;
        public Action OnPressed;
        public override bool Interactive => true;
        private int _shape = -1;
        private UiText _legend, _top;
        private float _flash;
        private bool _latched, _lit;

        public UiKeycap(KeyDef key) { Key = key; }

        protected override void Build()
        {
            // the cap is the key's cell less the gap between caps
            var cap = new Rect(Rect.x + UiSurfaceKeyboard.CapGap * 0.5f, Rect.y + UiSurfaceKeyboard.CapGap * 0.5f, Rect.width - UiSurfaceKeyboard.CapGap, Rect.height - UiSurfaceKeyboard.CapGap);
            var s = Panel.Shape(UiShapeKind.Keycap, cap, Theme.Accent, 0.0022f, 0f, 0.0016f);
            s.Border = 0.0005f;
            _shape = Panel.Shapes.Add(s);
            bool word = Key.Label.Length > 1; // esc, tab, shift, F5, pg up ...
            if (Key.Top != null)
            {
                _top = UiText.Create(Panel, new Rect(cap.x, cap.y + 0.0006f, cap.width, cap.height * 0.5f), Key.Top, Theme.Body, 0.0062f, Color.Lerp(Theme.Text, Theme.TextDim, 0.4f), TextAlignmentOptions.Center);
                _legend = UiText.Create(Panel, new Rect(cap.x, cap.y + cap.height * 0.46f, cap.width, cap.height * 0.5f), Key.Label, Theme.Body, 0.0066f, Theme.Text, TextAlignmentOptions.Center);
            }
            else if (Key.Label.Length > 0)
                _legend = UiText.Create(Panel, cap, Key.Label, Theme.Body, word ? 0.0046f : 0.0082f, word ? Color.Lerp(Theme.Text, Theme.TextDim, 0.35f) : Theme.Text, TextAlignmentOptions.Center);
            Refresh();
        }

        /// Shift, Caps Lock, Ctrl, Alt while they are on.
        public void SetLatched(bool on) { if (_latched == on) return; _latched = on; Refresh(); }
        /// A lifted finger is over it (the table keyboard).
        public void SetLit(bool on) { if (_lit == on) return; _lit = on; Refresh(); }
        public void Flash() { _flash = 1f; Refresh(); }

        protected override void OnInteractionChanged() => Refresh();

        private void Refresh()
        {
            if (Panel == null || _shape < 0) return;
            float s = Pressed ? 1f : _latched ? 0.7f : (Hover || _lit) ? 0.38f : 0f;
            Panel.Shapes.SetState(_shape, Mathf.Max(s, _flash));
        }

        public override void Update()
        {
            if (_flash > 0f) { _flash = Mathf.Max(0f, _flash - Time.unscaledDeltaTime * 4f); Refresh(); }
        }

        public override void OnClick(UiPointerEvent e) => OnPressed?.Invoke();

        protected override void OnVisibility(bool v)
        {
            if (_shape >= 0) Panel.Shapes.SetVisible(_shape, v);
            _legend?.SetVisible(v);
            _top?.SetVisible(v);
        }

        protected override void Teardown()
        {
            if (_shape >= 0) Panel.Shapes.SetVisible(_shape, false);
            DestroyGo(_legend?.Tmp.gameObject);
            DestroyGo(_top?.Tmp.gameObject);
        }
    }

    /// MetaCodex's own keyboard (owner 2026-11-10: "I don't ever wanna see the default one"; "detect hands on a table,
    /// spawn the keyboard and lock it on the flat surface, a touchpad on the right, like the Experimental one").
    ///
    /// A normal keyboard (owner 2026-11-11: "the exact layout and design of a normal keyboard", "normal shape ... same
    /// proportions too"): the ANSI layout at its real size, 19.05 mm from key to key, plain rounded caps on a dark deck
    /// (KeyboardLayout), a touchpad on its right. Rest both hands flat on a table for a moment and it is laid on the
    /// table under them, the middle of F and J under the middle of the two index fingers, and stays there; until then
    /// it floats in front of you (poke or pinch its keys).
    ///
    /// On the table it is typed on with all ten fingers, the way Meta's Surface Keyboard is (docs/keyboard.md):
    /// which finger pressed and when is the press detector's (TapDetector: a floor per finger, strokes, followers,
    /// hands put down), which key it meant is the decoder's (KeyDecoder: a likelihood over the keys round the tip, what
    /// was typed before, an offset learned per finger). A lifted finger lights the key it is over; resting fingers type
    /// nothing. While a hand is over the keyboard it does nothing else: no ray, no pinch, no poke on a window, no
    /// gesture (owner 2026-11-11: "sometimes I click around when trying to type").
    /// Horizon OS's own keyboard is never opened (UiKeyboard routes here).
    public sealed class UiSurfaceKeyboard : MonoBehaviour
    {
        public enum KbMode { Hidden, Air, Surface }

        // ---- geometry (metres): a real keyboard's, nothing scaled
        public const float Unit = KeyboardLayout.Unit, CapGap = 0.0021f, Edge = 0.007f, StripH = 0.017f, PadGapU = 0.5f, PadU = 5f;
        // ---- the touchpad: a fingertip this near its floor touches (m), and lets go a little higher
        public float TouchSlack = 0.002f, ReleaseSlack = 0.006f;
        // ---- a hand this near the keyboard (m beside it, m over it) does nothing else; it is free again this long after it left
        public float ZoneMargin = 0.05f, ZoneHeight = 0.12f, ZoneLinger = 0.35f;

        public KbMode Mode { get; private set; } = KbMode.Hidden;
        public bool Visible => Mode != KbMode.Hidden;
        public IKeySink Sink { get; private set; }
        public UiPanel Panel => _panel;
        public bool Shift => _shift;
        public bool Caps => _caps;
        public bool CtrlOn => _ctrl;
        public bool AltOn => _alt;
        public KeyboardLayout Layout { get; private set; }
        public TapDetector Detector { get; } = new TapDetector();
        public KeyDecoder Decoder { get; private set; }
        /// The touchpad's cursor: on this panel, at this point (null: none yet).
        public UiPanel CursorPanel { get; private set; }
        public Vector2 CursorPoint { get; private set; }
        /// Self-tests and the shell: a key was sent (kind, text).
        public event Action<KbKind, string> Sent;

        private readonly Dictionary<KeyDef, UiKeycap> _caps_ = new Dictionary<KeyDef, UiKeycap>();
        private UiPanel _panel;
        private UiLabel _strip, _padLabel;
        private Rect _padRect;
        private Vector2 _block; // the key block's top-left corner in panel coordinates
        private UiInputRouter _router;
        private Func<Transform> _head;
        private Func<IHandSource> _hands;
        private Transform _root;
        private bool _shift, _caps, _ctrl, _alt, _registered;
        private float _shiftAt = -10f;
        private string _hint = "";
        private char _prev2, _prev1; // what was typed before, for the letter statistics

        // the table: where the keyboard lies, and the hands' rest
        private Vector3 _fjMid;
        private float _restSince = -1f;
        private readonly Vector3[] _prevTip = new Vector3[10];
        private readonly bool[] _seen = new bool[10];
        private readonly FingerSample[] _samples = new FingerSample[10];
        private readonly Vector2[] _tipPanel = new Vector2[10];
        private readonly List<TapEvent> _taps = new List<TapEvent>();
        private readonly float[] _zoneAt = { -10f, -10f };
        private float _prevTime, _now, _dt = 1f / 72f;
        // a press is learned from once the next key is not Backspace
        private bool _learnPending;
        private int _learnFinger;
        private float _learnX, _learnY;
        private KeyDef _learnKey;
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
            Layout = KeyboardLayout.Ansi();
            Decoder = new KeyDecoder(Layout);
            var table = Resources.Load<TextAsset>("Typing/letters");
            if (table != null && table.bytes != null && table.bytes.Length == LetterModel.Symbols * LetterModel.Symbols * LetterModel.Symbols) Decoder.Letters = new LetterModel(table.bytes);
            else Debug.LogWarning("[MetaCodex] keyboard: no letter statistics (Resources/Typing/letters.bytes): keys are chosen by place alone");
            Build();
            _panel.gameObject.SetActive(false);
        }

        // ------------------------------------------------------------------ layout

        private static float BlockW => KeyboardLayout.UnitsWide * Unit;
        private static float BlockH => KeyboardLayout.UnitsHigh * Unit;
        public static float PanelW => 2f * Edge + BlockW + (PadGapU + PadU) * Unit;
        public static float PanelH => 2f * Edge + StripH + BlockH;

        private Rect KeyRect(KeyDef k) => new Rect(_block.x + k.X, _block.y + k.Y, k.W, k.H);

        private void Build()
        {
            var t = UiTheme.Current;
            _block = new Vector2(Edge, Edge + StripH);
            _panel = UiPanel.Create<UiPanel>("keyboard", "", PanelW, PanelH, _root, header: false);
            _panel.Closable = false;
            _panel.Draggable = false;
            _panel.LightTransition = true;
            _panel.SetPlainBody(0.008f);
            foreach (var k in Layout.Keys)
            {
                var kk = k;
                var cap = _panel.Add(new UiKeycap(k) { Rect = KeyRect(k) });
                cap.OnPressed = () => { UiSounds.Fire("click"); Press(kk); };
                _caps_[k] = cap;
            }
            // the touchpad: beside the keys, from the number row down, as wide as it is high
            _padRect = new Rect(_block.x + BlockW + PadGapU * Unit, _block.y + (1f + KeyboardLayout.FunctionGap) * Unit + CapGap * 0.5f, PadU * Unit - CapGap, PadU * Unit - CapGap);
            var pad = _panel.Shape(UiShapeKind.Keycap, _padRect, t.Accent, 0.005f, 0f, 0.002f);
            pad.Border = 0.0005f;
            _panel.Shapes.Add(pad);
            _padLabel = _panel.Add(new UiLabel("touchpad", 0.0052f) { Rect = new Rect(_padRect.x, _padRect.yMax - 0.012f, _padRect.width, 0.010f), Align = TextAlignmentOptions.Center, Color = Color.Lerp(t.TextDim, Color.black, 0.35f) });
            _strip = _panel.Add(new UiLabel("", 0.0080f) { Rect = new Rect(Edge + 0.002f, Edge * 0.5f, PanelW - 2f * Edge - 0.004f, StripH), Color = t.TextAccent });
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
            if (sink != Sink) { _prev2 = _prev1 = '\0'; _learnPending = false; }
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
            _repeatKey = null;
            _learnPending = false;
            if (_cursorGo != null) _cursorGo.SetActive(false);
        }

        private void SetMode(KbMode m)
        {
            Mode = m;
            bool on = m != KbMode.Hidden;
            if (on && !_panel.gameObject.activeSelf) { _panel.gameObject.SetActive(true); _panel.Open(); }
            else if (!on && _panel.gameObject.activeSelf) _panel.Close();
            // in the air its keys are poked or pinched like any button; on a table the press detector decides
            bool wantRouter = m == KbMode.Air;
            if (_router != null && wantRouter != _registered)
            {
                if (wantRouter) _router.Register(_panel); else _router.Unregister(_panel);
                _registered = wantRouter;
            }
            _padLabel?.SetVisible(m == KbMode.Surface);
            _hint = m == KbMode.Air ? "rest both hands flat on a table to type on it" : "";
            for (int i = 0; i < 10; i++) _seen[i] = false;
            if (m != KbMode.Surface) { _zoneAt[0] = _zoneAt[1] = -10f; ClearLit(); }
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

        // the table the keyboard lies on, kept for the next time it is asked for
        private bool _lastSurfaceUsable;
        private Vector3 _surfPos;
        private Quaternion _surfRot;

        private bool LastSurfaceNear()
        {
            var head = _head?.Invoke();
            return head == null || Vector3.Distance(head.position, _surfPos) < 1.0f;
        }

        private void ApplySurface()
        {
            _panel.transform.SetPositionAndRotation(_surfPos, _surfRot);
            _panel.BaseScale = Vector3.one;
        }

        /// Lays the keyboard on the table under two resting hands: the middle of F and J under the middle of the two
        /// index fingertips, the rows along the line from the left one to the right one, at its real size.
        private void PlaceOnSurface(HandFrame l, HandFrame r)
        {
            Vector3 li = l.IndexTip, ri = r.IndexTip;
            float plane = Mathf.Min(RestHeight(l), RestHeight(r)) - 0.006f; // the drawn keys sit on the table, under the joints
            var across = ri - li; across.y = 0f;
            float spread = across.magnitude;
            if (spread < 0.04f) return;
            across /= spread;
            var away = Vector3.Cross(across, Vector3.up); // to the user's front
            Vector2 f = KeyRect(Layout.Find("f")).center, j = KeyRect(Layout.Find("j")).center;
            var rot = Quaternion.LookRotation(Vector3.down, away);
            var mid = (li + ri) * 0.5f; mid.y = plane;
            _surfPos = mid - rot * _panel.PanelToLocal((f + j) * 0.5f);
            _surfPos.y = plane;
            _surfRot = rot;
            _fjMid = mid;
            _lastSurfaceUsable = true;
            ApplySurface();
            SetMode(KbMode.Surface);
            // every finger lies on the table now: where its tip is tracked is its floor
            Detector.Reset();
            for (int h = 0; h < 2; h++)
                for (int i = 0; i < 5; i++) Detector.Calibrate(h * 5 + i, Tip(h == 0 ? l : r, i).y - plane);
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
            _now = now;
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
                    bool far = Mode != KbMode.Surface || Vector2.Distance(new Vector2(mid.x, mid.z), new Vector2(_fjMid.x, _fjMid.z)) > 0.12f || Mathf.Abs(Mathf.Min(yl, yr) - 0.006f - _surfPos.y) > 0.03f;
                    if (far) PlaceOnSurface(l, r);
                    else
                    {
                        // on the keyboard where it lies: the tips are on the table, their floors follow (tracking drifts)
                        for (int h = 0; h < 2; h++)
                            for (int i = 0; i < 5; i++)
                            {
                                float height = Tip(h == 0 ? l : r, i).y - _surfPos.y;
                                if (Mathf.Abs(height - Detector.Floor(h * 5 + i)) < 0.010f) Detector.Calibrate(h * 5 + i, height, 0.06f);
                            }
                    }
                }
            }
            else _restSince = -1f;

            for (int h = 0; h < 2; h++)
            {
                bool ok = h == 0 ? hl : hr;
                var f = h == 0 ? l : r;
                for (int i = 0; i < 5; i++) { _seen[h * 5 + i] = ok; if (ok) _prevTip[h * 5 + i] = Tip(f, i); }
            }

            if (Mode == KbMode.Surface)
            {
                float palmL = Sample(0, hl, l), palmR = Sample(1, hr, r);
                _taps.Clear();
                Detector.Step(now, _samples, palmL, palmR, _taps);
                foreach (var tap in _taps) OnTap(tap, now);
                TickPad(now);
                TickRepeat(now);
            }
            RefreshLit();
        }

        /// One hand's five tips on the keyboard's plane (for the press detector), whether it is in the keyboard's
        /// zone, and how high its palm is over the plane (NaN: not tracked).
        private float Sample(int hand, bool tracked, HandFrame f)
        {
            for (int i = 0; i < 5; i++)
            {
                int idx = hand * 5 + i;
                if (!tracked) { _samples[idx] = default; continue; }
                _panel.SurfaceOf(Tip(f, i), out var pp, out float depth); // depth < 0: over the keyboard
                _tipPanel[idx] = pp;
                _samples[idx] = new FingerSample { Tracked = true, X = pp.x - _block.x, Y = pp.y - _block.y, H = -depth };
                if (InZone(pp, -depth)) _zoneAt[hand] = _now;
            }
            if (!tracked) return float.NaN;
            _panel.SurfaceOf(f.Palm, out var palm, out float palmDepth);
            if (InZone(palm, -palmDepth)) _zoneAt[hand] = _now;
            return -palmDepth;
        }

        private bool InZone(Vector2 pp, float height) =>
            height < ZoneHeight && height > -0.05f && pp.x > -ZoneMargin && pp.y > -ZoneMargin && pp.x < PanelW + ZoneMargin && pp.y < PanelH + ZoneMargin;

        /// A hand over the keyboard on the table: it types (or moves the touchpad's cursor) and does nothing else, and
        /// for a moment after it left.
        public bool HandInZone(int hand) => Mode == KbMode.Surface && hand >= 0 && hand < 2 && _now - _zoneAt[hand] < ZoneLinger;

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

        // ------------------------------------------------------------------ presses on the table

        private bool OverPad(float blockX, float blockY) => _padRect.Contains(new Vector2(blockX + _block.x, blockY + _block.y));

        private void OnTap(TapEvent tap, float now)
        {
            if (Sink == null || OverPad(tap.X, tap.Y)) return; // the touchpad takes its own
            var key = Decoder.Decode(tap.Finger, tap.X, tap.Y, _prev2, _prev1, _shift || _caps, Sink.Context);
            if (key == null) return;
            // the press before this one was right (it is not being taken back): its finger's offset learns from it
            if (_learnPending && key.Kind != KbKind.Backspace) Decoder.Learn(_learnFinger, _learnX, _learnY, _learnKey);
            _learnPending = (key.Kind == KbKind.Char || key.Kind == KbKind.Space) && Decoder.Choices.Count > 0 && Decoder.Choices[0].P >= 0.7f;
            if (_learnPending) { _learnFinger = tap.Finger; _learnX = tap.X; _learnY = tap.Y; _learnKey = key; }
            Press(key);
            UiSounds.Fire("key");
            if (key.Kind == KbKind.Backspace || key.Kind == KbKind.Delete || key.Kind == KbKind.Left || key.Kind == KbKind.Right || key.Kind == KbKind.Up || key.Kind == KbKind.Down)
            { _repeatKey = key; _repeatTip = tap.Finger; _repeatAt = now + 0.45f; }
        }

        /// Backspace, Delete and the arrows repeat while the finger that pressed stays down.
        private void TickRepeat(float now)
        {
            if (_repeatKey == null) return;
            if (_repeatTip < 0 || !Detector.Down(_repeatTip) || Detector.Rel(_repeatTip) > Detector.ContactTol * 1.6f) { _repeatKey = null; _repeatTip = -1; return; }
            if (now < _repeatAt) return;
            _repeatAt = now + 0.07f;
            Press(_repeatKey, repeat: true);
        }

        // ------------------------------------------------------------------ the touchpad

        private void TickPad(float now)
        {
            for (int idx = 0; idx < 10; idx++)
            {
                int finger = idx % 5;
                bool seen = _samples[idx].Tracked;
                bool onPad = seen && finger >= 1 && finger <= 2 && _padRect.Contains(_tipPanel[idx]); // index or middle fingers only
                bool touching = onPad && Detector.Touching(idx, _padTip == idx || _padTip2 == idx ? ReleaseSlack : TouchSlack);
                if (!touching)
                {
                    if (_padTip == idx) EndPad(now);
                    if (_padTip2 == idx) _padTip2 = -1;
                    continue;
                }
                var pp = _tipPanel[idx];
                if (_padTip < 0) { _padTip = idx; _padStart = _padLast = pp; _padDownAt = now; _padMoved = false; EnsureCursorPanel(); continue; }
                if (_padTip != idx) { if (_padTip2 < 0) _padTip2 = idx; continue; }
                var d = pp - _padLast;
                _padLast = pp;
                if ((pp - _padStart).magnitude > 0.008f) _padMoved = true;
                if (CursorPanel == null) continue;
                if (_padTip2 >= 0)
                {
                    // two fingers: scroll what is under the cursor
                    var el = CursorPanel.HitTest(CursorPoint);
                    if (el is UiList list && Mathf.Abs(d.y) > 1e-4f) list.ScrollBy(-d.y * 160f);
                    continue;
                }
                float speed = d.magnitude / _dt;
                float gain = Mathf.Lerp(2.2f, 5.5f, Mathf.InverseLerp(0.02f, 0.25f, speed)); // pointer acceleration
                var c = CursorPoint + d * gain;
                CursorPoint = new Vector2(Mathf.Clamp(c.x, 0f, CursorPanel.Width), Mathf.Clamp(c.y, 0f, CursorPanel.Height));
                PlaceCursor();
            }
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
            bool ctrl = _ctrl, alt = _alt, shift = _shift;
            switch (k.Kind)
            {
                case KbKind.Shift:
                    // once for the next key; twice quickly is Caps Lock
                    if (_shift && Time.unscaledTime - _shiftAt < 0.45f) { _caps = !_caps; _shift = false; }
                    else { _shift = !_shift; _shiftAt = Time.unscaledTime; }
                    RefreshLatches();
                    break;
                case KbKind.Caps: _caps = !_caps; RefreshLatches(); break;
                case KbKind.Ctrl: _ctrl = !_ctrl; RefreshLatches(); break;
                case KbKind.Alt: _alt = !_alt; RefreshLatches(); break;
                case KbKind.Hide: Hide(); return;
                case KbKind.Char:
                case KbKind.Space:
                {
                    // Caps Lock turns letters only; Shift turns everything (and turns a Caps Lock letter back)
                    bool letter = k.Kind == KbKind.Char && k.Normal.Length == 1 && char.IsLetter(k.Normal[0]);
                    string s = k.Kind == KbKind.Space ? " " : (letter ? shift != _caps : shift) ? k.Shifted : k.Normal;
                    Sink.Type(s, ctrl, alt);
                    Sent?.Invoke(k.Kind, s);
                    _prev2 = _prev1; _prev1 = s[0];
                    if (_shift || ctrl || alt) { _shift = _ctrl = _alt = false; RefreshLatches(); }
                    break;
                }
                default:
                    Sink.Key(k.Kind, ctrl, alt, shift, k.Fn);
                    Sent?.Invoke(k.Kind, null);
                    if (k.Kind == KbKind.Backspace) { _prev1 = _prev2; _prev2 = '\0'; } else _prev2 = _prev1 = '\0';
                    if (_shift || ctrl || alt) { _shift = _ctrl = _alt = false; RefreshLatches(); }
                    if (Sink != null && !Sink.KeepsKeyboard && (k.Kind == KbKind.Enter || k.Kind == KbKind.Escape)) { Hide(); return; }
                    break;
            }
            if (!repeat && _caps_.TryGetValue(k, out var cap)) cap.Flash();
            RefreshStrip();
        }

        private void RefreshLatches()
        {
            foreach (var kv in _caps_)
            {
                var kind = kv.Key.Kind;
                if (kind == KbKind.Shift) kv.Value.SetLatched(_shift);
                else if (kind == KbKind.Caps) kv.Value.SetLatched(_caps);
                else if (kind == KbKind.Ctrl) kv.Value.SetLatched(_ctrl);
                else if (kind == KbKind.Alt) kv.Value.SetLatched(_alt);
            }
            RefreshStrip();
        }

        /// A lifted finger near the keys lights the key it would press.
        private readonly HashSet<UiKeycap> _lit = new HashSet<UiKeycap>(), _litNow = new HashSet<UiKeycap>();

        private void RefreshLit()
        {
            _litNow.Clear();
            if (Mode == KbMode.Surface)
                for (int i = 0; i < 10; i++)
                {
                    if (!_samples[i].Tracked || !Detector.Hovering(i)) continue;
                    var k = Decoder.Under(i, Detector.X(i), Detector.Y(i));
                    if (k != null && !(i % 5 == 0 && k.Kind != KbKind.Space && k.Kind != KbKind.Alt) && _caps_.TryGetValue(k, out var cap)) _litNow.Add(cap);
                }
            foreach (var c in _lit) if (!_litNow.Contains(c)) c.SetLit(false);
            foreach (var c in _litNow) if (!_lit.Contains(c)) c.SetLit(true);
            _lit.Clear();
            foreach (var c in _litNow) _lit.Add(c);
        }

        private void ClearLit()
        {
            foreach (var c in _lit) c.SetLit(false);
            _lit.Clear();
        }

        public void RefreshStrip()
        {
            if (_strip == null) return;
            string mods = (_caps ? "CAPS " : "") + (_shift ? "SHIFT " : "") + (_ctrl ? "CTRL " : "") + (_alt ? "ALT " : "");
            string target = Sink != null ? Sink.Title : "";
            string preview = Sink?.Preview;
            string text = target + (preview != null ? "  ▸ " + Tail(preview, 52) + "▏" : "") + (mods.Length > 0 ? "   " + mods : "") + (_hint.Length > 0 ? "   · " + _hint : "");
            _strip.Set(text);
        }

        private static string Tail(string s, int n) => s.Length <= n ? s : "…" + s.Substring(s.Length - n);

        // ------------------------------------------------------------------ self-tests

        public Rect KeyRectForTest(string id) { var k = Layout.Find(id); return k != null ? KeyRect(k) : default; }
        public Vector3 KeyWorldForTest(string id, float height = 0f)
        {
            var r = KeyRectForTest(id);
            return _panel.transform.TransformPoint(_panel.PanelToLocal(r.center)) + Vector3.up * height;
        }
        public Vector3 PadWorldForTest(Vector2 uv) => _panel.transform.TransformPoint(_panel.PanelToLocal(new Vector2(_padRect.x + uv.x * _padRect.width, _padRect.y + uv.y * _padRect.height)));
        public int KeyCountForTest => Layout.Keys.Count;
        public void PressForTest(string id) { var k = Layout.Find(id); if (k != null) Press(k); }
        public string StripTextForTest => _strip != null ? _strip.Text : "";
        public void SetCursorForTest(UiPanel p, Vector2 point) { CursorPanel = p; CursorPoint = point; PlaceCursor(); }
        public void ForgetSurfaceForTest() { _lastSurfaceUsable = false; Decoder.ForgetOffsets(); }
        public bool LitForTest(string id) { var k = Layout.Find(id); return k != null && _caps_.TryGetValue(k, out var c) && _lit.Contains(c); }
    }
}

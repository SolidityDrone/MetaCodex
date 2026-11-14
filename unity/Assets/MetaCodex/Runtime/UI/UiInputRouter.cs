using System;
using System.Collections.Generic;
using System.Linq;
using MetaCodex.Hands;
using UnityEngine;

namespace MetaCodex.UI
{
    public enum PointerKind { Ray, Poke, Mouse, Test }

    /// One pointer per frame: a hand ray (clicks with a pinch), a fingertip (clicks by poking), the mouse in
    /// the Editor, or a test pointer. The router keeps the per-pointer interaction state between frames.
    public sealed class UiPointer
    {
        public int Id;
        public PointerKind Kind;
        public string Hand = "";
        public bool Active;
        public Ray Ray;
        public Vector3 Tip;
        public bool Down;
        public bool GrabHeld;
        public Vector3 GrabPoint;

        public bool HasHit;
        public Vector3 HitPoint;
        public UiPanel HitPanel;
        public Vector2 HitPanelPoint;

        internal UiElement HoverElement, Captured;
        internal UiPanel CapturedPanel, DragPanel, SelectPanel;
        internal Vector2 PressPoint, SelectStart, SelectEnd;
        internal Vector3 DragOffset;
        internal float DragDistance;
        internal bool DragByGrab, WasDown, WasGrab, Selecting, PokeTouching;
    }

    /// Routes hand/mouse pointers to panels: hover, press, drag, release/click, panel moving (pinch the
    /// header, or close the hand while pointing at a panel), and the selection tool (pinch-drag a rectangle
    /// on a panel). Draws the hand beams and reticles. Pumps the system keyboard.
    public sealed class UiInputRouter : MonoBehaviour
    {
        public readonly List<UiPanel> Panels = new List<UiPanel>();
        public GestureEngine Gestures;
        public GestureSettings Settings = new GestureSettings();
        public Transform Head;
        public Camera MouseCamera;
        /// While on, a pinch on a panel draws a selection rectangle instead of clicking.
        public bool SelectionMode;
        public event Action<UiPanel, Rect> Selected;
        public event Action<UiPanel, UiElement> Clicked;
        /// A hand (0 left, 1 right) that is busy with something that is not a panel (the orb): it neither points nor pokes.
        public Func<int, bool> HandBlocked;
        /// The panel whose control is being pressed or released right now (null between): a dialog it opens belongs to
        /// it (UiModal places it over that panel).
        public UiPanel ActivePanel { get; private set; }

        private readonly UiPointer[] _hand = new UiPointer[4]; // 0 left ray, 1 right ray, 2 left poke, 3 right poke
        private readonly UiPointer _mouse = new UiPointer { Id = 9, Kind = PointerKind.Mouse, Hand = "mouse" };
        private readonly LineRenderer[] _beams = new LineRenderer[2];
        private readonly UiShapeMesh[] _reticles = new UiShapeMesh[3];
        private readonly Dictionary<UiPanel, int> _selectShape = new Dictionary<UiPanel, int>();
        // the hands' light on the panels' glass (MetaCodex/SciFiUI): a fingertip, or a point just in front of a ray's cursor
        private static readonly int[] PointerIds = { Shader.PropertyToID("_McxPointer0"), Shader.PropertyToID("_McxPointer1") };
        private readonly Vector4[] _pointerLights = new Vector4[2];

        public void Init()
        {
            for (int i = 0; i < 4; i++)
                _hand[i] = new UiPointer { Id = i, Kind = i < 2 ? PointerKind.Ray : PointerKind.Poke, Hand = i % 2 == 0 ? "left" : "right" };
            for (int i = 0; i < 2; i++)
            {
                var go = new GameObject(i == 0 ? "beam-left" : "beam-right");
                go.transform.SetParent(transform, false);
                var lr = go.AddComponent<LineRenderer>();
                lr.sharedMaterial = UiTheme.Current.BeamMaterial;
                lr.positionCount = 2;
                lr.widthMultiplier = 0.004f;
                lr.useWorldSpace = true;
                lr.textureMode = LineTextureMode.Stretch;
                lr.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
                lr.receiveShadows = false;
                lr.enabled = false;
                _beams[i] = lr;
            }
            for (int i = 0; i < 3; i++)
            {
                var rg = new GameObject("reticle-" + i, typeof(MeshFilter), typeof(MeshRenderer));
                rg.transform.SetParent(transform, false);
                _reticles[i] = rg.AddComponent<UiShapeMesh>();
                _reticles[i].Init(UiTheme.Current.ShapeMaterial, false);
                _reticles[i].Add(new UiShape { Kind = UiShapeKind.Dot, Size = new Vector2(0.012f, 0.012f), Margin = 0.004f, Tint = UiTheme.Current.Accent, Visible = true });
                rg.SetActive(false);
            }
        }

        public void Register(UiPanel p) { if (!Panels.Contains(p)) Panels.Add(p); }

        /// Panels a pointer is moving right now (the float layout leaves them alone until released).
        public bool IsDragging(UiPanel p)
        {
            foreach (var h in _hand) if (h != null && h.DragPanel == p) return true;
            return _mouse.DragPanel == p;
        }

        /// The panel under a hand's ray this frame (0 left, 1 right), or null.
        public UiPanel HandRayPanel(int hand) => hand >= 0 && hand < 2 && _hand[hand] != null && _hand[hand].Active && _hand[hand].HasHit ? _hand[hand].HitPanel : null;

        /// A hand is on the UI: its ray or its fingertip on a panel, or it moves or holds something (gesture bindings
        /// do not fire then: a pose made while poking or pointing at a window is not a command).
        public bool HandOnUi(int hand) => HandBusy(hand) || HandRayPanel(hand) != null
            || (hand >= 0 && hand < 2 && _hand[hand + 2] != null && _hand[hand + 2].Active && _hand[hand + 2].HasHit);

        /// A hand is moving a panel or holding an element.
        public bool HandBusy(int hand) => hand >= 0 && hand < 2 && _hand[hand] != null && (_hand[hand].DragPanel != null || _hand[hand].Captured != null || _hand[hand + 2].Captured != null);
        public void Unregister(UiPanel p)
        {
            Panels.Remove(p);
            foreach (var ptr in AllPointers()) Forget(ptr, p);
        }

        /// Drops a pointer's references to a panel that is gone (unregistered, or destroyed by a rebuild that a
        /// click on it started: accent / theme changes rebuild every panel).
        private UiPointer _lastInjected;

        private IEnumerable<UiPointer> AllPointers()
        {
            foreach (var h in _hand) if (h != null) yield return h;
            yield return _mouse;
            if (_lastInjected != null) yield return _lastInjected;
        }

        private static void Forget(UiPointer ptr, UiPanel gone)
        {
            if (ptr == null) return;
            bool Dead(UiPanel x) => x == null || (gone != null && x == gone);
            if (ptr.HitPanel != null && Dead(ptr.HitPanel)) { ptr.HitPanel = null; ptr.HasHit = false; }
            if (ptr.CapturedPanel != null && Dead(ptr.CapturedPanel)) { ptr.CapturedPanel = null; ptr.Captured = null; }
            if (ptr.DragPanel != null && Dead(ptr.DragPanel)) ptr.DragPanel = null;
            if (ptr.SelectPanel != null && Dead(ptr.SelectPanel)) { ptr.SelectPanel = null; ptr.Selecting = false; }
            if (ptr.HoverElement != null && (ptr.HoverElement.Panel == null || ptr.HoverElement.Panel == gone)) ptr.HoverElement = null;
            if (ptr.Captured != null && (ptr.Captured.Panel == null || ptr.Captured.Panel == gone)) { ptr.Captured = null; ptr.CapturedPanel = null; }
        }

        /// Self-tests: one frame of the router (keyboard, gestures, pointers, beams).
        public void UpdateForTest() => Update();

        private void Update()
        {
            UiKeyboard.Pump();
            Gestures?.Tick(Time.unscaledTime, Time.unscaledDeltaTime);
            CollectHands();
            CollectMouse();
            for (int i = 0; i < 4; i++) Process(_hand[i]);
            Process(_mouse);
            DrawFeedback();
        }

        /// Runs one pointer through the router now (self-tests, control port).
        public void Inject(UiPointer p) { _lastInjected = p; Process(p); }

        private void CollectHands()
        {
            for (int i = 0; i < 4; i++) _hand[i].Active = false;
            if (Gestures == null) return;
            for (int h = 0; h < 2; h++)
            {
                var st = Gestures.Get((Handedness)h);
                if (!st.Tracked) continue;
                if (HandBlocked != null && HandBlocked(h) && _hand[h].Captured == null && _hand[h].DragPanel == null && _hand[2 + h].Captured == null) continue;
                bool pinchClicks = Settings.clickMode != 1;
                bool pokeClicks = Settings.clickMode != 0;
                var ray = _hand[h];
                bool rayAllowed = Settings.pointMode == 0 || st.Pointing || st.Pinching || ray.Captured != null || ray.DragPanel != null;
                ray.Active = pinchClicks && rayAllowed;
                ray.Ray = st.Frame.Ray();
                ray.Down = st.Pinching;
                ray.GrabHeld = st.Grabbing;
                ray.GrabPoint = st.Frame.Palm;
                var poke = _hand[2 + h];
                poke.Active = pokeClicks;
                poke.Tip = st.Frame.IndexTip;
                // a fingertip near a panel suppresses that hand's ray (no double interaction)
                if (poke.Active && NearestPokePanel(poke.Tip, out _, out _, out float z) && z > -0.06f) ray.Active = ray.Captured != null || ray.DragPanel != null;
            }
        }

        private void CollectMouse()
        {
            _mouse.Active = false;
#if UNITY_EDITOR
            var cam = MouseCamera != null ? MouseCamera : Camera.main;
            var mouse = UnityEngine.InputSystem.Mouse.current;
            if (cam == null || mouse == null || !Application.isPlaying) return;
            _mouse.Active = true;
            _mouse.Ray = cam.ScreenPointToRay(mouse.position.ReadValue());
            _mouse.Down = mouse.leftButton.isPressed;
            _mouse.GrabHeld = mouse.rightButton.isPressed;
            _mouse.GrabPoint = _mouse.Ray.origin + _mouse.Ray.direction * 0.6f;
#endif
        }

        private bool NearestPokePanel(Vector3 tip, out UiPanel panel, out Vector2 point, out float z)
        {
            panel = null; point = default; z = float.MaxValue;
            float best = float.MaxValue;
            foreach (var p in Panels)
            {
                if (p == null || !p.IsOpen) continue;
                p.SurfaceOf(tip, out var pt, out float depth); // the drawn surface, bent or flat
                if (pt.x < 0 || pt.y < 0 || pt.x > p.Width || pt.y > p.Height) continue;
                // depth < 0 is in front of the panel (towards the user)
                if (depth < -0.04f || depth > 0.03f) continue;
                float d = Mathf.Abs(depth);
                if (d < best) { best = d; panel = p; point = pt; z = depth; }
            }
            return panel != null;
        }

        private bool FindHit(UiPointer p, out UiPanel panel, out Vector2 point)
        {
            panel = null; point = default;
            if (p.Kind == PointerKind.Poke)
            {
                if (!NearestPokePanel(p.Tip, out panel, out point, out float z)) { p.PokeTouching = false; p.Down = false; return false; }
                p.PokeTouching = p.PokeTouching ? z > -0.014f : z > -0.006f;
                p.Down = p.PokeTouching;
                p.HitPoint = panel.SurfacePoint(point);
                return true;
            }
            float best = float.MaxValue;
            foreach (var pan in Panels)
            {
                if (pan == null || !pan.IsOpen) continue;
                if (pan.Raycast(p.Ray, out var pt, out float d) && d < best) { best = d; panel = pan; point = pt; }
            }
            if (panel != null) p.HitPoint = p.Ray.origin + p.Ray.direction * best;
            return panel != null;
        }

        private bool PointOn(UiPointer p, UiPanel panel, out Vector2 pt)
        {
            if (p.Kind == PointerKind.Poke)
            {
                panel.SurfaceOf(p.Tip, out pt, out float depth);
                p.PokeTouching = depth > -0.014f;
                p.Down = p.PokeTouching;
                return true;
            }
            return panel.ProjectRay(p.Ray, out pt);
        }

        private void Process(UiPointer p)
        {
            if (!p.Active)
            {
                if (p.Captured != null) { p.Captured.SetPressed(false); p.Captured = null; }
                if (p.HoverElement != null) { p.HoverElement.SetHover(false); p.HoverElement = null; }
                p.DragPanel = null;
                EndSelection(p, false);
                p.HasHit = false;
                p.WasDown = false; p.WasGrab = false;
                return;
            }

            if (p.DragPanel != null)
            {
                bool hold = p.DragByGrab ? p.GrabHeld : p.Down;
                if (!hold) p.DragPanel = null;
                else MoveDragged(p);
                Finish(p);
                return;
            }

            if (p.Selecting)
            {
                if (PointOn(p, p.SelectPanel, out var sp)) p.SelectEnd = Clamp(p.SelectPanel, sp);
                ShowSelection(p);
                if (!p.Down) EndSelection(p, true);
                Finish(p);
                return;
            }

            if (p.Captured != null)
            {
                bool ok = PointOn(p, p.CapturedPanel, out var pt);
                var ev = new UiPointerEvent { Point = pt, PressPoint = p.PressPoint, Pointer = p };
                if (p.Down) { if (ok) p.Captured.OnDrag(ev); }
                else
                {
                    var el = p.Captured;
                    bool inside = ok && el.Rect.Contains(pt);
                    el.SetPressed(false);
                    p.Captured = null;
                    ActivePanel = p.CapturedPanel;
                    try { el.OnRelease(ev, inside); } finally { ActivePanel = null; }
                    if (inside) Clicked?.Invoke(p.CapturedPanel, el);
                    if (p.CapturedPanel == null) { Forget(p, null); p.HasHit = false; Finish(p); return; } // the click rebuilt or closed it
                }
                p.HasHit = ok;
                if (ok) { p.HitPanel = p.CapturedPanel; p.HitPanelPoint = pt; if (p.Kind != PointerKind.Poke) p.CapturedPanel.ProjectRay(p.Ray, out _); }
                Finish(p);
                return;
            }

            p.HasHit = FindHit(p, out var panel, out var point);
            p.HitPanel = panel;
            p.HitPanelPoint = point;
            var element = panel != null ? panel.HitTest(point) : null;
            if (element != p.HoverElement)
            {
                p.HoverElement?.SetHover(false);
                p.HoverElement = element;
                element?.SetHover(true);
            }
            if (element != null) element.OnHover(new UiPointerEvent { Point = point, PressPoint = point, Pointer = p });

            bool pressEdge = p.Down && !p.WasDown;
            bool grabEdge = p.GrabHeld && !p.WasGrab;
            if (grabEdge && panel != null && panel.Draggable && Settings.grabMode != 0 && p.Kind != PointerKind.Poke)
            {
                p.DragPanel = panel;
                p.DragByGrab = true;
                p.DragOffset = panel.transform.position - p.GrabPoint;
            }
            else if (pressEdge && panel != null)
            {
                // the selection tool draws on content panels; the dock always clicks (SELECT turns it off again)
                if (SelectionMode && p.Kind != PointerKind.Poke && panel.Id != "dock")
                {
                    p.Selecting = true;
                    p.SelectPanel = panel;
                    p.SelectStart = p.SelectEnd = Clamp(panel, point);
                    ShowSelection(p);
                }
                else if (element != null)
                {
                    p.Captured = element;
                    p.CapturedPanel = panel;
                    p.PressPoint = point;
                    element.SetPressed(true);
                    ActivePanel = panel;
                    try { element.OnPress(new UiPointerEvent { Point = point, PressPoint = point, Pointer = p }); } finally { ActivePanel = null; }
                }
                else if (panel.InHeader(point) && panel.Draggable && Settings.grabMode != 1 && p.Kind != PointerKind.Poke)
                {
                    p.DragPanel = panel;
                    p.DragByGrab = false;
                    p.DragDistance = Vector3.Distance(p.Ray.origin, p.HitPoint);
                    p.DragOffset = panel.transform.position - p.HitPoint;
                }
            }
            Finish(p);
        }

        private static void Finish(UiPointer p) { p.WasDown = p.Down; p.WasGrab = p.GrabHeld; }

        private static Vector2 Clamp(UiPanel panel, Vector2 pt) => new Vector2(Mathf.Clamp(pt.x, 0, panel.Width), Mathf.Clamp(pt.y, 0, panel.Height));

        private void MoveDragged(UiPointer p)
        {
            var panel = p.DragPanel;
            Vector3 target = p.DragByGrab ? p.GrabPoint + p.DragOffset : p.Ray.origin + p.Ray.direction * p.DragDistance + p.DragOffset;
            panel.transform.position = Vector3.Lerp(panel.transform.position, target, 0.6f);
            if (Head != null) panel.FaceTowards(Head.position);
            p.HasHit = true;
            p.HitPanel = panel;
            p.HitPoint = p.DragByGrab ? p.GrabPoint : p.Ray.origin + p.Ray.direction * p.DragDistance;
        }

        private void ShowSelection(UiPointer p)
        {
            var panel = p.SelectPanel;
            if (panel == null) return;
            var r = Rect.MinMaxRect(Mathf.Min(p.SelectStart.x, p.SelectEnd.x), Mathf.Min(p.SelectStart.y, p.SelectEnd.y),
                Mathf.Max(p.SelectStart.x, p.SelectEnd.x), Mathf.Max(p.SelectStart.y, p.SelectEnd.y));
            if (!_selectShape.TryGetValue(panel, out int id))
            {
                id = panel.Shapes.Add(panel.Shape(UiShapeKind.Selection, r, UiTheme.Current.Warn, 0.004f, 0.004f, 0.006f));
                _selectShape[panel] = id;
            }
            var s = panel.Shapes.Get(id);
            s.Center = panel.PanelToLocal(r.center, -0.0006f);
            s.Size = new Vector2(Mathf.Max(r.width, 0.004f), Mathf.Max(r.height, 0.004f));
            s.Visible = true;
            s.Tint = UiTheme.Current.Warn;
            panel.Shapes.Set(id, s);
        }

        private void EndSelection(UiPointer p, bool commit)
        {
            if (!p.Selecting) return;
            p.Selecting = false;
            var panel = p.SelectPanel;
            p.SelectPanel = null;
            if (panel == null) return;
            if (_selectShape.TryGetValue(panel, out int id) && !commit) panel.Shapes.SetVisible(id, false);
            if (!commit) return;
            var r = Rect.MinMaxRect(Mathf.Min(p.SelectStart.x, p.SelectEnd.x), Mathf.Min(p.SelectStart.y, p.SelectEnd.y),
                Mathf.Max(p.SelectStart.x, p.SelectEnd.x), Mathf.Max(p.SelectStart.y, p.SelectEnd.y));
            if (r.width < 0.008f && r.height < 0.008f)
            {
                // a tap, not a rectangle: click what is under it (toggles, buttons, SELECT itself in Settings)
                if (_selectShape.TryGetValue(panel, out int sid)) panel.Shapes.SetVisible(sid, false);
                var el = panel.HitTest(p.SelectStart);
                if (el != null)
                {
                    var ev = new UiPointerEvent { Point = p.SelectStart, PressPoint = p.SelectStart, Pointer = p };
                    ActivePanel = panel;
                    try { el.OnPress(ev); el.OnRelease(ev, true); } finally { ActivePanel = null; }
                    Clicked?.Invoke(panel, el);
                }
                return;
            }
            Selected?.Invoke(panel, r);
            Gestures?.RaiseExternal("select", p.Hand, "tap", Time.unscaledTime, panel.transform.TransformPoint(panel.PanelToLocal(r.center)),
                $"{panel.Id} {r.x:F3} {r.y:F3} {r.width:F3} {r.height:F3}");
        }

        /// Hides the last committed selection frame on a panel.
        /// Tests: whether a panel shows a selection frame.
        public bool SelectionShown(UiPanel panel) => panel != null && _selectShape.TryGetValue(panel, out int id) && panel.Shapes.Get(id).Visible;

        public void ClearSelection(UiPanel panel)
        {
            if (panel != null && _selectShape.TryGetValue(panel, out int id)) panel.Shapes.SetVisible(id, false);
        }

        private void DrawFeedback()
        {
            var theme = UiTheme.Current;
            for (int h = 0; h < 2; h++)
            {
                var p = _hand[h];
                var beam = _beams[h];
                bool show = p.Active && Settings.rayVisible && beam != null;
                if (beam != null) beam.enabled = show;
                if (show)
                {
                    var end = p.HasHit ? p.HitPoint : p.Ray.origin + p.Ray.direction * 0.45f;
                    beam.SetPosition(0, p.Ray.origin);
                    beam.SetPosition(1, end);
                    var c = SelectionMode ? theme.Warn : theme.Accent;
                    c.a = p.Down ? 0.95f : p.HasHit ? 0.7f : 0.28f;
                    beam.startColor = beam.endColor = UiTheme.Lin(c);
                    beam.widthMultiplier = p.Down ? 0.006f : 0.004f;
                }
                SetReticle(h, p.Active && p.HasHit, p, p.Down ? 1f : 0.2f, SelectionMode ? theme.Warn : theme.Accent);
            }
            var poke = _hand[2].Active && _hand[2].HasHit ? _hand[2] : _hand[3];
            SetReticle(2, poke.Active && poke.HasHit, poke, poke.Down ? 1f : 0.35f, theme.Ok);
            PushPointerLights(Time.unscaledDeltaTime);
        }

        /// The light each hand casts on the glass of the panels: its fingertip while it is within 14 cm of one (the
        /// pool tightens as it comes closer), else the cursor of its ray. Eased, so it never pops.
        public void PushPointerLights(float dt)
        {
            for (int h = 0; h < 2; h++)
            {
                Vector3 at = _pointerLights[h];
                float want = 0f;
                var poke = _hand[2 + h];
                var ray = _hand[h];
                if (poke != null && poke.Active && PokeProximity(poke.Tip, out float gap)) { at = poke.Tip; want = Mathf.Clamp01(1f - gap / 0.14f); }
                else if (ray != null && ray.Active && ray.HasHit) { at = ray.HitPoint - ray.Ray.direction * 0.015f; want = ray.Down ? 1f : 0.7f; }
                float now = Mathf.Lerp(_pointerLights[h].w, want, dt <= 0f ? 1f : 1f - Mathf.Exp(-dt * 14f));
                if (now < 0.004f && want <= 0f) now = 0f;
                _pointerLights[h] = new Vector4(at.x, at.y, at.z, now);
                Shader.SetGlobalVector(PointerIds[h], _pointerLights[h]);
            }
        }

        /// Self-tests: a light at a place in the world (strength 0 turns it off).
        public void SetPointerLightForTest(int hand, Vector3 at, float strength)
        {
            _pointerLights[hand] = new Vector4(at.x, at.y, at.z, strength);
            Shader.SetGlobalVector(PointerIds[hand], _pointerLights[hand]);
        }

        /// How far a fingertip is from the nearest open panel under it (within 14 cm in front, 3 cm behind).
        private bool PokeProximity(Vector3 tip, out float gap)
        {
            gap = float.MaxValue;
            foreach (var p in Panels)
            {
                if (p == null || !p.IsOpen) continue;
                p.SurfaceOf(tip, out var pt, out float depth);
                if (pt.x < -0.03f || pt.y < -0.03f || pt.x > p.Width + 0.03f || pt.y > p.Height + 0.03f) continue;
                if (depth < -0.14f || depth > 0.03f) continue;
                gap = Mathf.Min(gap, Mathf.Abs(depth));
            }
            return gap < float.MaxValue;
        }

        private void SetReticle(int slot, bool visible, UiPointer p, float state, Color tint)
        {
            var r = _reticles[slot];
            if (r == null) return;
            visible = visible && p.HitPanel != null;
            if (r.gameObject.activeSelf != visible) r.gameObject.SetActive(visible);
            if (!visible) return;
            var rot = p.HitPanel.transform.rotation;
            r.transform.SetPositionAndRotation(p.HitPoint - rot * Vector3.forward * 0.002f, rot);
            r.SetState(0, state);
            r.SetTint(0, tint);
        }
    }


    /// A small dialog: title, text and a row of buttons; closes on any answer.
    /// One per question: asking the same again (a button pressed twice) brings the open one back instead of a second
    /// copy, and a window asks one thing at a time (its new question replaces its open one). Asked from a window
    /// (`owner`, the router's ActivePanel), it opens parallel to that window, centred on it and a few centimetres in
    /// front of it, towards the user: on top of it, never through it or half off its edge (owner 2026-11-09).
    /// Asked by voice or a script, it opens in front of the user.
    public static class UiModal
    {
        private sealed class Asked { public UiPanel Panel, Owner; public string Key; }
        private static readonly List<Asked> Open = new List<Asked>();

        /// Open dialogs (tests).
        public static int OpenCount { get { Prune(); return Open.Count; } }
        public static IEnumerable<UiPanel> OpenPanels { get { Prune(); return Open.Select(a => a.Panel); } }

        private static void Prune() => Open.RemoveAll(a => a.Panel == null || !a.Panel.IsOpen);

        /// Seconds a new dialog over a window ignores its buttons: the second tap of a double tap on the button that asked
        /// lands on the dialog (it opens over that window) and must not answer it (DELETE ALL).
        public static float InputGrace = 0.35f;

        public static UiPanel Show(UiInputRouter router, Transform head, string title, string text, string[] buttons, Action<int> onResult, Transform parent = null, UiPanel owner = null)
        {
            Prune();
            string key = (title ?? "") + "\n" + (text ?? "") + "\n" + string.Join("|", buttons ?? new string[0]);
            var same = Open.FirstOrDefault(a => a.Key == key);
            if (same != null)
            {
                same.Panel.SetHighlight(1f);
                UiSounds.Fire("click");
                return same.Panel;
            }
            if (owner != null)
                foreach (var old in Open.Where(a => a.Owner == owner).ToList())
                {
                    router.Unregister(old.Panel);
                    Open.Remove(old);
                    old.Panel.CloseAndDestroy();
                }
            var t = UiTheme.Current;
            int lines = Mathf.Clamp(1 + (text?.Length ?? 0) / 52, 1, 8);
            float h = t.HeaderHeight + 0.03f + lines * 0.022f + t.ButtonHeight + 2 * t.Padding;
            var panel = UiPanel.Create<UiPanel>("modal", title, 0.50f, h, parent);
            panel.Add(new UiLabel(text, t.BodySize) { Rect = new Rect(t.Padding, t.HeaderHeight + t.Padding, 0.50f - 2 * t.Padding, lines * 0.022f + 0.01f), Wrap = true });
            float bw = (0.50f - 2 * t.Padding - t.Gap * (buttons.Length - 1)) / buttons.Length;
            float opened = Time.realtimeSinceStartup, grace = owner != null ? InputGrace : 0f;
            for (int i = 0; i < buttons.Length; i++)
            {
                int k = i;
                panel.Add(new UiButton(buttons[i], () =>
                {
                    if (Time.realtimeSinceStartup - opened < grace) return;
                    router.Unregister(panel);
                    panel.CloseAndDestroy(); // goes with its closing transition
                    onResult?.Invoke(k);
                }, i == 0 ? UiButtonStyle.Primary : UiButtonStyle.Normal)
                { Rect = new Rect(t.Padding + i * (bw + t.Gap), h - t.Padding - t.ButtonHeight, bw, t.ButtonHeight) });
            }
            panel.Closed += _ =>
            {
                router.Unregister(panel);
                Open.RemoveAll(a => a.Panel == panel);
                if (Application.isPlaying) panel.CloseAndDestroy(); // closed by its ✕: destroyed once its transition ends
            };
            if (owner != null && owner.IsOpen) PlaceOver(panel, owner);
            else panel.PlaceInFront(head, 0.6f, 0.02f);
            panel.SetHighlight(1f);
            router.Register(panel);
            panel.Open();
            Open.Add(new Asked { Panel = panel, Owner = owner, Key = key });
            return panel;
        }

        /// Parallel to `owner`, centred on it, `gap` metres in front of it towards the user, plus how far a bent owner's
        /// sides come forward under the dialog's width (a bent panel curves towards the user): clear of it everywhere.
        public static void PlaceOver(UiPanel panel, UiPanel owner, float gap = 0.05f)
        {
            var ot = owner.transform;
            float scale = Mathf.Max(1e-4f, owner.RestLossyScale.x);
            float r = owner.BendNow * scale, half = panel.Width * panel.RestLossyScale.x * 0.5f;
            float sag = r > 0.01f ? r - Mathf.Sqrt(Mathf.Max(0f, r * r - Mathf.Min(half, r) * Mathf.Min(half, r))) : 0f;
            panel.transform.SetPositionAndRotation(ot.position - ot.forward * (gap + sag), ot.rotation);
        }
    }
}

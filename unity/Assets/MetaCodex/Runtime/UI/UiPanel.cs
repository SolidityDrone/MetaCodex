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
        /// No frame: the panel draws no body shape (so no frame, shadow or halo); only its elements show. Set at creation
        /// (the orb's menu squares). Its frame calls (SetHighlight) do nothing.
        public bool Frameless { get; private set; }
        /// Open as far as everything else is concerned: false as soon as Close() is called, while it may still be
        /// animating out (the router, layouts and toggles see it closed at once). Only "closing while still drawn" is
        /// kept as a flag: a panel turned on by its owner directly is open without any callback (the Editor calls no
        /// OnEnable outside play mode).
        public bool IsOpen => gameObject.activeSelf && !_closing;
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
        private bool _closing, _presented, _activating, _destroyWhenClosed;
        private Vector3 _baseScale = Vector3.one;

        /// The panel's size (the UI scale setting). Set this, not transform.localScale: the panel puts its own scale back
        /// when a transition ends or it is refreshed (a scale written straight to the transform was undone then: panels
        /// jumped between 1.0 and the UI scale).
        public Vector3 BaseScale
        {
            get => _baseScale;
            set { _baseScale = value; transform.localScale = value; } // at once, also while it is cast: a cast scales nothing
        }

        /// The panel's world scale when it is not opening or closing (layouts and placement use this, not the
        /// squashed scale of a transition).
        public Vector3 RestLossyScale => transform.parent != null ? Vector3.Scale(transform.parent.lossyScale, _baseScale) : _baseScale;

        public static T Create<T>(string id, string title, float width, float height, Transform parent = null, bool header = true, bool frameless = false) where T : UiPanel
        {
            var go = new GameObject("panel-" + id);
            if (parent != null) go.transform.SetParent(parent, false);
            var p = go.AddComponent<T>();
            p.Frameless = frameless; // before Init: Init makes the frame (or not)
            p.Init(id, title, width, height, header);
            return p;
        }

        protected virtual void Init(string id, string title, float width, float height, bool header)
        {
            Id = id; Title = title; Width = width; Height = height; HasHeader = header;
            var shapesGo = new GameObject("shapes", typeof(MeshFilter), typeof(MeshRenderer));
            shapesGo.transform.SetParent(transform, false);
            Shapes = shapesGo.AddComponent<UiShapeMesh>();
            Shapes.Init(Theme.ShapeMaterial, !Frameless); // a frame: its body also writes depth first (UiShapeMesh.DepthFirstQueue)
            Hook();
            ClaimSlot();
            TagRenderer(shapesGo.GetComponent<MeshRenderer>(), Vector3.zero);
            _body = Frameless ? -1 : Shapes.Add(Shape(UiShapeKind.Panel, new Rect(0, 0, width, height), Theme.Frame, Theme.CutBig, Theme.CutSmall));
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

        /// Opens with its transition (UiPanelFx): from a closed panel, or one made this frame; a closing one plays its
        /// closing back from where it is. Outside play mode (Editor tools, self-tests) it is open at once.
        public void Open()
        {
            bool pinged = false, started = false;
            _closing = false;
            _destroyWhenClosed = false;
            if (!gameObject.activeSelf)
            {
                _activating = true;
                gameObject.SetActive(true);
                _activating = false;
                BeginTransition(Phase.Opening);
                started = true;
            }
            else if (TransitionPhase == Phase.Closing) { _tDir = -1; started = true; }
            else if (TransitionPhase == Phase.Opening) { started = _tDir < 0; _tDir = 1; }
            else if (!_presented) { BeginTransition(Phase.Opening); started = true; }
            else { Ping(); pinged = true; } // already open: its brackets flash round it, so it is easy to spot
            _presented = true;
            if (!Light && (started || pinged)) UiSounds.Fire(pinged ? "ping" : "open"); // toasts and the HUD have their own
            Opened?.Invoke(this);
        }

        /// Closed at once for everything else (IsOpen false, Closed raised); the panel goes with its transition and is
        /// turned off when it is gone. An opening one plays its opening back.
        public void Close()
        {
            if (!gameObject.activeSelf || _closing) return;
            _closing = true;
            bool seen = _presented;
            // never seen (made and closed in the same frame, e.g. hidden until asked for): off at once, no light
            if (!Animates || !_presented && TransitionPhase == Phase.Open) FinishClosed();
            else if (TransitionPhase == Phase.Opening) _tDir = -1;
            else if (TransitionPhase == Phase.Closing) _tDir = 1;
            else BeginTransition(Phase.Closing);
            if (!Light && seen) UiSounds.Fire("close");
            Closed?.Invoke(this);
        }

        /// Closes with its transition, then destroys the panel (dialogs, toasts).
        public void CloseAndDestroy()
        {
            _destroyWhenClosed = true;
            if (gameObject.activeSelf && !_closing) Close();
            else if (!Transitioning) FinishClosed();
        }

        /// Plays the opening of a panel that is already showing (made active by its owner, e.g. a toast).
        public void PlayOpen() { _closing = false; _presented = true; if (gameObject.activeSelf) BeginTransition(Phase.Opening); }

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

        /// The frame's highlight (a frame has none when Frameless: nothing to light).
        public void SetHighlight(float state) { if (_body >= 0) Shapes.SetState(_body, state); }

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
        /// The panel in a shader slot (null: free).
        public static UiPanel InSlot(int slot) => slot >= 0 && slot < MaxSlots ? Slots[slot] : null;
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
            if (Slot >= 0 && Slots[Slot] == this) { Slots[Slot] = null; BendData[Slot] = Vector4.zero; FxData[Slot] = Vector4.zero; ScanData[Slot] = Vector4.zero; }
        }

        // ------------------------------------------------------------------ opening and closing

        // A panel is cast and taken back (UiPanelFx: the timelines and the light): a line of light crosses it, and
        // everything in it, shapes, texts, icons, images, a terminal, a 3D stage, exists only behind that line. Nothing
        // is moved or scaled: the shaders read the line as a plane from two more global arrays (McxPanelFx.hlsl),
        // _PanelFx[slot] = (1 while cast, flash, all hidden, own brackets hidden) and _PanelScan[slot] = the plane, all
        // 0 at rest, and a light overlay draws the flying brackets, the line, the caster's fan and the last spark.
        public enum Phase { Open, Opening, Closing }
        public Phase TransitionPhase { get; private set; } = Phase.Open;
        /// Progress of the current timeline 0..1 (Opening: 1 = open; Closing: 1 = gone).
        public float TransitionT { get; private set; } = 1f;
        public bool Transitioning => TransitionPhase != Phase.Open;
        /// Self-tests: animate in the Editor too (they step it with TickTransitionForTest).
        public static bool AnimateInEditor;
        private static readonly Vector4[] FxData = new Vector4[MaxSlots];
        private static readonly Vector4[] ScanData = new Vector4[MaxSlots];
        private static readonly int FxArrayId = Shader.PropertyToID("_PanelFx");
        private static readonly int ScanArrayId = Shader.PropertyToID("_PanelScan");
        private int _tDir = 1;
        private UiPanelFx _fx;
        private bool Animates => Application.isPlaying || AnimateInEditor;
        /// A small passing panel (a callout, the voice HUD, the orb's menu): the line crosses it quickly, no brackets fly.
        public bool LightTransition { get; set; }
        private bool Light => LightTransition || Id == "toast" || Id == "voice-hud";
        /// The transition's light (made on first use): which way the scan line crosses, tests.
        public UiPanelFx Fx => _fx != null ? _fx : _fx = UiPanelFx.Create(this);

        private void BeginTransition(Phase phase)
        {
            if (!Animates) { EndOpen(); return; }
            TransitionPhase = phase;
            TransitionT = 0f;
            _tDir = 1;
            Fx.Plan(this); // the line leaves from the caster's side, and comes back to it
            TagForeign();
            ApplyTransition();
            UiPanelFx.RaiseCasting(this, phase == Phase.Opening);
        }

        private readonly HashSet<Renderer> _tagged = new HashSet<Renderer>();

        /// Everything drawn inside the panel that it did not make itself (a terminal's rows and background, a 3D stage)
        /// gets the panel's slot, so its shader is cast with the panel (shaders that do not know the slot ignore it).
        private void TagForeign()
        {
            if (Slot < 0) return;
            _tagged.RemoveWhere(r => r == null);
            foreach (var r in GetComponentsInChildren<Renderer>(true))
            {
                if (_tagged.Contains(r) || (_fx != null && r == _fx.Renderer)) continue;
                if (r.GetComponentInParent<UiPanel>() != this) continue; // another panel's (a panel inside this one): it has its own slot
                TagRenderer(r, transform.InverseTransformPoint(r.transform.position));
            }
        }

        private float _pingT = 1f;

        /// A short flash of the frame and its corner brackets (an open panel asked to open again).
        public void Ping()
        {
            if (!Animates || TransitionPhase != Phase.Open || !gameObject.activeSelf) return;
            _pingT = 0f;
            Fx.Plan(this);
            StepPing(0f);
            UiPanelFx.RaiseCasting(this, true); // the caster turns to it and lights it up
        }

        private void StepPing(float dt)
        {
            if (_pingT >= 1f) return;
            _pingT = Mathf.Min(1f, _pingT + dt / UiTheme.MotionSlow); // the shared motion scale (was 0.42 s)
            var tl = UiPanelFx.Ping(_pingT, Light);
            if (Slot >= 0) FxData[Slot] = _pingT >= 1f ? Vector4.zero : new Vector4(0f, tl.Flash, 0f, 0f);
            if (_pingT >= 1f) Fx.Hide(); else Fx.Draw(this, tl);
        }

        private void StepTransition(float dt)
        {
            if (TransitionPhase == Phase.Open) { StepPing(dt); return; }
            _pingT = 1f;
            float dur = UiPanelFx.Duration(TransitionPhase == Phase.Opening, Light);
            TransitionT += _tDir * dt / dur;
            bool opening = TransitionPhase == Phase.Opening;
            if (TransitionT >= 1f) { if (opening) EndOpen(); else { UiPanelFx.RaiseAbsorbed(this); FinishClosed(); } return; }
            if (TransitionT <= 0f) { if (opening) FinishClosed(); else EndOpen(); return; }
            ApplyTransition();
        }

        private void ApplyTransition()
        {
            var tl = UiPanelFx.Evaluate(TransitionPhase == Phase.Opening, TransitionT, Light);
            if (Slot >= 0)
            {
                FxData[Slot] = new Vector4(1f, tl.Flash, tl.Hide, tl.BracketsOwn);
                ScanData[Slot] = Fx.ScanPlane(this, tl.Scan); // every frame: the panel may be moved while it is cast
            }
            Fx.Draw(this, tl);
        }

        private void EndOpen()
        {
            TransitionPhase = Phase.Open;
            TransitionT = 1f;
            _tDir = 1;
            transform.localScale = _baseScale;
            if (Slot >= 0) { FxData[Slot] = Vector4.zero; ScanData[Slot] = Vector4.zero; }
            if (_fx != null) _fx.Hide();
        }

        private void FinishClosed()
        {
            EndOpen();
            _closing = false; // off now: turned on again it is open
            if (_destroyWhenClosed) { if (Application.isPlaying) Destroy(gameObject); else DestroyImmediate(gameObject); return; }
            if (gameObject.activeSelf) gameObject.SetActive(false);
        }

        // turned on or off by its owner directly (not Open / Close): no transition, as before
        private void OnEnable()
        {
            if (_activating) return;
            _closing = false;
            if (Id != null) _presented = true; // turned on again by its owner (not while it is being made)
            if (TransitionPhase != Phase.Open) EndOpen();
        }

        private void OnDisable()
        {
            if (TransitionPhase != Phase.Open) EndOpen();
        }

        /// Self-tests: what the shaders get for this panel (all zero at rest).
        public Vector4 FxDataForTest => Slot >= 0 ? FxData[Slot] : Vector4.zero;
        public Vector4 ScanDataForTest => Slot >= 0 ? ScanData[Slot] : Vector4.zero;

        /// Self-tests: one step of the transition or ping (the Editor has no frame loop).
        public void TickTransitionForTest(float dt) { StepTransition(dt); }

        /// Self-tests: shows a moment of a timeline (pictures of the transition).
        public void PoseTransitionForTest(bool opening, float t)
        {
            if (TransitionPhase == Phase.Open) { Fx.Plan(this); TagForeign(); }
            TransitionPhase = opening ? Phase.Opening : Phase.Closing;
            TransitionT = Mathf.Clamp01(t);
            ApplyTransition();
            PushBendGlobals();
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
            Shader.SetGlobalVectorArray(FxArrayId, FxData);
            Shader.SetGlobalVectorArray(ScanArrayId, ScanData);
            _pushedFrame = Time.frameCount;
        }

        /// Tags a renderer of this panel (shapes, a text, a text's fallback sub-mesh) with the panel's slot and, for
        /// a text, where its origin sits in the panel.
        public void TagRenderer(Renderer r, Vector3 offset)
        {
            if (r == null) return;
            _tagged.Add(r);
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

        /// Captures: draws this panel flat for one render (every other panel keeps its bend), then as it was.
        public void RenderFlat(Action render)
        {
            // at rest for the render too: whole, no transition light (a capture while the panel is being cast is whole)
            if (Slot < 0) { render(); return; }
            var keep = BendData[Slot];
            var keepFx = FxData[Slot];
            var keepScan = ScanData[Slot];
            bool light = _fx != null && _fx.Renderer != null && _fx.Renderer.enabled;
            if (light) _fx.Renderer.enabled = false;
            BendData[Slot] = Vector4.zero;
            FxData[Slot] = Vector4.zero;
            ScanData[Slot] = Vector4.zero;
            PushBendGlobals();
            try { render(); }
            finally
            {
                BendData[Slot] = keep;
                FxData[Slot] = keepFx;
                ScanData[Slot] = keepScan;
                PushBendGlobals();
                if (light) _fx.Renderer.enabled = true;
            }
        }

        private void LateUpdate()
        {
            if (_pushedFrame != Time.frameCount) PushBendGlobals();
        }

        /// Self-tests: one frame of this panel (the Editor has no frame loop).
        public void UpdateForTest() => Update();

        protected virtual void Update()
        {
            EaseBend(Time.unscaledDeltaTime);
            _presented = true;
            StepTransition(Time.unscaledDeltaTime);
            if (this == null || !gameObject.activeSelf) return; // gone this frame
            if (IsOpen) Tick?.Invoke(this);
            foreach (var e in Elements) if (e.Visible) e.Update();
        }

        /// Editor/self-test: lay out and upload everything without a frame loop.
        public void ForceRefresh()
        {
            if (TransitionPhase != Phase.Open) { if (!_closing) EndOpen(); else { FinishClosed(); return; } }
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

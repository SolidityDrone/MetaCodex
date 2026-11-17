using System.Collections.Generic;
using System.Linq;
using MetaCodex.UI;
using UnityEngine;

namespace MetaCodex.Shell
{
    /// The two layouts. FIXED: panels are world-locked where you leave them (bent towards you when "Bend panels"
    /// is on). FLOAT: every panel sits on an invisible cylinder around you (a circular screen) with a gap
    /// between neighbours; the cylinder follows you horizontally as you walk, never your head's pitch or roll,
    /// and spins freely with inertia (close the hand in empty space and swipe sideways; no snapping). When the
    /// open panels do not fit around the circle it grows, so panels never overlap; a panel you drop moves to
    /// the nearest free place at its height.
    public sealed partial class MetaCodexShell
    {
        private Transform _carousel;
        private float _baseYaw, _spin, _spinVel, _carouselY;
        private readonly Dictionary<Transform, (float angle, float height)> _slots = new Dictionary<Transform, (float, float)>();
        private readonly bool[] _spinning = new bool[2];
        private readonly float[] _spinLast = new float[2];
        private readonly bool[] _wasGrab = new bool[2];
        private float _dockYaw;

        public bool Floating => Settings != null && Settings.layout == "float";
        public float CarouselSpin => _spin;

        public void SetLayout(string mode)
        {
            mode = mode == "float" ? "float" : "fixed";
            if (Settings.layout == mode && (mode == "fixed" || _carousel != null)) return;
            Settings.layout = mode;
            Settings.Save(_settingsPath);
            if (mode == "float") EnterFloat(); else ExitFloat();
            Toast(mode == "float" ? "Float layout: close your hand in empty space and swipe to spin" : "Fixed layout: panels stay where you put them", "info");
        }

        private IEnumerable<UiPanel> LayoutPanels()
        {
            foreach (var kv in _panels) if (kv.Key != "dock" && kv.Key != "picker" && kv.Key != ControlId && kv.Key != SidebarId && !IsGroupedPanel(kv.Value)) yield return kv.Value; // CONTROL's rail and THREAD's sidebar ride with their window; a grouped window (thread or web) with its group
            foreach (var f in _frames.Values) yield return f;
        }

        /// THREAD's sidebar rides at the thread's right edge on the ring (FLOAT): the pair takes one slot.
        private float SbGapDeg(float r) => SbGap * (_sb != null ? _sb.RestLossyScale.x : 1f) / Mathf.Max(r, 0.1f) * Mathf.Rad2Deg;

        /// Half the arc a panel takes on the ring, including its sidebar when it is the thread.
        private float GroupHalfArc(UiPanel p, float r) =>
            p != null && p == SbAnchor && SbLinked ? HalfArc(p, r) + SbGapDeg(r) * 0.5f + HalfArc(_sb, r)
            : RailBeside(p) ? HalfArc(p, r) + RailShift(r) // CONTROL: the rail on the window's left
            : HalfArc(p, r);

        /// A panel's own centre for a group centred at `groupAngle`: the thread left of its workspace, a CONTROL window
        /// right of its rail.
        private float PanelAngleInGroup(UiPanel p, float groupAngle, float r) =>
            p != null && p == SbAnchor ? ThreadAngleInGroup(groupAngle, r) : RailBeside(p) ? groupAngle + RailShift(r) : groupAngle;

        /// The thread's own centre for a group centred at `groupAngle` (the sidebar sits to its right).
        private float ThreadAngleInGroup(float groupAngle, float r) => SbLinked ? groupAngle - SbGapDeg(r) * 0.5f - HalfArc(_sb, r) : groupAngle;

        /// The centre of the thread's group (thread + sidebar) for the thread's own angle.
        private float ThreadGroupAngle(float threadAngle, float r) => SbLinked ? threadAngle + SbGapDeg(r) * 0.5f + HalfArc(_sb, r) : threadAngle;

        private void EnterFloat()
        {
            var head = Head;
            Vector3 hp = head != null ? head.position : new Vector3(0, 1.3f, 0);
            if (_carousel == null) { _carousel = new GameObject("Carousel").transform; _carousel.SetParent(_root, false); }
            _carousel.position = new Vector3(hp.x, 0f, hp.z);
            _carouselY = hp.y;
            var fwd = head != null ? head.forward : Vector3.forward;
            _baseYaw = Mathf.Atan2(fwd.x, fwd.z) * Mathf.Rad2Deg;
            _spin = 0f; _spinVel = 0f;
            _dockYaw = _baseYaw;
            _slots.Clear();
            ArrangeRing();
            EnsureRingBar();
            // a bar already open (a recenter while floating) keeps its yaw and follows from there: no jump
            if (!_ringBar.IsOpen) { _barYaw = _baseYaw; _barYawVel = 0f; _barFollowing = false; _ringBar.Open(); }
        }

        private const float RingGap = 4f;     // degrees between neighbours
        private const float RowHeight = -0.04f; // panel centres just below the eyes

        /// The cylinder's radius: the chosen one, grown when the open panels (plus gaps) would not fit around it.
        private float FloatRadius()
        {
            float r = Mathf.Clamp(Settings.carouselRadius, 0.5f, 2f);
            var open = LayoutPanels().Where(p => p != null && p.IsOpen).ToList();
            if (open.Count == 0) return r;
            float width = open.Sum(p => p.Width * p.RestLossyScale.x) + (_rail != null && _rail.IsOpen ? (RailW + RailGap) * _rail.RestLossyScale.x : 0f);
            float room = 2f * Mathf.PI * 0.94f - open.Count * RingGap * Mathf.Deg2Rad; // radians left for the panels themselves
            return room <= 0.2f ? 2.5f : Mathf.Clamp(Mathf.Max(r, width / room), r, 2.5f);
        }

        /// Half the angle a panel takes on the cylinder: a bent panel's width is an arc (conservative for flat ones).
        private static float HalfArc(UiPanel p, float r) => p.Width * 0.5f * p.RestLossyScale.x / Mathf.Max(r, 0.1f) * Mathf.Rad2Deg;

        private static bool SameRow(UiPanel a, float ha, UiPanel b, float hb) =>
            Mathf.Abs(ha - hb) < (a.Height * a.RestLossyScale.y + b.Height * b.RestLossyScale.y) * 0.5f;

        /// Angles taken by the open panels whose height range meets `p`'s, except `p`.
        private List<(float, float)> Occupied(UiPanel p, float height, float r)
        {
            var occ = new List<(float, float)>();
            foreach (var kv in _slots)
            {
                var q = kv.Key != null ? kv.Key.GetComponent<UiPanel>() : null;
                if (q == null || q == p || !q.IsOpen) continue;
                if (!SameRow(p, height, q, kv.Value.height)) continue;
                if (RailBeside(q)) { occ.Add((kv.Value.angle - RailShift(r), GroupHalfArc(q, r))); continue; } // with its rail
                if (q == SbAnchor && SbLinked) { occ.Add((ThreadGroupAngle(kv.Value.angle, r), GroupHalfArc(q, r))); continue; } // with its sidebar
                occ.Add((kv.Value.angle, HalfArc(q, r)));
            }
            return occ;
        }

        /// Side by side around the circle: terminals in the middle (in front), the other panels alternating
        /// right and left, ordered by where they were. Spin to reach the ones behind you.
        private void ArrangeRing()
        {
            float r = FloatRadius();
            var c = _carousel.position;
            float Yaw(UiPanel p) { var d = p.transform.position - c; return Mathf.DeltaAngle(_baseYaw, Mathf.Atan2(d.x, d.z) * Mathf.Rad2Deg); }
            var open = LayoutPanels().Where(p => p != null && p.IsOpen).ToList();
            var ordered = open.Where(p => p.Id.StartsWith("term-")).OrderBy(Yaw).Concat(open.Where(p => !p.Id.StartsWith("term-")).OrderBy(p => Mathf.Abs(Yaw(p)))).ToList();
            _slots.Clear();
            float right = 0f, left = 0f;
            bool first = true, toRight = true;
            foreach (var p in ordered)
            {
                float half = GroupHalfArc(p, r);
                float angle;
                if (first) { angle = 0f; right = half; left = -half; }
                else if (toRight) { angle = right + RingGap + half; right = angle + half; }
                else { angle = left - RingGap - half; left = angle - half; }
                if (!first) toRight = !toRight;
                first = false;
                _slots[p.transform] = (PanelAngleInGroup(p, angle, r), RowHeight);
            }
            _ringRadius = r;
        }

        private float _ringRadius;

        /// A panel opened while floating takes the free gap of the ring nearest to where the user looks; when
        /// there is none the ring grows and is laid out again.
        private void OnOpenedFloating(UiPanel p)
        {
            if (!Floating || p == null || _carousel == null || p.Id == "dock" || p.Id == "picker" || IsGroupedPanel(p)) return;
            float r = FloatRadius();
            if (r > _ringRadius + 0.02f) { ArrangeRing(); return; }
            var head = Head;
            float gazeWorld = head != null ? Mathf.Atan2(head.forward.x, head.forward.z) * Mathf.Rad2Deg : _baseYaw;
            float gaze = Mathf.DeltaAngle(_baseYaw + _spin, gazeWorld);
            // in view first (owner 2026-11-13: new windows in front of the user), the rest of the ring only when the view is full
            float half = GroupHalfArc(p, r);
            var occ = Occupied(p, RowHeight, r);
            float a = FindFreeYaw(gaze, half, occ, ViewYaw, RingGap);
            if (float.IsNaN(a)) a = FindFreeYaw(gaze, half, occ, 180f, RingGap);
            if (float.IsNaN(a)) { ArrangeRing(); return; }
            _slots[p.transform] = (PanelAngleInGroup(p, a, r), RowHeight);
        }

        private void ExitFloat()
        {
            _slots.Clear();
            if (_ringBar != null && _ringBar.IsOpen) _ringBar.Close();
            foreach (var p in LayoutPanels()) p.SetBend(0f);
            UpdateFixedBend();
        }

        // ---- FLOAT's TURN bar (owner 2026-11-13: "keep the stuff fixed in the rig, follow us, and add a scroll bar below to
        // rotate the rig; the float should not rotate by itself"): a bar under the ring; its slider turns the ring. No
        // gesture turns the ring any more. It does not spin with the ring; it follows the head's yaw lazily (owner
        // 2026-11-16: "if it is fixed we have to turn all around to select and turn. Not locked to the camera, but a smooth
        // follow"): it stays put while the user looks within BarDeadZoneDeg of it, beyond that it eases round the ring to
        // where they face (critically damped, no overshoot), and it holds still while a hand aims at it or holds it.
        private UiPanel _ringBar;
        private UiSlider _ringTurn;
        private float _barYaw, _barYawVel; // the bar's world yaw (degrees) and its follow speed (degrees/s)
        private bool _barFollowing;
        /// The body may move this far (metres) before the ring follows it.
        public const float RigDeadZone = 0.30f;
        /// The TURN bar stays put while the user's yaw is within this many degrees of it; beyond, it follows.
        public const float BarDeadZoneDeg = 25f;
        /// The follow's smooth time (s; Mathf.SmoothDampAngle: critically damped, it arrives in about this long).
        public const float BarFollowTime = 1.5f;
        /// The follow ends this close to where the user faces (degrees); then the dead zone holds the bar again.
        private const float BarSettleDeg = 1f;

        private void EnsureRingBar()
        {
            if (_ringBar != null) return;
            _ringBar = UiPanel.Create<UiPanel>("ring-bar", "", 0.62f, 0.07f, _root, header: false);
            _ringBar.Closable = false;
            _ringBar.Draggable = false;
            Router.Register(_ringBar); // like the dock: not in _panels, so the layout leaves it to this
            _ringTurn = _ringBar.Add(new UiSlider("TURN", -360f, 360f, 0f, v => { _spin = v; _spinVel = 0f; }) { Rect = _ringBar.Content, Format = "0", LabelWidth = 0.09f });
        }

        /// The bar sits below the windows' row on the ring's radius, facing the user, at its own yaw (not the ring's: the
        /// ring's spin does not move it), which follows the head lazily; its slider shows the turn (shown wrapped to
        /// -360..360: a full turn is the same ring). Per frame: a few float ops, no allocation.
        private void UpdateRingBar(Vector3 c, float r, float dt)
        {
            if (_ringBar == null) return;
            // a hand holds its slider (or presses on it), or aims at it: the bar holds still, and the follow restarts from rest
            bool held = Router != null && (Router.IsPressing(_ringBar) || Router.HandRayPanel(0) == _ringBar || Router.HandRayPanel(1) == _ringBar);
            var head = Head;
            if (held) _barYawVel = 0f;
            else if (head != null && dt > 0f)
            {
                var f = head.forward;
                if (f.x * f.x + f.z * f.z > 0.04f) // looking almost straight up or down the yaw is noise: the bar waits
                {
                    float face = Mathf.Atan2(f.x, f.z) * Mathf.Rad2Deg;
                    if (!_barFollowing && Mathf.Abs(Mathf.DeltaAngle(_barYaw, face)) > BarDeadZoneDeg) _barFollowing = true;
                    if (_barFollowing)
                    {
                        _barYaw = Mathf.Repeat(Mathf.SmoothDampAngle(_barYaw, face, ref _barYawVel, BarFollowTime, Mathf.Infinity, dt), 360f);
                        if (Mathf.Abs(Mathf.DeltaAngle(_barYaw, face)) < BarSettleDeg) { _barFollowing = false; _barYawVel = 0f; }
                    }
                }
            }
            float a = _barYaw * Mathf.Deg2Rad;
            var dir = new Vector3(Mathf.Sin(a), 0f, Mathf.Cos(a));
            _ringBar.transform.SetPositionAndRotation(new Vector3(c.x, _carouselY - 0.36f, c.z) + dir * r, Quaternion.LookRotation(dir, Vector3.up));
            float shown = Mathf.Repeat(_spin + 360f, 720f) - 360f;
            if (_ringTurn != null && !held && Mathf.Abs(shown - _ringTurn.Value) > 0.5f) _ringTurn.Set(shown);
        }

        /// Windows that never bend: terminals (their text grid cannot bend yet) and WEB (the page is one flat picture,
        /// so a bent frame around it would not match where the pointer touches the page). A window group bends; its
        /// thread windows lie on its curve, its web windows flat on their cells' tangent planes (ShellThreadGroups).
        public static bool StaysFlat(UiPanel p) => p.Id.StartsWith("term-") || IsWebPanelId(p.Id); // every web window (ShellWeb)

        // ---- curve mode's lean (owner 2026-11-16: "if a panel is above a certain height, curve mode should not only curve it
        // but also tilt it, so a panel high up faces slightly toward my eyes"): a bent window high in the view leans its top
        // towards the eyes. Keyed on where the window is (its elevation), not on its size: a normal window placed high leans.

        /// The elevation (degrees: how far above the eyes the window's centre is seen) from which a bent window leans, the
        /// elevation at which it leans in full, and the most it leans (degrees). Between the first two the lean ramps up.
        public const float TiltFromDeg = 8f, TiltFullDeg = 20f, TiltMaxDeg = 30f;

        /// The lean (degrees) of a window in curve mode, any size: elev = atan2(centre.y - eye.y, horizontal distance);
        /// 0 below TiltFromDeg; else the ramp k (0 at TiltFromDeg, 1 at TiltFullDeg) times elev, at most TiltMaxDeg. The
        /// yaw is not touched; the panel leans its top towards the user (UiPanel.ApplyTilt), so its face turns down to the eyes.
        public static float LayoutTilt(UiPanel p, Vector3 eye)
        {
            var d = p.transform.position - eye; // the panel's pivot is its centre (UiPanel.PanelToLocal)
            if (d.y <= 0f) return 0f;
            float elev = Mathf.Atan2(d.y, Mathf.Sqrt(d.x * d.x + d.z * d.z)) * Mathf.Rad2Deg;
            float k = Mathf.Clamp01((elev - TiltFromDeg) / (TiltFullDeg - TiltFromDeg));
            return k <= 0f ? 0f : Mathf.Min(TiltMaxDeg, k * elev);
        }

        /// Fixed layout with "Bend panels": each panel curves towards you around its own distance (bent on the GPU,
        /// eased by the panel, so it follows you smoothly). Terminals and WEB stay flat (StaysFlat). A thread group gets
        /// the same curve in its own units (LayoutBend); its windows are not layout panels, they follow it. A bent
        /// window seen high above the eyes also leans its top to them (LayoutTilt); with bending off nothing leans.
        public void UpdateFixedBend()
        {
            if (Floating || Settings == null) return;
            var head = Head;
            foreach (var p in LayoutPanels())
            {
                if (p == null) continue;
                if (!Settings.bendPanels || StaysFlat(p) || head == null) { p.SetBend(0f); p.SetTilt(0f); continue; }
                var d = p.transform.position - head.position;
                float dist = Mathf.Max(0.45f, new Vector2(d.x, d.z).magnitude);
                p.SetBend(LayoutBend(p, dist)); // the GPU bends; the panel eases towards it (no steps)
                p.SetTilt(LayoutTilt(p, head.position)); // its centre as the eyes see it now; the panel eases towards it too
            }
        }

        private void ResetCarousel()
        {
            if (Floating) EnterFloat();
        }

        /// Records a panel's place on the cylinder from where it is now (angle around you, height), moved to the
        /// nearest free angle when it would overlap a panel at the same height.
        private void Slot(Transform t)
        {
            var c = _carousel.position;
            var d = t.position - c;
            float world = Mathf.Atan2(d.x, d.z) * Mathf.Rad2Deg;
            float angle = Mathf.DeltaAngle(_baseYaw + _spin, world);
            float height = t.position.y - _carouselY;
            var p = t.GetComponent<UiPanel>();
            if (p != null)
            {
                float r = Mathf.Max(_ringRadius, FloatRadius());
                bool group = p == SbAnchor && SbLinked;
                // a dropped thread: its group (thread + sidebar) is centred a little to the right of the thread; a
                // CONTROL window's group (rail + window) a little to its left
                float groupAngle = group ? ThreadGroupAngle(angle, r) : RailBeside(p) ? angle - RailShift(r) : angle;
                float free = FindFreeYaw(groupAngle, GroupHalfArc(p, r), Occupied(p, height, r), 180f, RingGap);
                if (!float.IsNaN(free)) angle = group ? ThreadAngleInGroup(free, r) : RailBeside(p) ? free + RailShift(r) : free;
            }
            _slots[t] = (angle, height);
        }

        private void UpdateLayout() => UpdateLayout(Mathf.Min(Time.unscaledDeltaTime, 0.05f));

        /// Self-tests: run the layout for `steps` frames of `dt` seconds.
        public void TickLayoutForTest(float dt, int steps) { for (int i = 0; i < steps; i++) UpdateLayout(dt); }

        public Vector3 CarouselCenter => _carousel != null ? new Vector3(_carousel.position.x, _carouselY, _carousel.position.z) : Vector3.zero;

        private float _nextFixedBend;

        private void UpdateLayout(float dt)
        {
            if (Settings == null) return;
            TickThreadWindows(dt); // thread windows dropped on each other, the groups' grids (ShellThreadGroups)
            TickGlide(dt);
            UpdateSidebarLink();
            UpdateControlRail();
            if (!Floating)
            {
                // fixed: follow the distance every frame (panels are dragged, the user walks): only a uniform changes
                // after the windows' curve and lean: THREAD's sidebar, and CONTROL's rail on its window's surface
                if (Settings.bendPanels) { UpdateFixedBend(); UpdateSidebarLink(); UpdateControlRail(); }
                return;
            }
            if (_carousel == null) EnterFloat();
            var head = Head;
            if (head != null)
            {
                // follow walking (x/z), never head nods or tilts; and only past a dead zone: a step of a few centimetres
                // no longer slides every window (owner 2026-11-13: "float moves too much around")
                var off = new Vector3(head.position.x - _carousel.position.x, 0f, head.position.z - _carousel.position.z);
                float far = off.magnitude;
                if (far > RigDeadZone) _carousel.position += off * ((far - RigDeadZone) / far) * (1f - Mathf.Exp(-dt * 2.5f));
            }
            // no gesture turns the ring any more: the TURN bar does (a grab in empty space turned it by accident, and went on)
            if (!_spinning[0] && !_spinning[1])
            {
                _spin += _spinVel * dt;
                _spinVel *= Mathf.Exp(-dt * 1.6f);
                if (Mathf.Abs(_spinVel) < 0.5f) _spinVel = 0f;
            }

            float r = Mathf.Max(_ringRadius, Mathf.Clamp(Settings.carouselRadius, 0.5f, 2f));
            if (_ringRadius <= 0f) _ringRadius = r;
            var c = _carousel.position;
            foreach (var p in LayoutPanels())
            {
                if (p == null || !p.IsOpen) continue;
                var t = p.transform;
                if (Router.IsDragging(p) || RailCarries(p) || p == SbAnchor && SbLinked && Router.IsDragging(_sb) || IsResizing(p)) { _slots.Remove(t); continue; } // re-slotted where it is dropped
                if (!_slots.TryGetValue(t, out var slot)) { Slot(t); slot = _slots[t]; }
                float a = (_baseYaw + _spin + slot.angle) * Mathf.Deg2Rad;
                var dir = new Vector3(Mathf.Sin(a), 0f, Mathf.Cos(a));
                var pos = new Vector3(c.x, _carouselY + slot.height, c.z) + dir * r;
                t.SetPositionAndRotation(Vector3.Lerp(t.position, pos, 1f - Mathf.Exp(-dt * 12f)), Quaternion.LookRotation(dir, Vector3.up));
                p.SetBend(Settings.bendPanels && !StaysFlat(p) ? LayoutBend(p, r) : 0f);
                p.SetTilt(0f); // the ring stands upright: its poses are set above, every frame (no lean from FIXED)
            }
            UpdateRingBar(_carousel.position, r, dt); // the bar under the ring: follows the head's yaw lazily
            UpdateControlRail(); // after the ring moved its window
            UpdateSidebarLink(); // after the ring moved the thread

            // the dock: lower and closer, turns lazily to stay in front of you (it does not spin with the carousel)
            if (_panels.TryGetValue("dock", out var dock) && dock.IsOpen && head != null && !Router.IsDragging(dock))
            {
                var f = head.forward;
                float headYaw = Mathf.Atan2(f.x, f.z) * Mathf.Rad2Deg;
                if (Mathf.Abs(Mathf.DeltaAngle(_dockYaw, headYaw)) > 40f) _dockYaw = Mathf.MoveTowardsAngle(_dockYaw, headYaw, 90f * dt);
                float a = _dockYaw * Mathf.Deg2Rad;
                var dir = new Vector3(Mathf.Sin(a), 0f, Mathf.Cos(a));
                var pos = new Vector3(c.x, _carouselY - 0.42f, c.z) + dir * 0.55f;
                dock.transform.position = Vector3.Lerp(dock.transform.position, pos, 1f - Mathf.Exp(-dt * 6f));
                dock.transform.rotation = Quaternion.LookRotation((dock.transform.position - head.position).normalized, Vector3.up);
            }
        }

        // ---- THREAD + its sidebar in FIXED (owner 2026-11-11): the sidebar opens at the thread's right edge, so the pair
        // spans about 95 degrees and the sidebar sits far to the right. When it opens, the pair glides round the user
        // until it is centred where they face, as far as the other windows and the room's walls let it (ShellPlacement's
        // view rectangles and depth); when it folds and the thread is still where the glide left it, it glides back.

        private string _pairThread;
        private float _pairShift;
        private Vector3 _pairPos;
        private Quaternion _pairRot;
        private UiPanel _glide;
        private float _glideT = -1f;
        private Vector3 _glideFromPos, _glideToPos;
        private Quaternion _glideFromRot, _glideToRot;
        private const float GlideTime = UiTheme.MotionSlow, PairMinShift = 3f; // the shared motion scale (was 0.45 s)

        private static ViewRect Union(ViewRect a, ViewRect b) => new ViewRect
            { Yaw0 = Mathf.Min(a.Yaw0, b.Yaw0), Yaw1 = Mathf.Max(a.Yaw1, b.Yaw1), Pitch0 = Mathf.Min(a.Pitch0, b.Pitch0), Pitch1 = Mathf.Max(a.Pitch1, b.Pitch1) };

        private static ViewRect Shifted(ViewRect r, float yaw) => new ViewRect { Yaw0 = r.Yaw0 + yaw, Yaw1 = r.Yaw1 + yaw, Pitch0 = r.Pitch0, Pitch1 = r.Pitch1 };

        /// The largest shift (degrees, from `want` towards 0) that puts `group` on no other window (2 degree gap) and keeps
        /// it 8 cm in front of the room's walls; 0 when none.
        private float FreePairShift(ViewRect group, float want, float dist, Vector3 eye, float front, params UiPanel[] skip)
        {
            var taken = new List<ViewRect>();
            foreach (var q in PlacementObstacles(null))
            {
                if (q == null || q.Id == "dock" || System.Array.IndexOf(skip, q) >= 0) continue;
                if (ViewRectOf(q, eye, front, out var r)) taken.Add(r);
            }
            var room = RoomClearance(eye, front, out int yb, out int pb);
            int steps = Mathf.Max(1, Mathf.CeilToInt(Mathf.Abs(want) / 1.5f));
            for (int i = steps; i >= 1; i--)
            {
                float s = want * i / steps;
                if (Mathf.Abs(s) < PairMinShift) break;
                var c = Shifted(group, s);
                bool free = ClearanceIn(room, yb, pb, c) - 0.08f >= dist - 0.02f;
                for (int k = 0; k < taken.Count && free; k++) if (c.Overlaps(taken[k], 2f)) free = false;
                if (free) return s;
            }
            return 0f;
        }

        /// The sidebar just opened (FIXED): the pair is centred where the user faces when there is room.
        private void CentreThreadPair()
        {
            _pairThread = null;
            var head = Head;
            var th = SbAnchor; // the sidebar's window, or its group
            if (Floating || head == null || _sb == null || !_sb.IsOpen || th == null || !th.IsOpen) return;
            if (Router != null && (Router.IsDragging(th) || Router.IsDragging(_sb))) return;
            Vector3 eye = head.position;
            float front = FrontYaw();
            if (!ViewRectOf(th, eye, front, out var a) || !ViewRectOf(_sb, eye, front, out var b)) return;
            var group = Union(a, b);
            float want = -group.YawMid;
            if (Mathf.Abs(want) < PairMinShift) return; // centred already
            var d = th.transform.position - eye;
            float shift = FreePairShift(group, want, new Vector2(d.x, d.z).magnitude, eye, front, th, _sb);
            if (shift == 0f) return;
            GlideRound(th, eye, shift, th.TransitionPhase != UiPanel.Phase.Open);
            _pairThread = _sbOwner.threadOpen;
            _pairShift = shift;
            _pairPos = _glideToPos;
            _pairRot = _glideToRot;
        }

        /// The sidebar folded (FIXED): the thread goes back where it was before the pair was centred, when the user has
        /// not moved it since and that place is still free.
        private void UncentreThreadPair()
        {
            string was = _pairThread;
            _pairThread = null;
            var head = Head;
            var th = SbAnchor;
            if (was == null || was != _sbOwner.threadOpen || Floating || head == null || th == null || !th.IsOpen) return;
            bool gliding = _glide == th && _glideT >= 0f;
            var pos = gliding ? _glideToPos : th.transform.position;
            var rot = gliding ? _glideToRot : th.transform.rotation;
            if (Vector3.Distance(pos, _pairPos) > 0.01f || Quaternion.Angle(rot, _pairRot) > 1f) return; // moved by the user: it stays
            if (gliding) { th.transform.SetPositionAndRotation(_glideToPos, _glideToRot); _glideT = -1f; }
            Vector3 eye = head.position;
            float front = FrontYaw();
            if (!ViewRectOf(th, eye, front, out var a)) return;
            var d = th.transform.position - eye;
            float back = FreePairShift(a, -_pairShift, new Vector2(d.x, d.z).magnitude, eye, front, th, _sb);
            if (Mathf.Abs(back - -_pairShift) > 0.01f) return; // its old place is taken now: it stays
            GlideRound(th, eye, back, false);
        }

        /// Turns a panel round the user's vertical axis through `eye` by `yaw` degrees (eased over 0.45 s, or at once).
        private void GlideRound(UiPanel p, Vector3 eye, float yaw, bool now)
        {
            var q = Quaternion.Euler(0f, yaw, 0f);
            var e = new Vector3(eye.x, 0f, eye.z);
            var pos = p.transform.position;
            var flatFrom = new Vector3(pos.x, 0f, pos.z) - e;
            _glideFromPos = pos;
            _glideFromRot = p.transform.rotation;
            _glideToPos = e + q * flatFrom + Vector3.up * pos.y;
            _glideToRot = q * p.transform.rotation;
            _glide = p;
            _glideT = 0f;
            if (now) TickGlide(GlideTime);
        }

        private void TickGlide(float dt)
        {
            if (_glide == null || _glideT < 0f) return;
            if (!_glide.IsOpen || Floating || Router != null && (Router.IsDragging(_glide) || _sb != null && Router.IsDragging(_sb))) { _glideT = -1f; return; } // the user took it
            _glideT = Mathf.Min(1f, _glideT + dt / GlideTime);
            float k = _glideT * _glideT * (3f - 2f * _glideT);
            // round the user, not through them: the angle is eased, the distance kept
            var q = Quaternion.Slerp(_glideFromRot, _glideToRot, k);
            var e = Head != null ? Head.position : Vector3.zero;
            var from = _glideFromPos; var to = _glideToPos;
            float yawFrom = Mathf.Atan2(from.x - e.x, from.z - e.z) * Mathf.Rad2Deg, yawTo = Mathf.Atan2(to.x - e.x, to.z - e.z) * Mathf.Rad2Deg;
            float r0 = new Vector2(from.x - e.x, from.z - e.z).magnitude, r1 = new Vector2(to.x - e.x, to.z - e.z).magnitude;
            float yw = Mathf.LerpAngle(yawFrom, yawTo, k) * Mathf.Deg2Rad, rr = Mathf.Lerp(r0, r1, k);
            var pos = k >= 1f ? to : new Vector3(e.x + Mathf.Sin(yw) * rr, Mathf.Lerp(from.y, to.y, k), e.z + Mathf.Cos(yw) * rr);
            _glide.transform.SetPositionAndRotation(pos, q);
            if (_glideT >= 1f) _glideT = -1f;
        }

        /// Self-tests: the pair's last centring shift (degrees; 0: none) and whether it is still gliding.
        public float PairShiftForTest => _pairThread != null ? _pairShift : 0f;
        public bool PairGlidingForTest => _glideT >= 0f;

        /// Spin: a closed hand that started in empty space (its ray on no panel) and moves sideways.
        private void SpinInput(float dt)
        {
            if (Gestures == null || _carousel == null) return;
            for (int h = 0; h < 2; h++)
            {
                var st = Gestures.Get((MetaCodex.Hands.Handedness)h);
                bool grab = st.Tracked && st.Grabbing;
                var d = st.Frame.Palm - _carousel.position;
                float ang = Mathf.Atan2(d.x, d.z) * Mathf.Rad2Deg;
                if (grab && !_wasGrab[h] && Router.HandRayPanel(h) == null && !Router.HandBusy(h)) { _spinning[h] = true; _spinLast[h] = ang; _spinVel = 0f; }
                if (_spinning[h])
                {
                    if (!grab) _spinning[h] = false;
                    else
                    {
                        // moving the hand around you turns the carousel with it (amplified: the hand is close)
                        float delta = Mathf.DeltaAngle(_spinLast[h], ang) * 2.2f;
                        _spinLast[h] = ang;
                        _spin += delta;
                        if (dt > 1e-4f) _spinVel = Mathf.Lerp(_spinVel, delta / dt, 0.35f);
                    }
                }
                _wasGrab[h] = grab;
            }
        }

        /// Tests and the control port: spin by degrees (as if swiped), with the given release speed.
        public void Spin(float degrees, float releaseSpeed = 0f) { _spin += degrees; _spinVel = releaseSpeed; }
    }
}

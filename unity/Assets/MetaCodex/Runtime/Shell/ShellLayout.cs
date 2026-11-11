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
            foreach (var kv in _panels) if (kv.Key != "dock" && kv.Key != "picker" && kv.Key != ControlId) yield return kv.Value; // CONTROL's rail rides with its window
            foreach (var f in _frames.Values) yield return f;
        }

        /// The thread's workspace module rides beside the thread on the ring (FLOAT): the pair takes one slot.
        private bool WsLinked => _ws != null && _ws.IsOpen && _panels.TryGetValue("thread", out var th) && th.IsOpen;
        private const float WsRingGap = 1.2f;

        /// Half the arc a panel takes on the ring, including its workspace when it is the thread.
        private float GroupHalfArc(UiPanel p, float r) =>
            p != null && p.Id == "thread" && WsLinked ? HalfArc(p, r) + WsRingGap * 0.5f + HalfArc(_ws, r)
            : RailBeside(p) ? HalfArc(p, r) + RailShift(r) // CONTROL: the rail on the window's left
            : HalfArc(p, r);

        /// A panel's own centre for a group centred at `groupAngle`: the thread left of its workspace, a CONTROL window
        /// right of its rail.
        private float PanelAngleInGroup(UiPanel p, float groupAngle, float r) =>
            p != null && p.Id == "thread" ? ThreadAngleInGroup(groupAngle, r) : RailBeside(p) ? groupAngle + RailShift(r) : groupAngle;

        /// The thread's own centre for a group centred at `groupAngle` (the workspace sits to its right).
        private float ThreadAngleInGroup(float groupAngle, float r) => WsLinked ? groupAngle - WsRingGap * 0.5f - HalfArc(_ws, r) : groupAngle;

        private void SlotWorkspaceBesideThread(float r)
        {
            if (!WsLinked || !_panels.TryGetValue("thread", out var th) || !_slots.TryGetValue(th.transform, out var ts)) return;
            _slots[_ws.transform] = (ts.angle + HalfArc(th, r) + WsRingGap + HalfArc(_ws, r), ts.height);
        }

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
                if (p != null && p.Id == "thread" && q == _ws) continue; // the thread's own workspace
                if (!SameRow(p, height, q, kv.Value.height)) continue;
                if (RailBeside(q)) { occ.Add((kv.Value.angle - RailShift(r), GroupHalfArc(q, r))); continue; } // with its rail
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
            var open = LayoutPanels().Where(p => p != null && p.IsOpen && !(p == _ws && WsLinked)).ToList();
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
            SlotWorkspaceBesideThread(r);
            _ringRadius = r;
        }

        private float _ringRadius;

        /// A panel opened while floating takes the free gap of the ring nearest to where the user looks; when
        /// there is none the ring grows and is laid out again.
        private void OnOpenedFloating(UiPanel p)
        {
            if (!Floating || p == null || _carousel == null || p.Id == "dock" || p.Id == "picker") return;
            float r = FloatRadius();
            if (r > _ringRadius + 0.02f) { ArrangeRing(); return; }
            if (p == _ws)
            {
                // beside its thread; when that spot is taken, the ring is laid out again
                if (!WsLinked || !_panels.TryGetValue("thread", out var th) || !_slots.ContainsKey(th.transform)) return;
                SlotWorkspaceBesideThread(r);
                var ws = _slots[_ws.transform];
                foreach (var o in Occupied(_ws, ws.height, r))
                {
                    if (o.Item1 == _slots[th.transform].angle) continue;
                    if (Mathf.Abs(Mathf.DeltaAngle(o.Item1, ws.angle)) < HalfArc(_ws, r) + o.Item2 + 1f) { ArrangeRing(); return; }
                }
                return;
            }
            var head = Head;
            float gazeWorld = head != null ? Mathf.Atan2(head.forward.x, head.forward.z) * Mathf.Rad2Deg : _baseYaw;
            float gaze = Mathf.DeltaAngle(_baseYaw + _spin, gazeWorld);
            float a = FindFreeYaw(gaze, GroupHalfArc(p, r), Occupied(p, RowHeight, r), 180f, RingGap);
            if (float.IsNaN(a)) { ArrangeRing(); return; }
            _slots[p.transform] = (PanelAngleInGroup(p, a, r), RowHeight);
            if (p.Id == "thread") SlotWorkspaceBesideThread(r);
        }

        private void ExitFloat()
        {
            _slots.Clear();
            foreach (var p in LayoutPanels()) p.SetBend(0f);
            UpdateFixedBend();
        }

        /// Windows that never bend: terminals (their text grid cannot bend yet) and WEB (the page is one flat picture,
        /// so a bent frame around it would not match where the pointer touches the page).
        public static bool StaysFlat(UiPanel p) => p.Id.StartsWith("term-") || p.Id == "web";

        /// Fixed layout with "Bend panels": each panel curves towards you around its own distance (bent on the GPU,
        /// eased by the panel, so it follows you smoothly). Terminals and WEB stay flat (StaysFlat).
        public void UpdateFixedBend()
        {
            if (Floating || Settings == null) return;
            var head = Head;
            foreach (var p in LayoutPanels())
            {
                if (p == null) continue;
                if (!Settings.bendPanels || StaysFlat(p) || head == null) { p.SetBend(0f); continue; }
                var d = p.transform.position - head.position;
                float dist = Mathf.Max(0.45f, new Vector2(d.x, d.z).magnitude);
                p.SetBend(dist); // the GPU bends; the panel eases towards it (no steps)
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
                bool group = p.Id == "thread" && WsLinked;
                // a dropped thread: its group (thread + workspace) is centred a little to the right of the thread; a
                // CONTROL window's group (rail + window) a little to its left
                float groupAngle = group ? angle + WsRingGap * 0.5f + HalfArc(_ws, r) : RailBeside(p) ? angle - RailShift(r) : angle;
                float free = FindFreeYaw(groupAngle, GroupHalfArc(p, r), Occupied(p, height, r), 180f, RingGap);
                if (!float.IsNaN(free)) angle = group ? ThreadAngleInGroup(free, r) : RailBeside(p) ? free + RailShift(r) : free;
            }
            _slots[t] = (angle, height);
            if (p != null && p.Id == "thread") SlotWorkspaceBesideThread(Mathf.Max(_ringRadius, FloatRadius()));
        }

        private void UpdateLayout() => UpdateLayout(Mathf.Min(Time.unscaledDeltaTime, 0.05f));

        /// Self-tests: run the layout for `steps` frames of `dt` seconds.
        public void TickLayoutForTest(float dt, int steps) { for (int i = 0; i < steps; i++) UpdateLayout(dt); }

        public Vector3 CarouselCenter => _carousel != null ? new Vector3(_carousel.position.x, _carouselY, _carousel.position.z) : Vector3.zero;

        private float _nextFixedBend;

        private void UpdateLayout(float dt)
        {
            if (Settings == null) return;
            UpdateWorkspaceLink();
            UpdateControlRail();
            if (!Floating)
            {
                // fixed: follow the distance every frame (panels are dragged, the user walks): only a uniform changes
                if (Settings.bendPanels) UpdateFixedBend();
                return;
            }
            if (_carousel == null) EnterFloat();
            var head = Head;
            if (head != null)
            {
                // follow walking (x/z), never head nods (y) or tilts
                var target = new Vector3(head.position.x, 0f, head.position.z);
                _carousel.position = Vector3.Lerp(_carousel.position, target, 1f - Mathf.Exp(-dt * 2.5f));
            }
            SpinInput(dt);
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
                if (Router.IsDragging(p) || RailCarries(p)) { _slots.Remove(t); continue; } // re-slotted where it is dropped
                if (p == _ws && WsLinked) SlotWorkspaceBesideThread(r); // follows its thread (snaps back after a drag)
                if (!_slots.TryGetValue(t, out var slot)) { Slot(t); slot = _slots[t]; }
                float a = (_baseYaw + _spin + slot.angle) * Mathf.Deg2Rad;
                var dir = new Vector3(Mathf.Sin(a), 0f, Mathf.Cos(a));
                var pos = new Vector3(c.x, _carouselY + slot.height, c.z) + dir * r;
                t.SetPositionAndRotation(Vector3.Lerp(t.position, pos, 1f - Mathf.Exp(-dt * 12f)), Quaternion.LookRotation(dir, Vector3.up));
                p.SetBend(Settings.bendPanels && !StaysFlat(p) ? r : 0f);
            }
            UpdateControlRail(); // after the ring moved its window

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

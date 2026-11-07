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
            foreach (var kv in _panels) if (kv.Key != "dock") yield return kv.Value;
            foreach (var f in _frames.Values) yield return f;
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
            float width = open.Sum(p => p.Width * p.transform.lossyScale.x);
            float room = 2f * Mathf.PI * 0.94f - open.Count * RingGap * Mathf.Deg2Rad; // radians left for the panels themselves
            return room <= 0.2f ? 2.5f : Mathf.Clamp(Mathf.Max(r, width / room), r, 2.5f);
        }

        /// Half the angle a panel takes on the cylinder: a bent panel's width is an arc (conservative for flat ones).
        private static float HalfArc(UiPanel p, float r) => p.Width * 0.5f * p.transform.lossyScale.x / Mathf.Max(r, 0.1f) * Mathf.Rad2Deg;

        private static bool SameRow(UiPanel a, float ha, UiPanel b, float hb) =>
            Mathf.Abs(ha - hb) < (a.Height * a.transform.lossyScale.y + b.Height * b.transform.lossyScale.y) * 0.5f;

        /// Angles taken by the open panels whose height range meets `p`'s, except `p`.
        private List<(float, float)> Occupied(UiPanel p, float height, float r)
        {
            var occ = new List<(float, float)>();
            foreach (var kv in _slots)
            {
                var q = kv.Key != null ? kv.Key.GetComponent<UiPanel>() : null;
                if (q == null || q == p || !q.IsOpen) continue;
                if (!SameRow(p, height, q, kv.Value.height)) continue;
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
                float half = HalfArc(p, r);
                float angle;
                if (first) { angle = 0f; right = half; left = -half; }
                else if (toRight) { angle = right + RingGap + half; right = angle + half; }
                else { angle = left - RingGap - half; left = angle - half; }
                if (!first) toRight = !toRight;
                first = false;
                _slots[p.transform] = (angle, RowHeight);
            }
            _ringRadius = r;
        }

        private float _ringRadius;

        /// A panel opened while floating takes the free gap of the ring nearest to where the user looks; when
        /// there is none the ring grows and is laid out again.
        private void OnOpenedFloating(UiPanel p)
        {
            if (!Floating || p == null || _carousel == null || p.Id == "dock") return;
            float r = FloatRadius();
            if (r > _ringRadius + 0.02f) { ArrangeRing(); return; }
            var head = Head;
            float gazeWorld = head != null ? Mathf.Atan2(head.forward.x, head.forward.z) * Mathf.Rad2Deg : _baseYaw;
            float gaze = Mathf.DeltaAngle(_baseYaw + _spin, gazeWorld);
            float a = FindFreeYaw(gaze, HalfArc(p, r), Occupied(p, RowHeight, r), 180f, RingGap);
            if (float.IsNaN(a)) { ArrangeRing(); return; }
            _slots[p.transform] = (a, RowHeight);
        }

        private void ExitFloat()
        {
            _slots.Clear();
            foreach (var p in LayoutPanels()) p.SetBend(0f);
            UpdateFixedBend();
        }

        /// Fixed layout with "Bend panels": each panel curves towards you around its own distance (5 cm steps, so
        /// text is not rebuilt every frame). Terminals stay flat (their text grid cannot bend yet).
        public void UpdateFixedBend()
        {
            if (Floating || Settings == null) return;
            var head = Head;
            foreach (var p in LayoutPanels())
            {
                if (p == null) continue;
                if (!Settings.bendPanels || p.Id.StartsWith("term-") || head == null) { p.SetBend(0f); continue; }
                var d = p.transform.position - head.position;
                float dist = Mathf.Max(0.45f, new Vector2(d.x, d.z).magnitude);
                p.SetBend(Mathf.Round(dist / 0.05f) * 0.05f);
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
                float free = FindFreeYaw(angle, HalfArc(p, r), Occupied(p, height, r), 180f, RingGap);
                if (!float.IsNaN(free)) angle = free;
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
            if (!Floating)
            {
                // fixed: re-bend now and then (panels are dragged, the user walks); cheap when nothing changed
                if (Settings.bendPanels && Time.unscaledTime >= _nextFixedBend) { _nextFixedBend = Time.unscaledTime + 0.5f; UpdateFixedBend(); }
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
                if (Router.IsDragging(p)) { _slots.Remove(t); continue; } // re-slotted where it is dropped
                if (!_slots.TryGetValue(t, out var slot)) { Slot(t); slot = _slots[t]; }
                float a = (_baseYaw + _spin + slot.angle) * Mathf.Deg2Rad;
                var dir = new Vector3(Mathf.Sin(a), 0f, Mathf.Cos(a));
                var pos = new Vector3(c.x, _carouselY + slot.height, c.z) + dir * r;
                t.SetPositionAndRotation(Vector3.Lerp(t.position, pos, 1f - Mathf.Exp(-dt * 12f)), Quaternion.LookRotation(dir, Vector3.up));
                bool isFrame = p.Id.StartsWith("term-"); // terminals stay flat: their text grid cannot bend yet
                p.SetBend(Settings.bendPanels && !isFrame ? r : 0f);
            }

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

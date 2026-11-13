using System.Collections.Generic;
using MetaCodex.UI;
using UnityEngine;

namespace MetaCodex.Shell
{
    /// THE ORB KEEPS OFF THE WINDOW YOU WATCH (owner 2026-11-11: "avoid any weird position when it can with the menu or
    /// stuff cover my view on the panels i'm watching. have the orb to be less invasive but always available").
    /// - The watched window: the first one your gaze meets (looking at the orb, its menu or a callout keeps the one
    ///   before); it stays watched for 2 s after your gaze leaves it.
    /// - What the orb takes of your view: its disc, its open menu's arc on its left, its callouts' arc on its right
    ///   (ShellPlacement's view rectangles: yaw and pitch, seen from your eyes).
    /// - When that would cover the watched window, the orb glides aside or below (above costs more) to the nearest
    ///   place where it covers nothing of it, in front of no other window, within 60 degrees of where you look; it
    ///   never moves while a hand is at it or at its items; it goes back to its own place 1.5 s after that is free again.
    public sealed partial class MetaCodexShell
    {
        private UiPanel _watched;
        private float _watchedAt = -10f, _watchedCheckAt;
        private float _avoidYaw, _avoidPitch, _avoidCheckAt, _avoidFreeSince = -1f;
        private readonly List<ViewRect> _footprint = new List<ViewRect>();
        private readonly List<ViewRect> _avoidWindows = new List<ViewRect>();

        /// The window the user is looking at now (null: none for 2 s).
        public UiPanel WatchedPanel => _watched;

        /// The orb's own items: its menu's actions, its callouts, the voice HUD.
        private bool IsOrbItem(UiPanel p) =>
            p != null && (p.Id != null && (p.Id.StartsWith("orb-") || p.Id == "toast" || p.Id == "voice-hud") || p == Toasts.Pinned);

        private void TickWatched()
        {
            var head = Head;
            if (head == null || Router == null) return;
            if (_orbClock < _watchedCheckAt) return;
            _watchedCheckAt = _orbClock + 0.1f;
            var ray = new Ray(head.position, head.forward);
            float best = float.MaxValue;
            UiPanel hit = null;
            bool onOrb = false;
            // the orb itself: looking at it keeps the window watched before
            var toOrb = Orb.Position - head.position;
            float along = Vector3.Dot(toOrb, ray.direction);
            if (along > 0.1f && (toOrb - ray.direction * along).magnitude < OrbOuterRadius) { best = along; onOrb = true; }
            foreach (var p in Router.Panels)
            {
                if (p == null || !p.IsOpen || p.Id == "dock") continue;
                bool mine = IsOrbItem(p);
                if (!mine && !IsWindow(p)) continue;
                if (p.Raycast(ray, out _, out float d) && d < best) { best = d; hit = p; onOrb = mine; }
            }
            if (onOrb) { if (_watched != null) _watchedAt = _orbClock; return; }
            if (hit != null) { if (hit != _watched) _avoidCheckAt = 0f; _watched = hit; _watchedAt = _orbClock; return; }
            if (_watched != null && (!_watched.IsOpen || _orbClock - _watchedAt > 2f)) { _watched = null; _avoidCheckAt = 0f; }
        }

        private bool IsWindow(UiPanel p)
        {
            foreach (var q in PlacementObstacles(null)) if (q == p) return true;
            return false;
        }

        /// What the orb takes of the view with its centre at (yaw off the frame, pitch, distance): its disc, its open
        /// menu (left), its callouts (right). Rectangles in degrees, yaw relative to the frame.
        private void OrbFootprint(float yaw, float pitch, float dist, List<ViewRect> into)
        {
            into.Clear();
            dist = Mathf.Max(dist, 0.2f);
            float cp = Mathf.Max(0.35f, Mathf.Cos(pitch * Mathf.Deg2Rad));
            void Add(float x0, float x1, float y0, float y1) => into.Add(new ViewRect
            {
                Yaw0 = yaw + Mathf.Atan2(x0, dist) * Mathf.Rad2Deg / cp, Yaw1 = yaw + Mathf.Atan2(x1, dist) * Mathf.Rad2Deg / cp,
                Pitch0 = pitch + Mathf.Atan2(y0, dist) * Mathf.Rad2Deg, Pitch1 = pitch + Mathf.Atan2(y1, dist) * Mathf.Rad2Deg,
            });
            float r = OrbOuterRadius;
            Add(-r, r, -r, r);
            if (OrbMenuOpen) { var e = OrbMenuExtent(OrbMenuScaleNow); Add(e.xMin, e.xMax, e.yMin, e.yMax); }
            if (Toasts.Count > 0 || Toasts.Pinned != null && Toasts.Pinned.gameObject.activeSelf)
            {
                var e = Toasts.Extent;
                if (e.width > 0f) Add(e.xMin, e.xMax, e.yMin, e.yMax);
            }
        }

        private static float OverlapArea(ViewRect a, ViewRect b, float margin)
        {
            float w = Mathf.Min(a.Yaw1, b.Yaw1 + margin) - Mathf.Max(a.Yaw0, b.Yaw0 - margin);
            float h = Mathf.Min(a.Pitch1, b.Pitch1 + margin) - Mathf.Max(a.Pitch0, b.Pitch0 - margin);
            return w > 0f && h > 0f ? w * h : 0f;
        }

        private float FootprintOver(ViewRect watched, float yaw, float pitch, float dist)
        {
            OrbFootprint(yaw, pitch, dist, _footprint);
            float a = 0f;
            // its disc may come within a degree of the window (it never sits in front of one anyway); its menu and callouts
            // keep 3 degrees off it
            for (int i = 0; i < _footprint.Count; i++) a += OverlapArea(_footprint[i], watched, i == 0 ? 1f : 3f);
            return a;
        }

        /// A hand at the orb or its items (a fingertip near, a ray on one): it does not move away from under it.
        private bool HandAtOrb()
        {
            for (int h = 0; h < 2; h++)
            {
                var hand = OrbHand(h);
                if (!hand.tracked) continue;
                if (Vector3.Distance(hand.tip, Orb.Position) < 0.16f || OrbRayOn(hand.ray, out _)) return true;
                var on = Router.HandRayPanel(h);
                if (on != null && IsOrbItem(on)) return true;
                foreach (var a in _orbActs) if (a.IsOpen && Vector3.Distance(hand.tip, a.transform.position) < 0.10f) return true;
            }
            return false;
        }

        /// Five times a second: the offset (yaw, pitch off its own place) that keeps the orb, its menu and its callouts
        /// off the watched window.
        private void TickOrbAvoid()
        {
            var head = Head;
            if (head == null || _orbGrabHand >= 0 || _orbClock < _avoidCheckAt) return;
            _avoidCheckAt = _orbClock + 0.2f;
            if (HandAtOrb()) return; // never from under a finger
            var t = OrbTargetBase();
            Vector3 eye = head.position;
            ViewRect wr = default;
            bool has = _watched != null && _watched.IsOpen && ViewRectOf(_watched, eye, _orbYaw, out wr);
            float baseOver = has ? FootprintOver(wr, t.yaw, t.pitch, t.dist) : 0f;
            if (baseOver <= 0f)
            {
                // its own place is free: it goes back there, lazily
                if (_avoidYaw != 0f || _avoidPitch != 0f)
                {
                    if (_avoidFreeSince < 0f) _avoidFreeSince = _orbClock;
                    else if (_orbClock - _avoidFreeSince >= 1.5f) { _avoidYaw = 0f; _avoidPitch = 0f; _avoidFreeSince = -1f; }
                }
                return;
            }
            _avoidFreeSince = -1f;
            if ((_avoidYaw != 0f || _avoidPitch != 0f) && FootprintOver(wr, t.yaw + _avoidYaw, t.pitch + _avoidPitch, t.dist) <= 0f) return; // where it is still clears it: it stays
            // the other windows: its disc keeps off them too
            _avoidWindows.Clear();
            foreach (var q in PlacementObstacles(null))
                if (q != null && q != _watched && q.Id != "dock" && ViewRectOf(q, eye, _orbYaw, out var r)) _avoidWindows.Add(r);
            var f = head.forward;
            float headYaw = Mathf.DeltaAngle(_orbYaw, Mathf.Atan2(f.x, f.z) * Mathf.Rad2Deg);
            float best = float.MaxValue, bestY = _avoidYaw, bestP = _avoidPitch;
            float discR = Mathf.Atan2(OrbOuterRadius, t.dist) * Mathf.Rad2Deg;
            for (float dp = -36f; dp <= 30f; dp += 3f)
            {
                float pitch = t.pitch + dp;
                if (pitch < -34f || pitch > 40f) continue;
                for (float dy = -63f; dy <= 63f; dy += 3f)
                {
                    float yaw = t.yaw + dy;
                    if (Mathf.Abs(yaw - headYaw) > 60f) continue; // it stays in view, at hand
                    float over = FootprintOver(wr, yaw, pitch, t.dist);
                    var disc = new ViewRect { Yaw0 = yaw - discR, Yaw1 = yaw + discR, Pitch0 = pitch - discR, Pitch1 = pitch + discR };
                    bool blocked = false;
                    foreach (var o in _avoidWindows) if (disc.Overlaps(o, 0.5f)) { blocked = true; break; }
                    // aside or below first (owner: "aside or below"); a degree up costs more
                    float cost = over * 0.6f + Mathf.Abs(dy) + Mathf.Abs(dp) * (dp > 0f ? 1.5f : 1.1f) + (blocked ? 30f : 0f);
                    if (cost < best) { best = cost; bestY = dy; bestP = dp; }
                }
            }
            _avoidYaw = bestY;
            _avoidPitch = bestP;
        }

        /// The offset the orb takes now to keep off the watched window (degrees: yaw, pitch).
        public Vector2 OrbAvoidForTest => new Vector2(_avoidYaw, _avoidPitch);

        /// Self-tests: the watched window and the orb's footprint (degrees, yaw relative to the orb's frame).
        public List<ViewRect> OrbFootprintForTest()
        {
            var head = Head;
            var rel = Orb.Position - head.position;
            float dist = rel.magnitude;
            float yaw = Mathf.DeltaAngle(_orbYaw, Mathf.Atan2(rel.x, rel.z) * Mathf.Rad2Deg);
            float pitch = Mathf.Asin(Mathf.Clamp(rel.y / Mathf.Max(dist, 1e-3f), -1f, 1f)) * Mathf.Rad2Deg;
            var list = new List<ViewRect>();
            OrbFootprint(yaw, pitch, dist, list);
            return list;
        }

        public float OrbFrameYawForTest => _orbYaw;
        public void CloseOrbMenuForTest() { if (OrbMenuOpen) CloseOrbMenu(); }
        /// The menu's arc radius now (the squares' centres), metres.
        public float OrbMenuRadiusForTest => OrbActRadius(OrbMenuScaleNow);
        /// The callouts' circle: each card's corner nearest the orb lies on it (UiToasts), metres.
        public float OrbOuterRadiusForTest => OrbOuterRadius;
    }
}

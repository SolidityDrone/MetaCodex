using System.Collections.Generic;
using System.Linq;
using MetaCodex.UI;
using UnityEngine;

namespace MetaCodex.Shell
{
    /// WHERE A PANEL OPENS (owner 2026-11-10: "smart positions"). Every open panel is a rectangle in the user's view
    /// (the yaw and the pitch its corners span, seen from the eyes, bent or flat, wherever it was dragged). A new
    /// panel takes the free place that costs least:
    /// - in front of where the user faces, at eye level, when that is free;
    /// - else the nearest free place round it: to the side, or above or below a window when that is nearer than a
    ///   far turn of the head (never above 50 degrees or below 42); tilted to face the user;
    /// - a panel opened from a window's button (a thread from THREADS, a view from a thread, the picker) opens right
    ///   beside that window: to its right when there is room, else its left, else above or below it;
    /// - a panel that was open before comes back where the user left it, when that place is still free and still
    ///   in view; else it is placed anew;
    /// - when nothing is free at the panel distance it opens further away (up to twice as far, smaller in the view) in
    ///   the nearest gap; only when nothing is free even there it cascades in front, a little nearer each time;
    /// - "in front" is where the user's body faces when they look down at their wrist or the dock to press (the nose
    ///   points at the hand then, not at where the window should go);
    /// - never inside a wall: while the room's depth runs, the last reading (48 x 48 points, every 0.3 s, the one the
    ///   floor is found from) says how far the real things are in each direction; a panel opens at least 8 cm in front
    ///   of them, and a direction where something real is nearer than half a metre is a poor place.
    /// FLOAT keeps its ring (ShellLayout); this decides the first place there too.
    public sealed partial class MetaCodexShell
    {
        /// What a panel covers of the user's view: yaw (relative to a reference yaw) and pitch, in degrees.
        public struct ViewRect
        {
            public float Yaw0, Yaw1, Pitch0, Pitch1;
            public float YawMid => (Yaw0 + Yaw1) * 0.5f;
            public float PitchMid => (Pitch0 + Pitch1) * 0.5f;
            public bool Overlaps(ViewRect o, float margin) =>
                Yaw0 < o.Yaw1 + margin && o.Yaw0 < Yaw1 + margin && Pitch0 < o.Pitch1 + margin && o.Pitch0 < Pitch1 + margin;
            /// The gap to another rectangle in degrees (0 when they touch or overlap): sideways, and up or down.
            public (float yaw, float pitch) Gap(ViewRect o) =>
                (Mathf.Max(0f, Mathf.Max(o.Yaw0 - Yaw1, Yaw0 - o.Yaw1)), Mathf.Max(0f, Mathf.Max(o.Pitch0 - Pitch1, Pitch0 - o.Pitch1)));
        }

        private static readonly Vector2[] RectProbes =
        {
            new Vector2(0f, 0f), new Vector2(1f, 0f), new Vector2(0f, 1f), new Vector2(1f, 1f),
            new Vector2(0.5f, 0f), new Vector2(0.5f, 1f), new Vector2(0f, 0.5f), new Vector2(1f, 0.5f),
        };

        /// The rectangle a panel covers seen from `eye` (false: it lies across the user's back, or on the eye).
        public static bool ViewRectOf(UiPanel q, Vector3 eye, float refYaw, out ViewRect r)
        {
            r = new ViewRect { Yaw0 = float.MaxValue, Yaw1 = float.MinValue, Pitch0 = float.MaxValue, Pitch1 = float.MinValue };
            foreach (var probe in RectProbes)
            {
                var w = q.SurfacePoint(new Vector2(probe.x * q.Width, probe.y * q.Height)) - eye;
                float flat = new Vector2(w.x, w.z).magnitude;
                if (w.sqrMagnitude < 0.01f) return false;
                float yaw = Mathf.DeltaAngle(refYaw, Mathf.Atan2(w.x, w.z) * Mathf.Rad2Deg);
                float pitch = Mathf.Atan2(w.y, flat) * Mathf.Rad2Deg;
                r.Yaw0 = Mathf.Min(r.Yaw0, yaw); r.Yaw1 = Mathf.Max(r.Yaw1, yaw);
                r.Pitch0 = Mathf.Min(r.Pitch0, pitch); r.Pitch1 = Mathf.Max(r.Pitch1, pitch);
            }
            return r.Yaw1 - r.Yaw0 <= 180f;
        }

        private IEnumerable<UiPanel> PlacementObstacles(UiPanel except)
        {
            foreach (var q in _panels.Values) if (q != null && q != except && q.IsOpen && !IsGroupedPanel(q)) yield return q; // a group covers its windows
            foreach (var q in _frames.Values) if (q != null && q != except && q.IsOpen) yield return q;
        }

        /// The window whose button is being pressed right now, when it is a real window (not the dock, a message or
        /// the orb's menu): what opens from it opens beside it.
        private UiPanel OpenerNow()
        {
            var a = Router != null ? Router.ActivePanel : null;
            if (a == null || !a.IsOpen || a.Id == "dock" || a.Id == "toast" || a.Id == "voice-hud" || (a.Id ?? "").StartsWith("orb-") || a.Id == "picker") return null; // the orb's menu squares
            return a;
        }

        public const float PlaceYawLimit = 112f, PlaceTop = 50f, PlaceBottom = -42f;
        /// A window that opens stays in the user's view (owner 2026-11-13: "new panels shall spawn in front of us and
        /// oriented toward us, now often panels spawn weirdly off my view"): its middle within ViewYaw degrees of where
        /// the user faces and ViewPitch above or below eye level (two windows side by side fit: the second's middle is
        /// about 46 degrees out). When no free place is in view, it opens in front over the others (a little nearer).
        /// Only RECENTER, which lays every window out again, searches the wide range above.
        public const float ViewYaw = 50f, ViewPitch = 36f;

        // ---- the room: what the last depth reading saw (world points), for a few seconds
        private List<Vector3> _roomPts;
        private float _roomPtsAt = -100f;
        private const float RoomBin = 4f, RoomHold = 3f;

        /// The room's depth as world points (ShellAmbient reads it every 0.3 s while the depth runs; tests feed it).
        public void SetRoomPoints(List<Vector3> pts) { _roomPts = pts; _roomPtsAt = Time.unscaledTime; }
        public void ClearRoomPoints() { _roomPts = null; }

        /// How far the nearest real thing is in each direction (4 degree bins, yaw relative to `refYaw`): the second
        /// nearest point of a bin (one stray point is noise). Hands and arms are not the room: nothing nearer than
        /// 0.55 m counts. Null when there is no fresh reading.
        private float[,] RoomClearance(Vector3 eye, float refYaw, out int yawBins, out int pitchBins)
        {
            yawBins = Mathf.CeilToInt(360f / RoomBin); pitchBins = Mathf.CeilToInt(180f / RoomBin);
            if (_roomPts == null || _roomPts.Count < 40 || Time.unscaledTime - _roomPtsAt > RoomHold) return null;
            var first = new float[yawBins, pitchBins];
            var second = new float[yawBins, pitchBins];
            for (int i = 0; i < yawBins; i++) for (int j = 0; j < pitchBins; j++) { first[i, j] = float.MaxValue; second[i, j] = float.MaxValue; }
            foreach (var pt in _roomPts)
            {
                var w = pt - eye;
                float d = w.magnitude;
                if (d < 0.55f) continue;
                float yaw = Mathf.DeltaAngle(refYaw, Mathf.Atan2(w.x, w.z) * Mathf.Rad2Deg);
                float pitch = Mathf.Asin(Mathf.Clamp(w.y / d, -1f, 1f)) * Mathf.Rad2Deg;
                int i = Mathf.Clamp((int)((yaw + 180f) / RoomBin), 0, yawBins - 1), j = Mathf.Clamp((int)((pitch + 90f) / RoomBin), 0, pitchBins - 1);
                if (d < first[i, j]) { second[i, j] = first[i, j]; first[i, j] = d; }
                else if (d < second[i, j]) second[i, j] = d;
            }
            return second;
        }

        private static float ClearanceIn(float[,] room, int yawBins, int pitchBins, ViewRect c)
        {
            if (room == null) return float.MaxValue;
            int i0 = Mathf.Clamp((int)((c.Yaw0 + 180f) / RoomBin), 0, yawBins - 1), i1 = Mathf.Clamp((int)((c.Yaw1 + 180f) / RoomBin), 0, yawBins - 1);
            int j0 = Mathf.Clamp((int)((c.Pitch0 + 90f) / RoomBin), 0, pitchBins - 1), j1 = Mathf.Clamp((int)((c.Pitch1 + 90f) / RoomBin), 0, pitchBins - 1);
            float min = float.MaxValue;
            for (int i = i0; i <= i1; i++) for (int j = j0; j <= j1; j++) if (room[i, j] < min) min = room[i, j];
            return min;
        }

        /// Where "in front" is for a panel that opens: where the user faces; but when they look down (at the wrist menu,
        /// at the dock) the nose points at the hand, so it is where their body faces: the frame the orb floats in follows
        /// the user lazily and is that, as long as the head is not turned far out of it.
        private float FrontYaw()
        {
            var head = Head;
            if (head == null) return 0f;
            var f = head.forward;
            float yaw = Mathf.Atan2(f.x, f.z) * Mathf.Rad2Deg;
            float pitch = Mathf.Asin(Mathf.Clamp(f.y, -1f, 1f)) * Mathf.Rad2Deg;
            if (pitch < -14f && _orbFramed && Settings.orb && Mathf.Abs(Mathf.DeltaAngle(_orbYaw, yaw)) < 34f) return _orbYaw;
            return yaw;
        }

        /// The distance a panel opens at: the chosen one, further for a panel so wide that it would fill more than
        /// about 70 degrees of the view.
        private float PlaceDistance(UiPanel p) => Mathf.Max(Settings.panelDistance * 0.95f, p.Width * p.RestLossyScale.x * 0.5f / Mathf.Tan(35f * Mathf.Deg2Rad));

        /// Places `p` (not open yet) at the free place that costs least; `opener` = the window it was opened from.
        /// `obstacles` = the panels to keep clear of (default: every open one). When no place is free at the panel
        /// distance, it looks further away (up to twice as far: smaller in the view, so it fits a gap) before it
        /// cascades over the others: a window never covers another while some place is free.
        private void PlaceSmart(string id, UiPanel p, UiPanel opener = null, IEnumerable<UiPanel> obstacles = null)
        {
            bool inView = obstacles == null; // RECENTER passes the windows laid out so far: it may use the wide range
            var head = Head;
            if (id == "dock") { p.PlaceInFront(head, 0.55f, 0.42f); TiltTowards(p, head); return; }
            Vector3 eye = head != null ? head.position : new Vector3(0f, 1.4f, 0f);
            float gaze = FrontYaw();
            float dist = PlaceDistance(p);
            float halfW = p.Width * 0.5f * p.RestLossyScale.x, halfH = p.Height * 0.5f * p.RestLossyScale.y;
            // a CONTROL window brings its rail on its left: that room is kept free too, and the pair is what is centred
            float reserveM = IsControlPage(id) ? (RailW + RailGap * 2f) * p.RestLossyScale.x : 0f;
            var room = RoomClearance(eye, gaze, out int yawBins, out int pitchBins);
            var taken = new List<ViewRect>();
            ViewRect openerRect = default;
            bool hasOpener = false;
            foreach (var q in obstacles ?? PlacementObstacles(p))
            {
                if (q == null || q == p || !ViewRectOf(q, eye, gaze, out var r)) continue;
                taken.Add(r);
                if (q == opener) { openerRect = r; hasOpener = true; }
            }
            const float margin = 2f;
            float best = float.MaxValue, bestYaw = float.NaN, bestPitch = 0f, bestDist = dist, bestLevel = 0f;
            float bestFar = 0f;
            foreach (float far in PlaceFurther)
            {
                if (bestFar == 1f) break; // free at the panel distance: it goes there
                float fd = dist * far;
                float halfYaw = Mathf.Atan2(halfW, fd) * Mathf.Rad2Deg;
                float halfPitch = Mathf.Atan2(halfH, fd) * Mathf.Rad2Deg;
                float reserveYaw = RailReserveYaw(halfW, fd, reserveM);
                float level = Mathf.Asin(Mathf.Clamp(-0.06f / fd, -1f, 1f)) * Mathf.Rad2Deg; // centres just below the eyes, as before
                for (float pitchOff = 0f; pitchOff <= 60f; pitchOff += 3f)
                {
                    foreach (float ps in pitchOff == 0f ? new[] { 1f } : new[] { 1f, -1f })
                    {
                        float pitch = level + pitchOff * ps;
                        if (pitch + halfPitch > PlaceTop || pitch - halfPitch < PlaceBottom) continue;
                        if (inView && pitchOff > ViewPitch) continue;
                        bool tilted = pitchOff >= 1.5f;
                        float yawLimit = inView ? ViewYaw + reserveYaw * 0.5f : PlaceYawLimit; // the middle of the pair (window + rail) in view
                        for (float yawOff = 0f; yawOff <= yawLimit; yawOff += 2f)
                        {
                            foreach (float ys in yawOff == 0f ? new[] { 1f } : new[] { 1f, -1f })
                            {
                                float yaw = yawOff * ys;
                                var c = new ViewRect { Yaw0 = yaw - halfYaw - reserveYaw, Yaw1 = yaw + halfYaw, Pitch0 = pitch - halfPitch, Pitch1 = pitch + halfPitch };
                                // something real there (a wall, a monitor): the panel comes 8 cm in front of it, and is larger in
                                // the view for it; nearer than half a metre is a poor place
                                float d = fd, wall = 0f;
                                float clear = ClearanceIn(room, yawBins, pitchBins, c);
                                if (clear - 0.08f < fd)
                                {
                                    d = Mathf.Max(0.42f, clear - 0.08f);
                                    wall = d < 0.5f ? 12f + (0.5f - (clear - 0.08f)) * 60f : (fd - d) * 12f;
                                    float hy = Mathf.Atan2(halfW, d) * Mathf.Rad2Deg, hp = Mathf.Atan2(halfH, d) * Mathf.Rad2Deg;
                                    c = new ViewRect { Yaw0 = yaw - hy - RailReserveYaw(halfW, d, reserveM), Yaw1 = yaw + hy, Pitch0 = pitch - hp, Pitch1 = pitch + hp };
                                }
                                bool free = true;
                                foreach (var t in taken) if (c.Overlaps(t, margin)) { free = false; break; }
                                if (!free) continue;
                                // the quick guess above is a window straight ahead; the real pose (tilted above or below eye
                                // level, its rail turned towards the user) spreads wider: check what it really covers
                                var exact = PoseRect(yaw, pitch, d, halfW, halfH, reserveM, tilted);
                                foreach (var t in taken) if (exact.Overlaps(t, margin)) { free = false; break; }
                                if (!free) continue;
                                // turning the head sideways is easier than nodding: a degree up or down costs more
                                // (and above a window rather than below it: below is where the hands and the dock are);
                                // further away than the panel distance costs too (only when nothing nearer is free)
                                float groupYaw = yaw - reserveYaw * 0.5f;
                                float further = (far - 1f) * 40f;
                                float cost = Mathf.Abs(groupYaw) + Mathf.Abs(pitch - level) * (pitch >= level ? 1.6f : 3.0f) + wall + further;
                                if (hasOpener)
                                {
                                    // beside the window it was opened from: the gap to it counts most, its right side is preferred
                                    var gap = c.Gap(openerRect);
                                    cost = Mathf.Abs(groupYaw) * 0.25f + Mathf.Abs(pitch - level) * 0.5f + (gap.yaw + gap.pitch * 1.5f) * 1.6f + (c.YawMid < openerRect.YawMid ? 5f : 0f) + wall + further;
                                }
                                if (cost < best) { best = cost; bestYaw = yaw; bestPitch = pitch; bestDist = d; bestLevel = level; bestFar = far; }
                            }
                        }
                    }
                }
            }
            float lift = 0f;
            if (float.IsNaN(bestYaw))
            {
                // nowhere free: cascade in front, each extra panel a bit closer, higher and to the side; take the
                // first cascade place no open panel already sits in
                var sits = PlacementObstacles(p).Where(q => q.Id != "dock").Select(q => q.transform.position).ToList();
                float baseDist = dist;
                for (int k = 0; k < 16; k++)
                {
                    float y = 4f * k, d = Mathf.Max(0.45f, baseDist - 0.07f * (k + 1)), l = 0.035f * k;
                    float ar = (gaze + y) * Mathf.Deg2Rad;
                    var pos = eye + new Vector3(Mathf.Sin(ar), 0f, Mathf.Cos(ar)) * d + Vector3.down * 0.06f + Vector3.up * l;
                    bestYaw = y; dist = d; lift = l;
                    if (!sits.Any(q => Vector3.Distance(q, pos) < 0.05f)) break;
                }
                float a0 = (gaze + bestYaw) * Mathf.Deg2Rad;
                var dir0 = new Vector3(Mathf.Sin(a0), 0f, Mathf.Cos(a0));
                p.transform.SetPositionAndRotation(eye + dir0 * dist + Vector3.down * 0.06f + Vector3.up * lift, Quaternion.LookRotation(dir0, Vector3.up));
                return;
            }
            dist = bestDist;
            float a = (gaze + bestYaw) * Mathf.Deg2Rad, e = bestPitch * Mathf.Deg2Rad;
            var dir = new Vector3(Mathf.Sin(a) * Mathf.Cos(e), Mathf.Sin(e), Mathf.Cos(a) * Mathf.Cos(e));
            // on a sphere round the eyes, facing them: a panel above or below eye level is tilted towards the user
            // (at eye level this is the old upright pose)
            var flat = new Vector3(Mathf.Sin(a), 0f, Mathf.Cos(a));
            var facing = Mathf.Abs(bestPitch - bestLevel) < 1.5f ? flat : dir;
            p.transform.SetPositionAndRotation(eye + dir * dist, Quaternion.LookRotation(facing, Vector3.up));
        }

        /// The distances (times the panel distance) a panel may open at: the panel distance first, further only when
        /// nothing is free there.
        private static readonly float[] PlaceFurther = { 1f, 1.15f, 1.3f, 1.5f, 1.75f, 2f };

        /// The degrees a CONTROL window's rail adds on the window's left, seen from the eyes. The rail leaves the
        /// window's left edge turned RailYaw towards the user (RailPose), so beside a wide window it covers fewer degrees
        /// than the same metres straight ahead: atan(metres / distance) reserved ~4 degrees too many beside a 0.76 m window (a gap between it and its neighbour).
        private static float RailReserveYaw(float halfW, float d, float reserveM)
        {
            if (reserveM <= 0f) return 0f;
            float t = RailYaw * Mathf.Deg2Rad;
            return (Mathf.Atan2(halfW + reserveM * Mathf.Cos(t), d - reserveM * Mathf.Sin(t)) - Mathf.Atan2(halfW, d)) * Mathf.Rad2Deg;
        }

        /// What a panel posed as PlaceSmart poses it would cover (yaw relative to where it looks for a place): centred
        /// at (yaw, pitch) d from the eyes, upright or (`tilted`) facing the eyes, with `reserveM` of rail on its left.
        /// The probes of ViewRectOf: a window tilted above or below eye level spreads wider in yaw than half its width
        /// over its distance.
        private static ViewRect PoseRect(float yaw, float pitch, float d, float halfW, float halfH, float reserveM, bool tilted)
        {
            float a = yaw * Mathf.Deg2Rad, e = pitch * Mathf.Deg2Rad;
            var dir = new Vector3(Mathf.Sin(a) * Mathf.Cos(e), Mathf.Sin(e), Mathf.Cos(a) * Mathf.Cos(e));
            var rot = Quaternion.LookRotation(tilted ? dir : new Vector3(Mathf.Sin(a), 0f, Mathf.Cos(a)), Vector3.up);
            var centre = dir * d;
            var right = rot * Vector3.right;
            var up = rot * Vector3.up;
            var r = new ViewRect { Yaw0 = float.MaxValue, Yaw1 = float.MinValue, Pitch0 = float.MaxValue, Pitch1 = float.MinValue };
            foreach (var probe in RectProbes) Grow(ref r, centre + right * ((probe.x * 2f - 1f) * halfW) + up * ((1f - probe.y * 2f) * halfH));
            if (reserveM > 0f)
            {
                var railLeft = -(rot * (Quaternion.Euler(0f, -RailYaw, 0f) * Vector3.right)) * reserveM;
                Grow(ref r, centre - right * halfW + up * halfH + railLeft);
                Grow(ref r, centre - right * halfW - up * halfH + railLeft);
            }
            return r;
        }

        private static void Grow(ref ViewRect r, Vector3 w)
        {
            float yaw = Mathf.Atan2(w.x, w.z) * Mathf.Rad2Deg;
            float pitch = Mathf.Atan2(w.y, new Vector2(w.x, w.z).magnitude) * Mathf.Rad2Deg;
            r.Yaw0 = Mathf.Min(r.Yaw0, yaw); r.Yaw1 = Mathf.Max(r.Yaw1, yaw);
            r.Pitch0 = Mathf.Min(r.Pitch0, pitch); r.Pitch1 = Mathf.Max(r.Pitch1, pitch);
        }

        /// RECENTER (owner 2026-11-13: "they are all scrambled on top of each other ... they don't retain older positions"):
        /// every window turns round the user by the angle the main window turned (from its place before the recenter),
        /// so the layout keeps its shape. Then only a window that covers an earlier one (nearest to where the user looks
        /// first) takes the free place that costs least (PlaceSmart). Without a main window the group turns so its middle
        /// faces where the user looks.
        private void RecenterKeepLayout(Vector3? mainFrom, UiPanel main)
        {
            var head = Head;
            if (head == null) return;
            var eye = head.position;
            var all = LayoutPanels().Where(p => p != null && p.IsOpen && !(p.Id ?? "").StartsWith("orb-")).ToList();
            if (main != null && !main.IsOpen) main = null;
            float dYaw;
            if (mainFrom.HasValue && main != null) dYaw = Mathf.DeltaAngle(YawAround(eye, mainFrom.Value), YawAround(eye, main.transform.position));
            else
            {
                var others = all.Where(p => p != main).ToList();
                if (others.Count == 0) return;
                dYaw = Mathf.DeltaAngle(MeanYawAround(eye, others), FrontYaw());
            }
            var turn = Quaternion.Euler(0f, dYaw, 0f);
            var rest = all.Where(p => p != main).ToList();
            foreach (var p in rest)
            {
                var t = p.transform;
                t.SetPositionAndRotation(eye + turn * (t.position - eye), turn * t.rotation);
            }
            var settled = new List<UiPanel>();
            if (main != null) settled.Add(main);
            float gaze = FrontYaw();
            foreach (var p in rest.OrderBy(p => Mathf.Abs(Mathf.DeltaAngle(gaze, YawAround(eye, p.transform.position)))))
            {
                bool covers = false;
                if (ViewRectOf(p, eye, gaze, out var mine))
                    foreach (var s in settled)
                        if (ViewRectOf(s, eye, gaze, out var r) && mine.Overlaps(r, -1f)) { covers = true; break; } // a degree of touch is fine
                if (covers) PlaceSmart(p.Id, p, null, settled.ToList());
                settled.Add(p);
            }
        }

        private static float YawAround(Vector3 eye, Vector3 at)
        {
            var d = at - eye;
            return Mathf.Atan2(d.x, d.z) * Mathf.Rad2Deg;
        }

        private static float MeanYawAround(Vector3 eye, List<UiPanel> ps)
        {
            float s = 0f, c = 0f;
            foreach (var p in ps)
            {
                float a = YawAround(eye, p.transform.position) * Mathf.Deg2Rad;
                s += Mathf.Sin(a); c += Mathf.Cos(a);
            }
            return Mathf.Atan2(s, c) * Mathf.Rad2Deg;
        }

        /// A panel that was open before: its old place is still good when it is in view (its middle within ViewYaw of
        /// where the user faces, 3 m) and no open panel has taken it.
        private bool OldPlaceStillGood(UiPanel p)
        {
            var head = Head;
            if (head == null) return true;
            if (Vector3.Distance(p.transform.position, head.position) > 3f) return false;
            float gaze = FrontYaw();
            if (!ViewRectOf(p, head.position, gaze, out var mine)) return false;
            if (Mathf.Abs(mine.YawMid) > ViewYaw + 6f) return false; // out of view: placed anew, in front
            foreach (var q in PlacementObstacles(p))
                if (q.Id != "dock" && ViewRectOf(q, head.position, gaze, out var r) && mine.Overlaps(r, -1f)) return false; // a degree of overlap is a touch, not a cover
            return true;
        }
    }
}

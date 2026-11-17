using System;
using System.Collections.Generic;
using System.Linq;
using MetaCodex.AI;
using MetaCodex.UI;
using TMPro;
using UnityEngine;

namespace MetaCodex.Shell
{
    /// Window groups (owner 2026-11-14 for threads, 2026-11-17 "a grouping mechanic like we did for threads, for the web
    /// windows"; docs/thread-view.md "Thread windows and groups"). A group is a window of its own ("tgroup-1" ...) that
    /// holds thread windows and web windows (browser pages, the code editor), mixed as the user likes, in a grid: 2 -> 2x1,
    /// 3-4 -> 2x2, 5-6 -> 3x2. A cell is as wide as the widest of its windows and as tall as the tallest (a thread window
    /// 0.73 x 0.66, a web window 0.80 x 0.61); each window is centred in its cell, its top at the cell's top. The windows
    /// are its children at their full layout, shrunk by the group's scale: a thread keeps its whole chat, composer and
    /// approvals, a page its toolbar, touch, scroll, keyboard, chooser, dialogs and full screen (inside its cell: a page's
    /// full screen fills its page area, never the window). The group is one unit for placement, RECENTER and the FLOAT
    /// ring (it is in the panel list; its windows are not: IsGroupedPanel).
    /// - Drop a window (its title bar) on another one: the two make a group; on a group's window: it joins that group at
    ///   that place; on a window of its own group: the two swap places. The target lights up while it is under.
    /// - Let a grouped window go outside its group: it comes out (full size, where it was let go). Its ✕ closes it alone.
    /// - The group's title bar moves it; ⇲ UNGROUP sets every window free; ✕ closes them all; the RESIZE grip (bottom
    ///   right) scales the group and its windows (the top-left corner stays).
    /// - A thread of a project that has a window open joins that window's group when it opens (AutoGroup), unless the
    ///   user took that thread out of a group by hand while its window was open.
    /// - Moves are eased (about 0.25 s): a window joining, leaving or changing place glides to its cell and size.
    /// - Bent ("Bend panels"): the group bends like any window; its thread windows have no curve of their own, they lie on
    ///   the group's (CellPose, FitMemberBend), so rays and pokes hit them where they are drawn. A web window never bends:
    ///   it lies flat on the tangent plane at its cell's centre (CellPose's centre and normal), moved towards the user just
    ///   enough that its side edges are CellDepth in front of the curve, so the page is never behind the group's frame and
    ///   its page and pointer agree (UiPanel's flat ray maths, WebPageArea.ToPage).
    /// - Thread-only: AutoGroup (a project's threads) and _pulledOut; the voice command `thread_windows` (its group /
    ///   ungroup act on the focused thread window's group, which may hold web windows too).
    /// Simplified: groups are not kept across restarts; a group's grid size changes by making its frame again (its old
    /// frame fades out, the new one fades in; the windows glide).
    public sealed partial class MetaCodexShell
    {
        public const float GroupPad = 0.02f, GroupGap = 0.02f, GroupFooter = 0.05f, GroupScaleMin = 0.4f, GroupScaleMax = 1.2f;
        public const int GroupMax = 6;
        /// A window sits this far in front of its group's surface (group units, along the surface's normal; < 0: towards
        /// the user): the nearest panel along a ray is the window inside its cell, the group in its bar, frame and gaps.
        public const float CellDepth = -0.01f;

        /// A window a group can hold: a thread window (ShellThreadWindows) or a web window (ShellWeb: a page or the code
        /// editor). What the group needs of it: its panel and id, its group, its drag and glide, whether it may bend.
        private abstract class GroupMember
        {
            public readonly string Id;
            public UiPanel Panel;
            public WindowGroup Group;         // the group it is in, or null
            public bool Dragging;             // its title bar is held (drop onto a window or group)
            public bool Gliding;              // FIXED: eases to GlidePos (leaving a group by UNGROUP)
            public bool Easing;               // it left a group: its size eases back to FreeScale (TickMember)
            public Vector3 GlidePos;
            public Quaternion GlideRot;
            /// Its size when free, over the UI scale: 1, or a web window's 1.25 (a large page, the code editor), kept while
            /// it is in a group (there it has the group's scale) and eased back to when it leaves.
            public float FreeScale = 1f;
            protected GroupMember(string id) { Id = id; }
            /// Never bent (a web page is one flat picture): in a bent group it lies flat on its cell's tangent plane.
            public abstract bool Flat { get; }
            /// Closed for good: its group lets it go.
            public abstract bool Gone { get; }
        }

        private sealed class WindowGroup
        {
            public readonly int Number;
            public string Id => "tgroup-" + Number;
            public UiPanel Panel;
            public readonly List<GroupMember> Members = new List<GroupMember>();
            public float Scale;
            public bool UserScaled, Closing, Resizing;
            public int Cols, Rows;
            public float CellW = ThreadW, CellH = ThreadH; // a cell (group units): its widest and its tallest window
            public Vector3 ResizeAnchor;
            public float ResizeFrom, ResizeScale0;
            public float FitR; // the group's drawn bend its windows were last put on (FitGroupMembers)
            public WindowGroup(int n) { Number = n; }
        }

        private readonly List<WindowGroup> _groups = new List<WindowGroup>();
        private readonly HashSet<UiPanel> _groupedPanels = new HashSet<UiPanel>();
        private readonly HashSet<string> _pulledOut = new HashSet<string>(); // threads taken out of a group by hand (while open)
        private UiPanel _dropLit;
        private GroupMember _newGroupAt;

        private bool IsGroupedPanel(UiPanel p) => p != null && _groupedPanels.Contains(p);

        private static bool MemberOpen(GroupMember m) => m != null && !m.Gone && m.Panel != null && m.Panel.IsOpen;

        /// The group a window's panel is in (a thread or web window in a group), or null. No allocation.
        private WindowGroup GroupOfMemberPanel(UiPanel p)
        {
            if (p == null || !_groupedPanels.Contains(p)) return null;
            foreach (var g in _groups)
                foreach (var m in g.Members)
                    if (m.Panel == p) return g;
            return null;
        }

        /// A press on a window (or a drop that grouped it): a thread window gets the focus, a web window becomes the one
        /// touched last (pages the app opens and the hardware keyboard go there).
        private void MemberTouched(GroupMember m)
        {
            if (m is ThreadWindow tw) FocusWindow(tw);
            else if (m is WebWindow ww) WebMarkTouched(ww);
        }

        /// A thread put in a group by hand may join groups by itself again (AutoGroup).
        private void ClearPulledOut(GroupMember m)
        {
            if (m is ThreadWindow tw && tw.threadOpen != null) _pulledOut.Remove(tw.threadOpen);
        }

        private bool IsResizing(UiPanel p)
        {
            foreach (var g in _groups) if (g.Panel == p && g.Resizing) return true;
            return false;
        }

        private WindowGroup GroupOfPanel(UiPanel p)
        {
            if (p == null) return null;
            foreach (var g in _groups) if (g.Panel == p) return g;
            return null;
        }


        public static (int cols, int rows) GroupGrid(int n) => n <= 2 ? (2, 1) : n <= 4 ? (2, 2) : (3, 2);
        public static float DefaultGroupScale(int n) => n <= 2 ? 0.72f : n <= 4 ? 0.6f : 0.5f;

        /// A window's cell in its group (group panel units, the windows at their full layout size).
        private static Rect CellRect(WindowGroup g, int i)
        {
            int c = i % Mathf.Max(1, g.Cols), r = i / Mathf.Max(1, g.Cols);
            return new Rect(GroupPad + c * (g.CellW + GroupGap), UiTheme.Current.HeaderHeight + GroupPad + r * (g.CellH + GroupGap), g.CellW, g.CellH);
        }

        /// The cell for the group's windows now: the widest and the tallest of them (a thread window ThreadW x ThreadH, a
        /// web window 0.80 x 0.61), so each fits its cell at the group's scale.
        private static Vector2 CellSizeFor(WindowGroup g)
        {
            float w = 0f, h = 0f;
            foreach (var m in g.Members)
                if (m.Panel != null) { w = Mathf.Max(w, m.Panel.Width); h = Mathf.Max(h, m.Panel.Height); }
            return new Vector2(w > 0f ? w : ThreadW, h > 0f ? h : ThreadH);
        }

        private static string GroupTitle(WindowGroup g)
        {
            int threads = 0;
            foreach (var m in g.Members) if (m is ThreadWindow) threads++;
            return threads == g.Members.Count ? $"✥ Group {g.Number} · {g.Members.Count} threads" : $"✥ Group {g.Number} · {g.Members.Count} windows";
        }

        // ------------------------------------------------------------------ the group's curve (Bend panels)

        /// Where the window in cell `i` is drawn in its group (group-local), `depth` off the surface along its normal: its
        /// centre is the cell's centre across, its top the cell's top. Bent (radius `bend`, the group's units): on the
        /// group's arc at that centre's angle, turned to face the group's axis; a flat window (a web page) on the tangent
        /// plane there, nearer the axis by as much as its half width needs, so its side edges are `depth` in front of the
        /// curve (the frame never covers the page; e > h always but on a curve tighter than the window). Flat group: the
        /// centre, not turned.
        private static void CellPose(WindowGroup g, int i, float bend, float depth, out Vector3 pos, out Quaternion rot)
        {
            var cell = CellRect(g, i);
            var m = i >= 0 && i < g.Members.Count ? g.Members[i] : null;
            float mh = m != null && m.Panel != null ? Mathf.Min(m.Panel.Height, cell.height) : cell.height; // its height in the group (scale 1)
            var c = g.Panel.PanelToLocal(new Vector2(cell.center.x, cell.y + mh * 0.5f), depth);
            if (bend <= 0f) { pos = c; rot = Quaternion.identity; return; }
            float a = c.x / bend, rr = bend + depth; // the same arc as UiPanel.SurfacePoint
            if (m != null && m.Flat && m.Panel != null)
            {
                float h = m.Panel.Width * 0.5f * m.Panel.BaseScale.x; // its half width in the group's units (it eases to 1 when it joins)
                rr = rr > h ? Mathf.Sqrt(rr * rr - h * h) : rr * 0.5f;
            }
            pos = new Vector3(rr * Mathf.Sin(a), c.y, -bend + rr * Mathf.Cos(a));
            rot = Quaternion.Euler(0f, a * Mathf.Rad2Deg, 0f);
        }

        /// A grouped window's bend: no curve of its own, the group's. Placed by CellPose (CellDepth in front) and bent
        /// with the group's radius less that depth, in the window's units (its scale in the group), its axis is the
        /// group's axis: the two surfaces are concentric, the window lies on the group's curve edge to edge, and its own
        /// ray maths (UiPanel.Intersect) is the surface it is drawn on.
        /// A flat window (a web page) has no bend at all.
        private static void FitMemberBend(GroupMember w, float groupBend)
        {
            var m = w.Panel;
            float s = Mathf.Max(1e-4f, m.transform.localScale.x);
            float r = groupBend > 0f && !w.Flat ? (groupBend + CellDepth) / s : 0f;
            if (r < 0.05f) r = 0f; // UiPanel.SetBend's floor: never asked again every frame
            if (m.BendRadius == r && m.BendNow == r) return;
            m.SetBend(r);
            m.SnapBend(); // never eased on its own: it follows the group's easing
        }

        /// The group's windows on its surface, with its curve. A window in its cell (where the group's last curve put it)
        /// follows the new curve exactly; one still gliding to its cell moves by `k` (0: not this time). Called every
        /// frame (TickThreadGroups) and when the group's drawn bend changes (UiPanel.BendChanged: in the same tick it
        /// eases, so a window is never a frame off the curve). No allocation.
        private void FitGroupMembers(WindowGroup g, float k)
        {
            var gp = g.Panel;
            if (gp == null) return;
            float bend = gp.BendNow, was = g.FitR;
            g.FitR = bend;
            for (int i = 0; i < g.Members.Count; i++)
            {
                var m = g.Members[i];
                if (m.Panel == null || m.Panel.transform.parent != gp.transform) continue;
                var t = m.Panel.transform;
                if (!m.Dragging)
                {
                    CellPose(g, i, bend, CellDepth, out var lp, out var lq);
                    var wp = lp;
                    var wq = lq;
                    if (was != bend) CellPose(g, i, was, CellDepth, out wp, out wq);
                    bool inCell = (t.localPosition - wp).sqrMagnitude < 1e-7f && Quaternion.Angle(t.localRotation, wq) < 0.2f;
                    if (inCell)
                    {
                        if (t.localPosition != lp) t.localPosition = lp;
                        if (t.localRotation != lq) t.localRotation = lq;
                    }
                    else if (k > 0f)
                    {
                        var d = t.localPosition - lp;
                        if (d.sqrMagnitude > 1e-8f) t.localPosition = d.sqrMagnitude < 1e-7f ? lp : Vector3.Lerp(t.localPosition, lp, k);
                        if (t.localRotation != lq) t.localRotation = Quaternion.Angle(t.localRotation, lq) < 0.2f ? lq : Quaternion.Slerp(t.localRotation, lq, k);
                    }
                }
                FitMemberBend(m, bend);
            }
        }

        /// A layout panel's bend for a radius in the layout's units (UpdateFixedBend, the FLOAT ring): a group is drawn at
        /// its own scale (its windows at their full layout, shrunk by it), so the curve of the windows beside it is a
        /// larger radius in its units.
        private float LayoutBend(UiPanel p, float radius)
        {
            if (_groups.Count == 0) return radius;
            var g = GroupOfPanel(p);
            return g != null ? radius / Mathf.Max(0.05f, g.Scale) : radius;
        }

        // ------------------------------------------------------------------ the group's window

        /// Makes the group's window (again: a new grid or cell size, RebuildPanels) and puts its windows in it. `rebuilt`:
        /// the old frame is gone already (RebuildPanels); it is not opened here (the rebuild restores what was open).
        private void BuildGroupPanel(WindowGroup g, bool rebuilt = false)
        {
            var t = UiTheme.Current;
            (g.Cols, g.Rows) = GroupGrid(g.Members.Count);
            var cell = CellSizeFor(g);
            g.CellW = cell.x;
            g.CellH = cell.y;
            if (!g.UserScaled) g.Scale = DefaultGroupScale(g.Members.Count);
            float w = g.Cols * g.CellW + (g.Cols - 1) * GroupGap + 2 * GroupPad;
            float h = t.HeaderHeight + 2 * GroupPad + g.Rows * g.CellH + (g.Rows - 1) * GroupGap + GroupFooter;
            var old = g.Panel;
            bool hadOld = old != null; // a destroyed one compares equal to null (RebuildPanels)
            bool wasOpen = hadOld && old.IsOpen;
            var p = Panel<UiPanel>(g.Id, GroupTitle(g), w, h);
            g.Panel = p;
            p.HideCloseButton();
            float bh = t.HeaderHeight - 0.008f;
            var close = p.AddHeaderClose(() => { if (g.Panel != null) g.Panel.Close(); }, UiPanel.CloseWideW); // pressed beyond what it draws
            p.Add(new UiButton("⇲ UNGROUP", () => Ungroup(g)) { Rect = new Rect(close.Rect.x - t.Gap - 0.15f, 0.004f, 0.15f, bh), TextSize = t.SmallSize * 1.1f }); // left of the ✕'s hit rect
            p.Add(new UiLabel("the title bar moves the group · a window's title bar: drag it out to free it, onto another window to swap", t.SmallSize)
                { Rect = new Rect(GroupPad, h - GroupFooter + 0.006f, w - GroupPad - 0.2f, GroupFooter - 0.012f), Color = t.TextDim });
            p.Add(new UiResizeGrip
            {
                Rect = new Rect(w - 0.18f, h - GroupFooter + 0.004f, 0.172f, GroupFooter - 0.008f),
                OnPressAt = e => ResizeStart(g, e),
                OnDragAt = e => ResizeStep(g, e),
                OnReleaseAt = () => ResizeEnd(g),
            });
            p.Closed += _ => OnGroupClosed(g, p);
            p.BendChanged += _ => { if (g.Panel == p) FitGroupMembers(g, 0f); }; // its windows follow its curve as it eases
            p.BaseScale = Vector3.one * UiScaleNow * g.Scale;
            bool made = !hadOld && _newGroupAt != null && _newGroupAt.Panel != null && g.Members.Contains(_newGroupAt);
            // bent: curved from the start like the frame it replaces (a new grid) or the window it is made on, the same
            // curve in the world (the layout eases it from there, not from flat)
            var curveFrom = hadOld ? old : made ? _newGroupAt.Panel : null;
            if (curveFrom != null && curveFrom.BendNow > 0f && Settings != null && Settings.bendPanels)
            {
                p.SetBend(curveFrom.BendNow * curveFrom.RestLossyScale.x / Mathf.Max(1e-5f, p.RestLossyScale.x));
                p.SnapBend();
            }
            if (hadOld) p.transform.SetPositionAndRotation(old.transform.position, old.transform.rotation);
            else if (made)
            {
                // a new group: the window it was made on keeps its place (its cell is where it was, on the curve when bent)
                var at = _newGroupAt.Panel.transform;
                CellPose(g, g.Members.IndexOf(_newGroupAt), p.BendNow, 0f, out var cp, out var cq);
                p.transform.rotation = at.rotation * Quaternion.Inverse(cq);
                p.transform.position = at.position - p.transform.TransformVector(cp);
            }
            g.FitR = p.BendNow; // a rebuilt group's windows are put on this curve at once (AttachToGroup)
            for (int i = 0; i < g.Members.Count; i++) AttachToGroup(g.Members[i], g, rebuilt);
            if (hadOld)
            {
                // the old frame fades out with its closing (its windows are in the new one already)
                if (Floating && _slots.TryGetValue(old.transform, out var slot)) { _slots.Remove(old.transform); _slots[p.transform] = slot; }
                Router.Unregister(old);
                old.CloseAndDestroy();
                if (Floating && _carousel != null) { _slots.Remove(p.transform); Slot(p.transform); } // its new width on the ring
            }
            if (rebuilt) return;
            if (!hadOld || wasOpen)
            {
                p.Open();
                _placed.Add(g.Id);
                if (!hadOld) OnOpenedFloating(p);
            }
            UpdateThreadWindowVoice();
        }

        /// The groups' windows again (RebuildPanels destroyed them with their windows, which BuildWeb and BuildThread made
        /// anew: BuildWeb runs first, so this, from BuildThread, finds every member's new panel).
        private void RebuildGroupPanels()
        {
            _groupedPanels.Clear();
            foreach (var g in _groups.ToList())
            {
                g.Members.RemoveAll(m => m.Gone || m.Panel == null);
                if (g.Members.Count < 2) { foreach (var m in g.Members) m.Group = null; _groups.Remove(g); continue; }
                g.Panel = null;
                BuildGroupPanel(g, true);
            }
        }

        /// A window goes into the group's window: it keeps its place and size in the world for now and eases into its cell
        /// (TickThreadGroups); `snap`: at once. A web window coming in free keeps its free size (FreeScale) for when it leaves.
        private void AttachToGroup(GroupMember w, WindowGroup g, bool snap)
        {
            if (w.Panel == null || g.Panel == null) return;
            var t = w.Panel.transform;
            if (w is WebWindow && w.Group == null && !w.Easing && !snap)
                w.FreeScale = Mathf.Clamp(w.Panel.BaseScale.x / Mathf.Max(1e-4f, UiScaleNow), 0.5f, 2f);
            float rel = w.Panel.RestLossyScale.x / Mathf.Max(1e-5f, g.Panel.RestLossyScale.x); // its world size kept, in the group's units
            if (Floating) _slots.Remove(t);
            t.SetParent(g.Panel.transform, true);
            w.Group = g;
            w.Easing = false;
            _groupedPanels.Add(w.Panel);
            w.Gliding = false;
            if (snap)
            {
                CellPose(g, Mathf.Max(0, g.Members.IndexOf(w)), g.Panel.BendNow, CellDepth, out var lp, out var lq);
                t.localPosition = lp;
                t.localRotation = lq;
                w.Panel.BaseScale = Vector3.one;
            }
            else w.Panel.BaseScale = Vector3.one * (float.IsNaN(rel) || rel <= 0f ? 1f : rel);
            FitMemberBend(w, g.Panel.BendNow); // no curve of its own from now: the group's (flat group, a web page: flat)
        }

        /// A window leaves its group's window: it stays where it is in the world and eases back to its full size.
        private void DetachFromGroup(GroupMember w)
        {
            var g = w.Group;
            w.Group = null;
            if (g != null) g.Members.Remove(w);
            if (w.Panel == null) return;
            _groupedPanels.Remove(w.Panel);
            var t = w.Panel.transform;
            float world = w.Panel.RestLossyScale.x / Mathf.Max(1e-5f, _root != null ? _root.lossyScale.x : 1f);
            t.SetParent(_root, true);
            // open: it keeps its size in the world and eases to its full size; closed: at its full size at once
            w.Panel.BaseScale = Vector3.one * (!w.Panel.IsOpen || float.IsNaN(world) || world <= 0f ? UiScaleNow * w.FreeScale : world);
            w.Easing = w.Panel.IsOpen;
            // its own bend again: the layout's from the next frame, eased from the group's curve (its radius in the world is
            // kept by the new scale); "Bend panels" off: flat now (the frame loop bends nothing then)
            if (Settings == null || !Settings.bendPanels || w.Flat) { w.Panel.SetBend(0f); w.Panel.SnapBend(); }
            FaceUser(w.Panel);
            if (w.Panel.IsOpen) _placed.Add(w.Id);
            if (Floating && _carousel != null && w.Panel.IsOpen) Slot(t);
        }

        /// The group after a window came or went: two or more stay a group (its grid made again when it or its cell size
        /// changed), one alone is set free.
        private void RefreshGroup(WindowGroup g)
        {
            if (g.Closing) return;
            g.Members.RemoveAll(m => m.Gone || m.Panel == null);
            if (g.Members.Count < 2) { DissolveGroup(g); return; }
            var grid = GroupGrid(g.Members.Count);
            var cell = CellSizeFor(g);
            if (g.Panel == null || grid.cols != g.Cols || grid.rows != g.Rows || Mathf.Abs(cell.x - g.CellW) > 1e-4f || Mathf.Abs(cell.y - g.CellH) > 1e-4f
                || !g.UserScaled && Mathf.Abs(DefaultGroupScale(g.Members.Count) - g.Scale) > 1e-3f)
            {
                BuildGroupPanel(g);
                return;
            }
            foreach (var m in g.Members) if (m.Panel != null && m.Panel.transform.parent != g.Panel.transform) AttachToGroup(m, g, false);
            g.Panel.SetTitle(GroupTitle(g));
            UpdateThreadWindowVoice();
        }

        /// The group goes; its windows stay where they are, free.
        private void DissolveGroup(WindowGroup g)
        {
            g.Closing = true;
            foreach (var m in g.Members.ToList()) DetachFromGroup(m);
            g.Members.Clear();
            _groups.Remove(g);
            var p = g.Panel;
            if (p != null)
            {
                if (_panels.TryGetValue(g.Id, out var cur) && cur == p) _panels.Remove(g.Id);
                _placed.Remove(g.Id);
                _slots.Remove(p.transform);
                Router.Unregister(p);
                if (_dropLit == p) _dropLit = null;
                p.CloseAndDestroy();
            }
            UpdateThreadWindowVoice();
            UpdateSidebarHandle();
        }

        /// The group's ✕ (or CLOSE ALL, a layout): its windows close with it.
        private void OnGroupClosed(WindowGroup g, UiPanel closed)
        {
            if (g.Panel != closed || g.Closing) return; // an old frame of it (a new grid), or it is dissolving
            g.Closing = true;
            var ms = g.Members.ToList();
            foreach (var m in ms) DetachFromGroup(m);
            foreach (var m in ms) if (m.Panel != null && m.Panel.IsOpen) m.Panel.Close();
            _groups.Remove(g);
            if (_panels.TryGetValue(g.Id, out var cur) && cur == closed) _panels.Remove(g.Id);
            _placed.Remove(g.Id);
            _slots.Remove(closed.transform);
            if (_dropLit == closed) _dropLit = null;
            closed.CloseAndDestroy();
            UpdateThreadWindowVoice();
        }

        /// A window comes out of its group (closed, pulled out, moved to another group).
        private void RemoveFromGroup(GroupMember w, bool pulledOut)
        {
            var g = w.Group;
            if (g == null) return;
            if (pulledOut && w is ThreadWindow tw && tw.threadOpen != null) _pulledOut.Add(tw.threadOpen);
            DetachFromGroup(w);
            RefreshGroup(g);
        }

        /// Two free windows make a group: the target keeps its place (its cell is where it was), the dropped one joins.
        private WindowGroup MakeGroup(GroupMember target, GroupMember joining)
        {
            int n = 1;
            while (_groups.Any(x => x.Number == n)) n++;
            var g = new WindowGroup(n);
            g.Members.Add(target);
            g.Members.Add(joining);
            _groups.Add(g);
            _newGroupAt = target; // the group is made where the target is (BuildGroupPanel)
            try { BuildGroupPanel(g); } finally { _newGroupAt = null; }
            var gp = g.Panel.transform;
            if (Floating && _carousel != null) { _slots.Remove(gp); Slot(gp); }
            Toast($"Group {n}: drag a window's title bar out to free it", "info");
            return g;
        }

        /// The window joins the group at `index` (-1: at the end); out of its own group first.
        private bool MoveToGroup(GroupMember w, WindowGroup g, int index)
        {
            if (g.Members.Contains(w)) return true;
            if (g.Members.Count >= GroupMax) { Toast($"A group holds {GroupMax} windows", "warn"); return false; }
            if (w.Group != null) RemoveFromGroup(w, false);
            if (g.Closing || !_groups.Contains(g)) return false;
            if (index < 0 || index > g.Members.Count) index = g.Members.Count;
            g.Members.Insert(index, w);
            if (g.Panel != null) AttachToGroup(w, g, false);
            RefreshGroup(g);
            return true;
        }

        /// ⇲ UNGROUP: every window free at its full size, side by side where the group was.
        private void Ungroup(WindowGroup g)
        {
            if (g == null || g.Closing) return;
            var ms = g.Members.ToList();
            Vector3 centre = g.Panel != null ? g.Panel.transform.position : Vector3.zero;
            Quaternion rot = g.Panel != null ? g.Panel.transform.rotation : Quaternion.identity;
            DissolveGroup(g);
            UiPanel prev = null;
            foreach (var m in ms)
            {
                if (!MemberOpen(m)) continue;
                var t = m.Panel.transform;
                Vector3 fromPos = t.position;
                Quaternion fromRot = t.rotation;
                var small = m.Panel.BaseScale;
                m.Panel.BaseScale = Vector3.one * (UiScaleNow * m.FreeScale); // placed at its full size
                if (prev == null) t.SetPositionAndRotation(centre, rot);
                else PlaceSmart(m.Id, m.Panel, prev);
                if (!Floating)
                {
                    m.GlidePos = t.position;
                    m.GlideRot = t.rotation;
                    m.Gliding = true;
                    t.SetPositionAndRotation(fromPos, fromRot);
                }
                m.Panel.BaseScale = small; // and eases to it (TickThreadGroups)
                prev = m.Panel;
            }
            if (Floating && _carousel != null) ArrangeRing();
        }

        /// Every window ungrouped (voice).
        private int UngroupAll()
        {
            int n = _groups.Count;
            foreach (var g in _groups.ToList()) Ungroup(g);
            return n;
        }

        /// A thread just opened in `w`: when another open window shows a thread of the same project, w joins its group
        /// (or the two make one). Not when the user took this thread out of a group by hand while it was open.
        private void AutoGroup(ThreadWindow w)
        {
            if (!WinOpen(w) || w.Group != null) return;
            string proj = w.threadOpenProject ?? w.draft?.ProjectId;
            if (string.IsNullOrEmpty(proj)) return;
            if (w.threadOpen != null && _pulledOut.Contains(w.threadOpen)) return;
            var mate = OpenWindows.FirstOrDefault(o => o != w && (o.threadOpenProject ?? o.draft?.ProjectId) == proj && (o.Group == null || o.Group.Members.Count < GroupMax));
            if (mate == null) return;
            if (mate.Group != null) MoveToGroup(w, mate.Group, -1);
            else MakeGroup(mate, w);
            FocusWindow(w);
        }

        /// Groups the focused window with every other open free window (voice: "group the threads").
        private string GroupAllOpen()
        {
            var free = OpenWindows.Where(x => x.Group == null).ToList();
            var f = WinOpen(_twFocus) ? _twFocus : free.FirstOrDefault();
            if (f == null) return "error: open a thread first";
            var g = f.Group;
            var others = free.Where(x => x != f).ToList();
            if (others.Count == 0) return g != null ? "The open threads are grouped already" : "error: open another thread to group with it";
            foreach (var o in others)
            {
                if (g == null) g = MakeGroup(f, o);
                else if (!MoveToGroup(o, g, -1)) break;
            }
            return $"Group {g?.Number}: {g?.Members.Count} threads";
        }

        // ------------------------------------------------------------------ scales (the UI scale setting, a group's size)

        /// After the UI scale setting set every panel's scale: a group's windows are scaled by their group.
        private void ApplyGroupScales()
        {
            foreach (var g in _groups)
            {
                if (g.Panel == null) continue;
                g.Panel.BaseScale = Vector3.one * UiScaleNow * g.Scale;
                foreach (var m in g.Members) if (m.Panel != null) m.Panel.BaseScale = Vector3.one;
            }
        }

        private void SetGroupScale(WindowGroup g, float s)
        {
            g.Scale = Mathf.Clamp(s, GroupScaleMin, GroupScaleMax);
            if (g.Panel != null) g.Panel.BaseScale = Vector3.one * UiScaleNow * g.Scale;
        }

        private void ResizeStart(WindowGroup g, UiPointerEvent e)
        {
            if (g.Panel == null) return;
            g.Resizing = true;
            g.ResizeAnchor = g.Panel.SurfacePoint(Vector2.zero);
            g.ResizeFrom = Mathf.Max(0.05f, Vector3.Distance(g.Panel.SurfacePoint(e.Point), g.ResizeAnchor));
            g.ResizeScale0 = g.Scale;
        }

        /// The grip follows the hand: the group's scale is the hand's distance from the top-left corner over where the
        /// press started (one transform: live); the corner stays where it was.
        private void ResizeStep(WindowGroup g, UiPointerEvent e)
        {
            if (!g.Resizing || g.Panel == null) return;
            float d = Vector3.Distance(g.Panel.SurfacePoint(e.Point), g.ResizeAnchor);
            SetGroupScale(g, g.ResizeScale0 * d / g.ResizeFrom);
            g.Panel.transform.position += g.ResizeAnchor - g.Panel.SurfacePoint(Vector2.zero);
        }

        private void ResizeEnd(WindowGroup g)
        {
            if (!g.Resizing) return;
            g.Resizing = false;
            g.UserScaled = true;
            if (Floating && _carousel != null && g.Panel != null) { _slots.Remove(g.Panel.transform); Slot(g.Panel.transform); }
        }

        // ------------------------------------------------------------------ every frame

        /// Drops (a window let go on a window or group), the grids (windows ease to their cells), sizes and glides: the
        /// thread windows, then the web windows (two loops over the lists: no allocation).
        private void TickThreadGroups(float dt)
        {
            float k = UiTheme.Approach(dt, UiTheme.MotionBase); // settles in about 0.22 s, the shared motion scale
            var head = Head;
            for (int i = 0; i < Tws.Count; i++)
            {
                var w = Tws[i];
                TickMember(w, k, head);
                if (Tws.Count <= i || Tws[i] != w) break; // the list changed: the rest next frame
            }
            for (int i = 0; i < _webWins.Count; i++)
            {
                var w = _webWins[i];
                TickMember(w, k, head);
                if (_webWins.Count <= i || _webWins[i] != w) break;
            }
            // the windows glide to their cells, on the group's curve, with its bend in their units (after their scale eased)
            for (int gi = 0; gi < _groups.Count; gi++) FitGroupMembers(_groups[gi], k);
        }

        /// One window's frame: its drag (the drop target lit, a drop when it is let go), its size and its glide. A thread
        /// window's size always eases to its place (as before); a web window's only in a group or just out of one (its
        /// own scale, a large page's or the code editor's, is the web code's otherwise).
        private void TickMember(GroupMember w, float k, Transform head)
        {
            if (!MemberOpen(w)) { w.Dragging = false; return; }
            if (Router.IsDragging(w.Panel))
            {
                w.Dragging = true;
                w.Gliding = false;
                var (tw, tg) = DropTarget(w, head);
                LightDrop(tw != null ? tw.Panel : tg?.Panel);
                return;
            }
            if (w.Dragging)
            {
                w.Dragging = false;
                LightDrop(null);
                Dropped(w, head);
                if (!MemberOpen(w)) return;
            }
            // the size it eases to: full (the UI scale, times its own) when free, the group's units (1) in a group
            var p = w.Panel;
            if (!p.Transitioning && (w.Group != null || w.Easing || w is ThreadWindow))
            {
                var want = w.Group != null ? Vector3.one : Vector3.one * (UiScaleNow * w.FreeScale);
                var bs = p.BaseScale;
                if ((bs - want).sqrMagnitude > 1e-8f) p.BaseScale = (bs - want).sqrMagnitude < 1e-6f ? want : Vector3.Lerp(bs, want, k);
                else w.Easing = false;
            }
            if (w.Gliding && w.Group == null)
            {
                var t = p.transform;
                if (Floating) w.Gliding = false;
                else
                {
                    t.SetPositionAndRotation(Vector3.Lerp(t.position, w.GlidePos, k), Quaternion.Slerp(t.rotation, w.GlideRot, k));
                    if ((t.position - w.GlidePos).sqrMagnitude < 1e-6f) { t.SetPositionAndRotation(w.GlidePos, w.GlideRot); w.Gliding = false; }
                }
            }
        }

        private void LightDrop(UiPanel p)
        {
            if (_dropLit == p) return;
            if (_dropLit != null) _dropLit.SetHighlight(0f);
            _dropLit = p;
            if (p != null) p.SetHighlight(0.9f);
        }

        /// What a window carried by its title bar is over: the window (free, or in a group; a thread or a web window) or the
        /// group the line from the eyes through its middle meets, nearest first. A free window counts only well inside it
        /// (a near miss does not group); its own group's frame is no target (letting go there keeps it in).
        private (GroupMember win, WindowGroup group) DropTarget(GroupMember w, Transform head)
        {
            if (head == null || w.Panel == null) return (null, null);
            var eye = head.position;
            var dir = w.Panel.transform.position - eye;
            if (dir.sqrMagnitude < 1e-4f) return (null, null);
            var ray = new Ray(eye, dir.normalized);
            GroupMember bestW = null;
            WindowGroup bestG = null;
            float best = float.MaxValue;
            for (int i = 0; i < Tws.Count; i++) DropCandidate(Tws[i], w, ray, ref bestW, ref best);
            for (int i = 0; i < _webWins.Count; i++) DropCandidate(_webWins[i], w, ray, ref bestW, ref best);
            if (bestW != null) return (bestW, null);
            for (int i = 0; i < _groups.Count; i++)
            {
                var g = _groups[i];
                if (g == w.Group || g.Panel == null || !g.Panel.IsOpen || g.Closing) continue;
                if (g.Panel.Raycast(ray, out _, out float d) && d < best) { best = d; bestG = g; }
            }
            return (null, bestG);
        }

        private static void DropCandidate(GroupMember o, GroupMember carried, Ray ray, ref GroupMember bestW, ref float best)
        {
            if (o == carried || !MemberOpen(o)) return;
            if (!o.Panel.Raycast(ray, out var pt, out float d)) return;
            float m = o.Group != null ? 0.1f : 0.18f;
            if (pt.x < o.Panel.Width * m || pt.x > o.Panel.Width * (1f - m) || pt.y < o.Panel.Height * m || pt.y > o.Panel.Height * (1f - m)) return;
            if (d < best) { best = d; bestW = o; }
        }

        /// A window let go: swap, join, group or come out (see the class comment). A drop groups across projects and kinds
        /// (a thread beside its code editor, the editor beside a video): the only project rule is AutoGroup (on open).
        private void Dropped(GroupMember w, Transform head)
        {
            var (tw, tg) = DropTarget(w, head);
            if (tw != null)
            {
                if (w.Group != null && tw.Group == w.Group)
                {
                    var ms = w.Group.Members;
                    int a = ms.IndexOf(w), b = ms.IndexOf(tw);
                    if (a >= 0 && b >= 0) { ms[a] = tw; ms[b] = w; } // they glide to each other's cells
                    return;
                }
                LogGroupDrop(w, tw, null);
                if (tw.Group != null) { ClearPulledOut(w); MoveToGroup(w, tw.Group, tw.Group.Members.IndexOf(tw)); MemberTouched(w); return; }
                if (w.Group != null) RemoveFromGroup(w, false);
                if (MemberOpen(tw) && tw.Group == null) { ClearPulledOut(w); MakeGroup(tw, w); MemberTouched(w); }
                return;
            }
            if (tg != null) { LogGroupDrop(w, null, tg); ClearPulledOut(w); MoveToGroup(w, tg, -1); MemberTouched(w); return; }
            if (w.Group == null) return;
            // let go over its own group: it goes back to its cell; outside it: it comes out where it was let go
            var g = w.Group;
            bool inside = false;
            if (head != null && g.Panel != null)
            {
                var dir = w.Panel.transform.position - head.position;
                inside = dir.sqrMagnitude > 1e-4f && g.Panel.Raycast(new Ray(head.position, dir.normalized), out _, out _);
            }
            if (!inside) RemoveFromGroup(w, true);
        }

        /// The drop in the session log: which window was let go, on what, and what both show (a thread's project, a web
        /// page, the code editor; owner 2026-11-16: a drag across projects was reported as not grouping; this line shows
        /// what the drop did on the headset).
        private void LogGroupDrop(GroupMember w, GroupMember onto, WindowGroup group)
        {
            var with = onto ?? group?.Members.FirstOrDefault(m => m != w);
            Log($"window group: {w.Id} ({MemberWhat(w)}) let go on {(onto != null ? onto.Id : group.Id)} ({MemberWhat(with)})");
        }

        /// What a window shows, for the log: a thread's project, "web page" or "code editor".
        private string MemberWhat(GroupMember m) =>
            m is ThreadWindow tw ? ProjectNameOf(tw.threadOpenProject ?? tw.draft?.ProjectId) ?? "no project"
            : m is WebWindow ww ? (WebShowsIde(ww) ? "code editor" : "web page") : "nothing";

        // ------------------------------------------------------------------ voice (the command registry)

        public static readonly string[] ThreadWindowActions = { "close", "next", "group", "ungroup", "close_group" };

        private void RegisterThreadWindowVoiceCommand()
        {
            Voice.Register(new VoiceCommand
            {
                Name = "thread_windows",
                Description = "The thread windows (several can be open; the focused one is marked ●): close the focused one (the thread stays), next (focus the next open one), group (put the open free windows in one grid with the focused one), ungroup (set the focused window's group free, every window at full size), close_group (close the focused window's group and its windows)",
                Parameters = VoiceCommands.Schema(("action", "string", ThreadWindowActions, "close: the focused thread window; next: focus the next one; group: the open windows in one group; ungroup: the focused window's group set free; close_group: that group and its windows closed", true)),
                Synonyms = new Dictionary<string, Dictionary<string, string>>
                {
                    ["action"] = new Dictionary<string, string>
                    {
                        ["shut"] = "close", ["dismiss"] = "close", ["hide"] = "close",
                        ["switch"] = "next", ["other"] = "next", ["cycle"] = "next", ["following"] = "next",
                        ["merge"] = "group", ["combine"] = "group", ["tile"] = "group", ["grid"] = "group", ["join"] = "group",
                        ["split"] = "ungroup", ["separate"] = "ungroup", ["unmerge"] = "ungroup", ["break up"] = "ungroup", ["free"] = "ungroup",
                    },
                },
                Phrases = new[]
                {
                    "(close|shut|dismiss) [the|this] group [of threads|of thread windows] {action:close_group}",
                    "(close|shut|dismiss|hide) [the|this] thread window {action:close}",
                    "(close|shut|dismiss) this thread {action:close}",
                    "(next|other|switch) thread window {action:next}",
                    "(switch|go|move) to the (next|other) thread [window] {action:next}",
                    "(group|merge|combine|tile|join) [the|my|all] [open] (threads|thread windows|chats|conversations) [together] {action:group}",
                    "(ungroup|split|separate|unmerge) [the|this|my] (group|threads|thread windows|thread group) {action:ungroup}",
                    "{action} [the] thread windows",
                },
                Samples = new[]
                {
                    "put the open threads side by side in a grid", "close this thread window", "break up the group of threads", "switch to the other thread window",
                    "close the whole group of threads", "tile my conversations together",
                },
                Examples = new List<(string, string)> { ("show my threads side by side", "thread_windows group"), ("take the threads out of the group", "thread_windows ungroup") },
                Vocabulary = new[] { "thread window", "group", "ungroup" },
                Run = a => ThreadWindowVoice((string)a["action"]),
            });
            UpdateThreadWindowVoice();
        }

        /// Enabled while a thread window is open (rule 2 of the registry: never a command whose action cannot run).
        private void UpdateThreadWindowVoice()
        {
            if (Voice == null) return;
            Voice.SetEnabled("thread_windows", Tws.Any(WinOpen));
        }

        private string ThreadWindowVoice(string action)
        {
            var f = WinOpen(_twFocus) ? _twFocus : OpenWindows.FirstOrDefault();
            if (f == null) return "error: no thread window is open";
            switch (action)
            {
                case "close":
                    f.Panel.Close();
                    return "Closed the thread window" + (f.Number > 1 ? " " + f.Number : "");
                case "next":
                {
                    var open = Tws.Where(WinOpen).OrderBy(x => x.Number).ToList();
                    if (open.Count < 2) return "error: only one thread window is open";
                    var n = open[(open.IndexOf(f) + 1) % open.Count];
                    FocusWindow(n);
                    n.Panel.Ping();
                    return "Thread window " + n.Number + (n.threadOpen != null ? ": " + Cut(ThreadTitle(n.threadOpen), 40) : "");
                }
                case "group": return GroupAllOpen();
                case "ungroup":
                    if (f.Group != null) { Ungroup(f.Group); return "Group set free"; }
                    return _groups.Count > 0 ? $"Ungrouped {UngroupAll()} groups" : "error: no thread group is open";
                case "close_group":
                    if (f.Group == null) return "error: the focused thread window is not in a group";
                    f.Group.Panel?.Close();
                    return "Closed the group";
                default: return "error: unknown action " + action;
            }
        }

        // ------------------------------------------------------------------ self-tests

        public int GroupCountForTest => _groups.Count;
        /// The windows of each group in grid order (window ids), by group id.
        public Dictionary<string, List<string>> GroupsForTest => _groups.ToDictionary(g => g.Id, g => g.Members.Select(m => m.Id).ToList());
        public UiPanel GroupPanelForTest(string groupId) => _groups.FirstOrDefault(g => g.Id == groupId)?.Panel;
        public float GroupScaleForTest(string groupId) => _groups.FirstOrDefault(g => g.Id == groupId)?.Scale ?? 0f;
        public void TickThreadGroupsForTest(float dt, int steps) { for (int i = 0; i < steps; i++) TickThreadGroups(dt); }

        /// Self-tests: a thread or web window by id ("thread-2", "web-2").
        private GroupMember MemberForTest(string id) => (GroupMember)Tws.FirstOrDefault(x => x.Id == id) ?? _webWins.FirstOrDefault(x => x.Id == id);

        /// Self-tests (thread and web window ids): the drop of window `id` as if its title bar were let go where it is now.
        public void DropThreadWindowForTest(string id) { var w = MemberForTest(id); if (w != null) Dropped(w, Head); }
        public string DropTargetForTest(string id)
        {
            var w = MemberForTest(id);
            if (w == null) return null;
            var (tw, tg) = DropTarget(w, Head);
            return tw?.Id ?? tg?.Id;
        }
        public void MakeGroupForTest(string targetId, string joiningId)
        {
            var a = MemberForTest(targetId);
            var b = MemberForTest(joiningId);
            if (a == null || b == null) return;
            if (a.Group != null) MoveToGroup(b, a.Group, -1); else MakeGroup(a, b);
        }
        public void UngroupForTest(string groupId) => Ungroup(_groups.FirstOrDefault(g => g.Id == groupId));
        public void PullOutForTest(string id) { var w = MemberForTest(id); if (w != null) RemoveFromGroup(w, true); }
        public void SetGroupScaleForTest(string groupId, float s) { var g = _groups.FirstOrDefault(x => x.Id == groupId); if (g != null) { SetGroupScale(g, s); g.UserScaled = true; } }
        public string GroupOfWindowForTest(string id) => MemberForTest(id)?.Group?.Id;
    }
}

namespace MetaCodex.UI
{
    /// A thread group's RESIZE grip (bottom right): press and drag; the shell scales the group from the hand's distance
    /// to the group's top-left corner.
    public sealed class UiResizeGrip : UiCanvas
    {
        public Action<UiPointerEvent> OnPressAt, OnDragAt;
        public Action OnReleaseAt;
        public override bool Interactive => true;

        protected override void Relayout() => Redraw();
        protected override void OnInteractionChanged() => Redraw();

        protected override void Draw()
        {
            var t = Theme;
            var r = Rect;
            Box(r, Hover || Pressed ? t.Accent : t.AccentSoft, 0.004f, 1, Pressed ? 1f : Hover ? 0.5f : 0.2f, 0.001f);
            for (int i = 0; i < 3; i++)
            {
                float o = 0.007f + i * 0.007f;
                Stroke(new Vector2(r.xMax - o, r.yMax - 0.005f), new Vector2(r.xMax - 0.005f, r.yMax - o), 0.0018f, t.Text);
            }
            Txt(new Rect(r.x, r.y, r.width - 0.03f, r.height), "⇲ RESIZE", Mathf.Min(0.013f, r.height * 0.5f), Hover || Pressed ? t.Text : t.TextAccent, TextAlignmentOptions.Center);
        }

        public override void OnPress(UiPointerEvent e) => OnPressAt?.Invoke(e);
        public override void OnDrag(UiPointerEvent e) => OnDragAt?.Invoke(e);
        public override void OnRelease(UiPointerEvent e, bool inside) => OnReleaseAt?.Invoke();
    }
}

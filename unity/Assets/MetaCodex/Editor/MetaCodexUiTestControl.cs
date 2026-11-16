using System.Collections.Generic;
using System.Linq;
using MetaCodex.Shell;
using MetaCodex.UI;
using UnityEngine;

namespace MetaCodex.EditorTools
{
    /// Self-test section `control` (owner 2026-11-11, docs/control-center.md): the calmer main menu (13 entries, setup
    /// in CONTROL), CONTROL's rail on its window's surface (edge to edge, tops and bottoms level, one plane), one category
    /// at a time taking the shown window's place, old ids, SETTINGS' pages without their tab bar, the statuses, following
    /// a moved window, closing together, the FLOAT ring's room, voice, and work windows opening beside the window they
    /// belong to; the rail on a bent and leaning window (ControlRailOnCurve: one axis, one radius, one lean); the header ✕
    /// (HeaderCloseSize); then the menus-and-layout polish (ControlPolish) and CONTROL > WRIST MENU (WristMenuPage).
    public static partial class MetaCodexUiTest
    {
        private static Vector3 TopLeftOf(UiPanel p) => p.transform.TransformPoint(new Vector3(-p.Width * 0.5f, p.Height * 0.5f, 0f));

        private static void ControlSection(MetaCodexShell shell, Transform head, UiPanel frame)
        {
            foreach (var id in shell.PanelIds.ToList()) shell.ClosePanel(id);
            shell.SetLayout("fixed");
            bool bend0 = shell.Settings.bendPanels;
            shell.Settings.bendPanels = false;
            shell.OnSettingsChanged();
            // the main menu: work windows and quick actions; setup is in CONTROL
            var w = shell.Wrist;
            var inner = w.Inner.Select(x => x.Label).ToList();
            var outer = w.Outer.Select(x => x.Label).ToList();
            var dock = shell.PanelsById["dock"].Elements.OfType<UiButton>().Select(b => (b.Text ?? "").Split('\n').Last()).ToList();
            string[] setup = { "AGENTS", "MQH", "VAULT", "GIT", "VOICE", "SYSTEM", "SETUP" };
            Check(inner.SequenceEqual(new[] { "TALK", "TERMS", "FILES", "THREADS", "WEB", "CONTROL" }) && outer.Count == 5 && !inner.Concat(outer).Any(setup.Contains),
                $"the wrist menu: 6 + {outer.Count} tiles (was 6 + 13): {string.Join(" ", inner)} | {string.Join(" ", outer)}");
            Check(dock.Count == 11 && dock.Contains("CONTROL") && dock.Count(x => x.StartsWith("TERM")) == 1 && !dock.Any(setup.Contains), $"the dock: the same {dock.Count}: {string.Join(" ", dock)}");
            // one table builds both (MainMenuItems): the dock is the wrist in the same order (no TABLE: the table keyboard is gone)
            var wristOrder = inner.Concat(outer).ToList();
            Check(dock.SequenceEqual(wristOrder) && !outer.Contains("TABLE") && shell.MainMenuItems().Count == inner.Count + outer.Count,
                $"dock and wrist from one table: dock {string.Join(" ", dock)} = wrist {string.Join(" ", wristOrder)}");
            var cats = shell.ControlCategories;
            Check(cats.Count == 11 && cats.Select(c => c.Group).Distinct().Count() == 3 && new[] { "agents", "mqh", "vault", "voice", "system", "display", "wrist", "focus", "hands", "look", "about" }.All(k => cats.Any(c => c.Key == k)),
                "CONTROL has eleven categories in three groups: " + string.Join(", ", cats.Select(c => c.Group + "/" + c.Label)));
            string groups = string.Join(" / ", cats.GroupBy(c => c.Group).Select(g => g.Key + ": " + string.Join(", ", g.Select(c => c.Key))));
            Check(groups == "THIS HEADSET: display, wrist, look, focus, hands / AI: voice, agents, mqh, vault / LINUX & APP: system, about"
                && cats.First(c => c.Key == "focus").Glyph == "◐" && cats.First(c => c.Key == "look").Glyph == "◈",
                $"the rail's groups and glyphs (FOCUS ◐ as on the menu, LOOK & SOUND ◈): {groups}");

            // CONTROL from the menu: its last category (DISPLAY here), the rail on the window's surface on its left: edge to
            // edge (no gap, owner 2026-11-16), top edges level, in the window's plane (flat here)
            shell.OpenControl("display");
            shell.ToggleControl();
            shell.TickLayoutForTest(0.02f, 1);
            w.Inner.First(x => x.Label == "CONTROL").Act();
            shell.UpdateControlRailForTest();
            var st = shell.PanelsById["settings"];
            var rail = shell.ControlRail;
            var tabs = st.Elements.OfType<UiTabs>().First();
            var railCorner = st.transform.InverseTransformPoint(rail.SurfacePoint(new Vector2(rail.Width, 0f))); // its top-right corner, seen from the window
            bool docked = Mathf.Abs(railCorner.x + st.Width * 0.5f) < 0.001f && Mathf.Abs(railCorner.y - st.Height * 0.5f) < 0.001f && Mathf.Abs(railCorner.z) < 0.001f
                && Vector3.Angle(rail.transform.forward, st.transform.forward) < 0.1f && MetaCodexShell.RailGap == 0f;
            Check(st.IsOpen && rail.IsOpen && shell.ControlCategoryNow() == "display" && tabs.Selected == 1 && docked,
                $"CONTROL opens on DISPLAY with the rail on its left, edge to edge in its plane ({(railCorner.x + st.Width * 0.5f) * 1000f:F1} mm, {(railCorner.y - st.Height * 0.5f) * 1000f:F1} mm, {railCorner.z * 1000f:F1} mm from the window's corner; turned {Vector3.Angle(rail.transform.forward, st.transform.forward):F1} deg)");
            // as tall as the page: the bottom corners meet too
            var railBottom = st.transform.InverseTransformPoint(rail.SurfacePoint(new Vector2(rail.Width, rail.Height)));
            Check(Mathf.Abs(railBottom.y + st.Height * 0.5f) < 0.002f && Mathf.Abs(railBottom.x + st.Width * 0.5f) < 0.001f,
                $"the rail is as tall as its window: bottom corners meet ({(railBottom.x + st.Width * 0.5f) * 1000f:F1} mm, {(railBottom.y + st.Height * 0.5f) * 1000f:F1} mm)");
            bool tabBarGone = !st.Elements.OfType<UiButton>().Any(b => b.Visible && b.Style == UiButtonStyle.Tab && tabs.Names.Contains(b.Text));
            var blurb = st.Elements.OfType<UiLabel>().FirstOrDefault(l => l.Visible && (l.Text ?? "").Contains("Where windows go"));
            Check(tabBarGone && blurb != null && st.Title.Contains("DISPLAY"), $"SETTINGS' tab bar is gone (the rail is the navigation); a line says what DISPLAY is for; its title: {st.Title}");
            Check(shell.RailStatusForTest("display") == "fixed" && shell.RailStatusForTest("focus") == (shell.Settings.focusEnabled ? "on" : "off") && shell.RailStatusForTest("about").Contains(Application.version),
                $"the rail's statuses: display {shell.RailStatusForTest("display")}, focus {shell.RailStatusForTest("focus")}, mqh {shell.RailStatusForTest("mqh")}, agents {shell.RailStatusForTest("agents")}, voice {shell.RailStatusForTest("voice")}");
            st.ForceRefresh(); rail.ForceRefresh();
            var mid = (st.transform.position + rail.transform.position) * 0.5f;
            Render(shell, "ui-control.png", mid - st.transform.forward * 0.85f, st.transform.rotation, 70f, 1600, 1000);

            // another window's category takes the place: same top-left corner, the rail stays
            var corner0 = TopLeftOf(st);
            var rail0 = rail.transform.position;
            Click(shell.Router, rail, shell.RailButtonForTest("agents").Rect.center);
            shell.UpdateControlRailForTest();
            var ag = shell.PanelsById["agents"];
            Check(ag.IsOpen && !st.IsOpen && Vector3.Distance(TopLeftOf(ag), corner0) < 0.002f && shell.ControlRailPage == "agents" && rail.IsOpen && Vector3.Distance(rail.transform.position, rail0) < 0.002f,
                $"the rail's AGENTS: AGENTS takes SETTINGS' place (top-left corners {Vector3.Distance(TopLeftOf(ag), corner0) * 1000f:F1} mm apart), the rail stays ({Vector3.Distance(rail.transform.position, rail0) * 1000f:F1} mm)");
            // an old id from anywhere: git = the vault's GIT tab, in the same place, with the rail
            shell.OpenPanel("git");
            shell.UpdateControlRailForTest();
            var vault = shell.PanelsById["vault"];
            Check(vault.IsOpen && !ag.IsOpen && shell.VaultTabsForTest.Selected == 1 && Vector3.Distance(TopLeftOf(vault), corner0) < 0.002f && shell.ControlRailPage == "vault" && shell.ControlCategoryNow() == "vault",
                "“git” (ui.open, voice, old bindings) opens VAULT on GIT in the same place, beside the rail");
            // SETTINGS' pages: FOCUS takes VAULT's place; LOOK is the same window's page (no new cast)
            shell.OpenControl("focus");
            bool focusPage = st.IsOpen && !vault.IsOpen && tabs.Selected == 2 && Vector3.Distance(TopLeftOf(st), corner0) < 0.002f;
            var stPos = st.transform.position;
            shell.OpenControl("look");
            Check(focusPage && st.IsOpen && tabs.Names[tabs.Selected] == "LOOK" && Vector3.Distance(st.transform.position, stPos) < 1e-5f && shell.ControlCategoryNow() == "look",
                "the rail's FOCUS opens SETTINGS on FOCUS in VAULT's place; LOOK & SOUND then only changes its page");
            // dragged, the window takes the rail along; closed, the rail goes too
            st.transform.position += st.transform.right * 0.25f + Vector3.up * 0.05f;
            shell.TickLayoutForTest(0.02f, 1);
            var moved = st.transform.InverseTransformPoint(rail.SurfacePoint(new Vector2(rail.Width, 0f)));
            Check(Vector3.Distance(moved, railCorner) < 0.002f, $"the window moved: the rail follows it ({Vector3.Distance(moved, railCorner) * 1000f:F1} mm off its place)");
            shell.ClosePanel("settings");
            shell.TickLayoutForTest(0.02f, 1);
            Check(!rail.IsOpen, "✕ on the window (or voice “close settings”) closes the rail with it");
            // the menu's CONTROL again: the last category comes back; again: closes it
            shell.ToggleControl();
            shell.UpdateControlRailForTest();
            bool back = st.IsOpen && rail.IsOpen && tabs.Names[tabs.Selected] == "LOOK";
            shell.ToggleControl();
            shell.TickLayoutForTest(0.02f, 1);
            Check(back && !st.IsOpen && !rail.IsOpen, "CONTROL on the menu brings back the last category (LOOK & SOUND); pressed again it closes");
            // voice names
            var vm = shell.Voice.FastMatch("open the control panel");
            var vi = shell.Voice.FastMatch("show me the control centre");
            Check((string)vm?.Args["panel"] == "control" && (string)vi?.Args["panel"] == "control", $"voice: “open the control panel” -> {(string)vm?.Args["panel"]}, “show me the control centre” -> {(string)vi?.Args["panel"]}");
            // a saved layout never holds the rail on its own
            shell.OpenControl("mqh");
            shell.SaveNamedLayout("control-test");
            var lay = shell.Settings.layouts.First(x => x.name == "control-test");
            Check(lay.panels.Any(x => x.id == "mqh") && lay.panels.All(x => x.id != MetaCodexShell.ControlId), "a saved layout keeps MQH, not the rail (it comes back with its window)");
            shell.Settings.layouts.RemoveAll(x => x.name == "control-test");
            foreach (var id in shell.PanelIds.ToList()) shell.ClosePanel(id);
            shell.TickLayoutForTest(0.02f, 1);

            // FLOAT: the window and its rail take one slot of the ring, clear of the neighbours
            shell.SetLayout("float");
            foreach (var id in new[] { "files", "threads" }) shell.OpenPanel(id);
            shell.OpenControl("agents");
            shell.OpenPanel("clipboard");
            shell.TickLayoutForTest(0.05f, 120);
            var c = shell.CarouselCenter;
            float Ang(Vector3 pos) { var d = pos - c; return Mathf.Atan2(d.x, d.z) * Mathf.Rad2Deg; }
            float Half(UiPanel p) => Mathf.Atan2(p.Width * 0.5f * p.transform.lossyScale.x, new Vector2(p.transform.position.x - c.x, p.transform.position.z - c.z).magnitude) * Mathf.Rad2Deg;
            var ringed = new[] { "files", "threads", "agents", "clipboard" }.Select(id => shell.PanelsById[id]).ToList();
            float ra = Ang(rail.transform.position), rh = Half(rail);
            int clash = ringed.Where(p => p.Id != "agents").Count(p => Mathf.Abs(Mathf.DeltaAngle(Ang(p.transform.position), ra)) < Half(p) + rh);
            float agA = Ang(ag.transform.position);
            float seamF = Vector3.Distance(rail.SurfacePoint(new Vector2(rail.Width, 0f)), ag.SurfacePoint(Vector2.zero));
            Check(rail.IsOpen && clash == 0 && Mathf.DeltaAngle(agA, ra) < 0f && seamF < 0.001f,
                $"FLOAT: the rail rides on AGENTS' left ({Mathf.DeltaAngle(agA, ra):F1} deg), edge to edge ({seamF * 1000f:F1} mm), and no neighbour overlaps it ({clash})");
            shell.SetLayout("fixed");
            foreach (var id in shell.PanelIds.ToList()) shell.ClosePanel(id);
            shell.TickLayoutForTest(0.02f, 1);

            // work windows opened from the menu or by voice open beside the window they belong to
            if (frame != null)
            {
                frame.gameObject.SetActive(true);
                frame.Open();
                frame.transform.SetPositionAndRotation(head.position + Vector3.forward * 0.9f, Quaternion.identity);
                var opener = shell.ContextOpenerForTest("terminals");
                shell.PanelsById["terminals"].transform.position = Vector3.up * 50f; // nothing to come back to
                shell.OpenPanel("terminals");
                MetaCodexShell.ViewRectOf(frame, head.position, 0f, out var fr);
                MetaCodexShell.ViewRectOf(shell.PanelsById["terminals"], head.position, 0f, out var tr);
                Check(opener == frame && !tr.Overlaps(fr, 0f) && tr.Gap(fr).yaw + tr.Gap(fr).pitch < 5f,
                    $"TERMS from the menu opens beside the active terminal ({tr.Gap(fr).yaw:F1} deg apart), not just in front");
                shell.ClosePanel("terminals");
                frame.Close();
            }
            ControlRailOnCurve(shell, head);
            HeaderCloseSize(shell, head, frame);
            shell.Settings.bendPanels = bend0;
            shell.OnSettingsChanged();
            ControlPolish(shell);
            WristMenuPage(shell);
        }

        /// A point of a bent panel's axis (world): the cylinder it is drawn on (UiPanel.Intersect) has its axis BendNow in
        /// front of its centre, along its own up.
        private static Vector3 BendAxisPoint(UiPanel p) => p.transform.TransformPoint(new Vector3(0f, 0f, -p.BendNow));

        /// CONTROL as one window on a curve (owner 2026-11-16: "the sidebar ... would be better embedded in the same panel
        /// ...; otherwise the curve is not handled well"): with bending on, the rail lies on its window's cylinder (one axis,
        /// one world radius), edge to edge, tops and bottoms level, and a ray at one of its rows hits that row; seen high
        /// above the eyes the window leans (curve mode) and the rail leans with it, never on its own; another category keeps
        /// the rail on the new window's curve. FIXED layout; the Editor snaps the curve and the lean.
        private static void ControlRailOnCurve(MetaCodexShell shell, Transform head)
        {
            foreach (var id in shell.PanelIds.ToList()) shell.ClosePanel(id);
            shell.SetLayout("fixed");
            shell.Settings.bendPanels = true;
            shell.OnSettingsChanged();
            var eye = head.position;
            shell.OpenControl("display");
            var st = shell.PanelsById["settings"];
            var rail = shell.ControlRail;
            void Put(UiPanel p, Vector3 offset)
            {
                p.transform.SetPositionAndRotation(eye + offset, Quaternion.LookRotation(new Vector3(offset.x, 0f, offset.z), Vector3.up));
                shell.TickLayoutForTest(0.02f, 1); // its curve and lean from where it is (UpdateFixedBend), then the rail on it
                shell.UpdateControlRailForTest();
            }
            // what makes them one: the same axis line and world radius, the seam's two corners on each other, the rail's
            // middle on the window's cylinder; the same up (the lean)
            string OneCurve(UiPanel page, out bool ok)
            {
                var up = page.transform.up;
                float rWorld = page.BendNow * page.RestLossyScale.x;
                float axisOff = Vector3.ProjectOnPlane(BendAxisPoint(rail) - BendAxisPoint(page), up).magnitude;
                float radiusOff = Mathf.Abs(rail.BendNow * rail.RestLossyScale.x - rWorld);
                float seamTop = Vector3.Distance(rail.SurfacePoint(new Vector2(rail.Width, 0f)), page.SurfacePoint(Vector2.zero));
                float seamBottom = Vector3.Distance(rail.SurfacePoint(new Vector2(rail.Width, rail.Height)), page.SurfacePoint(new Vector2(0f, page.Height)));
                float onCyl = Mathf.Abs(Vector3.ProjectOnPlane(rail.SurfacePoint(new Vector2(rail.Width * 0.5f, rail.Height * 0.5f)) - BendAxisPoint(page), up).magnitude - rWorld);
                float upOff = Vector3.Angle(rail.transform.up, up);
                ok = page.BendNow > 0.3f && axisOff < 0.001f && radiusOff < 0.001f && seamTop < 0.001f && seamBottom < 0.001f && onCyl < 0.001f && upOff < 0.1f;
                return $"radius {rWorld:F2} m (rail {radiusOff * 1000f:F1} mm off), axes {axisOff * 1000f:F1} mm apart, seam {seamTop * 1000f:F1} / {seamBottom * 1000f:F1} mm, "
                    + $"rail's middle {onCyl * 1000f:F1} mm off the window's cylinder, ups {upOff:F2} deg apart";
            }

            // at eye level: bent, upright
            Put(st, new Vector3(0.25f, -0.05f, 0.9f));
            string level = OneCurve(st, out bool levelOk);
            var row = shell.RailButtonForTest("agents");
            var rowAt = rail.SurfacePoint(row.Rect.center);
            bool rowHit = rail.Raycast(new Ray(eye, (rowAt - eye).normalized), out var rowPt, out _) && rail.HitTest(rowPt) == row;
            Check(st.IsOpen && rail.IsOpen && levelOk && rowHit && st.TiltNow == 0f,
                $"bent: CONTROL's rail lies on its window's curve, one arc ({level}); a ray at its AGENTS row hits it ({rowHit})");
            st.ForceRefresh(); rail.ForceRefresh();
            var mid = (st.transform.position + rail.transform.position) * 0.5f;
            Render(shell, "ui-control-bent.png", eye + (mid - eye) * 0.15f, Quaternion.LookRotation(mid - eye), 80f, 1600, 1000);

            // high above the eyes: the window leans its top to them and the rail leans with it (none of its own)
            Put(st, new Vector3(0.25f, 0.45f, 0.9f));
            string lean = OneCurve(st, out bool leanOk);
            Check(st.TiltNow > 10f && rail.TiltNow == 0f && rail.TiltTarget == 0f && leanOk,
                $"bent and leaning {st.TiltNow:F0} deg: the rail leans with its window, none of its own ({rail.TiltNow:F0} deg); {lean}");

            // another category takes the place: with its curve and its lean (not levelled and eased back), the rail on it
            float lean0 = st.TiltNow;
            shell.OpenControl("agents");
            shell.UpdateControlRailForTest();
            var ag = shell.PanelsById["agents"];
            string swapped = OneCurve(ag, out bool swapOk);
            Check(ag.IsOpen && !st.IsOpen && shell.ControlRailPage == "agents" && swapOk && Mathf.Abs(ag.TiltNow - lean0) < 0.01f && Mathf.Abs(ag.TiltTarget - lean0) < 0.01f,
                $"bent: AGENTS in SETTINGS' place with its lean ({ag.TiltNow:F1} deg, was {lean0:F1}), the rail on its curve ({swapped})");
            foreach (var id in shell.PanelIds.ToList()) shell.ClosePanel(id);
            shell.TickLayoutForTest(0.02f, 1);
            shell.Settings.bendPanels = false;
            shell.OnSettingsChanged();
        }

        /// The header ✕ (owner 2026-11-16: "with curved panels I often struggle to close the panels: the close icon should be
        /// bigger"): in every window with one (the generic one, THREAD's wide one; groups build theirs the same way: the
        /// thread-windows section) it is drawn about 1.4 times the old 36 mm one with a glyph 1.4 times as large, it is
        /// pressed on about 1.5 times its width and the header's full height out to the frame's edge, nothing else in the
        /// header lies on that rect and the title ends before it; on a bent window leaning high above the eyes, rays at its
        /// outer end, just past it (right of it and above it, and past the frame's corner) and just left of it land on it,
        /// and a press and release past the frame's corner closes the window. The terminal frames' ✕ (MetaCodexShell.WrapTerminal) is checked too: it needs AddHeaderClose.
        private static void HeaderCloseSize(MetaCodexShell shell, Transform head, UiPanel frame)
        {
            var t = UiTheme.Current;
            foreach (var id in shell.PanelIds.ToList()) shell.ClosePanel(id);
            var bad = new List<string>();
            int seen = 0;
            void Inspect(UiPanel p)
            {
                if (p == null || !p.HasHeader) return;
                foreach (var b in p.Elements.OfType<UiButton>().Where(b => b.Visible && b.Text == "✕" && b.Style == UiButtonStyle.Danger))
                {
                    seen++;
                    if (!(b is UiCloseButton c)) { bad.Add($"{p.Id}: its ✕ is a plain button ({b.Rect.width * 1000f:F0} mm; use UiPanel.AddHeaderClose)"); continue; }
                    Rect d = c.Drawn, h = c.Rect;
                    bool size = d.width >= 0.036f * 1.35f && c.TextSize >= t.BodySize * 1.35f && d.height >= t.HeaderHeight - 0.0065f && d.yMin >= 0f && d.yMax <= t.HeaderHeight;
                    bool hit = h.xMin <= d.xMin && h.yMin <= Mathf.Min(0f, d.yMin) && h.xMax >= Mathf.Max(p.Width, d.xMax) && h.yMax >= Mathf.Max(t.HeaderHeight, d.yMax) - 1e-5f
                        && h.width >= Mathf.Min(1.45f * d.width, d.width + 0.024f);
                    var over = p.Elements.Where(e => e != b && e.Visible && e.Interactive && e.Rect.Overlaps(h)).Select(e => e.GetType().Name + (e is UiButton ob ? " " + ob.Text : "")).ToList();
                    bool title = p.TitleRectForTest.xMax <= h.xMin;
                    if (!size || !hit || over.Count > 0 || !title)
                        bad.Add($"{p.Id}: drawn {d.width * 1000f:F0} x {d.height * 1000f:F0} mm, glyph {c.TextSize * 1000f:F1} mm ({size}); hit {h.width * 1000f:F0} x {h.height * 1000f:F0} mm "
                            + $"from x {h.xMin * 1000f:F0} to {h.xMax * 1000f:F0} of {p.Width * 1000f:F0} ({hit}); on it: {(over.Count == 0 ? "nothing" : string.Join(", ", over))}; title ends at {p.TitleRectForTest.xMax * 1000f:F0} ({title})");
                }
            }
            foreach (var p in shell.PanelsById.Values) Inspect(p);
            Inspect(frame); // a terminal frame
            var gen = shell.PanelsById["files"].CloseButton as UiCloseButton;
            Check(seen >= 10 && bad.Count == 0 && gen != null,
                $"header ✕ in {seen} windows: drawn {gen?.Drawn.width * 1000f:F0} x {gen?.Drawn.height * 1000f:F0} mm (was 36 x 30), glyph {gen?.TextSize * 1000f:F1} mm (was {t.BodySize * 1000f:F0}), "
                + $"pressed on {gen?.Rect.width * 1000f:F0} x {gen?.Rect.height * 1000f:F0} mm" + (bad.Count == 0 ? "" : "; wrong: " + string.Join(" | ", bad)));
            if (gen == null) return;

            // bent and leaning (curve mode, high above the eyes): the ✕ is hit where it is drawn, and round it
            bool bend0 = shell.Settings.bendPanels;
            shell.Settings.bendPanels = true;
            shell.OnSettingsChanged();
            shell.SetLayout("fixed");
            var eye = head.position;
            shell.OpenPanel("files");
            var fp = shell.PanelsById["files"];
            var off = new Vector3(0.3f, 0.4f, 0.8f);
            fp.transform.SetPositionAndRotation(eye + off, Quaternion.LookRotation(new Vector3(off.x, 0f, off.z), Vector3.up));
            shell.TickLayoutForTest(0.02f, 1); // its curve and lean (UpdateFixedBend; the Editor snaps both)
            var dr = gen.Drawn;
            bool Hits(Vector2 at) => fp.Raycast(new Ray(eye, (fp.SurfacePoint(at) - eye).normalized), out var pt, out _) && fp.HitTest(pt) == gen;
            bool outerEnd = Hits(new Vector2(dr.xMax - 0.002f, dr.center.y));
            bool pastIt = Hits(new Vector2(dr.xMax + 0.006f, dr.yMin - 0.004f)); // right of it and above it: still its hit rect
            var beyond = new Vector2(fp.Width + 0.002f, -0.002f); // 2 mm past the frame's top-right corner, where a ray that misses the edge lands
            bool pastFrame = Hits(beyond);
            bool leftOf = Hits(new Vector2(dr.xMin - 0.010f, dr.center.y));
            bool title = !Hits(new Vector2(fp.TitleRectForTest.center.x, dr.center.y)); // the title bar stays the title bar
            // a press and release past the frame's corner, as the router gets them, closes the window
            var at = fp.SurfacePoint(beyond);
            var ptr = new UiPointer { Id = 63, Kind = PointerKind.Test, Hand = "test", Active = true, Ray = new Ray(eye, (at - eye).normalized) };
            shell.Router.Inject(ptr);
            ptr.Down = true;
            shell.Router.Inject(ptr);
            ptr.Down = false;
            shell.Router.Inject(ptr);
            ptr.Active = false;
            shell.Router.Inject(ptr);
            bool closed = !fp.IsOpen;
            Check(fp.BendNow > 0.3f && fp.TiltNow > 5f && outerEnd && pastIt && pastFrame && leftOf && title && closed,
                $"bent ({fp.BendNow:F2} m) and leaning ({fp.TiltNow:F0} deg): rays at the ✕'s outer end ({outerEnd}), 6 mm right of and 4 mm above it ({pastIt}), 2 mm past the frame's corner ({pastFrame}), 10 mm left of it ({leftOf}) hit it, the title does not ({title}); a pinch past the frame's corner closes the window ({closed})");
            foreach (var id in shell.PanelIds.ToList()) shell.ClosePanel(id);
            shell.Settings.bendPanels = bend0;
            shell.OnSettingsChanged();
        }

        /// The menus-and-layout polish (branch feat/polish): page ids, one size, entry points in their place, buttons that
        /// say one thing, dimmed controls, LOG's CLEAR ALL, the dock's lit tiles.
        private static void ControlPolish(MetaCodexShell shell)
        {
            foreach (var id in shell.PanelIds.ToList()) shell.ClosePanel(id);
            shell.SetLayout("fixed");
            shell.TickLayoutForTest(0.02f, 1);
            var st = shell.PanelsById["settings"];
            var tabs = st.Elements.OfType<UiTabs>().First();

            // CONTROL's SETTINGS pages by their category's id (ui.open, bindings); CONTROL comes back to the last one
            var bad = new System.Collections.Generic.List<string>();
            foreach (var (id, tab) in new[] { ("display", 1), ("look", 3), ("focus", 2), ("hands", 0), ("about", 4) })
            {
                shell.OpenPanel(id);
                shell.UpdateControlRailForTest();
                if (!st.IsOpen || tabs.Selected != tab || shell.ControlCategoryNow() != id || !shell.IsPanelOpen(id) || shell.ControlRailPage != "settings") bad.Add(id + "=" + tabs.Selected);
            }
            shell.ClosePanel("settings");
            shell.TickLayoutForTest(0.02f, 1);
            shell.OpenPanel("control");
            shell.UpdateControlRailForTest();
            bool backOnAbout = st.IsOpen && tabs.Selected == 4;
            Check(bad.Count == 0 && backOnAbout, $"ids display, look, focus, hands, about open their CONTROL page ({(bad.Count == 0 ? "all" : "wrong: " + string.Join(", ", bad))}); “control” brings back the last one, ABOUT ({backOnAbout})");
            // "control" is the window and the rail together: toggled or closed, both go
            shell.TogglePanel("control");
            shell.TickLayoutForTest(0.02f, 1);
            bool toggledAway = !st.IsOpen && !shell.ControlRail.IsOpen;
            shell.TogglePanel("control");
            shell.UpdateControlRailForTest();
            bool toggledBack = st.IsOpen && shell.ControlRail.IsOpen;
            shell.ClosePanel("control");
            Check(toggledAway && toggledBack && !st.IsOpen && !shell.ControlRail.IsOpen, $"toggle_panel / close_panel “control”: the window and the rail go and come together (away {toggledAway}, back {toggledBack})");

            // one size for the seven CONTROL windows: a category taking another's place covers the same rectangle
            var sizes = MetaCodexShell.ControlPages.Where(shell.PanelsById.ContainsKey).Select(id => (id, w: shell.PanelsById[id].Width, h: shell.PanelsById[id].Height)).ToList();
            bool oneSize = sizes.Count == 7 && sizes.All(x => Mathf.Abs(x.w - MetaCodexShell.ControlPageW) < 1e-4f && Mathf.Abs(x.h - MetaCodexShell.ControlPageH) < 1e-4f);
            Check(oneSize, "CONTROL's seven windows have one size: " + string.Join(", ", sizes.Select(x => $"{x.id} {x.w:F2}x{x.h:F2}")) + (oneSize ? "" : " (VOICE: ShellVoice's BuildVoice must use ControlPageW, ControlPageH)"));

            // HANDS: SELECT AREA, GESTURE STUDIO (MQH's studio, in HANDS' place), HORIZON HANDS
            shell.OpenControl("hands");
            var handsRow = st.Elements.OfType<UiButton>().Where(b => b.Visible && b.Style != UiButtonStyle.Tab).Select(b => b.Text).Where(x => x.StartsWith("SELECT AREA") || x.StartsWith("GESTURE STUDIO") || x.StartsWith("HORIZON")).ToList();
            var studio = st.Elements.OfType<UiButton>().FirstOrDefault(b => b.Visible && b.Text.StartsWith("GESTURE STUDIO"));
            studio?.Click();
            shell.UpdateControlRailForTest();
            bool studioOpen = shell.PanelsById["mqh"].IsOpen && shell.MqhTabsForTest.Selected == MetaCodexShell.MqhGesturesTab && shell.ControlRailPage == "mqh" && !st.IsOpen;
            Check(handsRow.SequenceEqual(new[] { "SELECT AREA ⌖", "GESTURE STUDIO ▸", "HORIZON HANDS ⚙" }) && studioOpen,
                $"HANDS: {string.Join(" | ", handsRow)}; GESTURE STUDIO opens MQH on GESTURES in its place ({studioOpen})");
            // MQH: the title names every tab; DAEMON is STATUS now (id daemon still opens it)
            var mqh = shell.PanelsById["mqh"];
            shell.OpenPanel("daemon");
            Check(mqh.Title == "MQH · SKILLS · PLUGINS · SCRIPTS · GESTURES" && shell.MqhTabsForTest.Names[shell.MqhTabsForTest.Selected] == "STATUS",
                $"MQH's title “{mqh.Title}”, the id daemon opens tab {shell.MqhTabsForTest.Names[shell.MqhTabsForTest.Selected]}");

            // DISPLAY: no CLIPBOARD SIZE (it is CLIPBOARD's KEEP); DISTANCE dimmed in FLOAT, FLOAT RADIUS in FIXED
            shell.OpenControl("display");
            var dist = st.Elements.OfType<UiSlider>().First(x => x.Text == "DISTANCE");
            var radius = st.Elements.OfType<UiSlider>().FirstOrDefault(x => x.Text == "FLOAT RADIUS");
            bool fixedDims = radius != null && radius.Dimmed && !dist.Dimmed;
            shell.SetLayout("float");
            st.ForceRefresh(); // its Tick follows the layout
            bool floatDims = radius != null && !radius.Dimmed && dist.Dimmed;
            shell.SetLayout("fixed");
            st.ForceRefresh();
            bool noClipSize = !st.Elements.OfType<UiSlider>().Any(x => x.Text == "CLIPBOARD SIZE" || x.Text == "CAROUSEL RADIUS");
            bool keep = shell.PanelsById["clipboard"].Elements.OfType<UiSlider>().Any(x => x.Text == "KEEP");
            Check(fixedDims && floatDims && noClipSize && keep, $"DISPLAY: FLOAT RADIUS dimmed in FIXED ({fixedDims}), DISTANCE in FLOAT ({floatDims}); CLIPBOARD SIZE is CLIPBOARD's KEEP ({noClipSize}, {keep})");
            st.ForceRefresh();
            RenderPanel(st, "ui-display-dims.png");

            // ABOUT: LICENCES opens FILES on the licences, APP PERMISSIONS; no raw log lines
            shell.OpenControl("about");
            var aboutBtns = st.Elements.OfType<UiButton>().Where(b => b.Visible && b.Style != UiButtonStyle.Tab).Select(b => b.Text).ToList();
            bool noLog = !st.Elements.OfType<UiLabel>().Any(l => l.Visible && (l.Text ?? "").Contains("shell up:"));
            st.Elements.OfType<UiButton>().FirstOrDefault(b => b.Visible && b.Text == "LICENCES ▸")?.Click();
            bool licences = shell.PanelsById["files"].IsOpen && shell.FilesPathForTest == MetaCodexShell.LicencesDir;
            Check(aboutBtns.Contains("LICENCES ▸") && aboutBtns.Contains("APP PERMISSIONS ⚙") && noLog && licences,
                $"ABOUT: {string.Join(" | ", aboutBtns)}; no log lines ({noLog}); LICENCES opens FILES at {shell.FilesPathForTest} ({licences})");
            shell.ClosePanel("files");
            // LINKS keeps ⟳ SCAN only; FILES APP is on FILES' row; SYSTEM has no TERMINAL probe
            var links = shell.PanelsById["browser"];
            var linkBtns = links.Elements.OfType<UiButton>().Select(b => b.Text).ToList();
            bool filesApp = shell.PanelsById["files"].Elements.OfType<UiButton>().Any(b => b.Text == "FILES APP");
            bool noProbe = !shell.PanelsById["system"].Elements.OfType<UiButton>().Any(b => b.Text == "TERMINAL");
            Check(links.Title == "LINKS" && linkBtns.Contains("⟳ SCAN") && !linkBtns.Any(x => x == "HANDS ⚙" || x == "APP ⚙" || x == "FILES APP") && filesApp && noProbe,
                $"LINKS (“{links.Title}”): {string.Join(" | ", linkBtns)}; FILES APP on FILES ({filesApp}); SYSTEM without TERMINAL ({noProbe})");
            // AGENTS: one action per button, no OPEN LINK; the rail says “sign in” until an agent is ready
            var agBtns = shell.PanelsById["agents"].Elements.OfType<UiButton>().Select(b => b.Text).ToList();
            string agStatus = shell.RailStatusForTest("agents");
            Check(!agBtns.Any(x => x.Contains(" / ") || x.Contains("OPEN LINK")) && agBtns.Any(x => x == "ENABLE" || x == "DISABLE") && agBtns.Contains("UPDATE") && (agStatus == "sign in" || agStatus.EndsWith("ready")),
                $"AGENTS: {string.Join(" | ", agBtns.Where(x => !x.Contains("\n")))}; the rail: {agStatus}");
            shell.ClosePanel("control");

            // LOG · MESSAGES: CLEAR ALL asks first (as CLIPBOARD's), then clears
            shell.Toast("polish: one message", "info");
            shell.OpenPanel("messages");
            var msgs = shell.PanelsById["messages"];
            var clear = msgs.Elements.OfType<UiButton>().FirstOrDefault(b => b.Text == "CLEAR ALL");
            int before = shell.Toasts.History.Count, dlg0 = UiModal.OpenCount;
            clear?.Click();
            bool asked = UiModal.OpenCount == dlg0 + 1 && shell.Toasts.History.Count == before;
            foreach (var d in UiModal.OpenPanels.ToList()) d.Close();
            shell.ClearMessages();
            Check(msgs.Title == "LOG · MESSAGES" && clear != null && clear.Style == UiButtonStyle.Danger && asked && shell.Toasts.History.Count == 0,
                $"LOG · MESSAGES (“{msgs.Title}”): CLEAR ALL in red asks first ({asked}), then clears");
            shell.ClosePanel("messages");

            // the dock shows what is on, as the wrist does (FILES open: its tile selected)
            shell.OpenPanel("dock");
            var dockFiles = shell.PanelsById["dock"].Elements.OfType<UiButton>().First(b => b.Text.EndsWith("FILES"));
            shell.OpenPanel("files");
            shell.RefreshDockForTest();
            bool litOn = dockFiles.Selected;
            shell.ClosePanel("files");
            shell.RefreshDockForTest();
            Check(litOn && !dockFiles.Selected, $"the dock's FILES is selected while FILES is open ({litOn}) and not after ({!dockFiles.Selected})");

            // "show the keyboard" never hides it: tested on the terminal input in the section syskeyboard
            foreach (var id in shell.PanelIds.ToList()) shell.ClosePanel(id);
            shell.TickLayoutForTest(0.02f, 1);
        }

        /// CONTROL > WRIST MENU (owner 2026-11-16): the layout rules (WristLayout, pure), then the page: it opens with the rail
        /// and a twin of the menu; tiles are dragged on the twin, off it and from the palette onto it (the router's press, drag
        /// and release events); edits change the twin only; CONTROL cannot be taken off; CANCEL drops; APPLY changes the wrist
        /// menu and the DOCK; a palette tap adds at the end; RESET LAYOUT + APPLY gives the default back; closing the page
        /// takes the twin away.
        private static void WristMenuPage(MetaCodexShell shell)
        {
            // the rules
            var ids = new List<string> { "talk", "terms", "files", "threads", "web", "control", "clip", "focus", "select", "keys", "center" };
            var outerDef = ids.Select((x, i) => i >= 6).ToList();
            List<string> inner = new List<string>(), outer = new List<string>(), off = new List<string>();
            WristLayout.Resolve(ids, outerDef, "control", null, null, null, inner, outer, off);
            bool def = inner.SequenceEqual(ids.Take(6)) && outer.SequenceEqual(ids.Skip(6)) && off.Count == 0;
            var withPlugin = ids.Concat(new[] { "plugin:p/t" }).ToList();
            WristLayout.Resolve(withPlugin, outerDef.Concat(new[] { true }).ToList(), "control",
                new[] { "web", "talk", "gone" }, new[] { "files", "clip" }, new[] { "control", "keys" }, inner, outer, off);
            bool saved = inner.SequenceEqual(new[] { "web", "talk", "control" }) && outer.SequenceEqual(new[] { "files", "clip", "terms", "threads", "focus", "select", "center", "plugin:p/t" })
                && off.SequenceEqual(new[] { "keys" });
            WristLayout.Resolve(ids, outerDef, "control", ids, null, null, inner, outer, off);
            bool innerMax = inner.Count == UiWristMenu.MaxInner && outer.SequenceEqual(new[] { "select", "keys", "center" });
            WristLayout.Resolve(ids, outerDef, "control", new[] { "talk" }, ids.Skip(1).ToList(), null, inner, outer, off);
            bool innerMin = inner.Count == UiWristMenu.MinInner;
            var many = ids.Concat(Enumerable.Range(0, 6).Select(k => "plugin:p/" + k)).ToList();
            WristLayout.Resolve(many, many.Select((x, i) => i >= 6).ToList(), "control", ids.Take(3).ToList(), many.Skip(3).ToList(), null, inner, outer, off);
            bool outerMax = outer.Count == UiWristMenu.MaxOuter && off.SequenceEqual(new[] { "plugin:p/4", "plugin:p/5" }) && outer.Contains("control");
            Check(def && saved && innerMax && innerMin && outerMax,
                $"wrist layout rules: default ({def}); saved order, unknown ids skipped, new ones at the end of the outer ring, CONTROL never off ({saved}); inner ring {UiWristMenu.MinInner}-{UiWristMenu.MaxInner} ({innerMin}, {innerMax}); outer ring {UiWristMenu.MaxOuter}, the rest off ({outerMax})");

            // the page (flat: the drags below map twin points to page points on a flat page)
            foreach (var id in shell.PanelIds.ToList()) shell.ClosePanel(id);
            bool bendW = shell.Settings.bendPanels;
            shell.Settings.bendPanels = false;
            shell.Settings.wrist = new WristMenuSettings();
            shell.OnSettingsChanged();
            shell.OpenControl("wrist");
            shell.UpdateControlRailForTest();
            var page = shell.PanelsById[MetaCodexShell.WristSettingsId];
            var twin = shell.WristTwinForTest;
            UiButton Btn(string text) => page.Elements.OfType<UiButton>().First(b => b.Text == text);
            bool HasFocus(IEnumerable<UiWristMenu.Item> items) => items.Any(x => x.Label == "FOCUS");
            var menu = shell.MainMenuItems();
            bool opened = page.IsOpen && shell.ControlRailPage == MetaCodexShell.WristSettingsId && shell.ControlCategoryNow() == "wrist" && twin != null && twin.Preview && twin.Ready
                && twin.Inner.Count == menu.Count(x => !x.Outer) && twin.Outer.Count == menu.Count(x => x.Outer) && twin.GetComponent<MeshRenderer>().sharedMaterial.renderQueue == 2995;
            Check(opened, $"WRIST MENU opens beside the rail with a twin of the menu ({twin?.Inner.Count} + {twin?.Outer.Count}, drawn after the page's glass)");
            if (twin == null) { shell.Settings.bendPanels = bendW; shell.OnSettingsChanged(); return; }
            twin.PoseTimelineForTest(1f, 1f);
            RenderPanel(page, "ui-wrist-settings.png");
            // drag and drop, as the router delivers it (press, drag frames, release; page points over the twin)
            var area = shell.WristTwinAreaForTest;
            UiPointerEvent Ev(Vector2 pt, Vector2 press) => new UiPointerEvent { Point = pt, PressPoint = press };
            Vector2 LocalOf(int part) { var l = twin.transform.InverseTransformPoint(twin.WorldOf(part)); return new Vector2(l.x, l.y); }
            int PartOf(string label) { int i = twin.Inner.FindIndex(x => x.Label == label); return i >= 0 ? i : twin.Outer.FindIndex(x => x.Label == label) is int o && o >= 0 ? twin.Inner.Count + o : -1; }
            void Drag(UiElement from, Vector2 a, Vector2 toLocal)
            {
                var b = shell.WristTwinToPageForTest(toLocal);
                from.OnPress(Ev(a, a));
                from.OnDrag(Ev(Vector2.Lerp(a, b, 0.5f), a));
                from.OnDrag(Ev(b, a));
                from.OnRelease(Ev(b, a), true);
            }
            void DragTile(string label, Vector2 toLocal) { int part = PartOf(label); if (part >= 0) Drag(area, shell.WristTwinToPageForTest(LocalOf(part)), toLocal); }
            var offTheMenu = new Vector2(0.11f, 0f);                     // beyond the outer ring
            Vector2 outerTop = new Vector2(0f, 0.0595f), innerBottom = new Vector2(0f, -0.035f);
            DragTile("FOCUS", offTheMenu);
            bool twinOnly = !HasFocus(twin.Outer) && shell.MainMenuItems().Any(x => x.Label == "FOCUS") && shell.Settings.wrist.IsDefaultLayout
                && shell.WristDraftForTest.hidden.Contains("focus") && shell.WristPaletteTileForTest("focus") != null;
            DragTile("CONTROL", offTheMenu);
            bool controlStays = twin.Inner.Any(x => x.Label == "CONTROL");
            DragTile("FILES", outerTop);
            bool toOuter = twin.Outer.Any(x => x.Label == "FILES") && !twin.Inner.Any(x => x.Label == "FILES");
            var focusTile = shell.WristPaletteTileForTest("focus");
            if (focusTile != null) Drag(focusTile, focusTile.Rect.center, innerBottom);
            bool fromPalette = twin.Inner.Any(x => x.Label == "FOCUS");
            Btn("CANCEL").Click();
            bool cancelled = HasFocus(twin.Outer) && twin.Inner.Any(x => x.Label == "FILES");
            DragTile("FOCUS", offTheMenu);
            Btn("APPLY").Click();
            var dock = shell.PanelsById["dock"].Elements.OfType<UiButton>().Select(b => (b.Text ?? "").Split('\n').Last()).ToList();
            bool applied = !shell.MainMenuItems().Any(x => x.Label == "FOCUS") && !dock.Contains("FOCUS") && shell.Settings.wrist.hidden.Contains("focus");
            var tap = shell.WristPaletteTileForTest("focus");
            if (tap != null) { tap.OnPress(Ev(tap.Rect.center, tap.Rect.center)); tap.OnRelease(Ev(tap.Rect.center, tap.Rect.center), true); }
            bool tapAdds = twin.Outer.Count > 0 && twin.Outer[twin.Outer.Count - 1].Label == "FOCUS";
            Btn("RESET LAYOUT").Click();
            bool resetOnTwin = HasFocus(twin.Outer) && !shell.MainMenuItems().Any(x => x.Label == "FOCUS");
            Btn("APPLY").Click();
            bool reset = shell.Settings.wrist.IsDefaultLayout && shell.MainMenuItems().Any(x => x.Label == "FOCUS");
            bool noButtons = !page.Elements.OfType<UiButton>().Any(b => b.Text == "HIDE" || b.Text == "TO INNER" || b.Text == "TO OUTER" || b.Text.Contains("MOVE"));
            shell.ClosePanel(MetaCodexShell.WristSettingsId);
            bool gone = shell.WristTwinForTest == null;
            Check(twinOnly && controlStays && toOuter && fromPalette && cancelled && applied && tapAdds && resetOnTwin && reset && noButtons && gone,
                $"WRIST MENU by drag and drop: FOCUS dragged off the twin goes to the palette, the wrist unchanged ({twinOnly}); CONTROL dragged off snaps back ({controlStays}); "
                + $"FILES dragged to the outer ring ({toOuter}); FOCUS dragged from the palette onto the inner ring ({fromPalette}); CANCEL drops ({cancelled}); APPLY changes the menu and the DOCK ({applied}); "
                + $"a tap on a palette tile adds it at the end ({tapAdds}); RESET LAYOUT on the twin ({resetOnTwin}), applied ({reset}); no MOVE / HIDE buttons ({noButtons}); the twin goes with the page ({gone})");

            // voice: the page's names, the glass (its own value), "bigger" = the menu's size (not the DOCK / WRIST switch)
            var vOpen = shell.Voice.FastMatch("open the radial menu settings");
            shell.SetSettingByVoice("wrist_opacity", "50 percent");
            float glass = shell.Wrist != null ? shell.Wrist.GetComponent<MeshRenderer>().sharedMaterial.GetFloat("_Opacity") : -1f;
            bool mode0 = shell.Settings.wristMenu;
            string bigger = shell.SetSettingByVoice("wrist_menu", "bigger");
            Check((string)vOpen?.Args["panel"] == MetaCodexShell.WristSettingsId && Mathf.Abs(glass - 0.5f) < 1e-3f && shell.Settings.wrist.size > 1.01f && shell.Settings.wristMenu == mode0,
                $"voice: “open the radial menu settings” -> {(string)vOpen?.Args["panel"]}; wrist_opacity 50 % -> the wrist glass {glass:F2}; “make the wrist menu bigger” -> {bigger}");
            shell.Settings.wrist = new WristMenuSettings();
            shell.Settings.bendPanels = bendW;
            shell.OnSettingsChanged();
            foreach (var id in shell.PanelIds.ToList()) shell.ClosePanel(id);
            shell.TickLayoutForTest(0.02f, 1);
        }
    }
}

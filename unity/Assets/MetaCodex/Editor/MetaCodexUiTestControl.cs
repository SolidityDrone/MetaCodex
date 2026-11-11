using System.Linq;
using MetaCodex.Shell;
using MetaCodex.UI;
using UnityEngine;

namespace MetaCodex.EditorTools
{
    /// Self-test section `control` (owner 2026-11-11, docs/control-center.md): the calmer main menu (13 entries, setup
    /// in CONTROL), CONTROL's rail beside its window (top edges level, its own gap), one category at a time taking the
    /// shown window's place, old ids, SETTINGS' pages without their tab bar, the statuses, following a moved window,
    /// closing together, the FLOAT ring's room, voice, and work windows opening beside the window they belong to.
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
            Check(inner.SequenceEqual(new[] { "TALK", "TERM", "FILES", "THREADS", "WEB", "CONTROL" }) && outer.Count == 7 && !inner.Concat(outer).Any(setup.Contains),
                $"the wrist menu: 6 + {outer.Count} tiles (was 6 + 13): {string.Join(" ", inner)} | {string.Join(" ", outer)}");
            Check(dock.Count == 12 && dock.Contains("CONTROL") && !dock.Any(setup.Contains), $"the dock: the same {dock.Count}: {string.Join(" ", dock)}");
            var cats = shell.ControlCategories;
            Check(cats.Count == 10 && cats.Select(c => c.Group).Distinct().Count() == 3 && new[] { "agents", "mqh", "vault", "voice", "system", "display", "focus", "hands", "look", "about" }.All(k => cats.Any(c => c.Key == k)),
                "CONTROL has ten categories in three groups: " + string.Join(", ", cats.Select(c => c.Group + "/" + c.Label)));

            // CONTROL from the menu: its last category (DISPLAY here), the rail on the window's left, top edges level, its gap
            shell.OpenControl("display");
            shell.ToggleControl();
            shell.TickLayoutForTest(0.02f, 1);
            w.Inner.First(x => x.Label == "CONTROL").Act();
            shell.UpdateControlRailForTest();
            var st = shell.PanelsById["settings"];
            var rail = shell.ControlRail;
            var tabs = st.Elements.OfType<UiTabs>().First();
            var railCorner = st.transform.InverseTransformPoint(rail.transform.TransformPoint(new Vector3(rail.Width * 0.5f, rail.Height * 0.5f, 0f))); // its top-right corner, seen from the window
            bool docked = railCorner.x < -st.Width * 0.5f && railCorner.x > -st.Width * 0.5f - MetaCodexShell.RailGap - 0.004f && Mathf.Abs(railCorner.y - st.Height * 0.5f) < 0.002f;
            Check(st.IsOpen && rail.IsOpen && shell.ControlCategoryNow() == "display" && tabs.Selected == 1 && docked,
                $"CONTROL opens on DISPLAY with the rail on its left, top edges level ({railCorner.x + st.Width * 0.5f:F3} m, {railCorner.y - st.Height * 0.5f:F3} m from the window's corner)");
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
            var moved = st.transform.InverseTransformPoint(rail.transform.TransformPoint(new Vector3(rail.Width * 0.5f, rail.Height * 0.5f, 0f)));
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
            var vi = shell.Voice.FastMatch("apri il pannello di controllo");
            Check((string)vm?.Args["panel"] == "settings" && (string)vi?.Args["panel"] == "settings", $"voice: “open the control panel” -> {(string)vm?.Args["panel"]}, “apri il pannello di controllo” -> {(string)vi?.Args["panel"]}");
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
            Check(rail.IsOpen && clash == 0 && Mathf.DeltaAngle(agA, ra) < 0f, $"FLOAT: the rail rides on AGENTS' left ({Mathf.DeltaAngle(agA, ra):F1} deg) and no neighbour overlaps it ({clash})");
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
            shell.Settings.bendPanels = bend0;
            shell.OnSettingsChanged();
        }
    }
}

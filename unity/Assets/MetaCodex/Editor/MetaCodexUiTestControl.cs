using System.Linq;
using MetaCodex.Shell;
using MetaCodex.UI;
using UnityEngine;

namespace MetaCodex.EditorTools
{
    /// Self-test section `control` (owner 2026-11-11, docs/control-center.md): the calmer main menu (13 entries, setup
    /// in CONTROL), CONTROL's rail beside its window (top edges level, its own gap), one category at a time taking the
    /// shown window's place, old ids, SETTINGS' pages without their tab bar, the statuses, following a moved window,
    /// closing together, the FLOAT ring's room, voice, and work windows opening beside the window they belong to; then
    /// the menus-and-layout polish (ControlPolish).
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
            Check(cats.Count == 11 && cats.Select(c => c.Group).Distinct().Count() == 3 && new[] { "agents", "mqh", "plugins", "vault", "voice", "system", "display", "focus", "hands", "look", "about" }.All(k => cats.Any(c => c.Key == k)),
                "CONTROL has eleven categories in three groups: " + string.Join(", ", cats.Select(c => c.Group + "/" + c.Label)));
            string groups = string.Join(" / ", cats.GroupBy(c => c.Group).Select(g => g.Key + ": " + string.Join(", ", g.Select(c => c.Key))));
            Check(groups == "THIS HEADSET: display, look, focus, hands / AI: voice, agents, mqh, plugins, vault / LINUX & APP: system, about"
                && cats.First(c => c.Key == "focus").Glyph == "◐" && cats.First(c => c.Key == "look").Glyph == "◈",
                $"the rail's groups and glyphs (FOCUS ◐ as on the menu, LOOK & SOUND ◈): {groups}");

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
            // as tall as the page: the bottom corners level too
            var railBottom = st.transform.InverseTransformPoint(rail.transform.TransformPoint(new Vector3(rail.Width * 0.5f, -rail.Height * 0.5f, 0f)));
            Check(Mathf.Abs(railBottom.y + st.Height * 0.5f) < 0.003f, $"the rail is as tall as its window: bottom edges level ({railBottom.y + st.Height * 0.5f:F3} m)");
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
            ControlPolish(shell);
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

            // one size for the six CONTROL windows: a category taking another's place covers the same rectangle
            var sizes = MetaCodexShell.ControlPages.Where(shell.PanelsById.ContainsKey).Select(id => (id, w: shell.PanelsById[id].Width, h: shell.PanelsById[id].Height)).ToList();
            bool oneSize = sizes.Count == 6 && sizes.All(x => Mathf.Abs(x.w - MetaCodexShell.ControlPageW) < 1e-4f && Mathf.Abs(x.h - MetaCodexShell.ControlPageH) < 1e-4f);
            Check(oneSize, "CONTROL's six windows have one size: " + string.Join(", ", sizes.Select(x => $"{x.id} {x.w:F2}x{x.h:F2}")) + (oneSize ? "" : " (VOICE: ShellVoice's BuildVoice must use ControlPageW, ControlPageH)"));

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
    }
}

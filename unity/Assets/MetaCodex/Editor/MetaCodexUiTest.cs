using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text;
using MetaCodex.AI;
using MetaCodex.App;
using MetaCodex.Hands;
using MetaCodex.Scripting;
using MetaCodex.Shell;
using MetaCodex.Terminal;
using MetaCodex.UI;
using Newtonsoft.Json.Linq;
using TMPro;
using UnityEditor;
using UnityEngine;

namespace MetaCodex.EditorTools
{
    /// Headless test of the spatial shell, no headset: builds every panel, renders pictures, clicks through the
    /// router with test pointers, recognises gestures from simulated hands, and runs the "double clap" story
    /// end to end: a Lua script (written to disk, as an agent would) is loaded without a rebuild, two simulated
    /// claps trigger it, an edit to the file changes the behaviour live, a runaway script is stopped, state
    /// survives a restart. Also checks git (through WSL), the Linux files view, the float carousel with bent
    /// panels, and MQH when it runs on 127.0.0.1:38990. Report and pictures: Bench/selftest/ui-*.
    public static partial class MetaCodexUiTest
    {
        private static StringBuilder _report;
        private static bool _ok;
        private static void Line(string s) => _report.AppendLine(s);
        private static void Fail(string s) { _ok = false; _report.AppendLine("FAIL " + s); }
        private static void Check(bool cond, string what) { if (cond) Line("ok   " + what); else Fail(what); }
        /// Only some sections (fast loop): MCX_UITEST_ONLY=voice,vault, or the same in unity/Bench/uitest-only (env
        /// vars do not cross from WSL into the Windows Editor); neither set = all sections.
        private static string _progress;

        /// One line per section with memory, written at once (a run stopped by the RAM guard still shows where).
        private static void Progress(string section)
        {
            try
            {
                if (_progress == null) { _progress = Path.Combine(MetaCodexSetup.ReportDir, "ui-progress.txt"); File.WriteAllText(_progress, ""); }
                long native = UnityEngine.Profiling.Profiler.GetTotalAllocatedMemoryLong() / 1048576;
                long gc = GC.GetTotalMemory(false) / 1048576;
                long ws = System.Diagnostics.Process.GetCurrentProcess().WorkingSet64 / 1048576;
                File.AppendAllText(_progress, $"{DateTime.Now:HH:mm:ss.fff} {section}: native {native} MB, managed {gc} MB, process {ws} MB\n");
            }
            catch (Exception) { }
        }

        private static bool Want(string section)
        {
            // what the section before left behind goes first: on this machine a run has a few hundred MB to spare
            GC.Collect();
            EditorUtility.UnloadUnusedAssetsImmediate();
            Progress("before " + section);
            string only = Environment.GetEnvironmentVariable("MCX_UITEST_ONLY");
            if (string.IsNullOrWhiteSpace(only) && File.Exists(Path.Combine("Bench", "uitest-only"))) only = File.ReadAllText(Path.Combine("Bench", "uitest-only")).Trim();
            return string.IsNullOrWhiteSpace(only) || only.Split(',').Any(x => x.Trim().Length > 0 && section.StartsWith(x.Trim(), StringComparison.OrdinalIgnoreCase));
        }

        /// Sections that only run when named (benches, exports), never in the full suite.
        private static bool WantNamed(string section)
        {
            string only = Environment.GetEnvironmentVariable("MCX_UITEST_ONLY");
            if (string.IsNullOrWhiteSpace(only) && File.Exists(Path.Combine("Bench", "uitest-only"))) only = File.ReadAllText(Path.Combine("Bench", "uitest-only")).Trim();
            return !string.IsNullOrWhiteSpace(only) && only.Split(',').Any(x => string.Equals(x.Trim(), section, StringComparison.OrdinalIgnoreCase));
        }

        [MenuItem("MetaCodex/4. UI self-test")]
        public static string Run()
        {
            _report = new StringBuilder();
            _ok = true;
            _progress = null;
            Progress("start");
            string dir = MetaCodexSetup.ReportDir;
            Directory.CreateDirectory(dir);
            SystemBridge.DryRun = true;
            string tmp = Path.GetFullPath(Path.Combine("Temp", "mcx-uitest"));
            if (Directory.Exists(tmp)) Directory.Delete(tmp, true);
            Directory.CreateDirectory(tmp);
            string scripts = Path.Combine(tmp, "scripts");

            foreach (var shaderName in new[] { "MetaCodex/SciFiUI", "MetaCodex/Beam", "MetaCodex/PanelUnlit", "MetaCodex/TextBend", "MetaCodex/UiIcon", "MetaCodex/ViewImage", "MetaCodex/WristArt", "MetaCodex/StageLit", "MetaCodex/Orb" })
            {
                var sh = Shader.Find(shaderName);
                if (sh == null) { Fail("shader missing: " + shaderName); continue; }
                var msgs = ShaderUtil.GetShaderMessages(sh).Where(m => m.severity == UnityEditor.Rendering.ShaderCompilerMessageSeverity.Error).ToList();
                int errors = msgs.Count;
                Check(errors == 0, $"shader {shaderName} compiles ({errors} errors)" + (errors > 0 ? ": " + string.Join(" | ", msgs.Take(3).Select(m => $"{m.message} (line {m.line})")) : ""));
            }

            var root = new GameObject("MetaCodexUiTest") { hideFlags = HideFlags.DontSave };
            MetaCodexShell shell = null;
            try
            {
                var head = new GameObject("head").transform;
                head.SetParent(root.transform, false);
                head.position = new Vector3(0f, 1.4f, 0f);

                // a terminal (also gives the mono font) to frame like on the headset
                var termGo = new GameObject("Terminal");
                termGo.transform.SetParent(root.transform, false);
                var view = termGo.AddComponent<TerminalView>();
                var so = new SerializedObject(view);
                so.FindProperty("monoFont").objectReferenceValue = AssetDatabase.LoadAssetAtPath<Font>(MetaCodexSetup.MonoFontPath);
                MetaCodexSetup.SetFallbackFonts(so);
                so.FindProperty("panelMaterial").objectReferenceValue = MetaCodexSetup.GetOrCreateMaterial(MetaCodexSetup.PanelMaterialPath, "MetaCodex/PanelUnlit");
                so.ApplyModifiedPropertiesWithoutUndo();
                var term = new VtTerminal(30, 96, 2000);
                view.Attach(term);
                term.Feed("\x1b[1;36mroot@quest\x1b[0m:\x1b[1;34m/root/work/metacodex\x1b[0m# git status\r\nOn branch \x1b[32mmain\x1b[0m\r\nnothing to commit, working tree clean\r\n" +
                          "\x1b[1;36mroot@quest\x1b[0m:\x1b[1;34m/root\x1b[0m# claude\r\n\x1b[38;5;208m⏺\x1b[0m Ready. What should we build?\r\n  ⎿  /mnt/quest/scripts watched\r\n$ ");
                view.transform.position = head.position + new Vector3(-view.Width / 2f, view.Height / 2f - 0.12f, 0.85f);

                var shellGo = new GameObject("Shell");
                shellGo.transform.SetParent(root.transform, false);
                shell = shellGo.AddComponent<MetaCodexShell>();
                var symbols = MetaCodexSetup.FallbackFontPaths.Select(AssetDatabase.LoadAssetAtPath<Font>).ToArray();
                shell.Configure(null,
                    AssetDatabase.LoadAssetAtPath<Font>(MetaCodexSetup.DisplayFontPath),
                    AssetDatabase.LoadAssetAtPath<Font>(MetaCodexSetup.BodyFontPath), symbols,
                    MetaCodexSetup.GetOrCreateMaterial(MetaCodexSetup.ShapeMaterialPath, "MetaCodex/SciFiUI"),
                    MetaCodexSetup.GetOrCreateMaterial(MetaCodexSetup.BeamMaterialPath, "MetaCodex/Beam"),
                    Path.Combine(tmp, "settings.json"));
                shell.HeadOverride = head;
                var hands = new SimulatedHands();
                Progress("before the shell is built");
                shell.Build(scripts, hands);
                Progress("shell built");
                UiTheme.Current.Mono = view.Font;
                var frame = shell.WrapForTest(new TerminalInstance { Number = 1, Terminal = term, View = view });
                view.Render();
                Check(frame != null, "terminal wrapped in a frame panel");

                if (Want("glyphs")) Glyphs(shell);
                if (Want("panels")) Panels(shell, head, frame);
                if (Want("interactions")) Interactions(shell, head);
                if (Want("gestures")) Gestures(shell, hands);
                if (Want("scripts")) Scripts(shell, hands, scripts, tmp);
                if (Want("agentscript")) AgentScript(shell, hands, tmp);
                if (Want("services")) Services(shell);
                if (Want("layout")) Layout(shell, head, frame);
                if (Want("gestureplugins")) GesturePlugins(shell, hands, tmp);
                if (Want("agentplugin")) AgentPlugin(shell, hands, tmp);
                if (Want("vault")) Vault(shell);
                if (Want("links")) Links(shell, term);
                if (Want("lookandlayouts")) LookAndLayouts(shell, head);
                if (Want("daemon")) Daemon(shell);
                if (Want("agentsandthreads")) AgentsAndThreads(shell);
                if (Want("voicecontrol")) VoiceControl(shell);
                if (Want("fixes")) Fixes(shell, head, frame, term, view);
                if (Want("opacitybend")) OpacityAndBend(shell, head);
                if (Want("voicewords")) VoiceWords(shell);
                if (Want("spokenreplies")) SpokenRepliesSection(shell);
                if (Want("voicefx")) VoiceFxSection(shell);
                if (Want("streaming")) StreamingSection(shell);
                if (Want("decider")) Decider(shell);
                if (Want("views")) Views(shell, head, tmp);
                if (Want("workspace")) Workspace(shell, head);
                if (Want("threadgroups")) ThreadGroups(shell);
                if (Want("vaultgit")) VaultGit(shell);
                if (Want("focus")) Focus(shell, head, frame);
                if (Want("system")) SystemPanel(shell, tmp);
                if (Want("qol")) Qol(shell, head, hands);
                if (Want("wrist")) Wrist(shell, head, hands);
                if (Want("transitions")) Transitions(shell, head);
                if (Want("orb")) OrbSection(shell, head, hands);
                if (Want("look")) Look(shell, head);
                if (Want("placement")) Placement(shell, head);
                if (Want("perf")) Perf(shell, head, hands);
                if (Want("web")) WebSection(shell, head);
                if (Want("syskeyboard")) SystemKeyboardSection(shell);
                if (Want("agentget")) AgentGetSection(shell);
                if (Want("mqh")) MqhPanel(shell);
                if (Want("captures")) Captures(shell, tmp);
                if (Want("returns")) ReturnsSection(shell); // MetaCodexUiTestReturns.cs
                if (Want("context")) SessionContextSection(shell, tmp);
                if (Want("chat")) ChatSection(shell, head); // MetaCodexUiTestChat.cs
                if (Want("control")) ControlSection(shell, head, frame); // MetaCodexUiTestControl.cs
                if (Want("workspaces")) WorkspacesSection(shell, head); // MetaCodexUiTestWorkspaces.cs
                if (Want("sidebar")) SidebarSection(shell, head, hands); // MetaCodexUiTestSidebar.cs
                if (Want("orbplace")) OrbPlaceSection(shell, head, hands); // MetaCodexUiTestOrbPlace.cs
                if (Want("cards")) CardsSection(shell, head); // MetaCodexUiTestCards.cs
                if (Want("roomanchor")) RoomAnchorSection(shell); // MetaCodexUiTestRoomAnchor.cs
                if (WantNamed("voiceexport")) VoiceExport(shell);
                if (WantNamed("voicedecide")) VoiceDecideBench(shell);
            }
            catch (Exception ex) { Fail("exception: " + ex); }
            finally
            {
                SystemBridge.DryRun = false;
                UiKeyboard.EditorProvider = null;
                SystemKeyboard.FakeForTest = false;
                UnityEngine.Object.DestroyImmediate(root);
            }

            string summary = (_ok ? "UI SELFTEST OK" : "UI SELFTEST FAILED") + "\n" + _report;
            File.WriteAllText(Path.Combine(dir, "ui-report.txt"), summary);
            if (!_ok) throw new Exception(summary);
            return summary.Split('\n')[0] + " (" + Path.Combine(dir, "ui-report.txt") + ")";
        }

        // ------------------------------------------------------------------ pieces

        private static void Glyphs(MetaCodexShell shell)
        {
            string needed = "▶▤◫⎇◎ƒ⚙⌨⌖⟲✕↑⌂▸·↪●◆◇◎▣⚠✓▌•↓−⟳✋⚿☰⇗⎘◈✎⇢⇣○■▲▼→⑂◧◐☑☐▾❚λ⚒⛁⇅⬢✦‹›✥✉⊞⇲◌";
            foreach (var (name, font) in new[] { ("display", UiTheme.Current.Display), ("body", UiTheme.Current.Body) })
            {
                font.HasCharacters(needed, out uint[] missing, true, true);
                Check(missing == null || missing.Length == 0, $"{name} font + symbol fallbacks cover the UI glyphs" +
                    (missing != null && missing.Length > 0 ? ": missing " + string.Join(" ", missing.Select(c => "U+" + c.ToString("X4"))) : ""));
            }
        }

        private static void Panels(MetaCodexShell shell, Transform head, UiPanel frame)
        {
            foreach (var id in new[] { "dock", "settings", "files", "git", "browser", "scripts", "terminals", "gestures", "agents", "threads", "vault" })
                Check(shell.OpenPanel(id), "panel opens: " + id);
            foreach (var p in shell.PanelsById.Values) p.ForceRefresh();
            frame.ForceRefresh();
            var open = shell.PanelsById.Values.Where(p => p.IsOpen && p.Id != "dock").ToList();
            int stacked = 0;
            for (int i = 0; i < open.Count; i++)
                for (int j = i + 1; j < open.Count; j++)
                    if (Vector3.Distance(open[i].transform.position, open[j].transform.position) < 0.03f) stacked++;
            Check(stacked == 0, $"{open.Count} open panels: none placed on top of another ({stacked} pairs within 3 cm)");
            Render(shell, "ui-overview.png", head.position, Quaternion.identity, 100f, 1920, 1080);
            foreach (var id in new[] { "settings", "files", "browser", "mqh", "terminals", "dock", "vault" })
            {
                if (MetaCodexShell.IsControlPage(id)) shell.OpenPanel(id); // CONTROL shows one category at a time
                RenderPanel(shell.PanelsById[id], "ui-" + id + ".png");
            }
            RenderPanel(frame, "ui-terminal.png");
        }

        private static void Click(UiInputRouter router, UiPanel panel, Vector2 panelPoint, Vector2? dragTo = null)
        {
            var world = panel.transform.TransformPoint(panel.PanelToLocal(panelPoint));
            var dir = panel.transform.forward;
            var p = new UiPointer { Id = 50, Kind = PointerKind.Test, Hand = "test", Active = true, Ray = new Ray(world - dir * 0.4f, dir) };
            router.Inject(p);
            p.Down = true;
            router.Inject(p);
            if (dragTo.HasValue)
            {
                var w2 = panel.transform.TransformPoint(panel.PanelToLocal(dragTo.Value));
                p.Ray = new Ray(w2 - dir * 0.4f, dir);
                router.Inject(p);
            }
            p.Down = false;
            router.Inject(p);
        }

        /// A pointer that moves over a panel point without pressing (hover only).
        private static void Hover(UiInputRouter router, UiPanel panel, Vector2 panelPoint)
        {
            var world = panel.transform.TransformPoint(panel.PanelToLocal(panelPoint));
            var dir = panel.transform.forward;
            var p = new UiPointer { Id = 51, Kind = PointerKind.Test, Hand = "test", Active = true, Ray = new Ray(world - dir * 0.4f, dir) };
            for (int i = 0; i < 3; i++) router.Inject(p);
            p.Active = false;
            router.Inject(p);
        }

        /// The owner's bug list of 0.4 (headset test): each fix checked.
        private static void Fixes(MetaCodexShell shell, Transform head, UiPanel frame, VtTerminal term, TerminalView view)
        {
            foreach (var id in shell.PanelIds.ToList()) shell.ClosePanel(id);
            // sliders move only when pinched, never by hovering
            shell.OpenPanel("settings");
            var st = shell.PanelsById["settings"];
            st.Elements.OfType<UiTabs>().First().Select(0);
            var slider = st.Elements.OfType<UiSlider>().First(sl => sl.Text == "PINCH AT");
            float before = slider.Value;
            Hover(shell.Router, st, new Vector2(slider.Rect.xMax - 0.01f, slider.Rect.center.y));
            Check(Mathf.Abs(slider.Value - before) < 1e-4f, $"hovering a slider leaves it alone ({before:F2} -> {slider.Value:F2})");
            Click(shell.Router, st, new Vector2(slider.Rect.xMax - 0.06f, slider.Rect.center.y), new Vector2(slider.Rect.xMax - 0.01f, slider.Rect.center.y));
            Check(slider.Value > before + 0.01f, $"pinch-dragging a slider moves it ({slider.Value:F2})");

            // the select tool can always be turned off: the dock clicks, a tap clicks through, one selection ends it
            shell.SetSelectionMode(true);
            var selBtn = st.Elements.OfType<UiButton>().First(b => b.Text.StartsWith("SELECT AREA"));
            Click(shell.Router, st, selBtn.Rect.center);
            Check(!shell.Router.SelectionMode, "select tool on: a tap on SELECT AREA (Settings) turns it off");
            shell.OpenPanel("dock");
            var dock = shell.PanelsById["dock"];
            shell.SetSelectionMode(true);
            var dockSel = dock.Elements.OfType<UiButton>().First(b => b.Text.EndsWith("SELECT"));
            Click(shell.Router, dock, dockSel.Rect.center);
            Check(!shell.Router.SelectionMode, "select tool on: SELECT on the dock turns it off");
            shell.OpenPanel("files");
            var files = shell.PanelsById["files"];
            shell.SetSelectionMode(true);
            Click(shell.Router, files, new Vector2(0.1f, 0.15f), new Vector2(0.25f, 0.25f));
            Check(!shell.Router.SelectionMode && shell.LastSelection.panel == "files", "one selection, then the select tool is off");

            // the dock moves by its grab bar
            var d0 = dock.transform.position;
            Click(shell.Router, dock, new Vector2(dock.Width * 0.5f, dock.HandleHeight * 0.5f), new Vector2(dock.Width * 0.5f + 0.12f, dock.HandleHeight * 0.5f));
            Check((dock.transform.position - d0).magnitude > 0.03f, $"the dock moves by its grab bar ({(dock.transform.position - d0).magnitude * 100f:F0} cm)");

            // terminals: hide keeps the shell, show brings it back where you look; the last one is hidden, not ended
            var inst = new TerminalInstance { Number = 9, Terminal = new VtTerminal(10, 40, 100), View = view };
            Check(frame != null && frame.IsOpen, "test terminal frame open");
            shell.HideTerminal(shell.FrameOwnerForTest(frame));
            Check(!frame.IsOpen, "– hides a terminal (its shell keeps running)");
            shell.ShowTerminal(shell.FrameOwnerForTest(frame));
            Check(frame.IsOpen, "TERM / SHOW brings it back");

            // panels bend towards you in the fixed layout too
            shell.SetLayout("fixed");
            shell.Settings.bendPanels = true;
            shell.UpdateFixedBend();
            Check(files.BendRadius > 0.3f && frame.BendRadius == 0f, $"fixed layout bends panels ({files.BendRadius:F2} m), terminals stay flat");
            shell.Settings.bendPanels = false;
            shell.UpdateFixedBend();
            Check(files.BendRadius == 0f, "bend off: flat again");
            shell.Settings.bendPanels = true;

            // float: many panels never overlap (the circle grows instead)
            foreach (var id in new[] { "settings", "files", "git", "browser", "scripts", "terminals", "gestures", "agents", "threads", "vault", "voice" }) shell.OpenPanel(id);
            shell.SetLayout("float");
            shell.TickLayoutForTest(0.05f, 120);
            var c = shell.CarouselCenter;
            var open = shell.PanelsById.Values.Where(p => p.IsOpen && p.Id != "dock").Concat(new[] { frame }).ToList();
            int overlaps = 0;
            string worst = "";
            for (int a = 0; a < open.Count; a++)
                for (int b = a + 1; b < open.Count; b++)
                {
                    var pa = open[a]; var pb = open[b];
                    if (Mathf.Abs(pa.transform.position.y - pb.transform.position.y) > (pa.Height * pa.transform.lossyScale.y + pb.Height * pb.transform.lossyScale.y) * 0.5f) continue;
                    var da = pa.transform.position - c; var db = pb.transform.position - c;
                    float r = new Vector2(da.x, da.z).magnitude;
                    float angle = Mathf.Abs(Mathf.DeltaAngle(Mathf.Atan2(da.x, da.z) * Mathf.Rad2Deg, Mathf.Atan2(db.x, db.z) * Mathf.Rad2Deg));
                    float need = (pa.Width * pa.transform.lossyScale.x + pb.Width * pb.transform.lossyScale.x) * 0.5f / r * Mathf.Rad2Deg;
                    if (angle < need - 0.5f) { overlaps++; worst = $"{pa.Id}/{pb.Id} {angle:F0}° < {need:F0}°"; }
                }
            Check(overlaps == 0, $"float layout with {open.Count} panels: no overlaps {worst}");
            shell.Spin(0f, 0f);
            Render(shell, "ui-float-many.png", head.position, Quaternion.identity, 100f, 1600, 900);
            shell.SetLayout("fixed");
            foreach (var id in shell.PanelIds.ToList()) shell.ClosePanel(id);
        }

        private static void Interactions(MetaCodexShell shell, Transform head)
        {
            var r = shell.Router;
            // only the panels under test, so no other panel sits between the test ray and its target
            foreach (var id in shell.PanelIds.ToList()) if (id != "settings" && id != "files" && id != MetaCodexShell.ControlId) shell.ClosePanel(id);
            shell.OpenPanel("settings"); // with CONTROL's rail on its left (its own tab bar is gone)
            var settings = shell.PanelsById["settings"];
            var rail = shell.ControlRail;
            var tabs = settings.Elements.OfType<UiTabs>().First();
            Click(r, rail, shell.RailButtonForTest("display").Rect.center);
            Check(tabs.Selected == 1, "a tap on the rail's DISPLAY switches SETTINGS to DISPLAY");
            settings.ForceRefresh();
            RenderPanel(settings, "ui-settings-display.png");
            Click(r, rail, shell.RailButtonForTest("look").Rect.center);
            Check(tabs.Names[tabs.Selected] == "LOOK", "the rail's LOOK & SOUND switches to LOOK");
            var fov = settings.Elements.OfType<UiToggle>().First(t => t.Text.StartsWith("Foveated"));
            bool before = fov.Value;
            Click(r, settings, fov.Rect.center);
            Check(fov.Value != before && shell.Settings.foveation == fov.Value, "toggle flips Foveated rendering and the setting");
            settings.ForceRefresh();
            RenderPanel(settings, "ui-settings-look.png");
            Click(r, settings, fov.Rect.center); // back off

            Click(r, rail, shell.RailButtonForTest("hands").Rect.center);
            var click = settings.Elements.OfType<UiSegmented>().First(sg => sg.Text == "CLICK");
            var w = click.Rect;
            float segW = (w.xMax - (w.x + click.LabelWidth)) / 3f;
            Click(r, settings, new Vector2(w.x + click.LabelWidth + segW * 1.5f, w.center.y));
            Check(shell.Settings.gestures.clickMode == 1, "segmented control sets click mode = POKE");
            Click(r, settings, new Vector2(w.x + click.LabelWidth + segW * 2.5f, w.center.y));
            var pinch = settings.Elements.OfType<UiSlider>().First(sl => sl.Text == "PINCH AT");
            var track = new Rect(pinch.Rect.x + pinch.LabelWidth, pinch.Rect.y, pinch.Rect.width - pinch.LabelWidth - 0.06f, pinch.Rect.height);
            Click(r, settings, new Vector2(track.x + track.width * 0.9f, pinch.Rect.center.y), new Vector2(track.x + track.width * 0.25f, pinch.Rect.center.y));
            Check(Mathf.Abs(pinch.Value - Mathf.Lerp(0.6f, 0.98f, 0.25f)) < 0.03f, $"slider drag sets pinch threshold ({pinch.Value:F2})");

            // the link to MQH is MQH > STATUS now (it was a Settings tab; its id is still daemon)
            shell.ClosePanel("settings");
            shell.OpenPanel("daemon");
            var mqhPanel = shell.PanelsById["mqh"];
            mqhPanel.ForceRefresh();
            UiKeyboard.EditorProvider = old => "http://127.0.0.1:38990";
            var url = mqhPanel.Elements.OfType<UiTextField>().First(f => f.Caption == "MQH URL");
            Click(r, mqhPanel, url.Rect.center);
            Check(shell.Settings.mqhUrl == "http://127.0.0.1:38990", "text field edit (keyboard) updates the MQH URL (MQH > STATUS)");
            shell.ClosePanel("mqh");
            shell.OpenPanel("settings"); // the steps below move it by its header
            Check(File.Exists(Path.Combine("Temp", "mcx-uitest", "settings.json")) && File.ReadAllText(Path.Combine("Temp", "mcx-uitest", "settings.json")).Contains("38990"), "settings persisted to disk");

            // files: a list row navigates. FILES must be open: a closed panel takes no ray (UiInputRouter skips it), and
            // this section also runs without "panels" (MCX_UITEST_ONLY=...,interactions), which was what opened it
            shell.OpenPanel("files");
            var files = shell.PanelsById["files"];
            shell.FilesNavigate("/");
            var list = files.Elements.OfType<UiList>().First();
            int unity = list.Items.ToList().FindIndex(i => i.Title == "unity/");
            Check(unity >= 0 && list.Items.Any(i => i.Title == "mqh/"), "files lists the repo root (unity/, mqh/)");
            if (unity >= 0 && unity < 12)
            {
                Click(r, files, new Vector2(list.Rect.x + 0.1f, list.Rect.y + (unity + 0.5f) * list.RowHeight));
                Check(files.Elements.OfType<UiLabel>().Any(l => l.Text == "/unity"), "tapping a folder opens it");
            }
            files.ForceRefresh();
            RenderPanel(files, "ui-files.png");

            // selection tool: rectangle on a panel -> event to scripts (before SETTINGS is dragged below, so it cannot
            // be moved over FILES' corner)
            shell.SetSelectionMode(true);
            int selects = 0;
            r.Selected += (p, rect) => selects++;
            Click(r, files, new Vector2(0.10f, 0.15f), new Vector2(0.30f, 0.30f));
            shell.SetSelectionMode(false);
            Check(selects == 1 && shell.LastSelection.panel == "files" && shell.LastSelection.rect.width > 0.15f,
                $"select tool reports a {shell.LastSelection.rect.width * 100:F0}x{shell.LastSelection.rect.height * 100:F0} cm area on {shell.LastSelection.panel ?? "nothing"}");

            // move a panel by pinching its header
            var before2 = settings.transform.position;
            Click(r, settings, new Vector2(0.2f, 0.015f), new Vector2(0.35f, 0.06f));
            Check(Vector3.Distance(settings.transform.position, before2) > 0.05f, "pinch-drag on the header moves the panel");
        }

        private static void Gestures(MetaCodexShell shell, SimulatedHands hands)
        {
            var g = shell.Gestures;
            float t = 10f;
            void Tick() { g.Tick(t, 1f / 60f); t += 1f / 60f; }
            var palm = new Vector3(0.2f, 1.2f, 0.35f);
            hands.SetOpen(Handedness.Right, palm, Vector3.forward, Vector3.down, 0f, 0f);
            Tick();
            Check(g.Right.Open && !g.Right.Pinching && !g.Right.Grabbing, "open hand recognised");
            hands.SetOpen(Handedness.Right, palm, Vector3.forward, Vector3.down, 0f, 1f);
            Tick();
            Check(g.Right.Pinching, "pinch recognised (OVR pinch strength)");
            hands.SetOpen(Handedness.Right, palm, Vector3.forward, Vector3.down, 1f, 0f);
            Tick();
            Check(g.Right.Grabbing && !g.Right.Pinching, "closed hand (fist) recognised");
            hands.SetOpen(Handedness.Right, palm, Vector3.forward, Vector3.down, 1f, 0f);
            var f = hands.Frames[(int)Handedness.Right];
            f.IndexTip = f.IndexKnuckle + (f.IndexKnuckle - f.Wrist).normalized * 0.075f; // index straight, others curled
            hands.Frames[(int)Handedness.Right] = f;
            Tick();
            Check(g.Right.Pointing, "point pose recognised");
            hands.SetOpen(Handedness.Right, palm, Vector3.forward, Vector3.up, 0f, 0f);
            Tick();
            Check(g.Right.PalmUp, "palm-up recognised");
            hands.Hide(Handedness.Right);
            Tick();
            Check(!g.Right.Tracked, "hand lost ends its gestures");

            // a real clap often loses tracking right at contact (the hands occlude each other): still a clap
            int claps = 0;
            System.Action<GestureEvent> onClap = e => { if (e.Name == "clap") claps++; };
            g.OnGesture += onClap;
            try
            {
                var c = new Vector3(0f, 1.25f, 0.35f);
                for (float half = 0.16f; half > 0.045f; half -= 0.012f) // fast approach (~0.7 m/s per palm)
                {
                    hands.SetOpen(Handedness.Left, c + Vector3.left * half, Vector3.forward, Vector3.right);
                    hands.SetOpen(Handedness.Right, c + Vector3.right * half, Vector3.forward, Vector3.left);
                    Tick();
                }
                hands.Hide(Handedness.Left);
                hands.Hide(Handedness.Right);
                Tick(); Tick();
                Check(claps == 1, $"clap when both hands vanish at contact ({claps} claps)");
                for (int i = 0; i < 60; i++) Tick(); // hands gone for a second: one clap only, re-armed after
                Check(claps == 1, "no second clap while the hands stay lost");
            }
            finally { g.OnGesture -= onClap; }
        }

        /// Two claps: palms approach fast and touch, separate, touch again.
        private static void Claps(MetaCodexShell shell, SimulatedHands hands, ref float t, int count, float gap = 0.35f)
        {
            var c = new Vector3(0f, 1.25f, 0.35f);
            void Pose(float half)
            {
                hands.SetOpen(Handedness.Left, c + Vector3.left * half, Vector3.forward, Vector3.right);
                hands.SetOpen(Handedness.Right, c + Vector3.right * half, Vector3.forward, Vector3.left);
            }
            for (int k = 0; k < count; k++)
            {
                float start = t;
                while (t < start + gap)
                {
                    float u = (t - start) / gap;            // 0..1: apart -> together -> apart
                    float half = Mathf.Lerp(0.16f, 0.02f, 1f - Mathf.Abs(u * 2f - 1f));
                    Pose(half);
                    shell.Gestures.Tick(t, 1f / 60f);
                    shell.Scripts.Tick(t);
                    t += 1f / 60f;
                }
            }
            Pose(0.16f);
            shell.Gestures.Tick(t, 1f / 60f);
        }

        private static void Scripts(MetaCodexShell shell, SimulatedHands hands, string dir, string tmp)
        {
            var host = shell.Scripts;
            foreach (var id in shell.PanelIds.ToList()) shell.ClosePanel(id);
            int clapsSeen = 0;
            shell.Gestures.OnGesture += e => { if (e.Name == "clap") clapsSeen++; };

            // 1. the script an agent writes for "if I double clap, open the files panel"
            File.WriteAllText(Path.Combine(dir, "double-clap.lua"),
@"-- description: double clap opens the files panel
gesture.sequence({""clap"", ""clap""}, 0.8, function(e)
  ui.toast(""double clap!"")
  ui.open(""files"")
end)
");
            host.Scan();
            var entry = host.Entries.FirstOrDefault(e => e.Name == "double-clap");
            Check(entry != null && entry.Error == null && entry.Handlers == 1, "script loaded from disk without a rebuild" + (entry?.Error != null ? ": " + entry.Error : ""));
            float t = 100f;
            Claps(shell, hands, ref t, 1);
            Check(clapsSeen == 1 && !shell.PanelsById["files"].IsOpen, "one clap: recognised, nothing happens");
            t += 1.5f;
            Claps(shell, hands, ref t, 2);
            Check(clapsSeen == 3, $"claps recognised from simulated hands ({clapsSeen})");
            Check(shell.PanelsById["files"].IsOpen, "double clap ran the script: files panel open");
            Check(shell.Toasts.Last == "double clap!", "script toast shown");

            // 2. live edit: same file, new behaviour, no restart
            shell.ClosePanel("files");
            File.WriteAllText(Path.Combine(dir, "double-clap.lua"), "gesture.sequence({'clap','clap'}, 0.8, function() ui.open('git') end)\n");
            File.SetLastWriteTimeUtc(Path.Combine(dir, "double-clap.lua"), DateTime.UtcNow.AddSeconds(5));
            host.Scan();
            t += 1.5f;
            Claps(shell, hands, ref t, 2);
            Check(shell.IsPanelOpen("git") && !shell.PanelsById["files"].IsOpen, "edited script reloaded live: double clap now opens git");

            // 3. a runaway script is stopped, the others keep working
            File.WriteAllText(Path.Combine(dir, "bad.lua"), "gesture.on('clap', function() while true do end end)\n");
            host.Scan();
            shell.ClosePanel("git");
            t += 1.5f;
            var sw = System.Diagnostics.Stopwatch.StartNew();
            Claps(shell, hands, ref t, 2);
            var bad = host.Entries.First(e => e.Name == "bad");
            Check(bad.Error != null && bad.Error.Contains("instructions"), $"endless loop stopped by the instruction budget in {sw.ElapsedMilliseconds} ms");
            Check(shell.IsPanelOpen("git"), "other scripts unaffected");

            // 4. state that survives a restart: per-script store + enabled flags
            File.WriteAllText(Path.Combine(dir, "counter.lua"), "gesture.on('clap', function() store.set('n', tostring((tonumber(store.get('n')) or 0) + 1)) end)\n");
            host.Scan();
            host.SetEnabled("bad", false);
            t += 1.5f;
            Claps(shell, hands, ref t, 3, 0.5f);
            var host2 = new ScriptHost(shell, dir, Path.Combine(tmp, "no-plugins-2"));
            host2.Scan();
            string state = File.ReadAllText(Path.Combine(dir, ".state.json"));
            Check(state.Contains("\"n\"") && state.Contains("\"3\""), "script store persisted (3 claps counted)");
            Check(host2.Entries.First(e => e.Name == "bad").Enabled == false, "disabled flag persisted across a restart");
            Check(host.Eval("return ui.panels and #ui.panels() > 5") == "true", "eval sees the panel API");

            var sp = shell.PanelsById["mqh"]; // SCRIPTS is a tab of MQH
            shell.OpenPanel("scripts");
            sp.ForceRefresh();
            RenderPanel(sp, "ui-scripts.png");
        }

        /// A script generated by a real agent from the skill (tools/agent-script-test.sh puts it here).
        private static void AgentScript(MetaCodexShell shell, SimulatedHands hands, string tmp)
        {
            string src = Path.GetFullPath(Path.Combine("Bench", "agent", "scripts"));
            if (!Directory.Exists(src) || Directory.GetFiles(src, "*.lua").Length == 0) { Line("skip agent script: none in Bench/agent/scripts (tools/agent-script-test.sh)"); return; }
            string dir = Path.Combine(tmp, "agent-scripts");
            Directory.CreateDirectory(dir);
            foreach (var f in Directory.GetFiles(src, "*.lua")) File.Copy(f, Path.Combine(dir, Path.GetFileName(f)), true);
            var host = new ScriptHost(shell, dir, Path.Combine(tmp, "no-plugins-agent"));
            host.Scan();
            Check(host.Entries.Count > 0 && host.Entries.All(e => e.Error == null), $"agent script(s) load: {string.Join(", ", host.Entries.Select(e => e.Name + (e.Error != null ? " ERROR " + e.Error : "")))}");
            foreach (var id in shell.PanelIds.ToList()) shell.ClosePanel(id);
            // route gestures to the agent's host only
            Action<GestureEvent> d = host.Dispatch;
            shell.Gestures.OnGesture += d;
            var old = shell.Scripts.Entries.Select(e => e.Name).ToList();
            foreach (var n in old) shell.Scripts.SetEnabled(n, false);
            float t = 300f;
            Claps(shell, hands, ref t, 2);
            shell.Gestures.OnGesture -= d;
            foreach (var n in old) shell.Scripts.SetEnabled(n, true);
            string expect = File.Exists(Path.Combine(src, "EXPECT")) ? File.ReadAllText(Path.Combine(src, "EXPECT")).Trim() : "files";
            Check(shell.PanelsById.TryGetValue(expect, out var p) && p.IsOpen, $"agent-written script: double clap opened '{expect}'");
        }

        private static void Services(MetaCodexShell shell)
        {
            string repo = GitService.ToLinuxPath(Path.GetFullPath(".."));
            var st = shell.Git.Status(repo).GetAwaiter().GetResult();
            Check(st.Error == null && !string.IsNullOrEmpty(st.Branch) && st.Log.Count >= 3, $"git via WSL: branch {st.Branch}, {st.Log.Count} commits, {st.Entries.Count} changed, {st.Branches.Count} branches" + (st.Error != null ? " ERROR " + st.Error : ""));
            var repos = shell.Git.FindRepos(GitService.ToLinuxPath(Path.GetFullPath("..")), 1).GetAwaiter().GetResult();
            Check(repos.Count >= 1, "git repos found: " + string.Join(", ", repos));
            shell.OpenPanel("git");
            var gp = shell.PanelsById["vault"]; // GIT lives in the vault now
            typeof(MetaCodexShell).GetMethod("GitShow", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance).Invoke(shell, new object[] { st });
            gp.ForceRefresh();
            RenderPanel(gp, "ui-git.png");
        }

        private static void Layout(MetaCodexShell shell, Transform head, UiPanel frame)
        {
            // its own windows only: with everything a section before left open the ring grows past the chosen radius
            // (seen when this section ran right after `panels`: 1.39 m instead of 0.95)
            foreach (var id in shell.PanelIds.ToList()) shell.ClosePanel(id);
            foreach (var id in new[] { "dock", "settings", "files", "git", "scripts", "browser" }) shell.OpenPanel(id);
            shell.SetLayout("float");
            shell.TickLayoutForTest(0.05f, 80);
            var c = shell.CarouselCenter;
            float r = shell.Settings.carouselRadius;
            bool onCircle = true;
            foreach (var id in new[] { "settings", "files", "vault" })
            {
                var p = shell.PanelsById[id].transform.position;
                float d = new Vector2(p.x - c.x, p.z - c.z).magnitude;
                if (Mathf.Abs(d - r) > 0.02f) { onCircle = false; Line($"     {id} at {d:F3} m from the centre"); }
            }
            Check(onCircle, $"float layout: panels on the carousel (r = {r:F2} m)");
            Check(Mathf.Abs(shell.PanelsById["files"].BendRadius - r) < 0.01f && frame.BendRadius == 0f, "panels bend to the carousel; terminals stay flat");
            foreach (var p in shell.PanelsById.Values) { p.ForceRefresh(); p.ApplyBendNow(); }
            frame.ForceRefresh();
            Render(shell, "ui-float.png", head.position, Quaternion.identity, 110f, 1920, 1080);
            var before = shell.PanelsById["files"].transform.position;
            shell.Spin(60f, 0f);
            shell.TickLayoutForTest(0.05f, 80);
            var after = shell.PanelsById["files"].transform.position;
            float turned = Vector2.SignedAngle(new Vector2(before.x - c.x, before.z - c.z), new Vector2(after.x - c.x, after.z - c.z));
            Check(Mathf.Abs(Mathf.Abs(turned) - 60f) < 3f, $"carousel spins without snapping ({turned:F1}°)");
            head.position += new Vector3(0.5f, 0.15f, 0.3f); // walk and nod: follow x/z only, past the dead zone
            shell.TickLayoutForTest(0.05f, 120);
            var c2 = shell.CarouselCenter;
            float gap = new Vector2(c2.x - head.position.x, c2.z - head.position.z).magnitude;
            Check(gap <= MetaCodexShell.RigDeadZone + 0.02f && gap > MetaCodexShell.RigDeadZone - 0.05f && Mathf.Abs(c2.y - c.y) < 1e-4f,
                $"carousel follows walking (x/z) past its {MetaCodexShell.RigDeadZone:F2} m dead zone, not head height ({gap:F2} m behind)");
            var toRing = new Vector3(c2.x - head.position.x, 0f, c2.z - head.position.z).normalized * 0.08f;
            head.position += toRing; // a small step (towards the ring): the windows stay
            shell.TickLayoutForTest(0.05f, 120);
            Check(Vector3.Distance(c2, shell.CarouselCenter) < 0.005f, "a step of a few centimetres does not move the ring");
            shell.Settings.bendPanels = false;
            shell.SetLayout("fixed");
            Check(shell.PanelsById["files"].BendRadius == 0f, "fixed layout with bend off flattens the panels");
            shell.Settings.bendPanels = true;
        }

        private static void Daemon(MetaCodexShell shell)
        {
            var mqh = shell.Mqh;
            mqh.BaseUrl = "http://127.0.0.1:38990";
            JToken version;
            try { version = mqh.Send(HttpMethod.Get, "/api/v1/system/version").GetAwaiter().GetResult(); }
            catch (Exception ex) { Line("skip MQH (not running on 127.0.0.1:38990: tools/mqh-dev.sh start): " + ex.Message); return; }
            Check(version != null, "MQH reachable: " + version.ToString(Newtonsoft.Json.Formatting.None).Substring(0, Math.Min(80, version.ToString().Length)));
            var plugins = mqh.Send(HttpMethod.Get, "/api/v1/plugins").GetAwaiter().GetResult()["plugins"] as JArray;
            Check(plugins != null && plugins.Count >= 10, $"MQH plugins listed: {plugins?.Count}");
            var prefs = mqh.Send(HttpMethod.Get, "/api/v1/preferences/ui").GetAwaiter().GetResult();
            int rev = prefs?["preferences"]?["quest.settings"]?["revision"]?.Value<int>() ?? 0;
            var value = shell.Settings.ToJObject();
            value["accent"] = "amber";
            mqh.Send(new HttpMethod("PUT"), "/api/v1/preferences/ui/quest.settings", new JObject { ["expectedRevision"] = rev, ["value"] = value }).GetAwaiter().GetResult();
            var back = mqh.Send(HttpMethod.Get, "/api/v1/preferences/ui").GetAwaiter().GetResult();
            Check((string)back?["preferences"]?["quest.settings"]?["value"]?["accent"] == "amber", "quest.settings stored in MQH and read back (agents can edit it)");
            value["accent"] = "cyan";
            int rev2 = back?["preferences"]?["quest.settings"]?["revision"]?.Value<int>() ?? rev + 1;
            mqh.Send(new HttpMethod("PUT"), "/api/v1/preferences/ui/quest.settings", new JObject { ["expectedRevision"] = rev2, ["value"] = value }).GetAwaiter().GetResult();
        }


        // ------------------------------------------------------------------ gesture plugins

        private static int CountEvents(MetaCodexShell shell, string name, string phase, Action act)
        {
            int n = 0;
            Action<GestureEvent> h = e => { if (e.Name == name && (phase == null || e.Phase == phase)) n++; };
            shell.Gestures.OnGesture += h;
            try { act(); } finally { shell.Gestures.OnGesture -= h; }
            return n;
        }

        /// A fast move of an open right hand (fingers up, palm `facing`) with velocity `v` (m/s, world) for `secs`.
        private static void Move(MetaCodexShell shell, SimulatedHands hands, Vector3 start, Vector3 v, Vector3 facing, float secs)
        {
            const float dt = 1f / 72f;
            var p = start;
            for (float t = 0f; t < secs; t += dt)
            {
                hands.SetOpen(Handedness.Right, p, Vector3.up, facing);
                shell.TickGesturesForTest(dt, dt);
                p += v * dt;
            }
        }

        private static void GesturePlugins(MetaCodexShell shell, SimulatedHands hands, string tmp)
        {
            var host = shell.Scripts;
            host.Scan();
            // a clean start: only core-poses, for the one default binding (the V toggles Ambient Focus, EnsureVictoryFocus,
            // owner 2026-11-14); no other example; the one-time cleanup marked the folder (a later + ADD is never cleaned up)
            Check(host.Find("plugin/core-poses") != null && Directory.Exists(Path.Combine(host.PluginsDir, "core-poses"))
                  && host.Find("plugin/swipes") == null && !Directory.Exists(Path.Combine(host.PluginsDir, "swipes"))
                  && shell.Settings.bindings.Count(b => b.gesture == "victory" && b.action == "focus") == 1,
                "fresh start: only core-poses, for the V -> Ambient Focus binding; no other gesture example");
            Check(File.Exists(Path.Combine(host.PluginsDir, MetaCodexShell.ExamplesMarker)), "fresh start: the cleanup marker " + MetaCodexShell.ExamplesMarker + " is written");
            shell.SetGestureExamplesOpen(false);
            var rows = shell.GestureRows();
            int sys = rows.FindIndex(r => r.Title.StartsWith("SYSTEM")), exh = rows.FindIndex(r => r.Title.StartsWith("EXAMPLES"));
            Check(rows.Count > 0 && rows[0].Title.StartsWith("YOURS") && sys > 0 && exh == sys + 8 && rows.Skip(sys + 1).Take(7).Select(r => r.Title).SequenceEqual(GestureEngine.Names),
                $"ALL: YOURS, SYSTEM (7 built-ins: {string.Join(" ", rows.Skip(sys + 1).Take(7).Select(r => r.Title + "=" + r.Sub))}), EXAMPLES");
            Check(exh == rows.Count - 1 && rows[exh].Icon == "▸", "ALL: EXAMPLES folded on a fresh start (nothing under it)");
            Check(rows[0].Title == "YOURS  (0)" && rows[1].Title.StartsWith("none"), "ALL: YOURS empty shows one hint row: " + (rows.Count > 1 ? rows[1].Title : "-"));
            Check(!rows.Any(r => r.Title.Contains("REFRESH")), "ALL: no ⟳ REFRESH");
            shell.SetGestureExamplesOpen(true);
            rows = shell.GestureRows();
            Check(rows.Count(r => r.Badge == "+ ADD") == 1 && rows.Any(r => r.Title == "core-poses" && r.Badge != "+ ADD") && rows.Any(r => r.Title == "swipes" && r.Badge == "+ ADD"),
                "EXAMPLES unfolded: core-poses already added (the V), swipes with + ADD");
            Check(shell.InstallGestureExample("no-such") != null, "+ ADD of an unknown example: an error, nothing written");
            Check(shell.InstallGestureExample("core-poses") == null, "+ ADD core-poses installs it");
            string viaPort = shell.ControlCommand("plugin", "example swipes");
            Check(viaPort.StartsWith("ok"), "control port: plugin example swipes → " + viaPort);
            Check(shell.ControlCommand("plugin", "examples").Contains("core-poses installed=True"), "control port: plugin examples lists them as installed");
            rows = shell.GestureRows();
            int core1 = rows.FindIndex(r => r.Title == "core-poses");
            Check(core1 >= 0 && rows[core1].Badge == "ADDED" && rows.Skip(core1 + 1).Take(5).All(r => r.Indent == 1) && rows.Count(r => r.Title == "thumbs-up") == 1 && rows[0].Title == "YOURS  (0)",
                "an added, untouched example lists its gestures under EXAMPLES, not YOURS");
            var core = host.Find("plugin/core-poses");
            var swipes = host.Find("plugin/swipes");
            Check(core != null && core.Error == null && core.Poses == 5, $"example core-poses loaded ({core?.Poses} poses{(core?.Error != null ? ", " + core.Error : "")})");
            Check(swipes != null && swipes.Error == null && swipes.Detectors == 3, $"example swipes loaded ({swipes?.Detectors} Lua detectors{(swipes?.Error != null ? ", " + swipes.Error : "")})");
            GestureExamplesCleanup(tmp);
            // SCRIPTS lists scripts only (the examples just added are plugins: MQH > PLUGINS), or one empty line
            var scriptRows = shell.ScriptRowsForTest;
            int nScripts = host.Entries.Count(e => e.Kind == "script");
            Check(scriptRows != null && (nScripts == 0 ? scriptRows.Count == 1 && scriptRows[0].Title == MetaCodexShell.ScriptsEmptyText && scriptRows[0].Tag == null
                                                       : scriptRows.Count == nScripts && scriptRows.All(r => r.Tag is ScriptHost.Entry e && e.Kind == "script")),
                $"SCRIPTS: only the {nScripts} script(s), no plugin rows" + (nScripts == 0 ? ", empty line shown" : ""));
            foreach (var id in shell.PanelIds.ToList()) shell.ClosePanel(id);
            hands.Hide(Handedness.Left);
            hands.Hide(Handedness.Right);
            shell.TickGesturesForTest(0.1f);

            int ups = CountEvents(shell, "thumbs-up", "start", () => { shell.PoseHand(Handedness.Right, 0, 1, 1, 1, 1, Vector3.up); shell.TickGesturesForTest(0.4f); });
            Check(ups == 1 && shell.Gestures.IsActive("thumbs-up", "right"), "pose from a plugin file: thumbs-up recognised once (after its hold)");
            var g = shell.Gestures.Right;
            Line($"     thumbs-up hand: curls {string.Join(" ", g.Fingers.Select(f => f.ToString("F2")))} thumb {shell.Gestures.Nearest(g.ThumbDir, g.Frame.Palm)} palm {shell.Gestures.Nearest(g.PalmNormal, g.Frame.Palm)}");
            int downs = CountEvents(shell, "thumbs-down", "start", () => { shell.PoseHand(Handedness.Right, 0, 1, 1, 1, 1, Vector3.down); shell.TickGesturesForTest(0.4f); });
            Check(downs == 1 && !shell.Gestures.IsActive("thumbs-up", "right"), "thumbs-down recognised, thumbs-up ended");
            int vic = CountEvents(shell, "victory", "start", () => { shell.PoseHand(Handedness.Right, 1, 0, 0, 1, 1); shell.TickGesturesForTest(0.4f); });
            Check(vic == 1, "victory recognised");
            int none = CountEvents(shell, "victory", "start", () => { shell.PoseHand(Handedness.Right, 1, 0, 0, 0, 0); shell.TickGesturesForTest(0.4f); });
            Check(none == 0, "an open hand is not victory");

            hands.Hide(Handedness.Right);
            shell.TickGesturesForTest(0.2f);
            var palm = shell.Gestures.Head.position + new Vector3(0.2f, -0.25f, 0.35f);
            int left = CountEvents(shell, "swipe-left", "start", () => Move(shell, hands, palm, new Vector3(-1.6f, 0f, 0f), Vector3.forward, 0.3f));
            Check(left == 1, "Lua detector: a fast open-hand move to the left is swipe-left");
            int slow = CountEvents(shell, "swipe-left", "start", () => Move(shell, hands, palm, new Vector3(-0.4f, 0f, 0f), Vector3.forward, 0.5f));
            Check(slow == 0, "a slow move is not a swipe");
            Check(shell.Scripts.DetectMs < 3.0, $"Lua detectors cost {shell.Scripts.DetectMs:F3} ms per pass");

            // a plugin written to disk, as an agent would: a Lua detector + reaction
            var push = shell.Scripts.SavePlugin("push-close", new System.Collections.Generic.Dictionary<string, string>
            {
                ["plugin.json"] = "{ \"id\": \"push-close\", \"name\": \"Push to close\", \"version\": \"1.0.0\", \"description\": \"push an open palm away to close git\" }",
                ["main.lua"] = "gesture.define('push', { hand = 'any', hold = 0, cooldown = 0.8 }, function(h) return h.open and h.palmFacing == 'away' and h.vel.z > 1.0 end)\n" +
                               "gesture.on('push', function(e) if e.phase == 'start' then ui.close('git'); ui.toast('pushed ' .. e.hand) end end)\n",
            });
            Check(push.Error == null && push.Detectors == 1 && push.Handlers == 1, "plugin saved and hot-loaded (1 detector, 1 handler)" + (push.Error != null ? ": " + push.Error : ""));
            shell.OpenPanel("git");
            hands.Hide(Handedness.Right);
            shell.TickGesturesForTest(0.2f);
            Move(shell, hands, palm, new Vector3(0f, 0f, 1.5f), Vector3.forward, 0.25f);
            Check(!shell.IsPanelOpen("git") && shell.Toasts.Last == "pushed right", "push detector closed git (reaction ran)");

            // no-code binding
            shell.AddBinding("victory", "open:vault");
            hands.Hide(Handedness.Right);
            shell.TickGesturesForTest(0.2f);
            shell.PoseHand(Handedness.Right, 1, 0, 0, 1, 1);
            shell.TickGesturesForTest(1.0f);
            bool early = shell.PanelsById["vault"].IsOpen;
            shell.TickGesturesForTest(1.3f);
            Check(!early && shell.PanelsById["vault"].IsOpen, $"binding victory → open:vault works only once the pose is held {MetaCodexShell.BindingHold:F0} s (open after 1 s: {early})");
            Check(File.ReadAllText(Path.Combine("Temp", "mcx-uitest", "settings.json")).Contains("open:vault"), "binding persisted in the settings");

            // record a pose from the hand and recognise it
            shell.PoseHand(Handedness.Right, 0, 0, 1, 1, 1, Vector3.up); // finger gun
            shell.TickGesturesForTest(0.1f);
            var rec = shell.RecordPoseNow(Handedness.Right, "finger-gun");
            Check(rec != null && File.Exists(Path.Combine(shell.Scripts.PluginsDir, "my-poses", "poses", "finger-gun.json")), "RECORD saved my-poses/poses/finger-gun.json: " + rec?.Summary());
            hands.Hide(Handedness.Right);
            shell.TickGesturesForTest(0.3f);
            int guns = CountEvents(shell, "finger-gun", "start", () => { shell.PoseHand(Handedness.Right, 0, 0, 1, 1, 1, Vector3.up); shell.TickGesturesForTest(0.5f); });
            Check(guns == 1, "the recorded pose is recognised");
            var yoursRows = shell.GestureRows();
            int sysAt = yoursRows.FindIndex(r => r.Title.StartsWith("SYSTEM"));
            Check(yoursRows.Take(sysAt).Any(r => (r.Tag as string) == "finger-gun") && yoursRows.Take(sysAt).Any(r => (r.Tag as string) == "push"), "ALL > YOURS lists the recorded pose and the saved plugin's gesture");

            // a runaway detector stops only its plugin
            var bad = shell.Scripts.SavePlugin("bad-detector", new System.Collections.Generic.Dictionary<string, string>
            {
                ["plugin.json"] = "{ \"id\": \"bad-detector\", \"name\": \"bad\" }",
                ["main.lua"] = "gesture.define('spin', { hand = 'any' }, function(h) while true do end end)\n",
            });
            shell.TickGesturesForTest(0.2f);
            Check(bad.Error != null && bad.Error.Contains("instructions"), "runaway detector stopped: " + bad.Error);
            int still = CountEvents(shell, "thumbs-up", "start", () => { hands.Hide(Handedness.Right); shell.TickGesturesForTest(0.2f); shell.PoseHand(Handedness.Right, 0, 1, 1, 1, 1, Vector3.up); shell.TickGesturesForTest(0.4f); });
            Check(still == 1, "other gestures keep working");
            shell.Scripts.Delete("plugin/bad-detector");

            // control port vocabulary
            Check(shell.ControlCommand("plugin", "list").Contains("core-poses"), "control port: plugin list");
            Check(shell.ControlCommand("gesture", "list").Contains("swipe-left lua"), "control port: gesture list shows Lua gestures");
            Check(shell.ControlCommand("action", "open:git") == "ok" && shell.IsPanelOpen("git"), "control port: action open:git");
            Check(shell.ControlCommand("gesture", "bind clap toggle:files") == "ok" && shell.ControlCommand("gesture", "bindings").Contains("clap"), "control port: gesture bind");
            shell.ControlCommand("gesture", "unbind clap");

            // BIND's presets: 12, TALK first, WEB is the WEB window (not LINKS); every one runs (RunAction → null)
            var presets = MetaCodexShell.PresetActions;
            Check(presets.Length == 12 && presets[0].action == "voice:toggle" && presets.Any(a => a.label == "WEB" && a.action == "toggle:web") && !presets.Any(a => a.action.Contains("browser")),
                "BIND: 12 presets: " + string.Join(", ", presets.Select(a => a.label)));
            foreach (var id in shell.PanelIds.ToList()) shell.ClosePanel(id);
            bool focusWas = shell.Settings.focusEnabled;
            var failed = new List<string>();
            foreach (var a in presets)
            {
                // voice:toggle would start the microphone and the AI service: the same verb runs with voice:cancel
                string act = a.action.StartsWith("voice:") ? "voice:cancel" : a.action;
                string err;
                try
                {
                    err = shell.RunAction(act);
                    if (err == null && (act.StartsWith("toggle:") || act == "select" || act == "focus")) err = shell.RunAction(act); // back as it was
                }
                catch (Exception ex) { err = ex.GetType().Name + " " + ex.Message; }
                if (err != null) failed.Add(a.label + ": " + err);
            }
            SystemKeyboard.Close();
            Check(failed.Count == 0 && shell.Settings.focusEnabled == focusWas && !shell.Router.SelectionMode, "every BIND preset runs (RunAction returns null)" + (failed.Count > 0 ? ": " + string.Join("; ", failed) : ""));
            Check(shell.RunAction("toggle:browser") == null && shell.RunAction("spin:40") == null && shell.RunAction("toast:old binding") == null,
                "verbs of the removed presets still run (old saved bindings keep working)");
            shell.RunAction("toggle:browser");

            // the panel, tab by tab (alone, so the picture shows only it)
            foreach (var id in shell.PanelIds.ToList()) shell.ClosePanel(id);
            shell.SetGestureExamplesOpen(true); // the picture shows the three groups, EXAMPLES open with what was added
            shell.OpenPanel("gestures"); // a tab of MQH, with the studio's own tabs
            var gp = shell.PanelsById["mqh"];
            var tabs = shell.GestureTabsForTest;
            shell.PoseHand(Handedness.Right, 0, 1, 1, 1, 1, Vector3.up);
            shell.PoseHand(Handedness.Left, 0.2f, 0, 0, 0, 0);
            shell.TickGesturesForTest(0.4f);
            string[] names = { "all", "live", "bind", "record" };
            for (int i = 0; i < names.Length; i++)
            {
                tabs.Select(i);
                if (i == 1) shell.RefreshGestureLive();
                gp.ForceRefresh();
                RenderPanel(gp, $"ui-gestures-{names[i]}.png");
            }
            tabs.Select(0);
            shell.SetGestureExamplesOpen(false);
            shell.OpenPanel("plugins"); // the gesture plugins are in MQH > PLUGINS now
            gp.ForceRefresh();
            RenderPanel(gp, "ui-gestures-plugins.png");
            hands.Hide(Handedness.Left);
            hands.Hide(Handedness.Right);
            shell.TickGesturesForTest(0.2f);
        }

        /// The one-time cleanup of the old starter copies (RetireStarterCopies), on folders of its own: only untouched
        /// AND unused copies go; edited ones, ones a binding or a script names, my-poses and other folders stay; a
        /// second run does nothing (so an example added later is never removed).
        private static void GestureExamplesCleanup(string tmp)
        {
            int n = 0;
            (string plugins, string scripts) Fresh()
            {
                var root = Path.Combine(tmp, "examples-cleanup-" + n++);
                if (Directory.Exists(root)) Directory.Delete(root, true);
                Directory.CreateDirectory(Path.Combine(root, "plugins"));
                Directory.CreateDirectory(Path.Combine(root, "scripts"));
                return (Path.Combine(root, "plugins"), Path.Combine(root, "scripts"));
            }
            void Seed(string plugins, string id, bool crlf = false)
            {
                foreach (var f in MetaCodexShell.StarterV1[id])
                {
                    var path = Path.Combine(plugins, id, f.Key.Replace('/', Path.DirectorySeparatorChar));
                    Directory.CreateDirectory(Path.GetDirectoryName(path));
                    File.WriteAllText(path, crlf ? f.Value.Replace("\n", "\r\n") : f.Value);
                }
            }
            bool Has(string plugins, string id) => Directory.Exists(Path.Combine(plugins, id));
            var none = new string[0];

            // an old headset: both starters untouched (one with Windows line ends), my-poses and another plugin beside them
            var a = Fresh();
            Seed(a.plugins, "core-poses");
            Seed(a.plugins, "swipes", crlf: true);
            File.WriteAllText(Path.Combine(a.plugins, ".starter-installed"), "old marker\n");
            Directory.CreateDirectory(Path.Combine(a.plugins, "my-poses", "poses"));
            File.WriteAllText(Path.Combine(a.plugins, "my-poses", "plugin.json"), "{ \"id\": \"my-poses\" }");
            File.WriteAllText(Path.Combine(a.plugins, "my-poses", "poses", "finger-gun.json"), "{ \"name\": \"finger-gun\" }");
            Directory.CreateDirectory(Path.Combine(a.plugins, "mine"));
            File.WriteAllText(Path.Combine(a.plugins, "mine", "plugin.json"), "{ \"id\": \"mine\" }");
            var log = MetaCodexShell.RetireStarterCopies(a.plugins, a.scripts, none);
            Check(!Has(a.plugins, "core-poses") && !Has(a.plugins, "swipes"), "cleanup: untouched, unused starter copies removed (" + string.Join("; ", log) + ")");
            Check(File.Exists(Path.Combine(a.plugins, "my-poses", "poses", "finger-gun.json")) && File.Exists(Path.Combine(a.plugins, "mine", "plugin.json")) && File.Exists(Path.Combine(a.plugins, ".starter-installed")),
                "cleanup: my-poses, other plugins and .starter-installed untouched");
            Check(File.Exists(Path.Combine(a.plugins, MetaCodexShell.ExamplesMarker)), "cleanup: marker written");
            // the user adds an example afterwards: the next start must leave it alone
            Seed(a.plugins, "core-poses");
            log = MetaCodexShell.RetireStarterCopies(a.plugins, a.scripts, none);
            Check(log.Count == 0 && Has(a.plugins, "core-poses"), "cleanup: a second run does nothing (an example added later stays)");

            // edited: one pose changed, or a file added
            var b = Fresh();
            Seed(b.plugins, "core-poses");
            File.AppendAllText(Path.Combine(b.plugins, "core-poses", "poses", "victory.json"), " ");
            Seed(b.plugins, "swipes");
            File.WriteAllText(Path.Combine(b.plugins, "swipes", "notes.txt"), "mine");
            log = MetaCodexShell.RetireStarterCopies(b.plugins, b.scripts, none);
            Check(Has(b.plugins, "core-poses") && Has(b.plugins, "swipes") && log.Count(l => l.Contains("edited")) == 2, "cleanup: edited copies kept (" + string.Join("; ", log) + ")");

            // used: a binding (even one switched off) names swipe-left; a script reacts to 'thumbs-up'
            var c = Fresh();
            Seed(c.plugins, "core-poses");
            Seed(c.plugins, "swipes");
            File.WriteAllText(Path.Combine(c.scripts, "thumbs.lua"), "gesture.on('thumbs-up', function(e) ui.open('git') end)\n");
            log = MetaCodexShell.RetireStarterCopies(c.plugins, c.scripts, new[] { "swipe-left" });
            Check(Has(c.plugins, "core-poses") && Has(c.plugins, "swipes") && log.Count(l => l.Contains("used")) == 2, "cleanup: copies a binding or a script names kept (" + string.Join("; ", log) + ")");

            // used by another plugin's Lua
            var d = Fresh();
            Seed(d.plugins, "core-poses");
            Directory.CreateDirectory(Path.Combine(d.plugins, "git-gestures"));
            File.WriteAllText(Path.Combine(d.plugins, "git-gestures", "plugin.json"), "{ \"id\": \"git-gestures\" }");
            File.WriteAllText(Path.Combine(d.plugins, "git-gestures", "main.lua"), "gesture.on(\"victory\", function(e) end)\n");
            log = MetaCodexShell.RetireStarterCopies(d.plugins, d.scripts, none);
            Check(Has(d.plugins, "core-poses"), "cleanup: a copy another plugin's Lua names kept (" + string.Join("; ", log) + ")");
        }

        /// A gesture plugin written by a real agent from the skill (tools/agent-plugin-test.sh puts it in Bench/agent/plugins).
        private static void AgentPlugin(MetaCodexShell shell, SimulatedHands hands, string tmp)
        {
            string src = Path.GetFullPath(Path.Combine("Bench", "agent", "plugins"));
            var dirs = Directory.Exists(src) ? Directory.GetDirectories(src).Where(d => File.Exists(Path.Combine(d, "plugin.json"))).ToArray() : new string[0];
            if (dirs.Length == 0) { Line("skip agent plugin: none in Bench/agent/plugins (tools/agent-plugin-test.sh)"); return; }
            // a plugin brings its own poses: nothing may rely on the examples (a fresh headset has none)
            foreach (var ex in MetaCodexShell.GestureExampleIds) shell.Scripts.Delete("plugin/" + ex);
            foreach (var d in dirs)
            {
                var dest = Path.Combine(shell.Scripts.PluginsDir, Path.GetFileName(d));
                foreach (var f in Directory.GetFiles(d, "*", SearchOption.AllDirectories))
                {
                    var rel = f.Substring(d.Length + 1);
                    Directory.CreateDirectory(Path.GetDirectoryName(Path.Combine(dest, rel)));
                    File.Copy(f, Path.Combine(dest, rel), true);
                }
            }
            shell.Scripts.Scan();
            var loaded = dirs.Select(d => shell.Scripts.Find("plugin/" + ScriptHost.Sanitize(Path.GetFileName(d)))).ToList();
            Check(loaded.All(e => e != null && e.Error == null), "agent plugin(s) load: " + string.Join(", ", loaded.Select(e => e == null ? "missing" : e.Name + (e.Error != null ? " ERROR " + e.Error : $" ({e.Poses} poses, {e.Detectors} detectors, {e.Handlers} handlers)"))));
            var owners = new HashSet<string>(loaded.Where(e => e != null).Select(e => e.Key));
            Check(shell.Gestures.Custom.Any(c => c.Name == "thumbs-up" && owners.Contains(c.Owner)),
                "agent plugin brings its own thumbs-up pose (no examples installed; an older agent output that relies on core-poses: re-run tools/agent-plugin-test.sh)");
            // the story the agent was asked for: right thumbs-up opens git; a fast push of an open palm closes it
            foreach (var id in shell.PanelIds.ToList()) shell.ClosePanel(id);
            hands.Hide(Handedness.Left);
            hands.Hide(Handedness.Right);
            shell.TickGesturesForTest(0.3f);
            shell.PoseHand(Handedness.Right, 0, 1, 1, 1, 1, Vector3.up);
            shell.TickGesturesForTest(0.6f);
            Check(shell.IsPanelOpen("git"), "agent plugin: right thumbs-up opened git");
            hands.Hide(Handedness.Right);
            shell.TickGesturesForTest(0.5f);
            var palm = shell.Gestures.Head.position + new Vector3(0.2f, -0.2f, 0.3f);
            Move(shell, hands, palm, new Vector3(0f, 0f, 1.6f), Vector3.forward, 0.3f);
            shell.TickGesturesForTest(0.2f);
            Check(!shell.IsPanelOpen("git"), "agent plugin: pushing an open palm away closed git");
            // a left thumbs-up must not open it (the user asked for the right hand)
            hands.Hide(Handedness.Right);
            shell.TickGesturesForTest(1.2f);
            shell.PoseHand(Handedness.Left, 0, 1, 1, 1, 1, Vector3.up);
            shell.TickGesturesForTest(0.6f);
            Check(!shell.IsPanelOpen("git"), "agent plugin: a LEFT thumbs-up does nothing");
            hands.Hide(Handedness.Left);
            shell.TickGesturesForTest(0.2f);
        }

        // ------------------------------------------------------------------ voice

        /// Voice control: the fast path (phrase templates, synonyms, fillers), the actions, a Lua plugin command,
        /// then, with a dev AI service (tools/local-ai-dev.sh device start), speech -> text -> command for real and the
        /// LLM path for phrasings no template covers.
        private static void VoiceControl(MetaCodexShell shell)
        {
            var v = shell.Voice;
            (string said, string cmd, string args)[] cases =
            {
                ("Open the providers page.", "open_panel", "{\"panel\":\"agents\"}"),
                ("Hey, could you open the git panel please", "open_panel", "{\"panel\":\"git\"}"),
                ("Open the provider's page.", "open_panel", "{\"panel\":\"agents\"}"),
                ("Close all windows.", "close_all_panels", "{}"),
                ("Open a new terminal.", "new_terminal", "{}"),
                ("Find the thread where we fixed the keyboard.", "find_thread", "{\"query\":\"we fixed the keyboard\"}"),
                ("Switch to the workspace called MetaCodex.", "switch_workspace", "{\"name\":\"metacodex\"}"),
                ("change workspace", "switch_workspace", "{}"),
                ("Start a thread to fix the login page.", "new_thread", "{\"task\":\"fix the login page\"}"),
                ("new thread", "new_thread", "{}"),
                ("Create a research project called solar panels.", "new_project", "{\"type\":\"research\",\"name\":\"solar panels\"}"),
                ("new project", "new_project", "{}"),
                ("use the floating layout", "set_layout", "{\"mode\":\"float\"}"),
                ("show me the source control", "open_panel", "{\"panel\":\"git\"}"),
                ("recenter", "recenter", "{}"),
                ("close the vault", "close_panel", "{\"panel\":\"vault\"}"),
                ("Where are my API keys?", "open_panel", "{\"panel\":\"vault\"}"),
                ("Which conversation was about the keyboard bug?", "find_thread", "{\"query\":\"the keyboard bug\"}"),
                ("Put everything back in front of me.", "recenter", "{}"),
            };
            foreach (var c in cases)
            {
                var m = v.FastMatch(c.said);
                string got = m == null ? "no match" : m.Command.Name + " " + m.Args.ToString(Newtonsoft.Json.Formatting.None);
                Check(m != null && m.Command.Name == c.cmd && JToken.DeepEquals(m.Args, JObject.Parse(c.args)), $"voice fast path “{c.said}” -> {got}");
            }
            Check(v.FastMatch("what is the weather like in Rome") == null, "voice: other sentences are left to the LLM");
            Check(v.FastMatch("open the banana page") == null, "voice: an unknown panel name is not forced onto a panel");
            Check(v.ToolSpecs().Count == v.All.Count && v.ToolSpecs().All(t => t["function"]?["parameters"]?["type"]?.ToString() == "object"), $"voice: {v.All.Count} commands offered to the LLM as tools");

            foreach (var id in shell.PanelIds.ToList()) shell.ClosePanel(id);
            shell.OpenPanel("files");
            shell.OpenPanel("git");
            VoiceResult r = null;
            shell.VoiceProcessText("close all windows", x => r = x);
            Check(r != null && r.Ok && r.Via == "fast" && !shell.PanelsById["files"].IsOpen && !shell.IsPanelOpen("git"), "“close all windows” closes the panels: " + r);
            shell.VoiceProcessText("open the providers page", x => r = x);
            Check(r != null && r.Ok && shell.PanelsById["agents"].IsOpen, "“open the providers page” opens AGENTS: " + r);
            shell.ClosePanel("agents");

            // a Lua plugin adds a command: phrases, an enum parameter with spoken synonyms, a run function
            string pdir = Path.Combine(shell.Scripts.PluginsDir, "voice-test");
            Directory.CreateDirectory(pdir);
            File.WriteAllText(Path.Combine(pdir, "plugin.json"), "{\"name\":\"Voice test\",\"version\":\"1.0\",\"description\":\"self-test\"}");
            File.WriteAllText(Path.Combine(pdir, "main.lua"),
                "voice.command{\n" +
                "  name = 'set_accent',\n" +
                "  description = 'Change the accent colour of the interface',\n" +
                "  params = { color = { type = 'string', enum = { 'cyan', 'amber', 'green', 'magenta' }, required = true } },\n" +
                "  phrases = { '(make|turn) [the] (ui|interface|panels) {color}', '(set|change) [the] (accent|colour|color) [to] {color}' },\n" +
                "  synonyms = { color = { orange = 'amber', yellow = 'amber', pink = 'magenta' } },\n" +
                "  run = function(args) store.set('accent', args.color) return 'Accent ' .. args.color end\n" +
                "}\n");
            shell.Scripts.Scan();
            var pe = shell.Scripts.Find("plugin/voice-test");
            Check(pe != null && pe.Error == null, "voice plugin loads: " + (pe?.Error ?? "ok"));
            var pm = v.FastMatch("make the interface orange");
            Check(pm != null && pm.Command.Owner == "plugin/voice-test" && (string)pm.Args["color"] == "amber", "plugin phrase + synonym: “make the interface orange” -> " + (pm == null ? "no match" : pm.Command.Name + " " + pm.Args));
            shell.VoiceProcessText("set the colour to pink", x => r = x);
            Check(r != null && r.Ok && r.Reply == "Accent magenta", "plugin command runs in Lua and replies: " + r);
            Check(v.ToolSpecs().Any(t => (string)t["function"]?["name"] == "set_accent"), "plugin command is offered to the LLM as a tool");
            shell.Scripts.SetEnabled("plugin/voice-test", false);
            Check(v.Find("set_accent") == null, "disabling the plugin removes its command");
            shell.Scripts.Delete("plugin/voice-test");

            // the control port speaks the same language
            string ctl = shell.ControlCommand("voice", "match open the vault");
            Check(ctl.StartsWith("open_panel") && ctl.Contains("vault"), "control port `voice match`: " + ctl);
            string asyncReply = null;
            Check(shell.VoiceAsyncCommand("voice run open the files", a => asyncReply = a) && asyncReply != null && asyncReply.Contains("\"command\":\"open_panel\""), "control port `voice run` answers with the result: " + asyncReply);
            shell.ClosePanel("files");

            // MQH-backed commands (when MQH runs): they answer, matched or not
            try
            {
                shell.Mqh.Send(HttpMethod.Get, "/api/v1/system/version").GetAwaiter().GetResult();
                r = null;
                shell.VoiceProcessText("find the thread where we tested the self test", x => r = x);
                shell.PumpForTest(8f);
                Check(r != null && (r.Ok || r.Error.StartsWith("no thread")), "find_thread asks MQH and answers: " + r);
                r = null;
                shell.VoiceProcessText("switch to the workspace called metacodex", x => r = x);
                shell.PumpForTest(8f);
                Check(r != null && (r.Ok || r.Error.StartsWith("no workspace")), "switch_workspace asks MQH and answers: " + r);
            }
            catch (Exception) { Line("skip voice MQH commands (MQH not running)"); }

            // the exact LLM request (prompt prefix + grammar), for replays against the service outside the Editor
            File.WriteAllText(Path.Combine(MetaCodexSetup.ReportDir, "voice-llm-request.json"), shell.VoiceLlmRequest("REQUEST").ToString());
            Check(shell.VoiceLlmRequest("x")["grammar"]?.ToString().Contains("open_panel") == true && !shell.VoiceLlmRequest("x")["grammar"].ToString().Contains("type_text"),
                "LLM grammar: shell commands, no free-text typing");
            // the real thing: the local AI service (QVAC) behind it
            DevForward();
            if (!shell.Qvac.ConnectDevNow()) { Line("skip voice AI: " + shell.Qvac.Status + " (tools/local-ai-dev.sh device start)"); RenderVoice(shell); return; }
            shell.PumpForTest(4f);
            Check(shell.QvacStatus != null && shell.QvacStatus["models"]?["asr"]?["installed"]?.Value<bool>() == true, "AI service status with models: " + shell.QvacStatus?["models"]?.ToString(Newtonsoft.Json.Formatting.None));
            byte[] wav = null;
            string err = null;
            var sw = System.Diagnostics.Stopwatch.StartNew();
            shell.Qvac.Client.Speak("Open the git panel.", null, (w, e) => { wav = w; err = e; });
            shell.PumpForTest(90f);
            Check(wav != null && wav.Length > 20000, $"text-to-speech: {(wav?.Length ?? 0) / 1024} KB of speech in {sw.ElapsedMilliseconds} ms {err}");
            if (wav != null)
            {
                foreach (var id in shell.PanelIds.ToList()) shell.ClosePanel(id);
                r = null;
                shell.VoiceProcessWav(wav, x => r = x);
                shell.PumpForTest(60f);
                // the synthetic voice says "git" with a soft g: Whisper may write a variant the registry knows ("jet", "get")
                var gitWords = new[] { "git" }.Concat(shell.Voice.Find("open_panel").Synonyms["panel"].Where(kv => kv.Value == "git").Select(kv => kv.Key)).ToList();
                string heard = VoiceCommands.Simplify(r?.Transcript ?? "");
                Check(r != null && r.Ok && r.Command == "open_panel" && gitWords.Any(w => VoiceDecider.HasWord(heard, w)) && shell.IsPanelOpen("git"),
                    "spoken “Open the git panel.” -> " + r);
            }
            sw.Restart();
            string werr = "pending";
            shell.WarmVoiceLlm(e => werr = e);
            shell.PumpForTest(120f);
            Check(werr == null, $"LLM warm-up (system prompt + {v.All.Count} tools cached) in {sw.ElapsedMilliseconds} ms {werr}");
            (string said, string cmd, string arg)[] llm =
            {
                ("I need to see which AI providers are signed in", "open_panel", "agents"),
                ("get rid of every window you have open", "close_all_panels", null),
                ("I want a fresh shell to type commands in", "new_terminal", null),
            };
            foreach (var c in llm)
            {
                r = null;
                shell.VoiceProcessText(c.said, x => r = x);
                shell.PumpForTest(60f);
                Check(r != null && r.Via == "llm" && r.Ok && r.Command == c.cmd && (c.arg == null || (r.Args ?? "").Contains(c.arg)), $"LLM path “{c.said}” -> {r}");
            }
            RenderVoice(shell);
        }

        /// The Editor's Android module starts its own adb (another version), which restarts the adb server and
        /// drops `adb forward`s: when the dev AI service runs on the headset, forward its port with that adb.
        private static void DevForward()
        {
            try
            {
                var dev = File.Exists(MetaCodex.AI.QvacHost.DevConfigPath) ? JObject.Parse(File.ReadAllText(MetaCodex.AI.QvacHost.DevConfigPath)) : null;
                if ((string)dev?["where"] != "device") return;
                string adb = Path.Combine(EditorApplication.applicationContentsPath, "PlaybackEngines", "AndroidPlayer", "SDK", "platform-tools", "adb.exe");
                if (!File.Exists(adb)) { Line("     no Editor adb at " + adb); return; }
                var port = new Uri((string)dev["url"]).Port;
                var psi = new System.Diagnostics.ProcessStartInfo(adb, $"forward tcp:{port} tcp:{port}") { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
                using (var p = System.Diagnostics.Process.Start(psi))
                {
                    if (!p.WaitForExit(15000)) { p.Kill(); Line("     adb forward timed out"); return; }
                    Line($"     adb forward tcp:{port} (Editor adb): exit {p.ExitCode} {p.StandardError.ReadToEnd().Trim()}");
                }
            }
            catch (Exception ex) { Line("     adb forward: " + ex.Message); }
        }

        private static void RenderVoice(MetaCodexShell shell)
        {
            shell.OpenPanel("voice");
            shell.PollVoiceStatus();
            shell.PumpForTest(3f);
            var vp = shell.PanelsById["voice"];
            vp.ForceRefresh();
            RenderPanel(vp, "ui-voice.png");
        }

        // ------------------------------------------------------------------ vault

        private static void Vault(MetaCodexShell shell)
        {
            var v = shell.Vault;
            Check(v != null, "vault opened (" + v?.Sealer.Kind + ", " + v?.Sealer.Level + ")");
            if (v == null) return;
            Check(v.Has(MetaCodexShell.MqhTokenName) && shell.Mqh.Token == v.Reveal(MetaCodexShell.MqhTokenName) && shell.Mqh.Token.Length == 64, "MQH API token generated in the vault and used by the client");
            Check(v.Set("OPENAI_API_KEY", "sk-test-1234567890", "api-key", "allow") == null, "secret stored");
            string file = File.ReadAllText(v.FilePath);
            Check(!file.Contains("sk-test-1234567890") && file.Contains("OPENAI_API_KEY"), "vault file holds only sealed values");
            Check(v.Reveal("OPENAI_API_KEY") == "sk-test-1234567890", "secret unsealed with the right name");
            var e = v.Find("OPENAI_API_KEY");
            var keep = e.value;
            var bytes = Convert.FromBase64String(e.value); bytes[bytes.Length / 2] ^= 1; e.value = Convert.ToBase64String(bytes);
            Check(v.Reveal("OPENAI_API_KEY") == null, "a tampered value is rejected (authenticated encryption)");
            e.value = keep;

            string Ask(string line)
            {
                string got = null;
                shell.SecretCommand(line, r => got = r);
                return got;
            }
            Check(Ask("secret get OPENAI_API_KEY test") == "value sk-test-1234567890", "policy allow: handed out");
            v.SetPolicy("OPENAI_API_KEY", "deny");
            Check(Ask("secret get OPENAI_API_KEY test").StartsWith("error: denied"), "policy deny: refused");
            string k1 = Ask("secret ensure test.key 32 mqh"), k2 = Ask("secret ensure test.key 32 agent");
            Check(k1 != null && k1.StartsWith("value ") && k1.Length == 6 + 64 && k2.StartsWith("error: denied"), "policy once: a new random key handed out once, then refused");
            Check(Ask("secret rearm test.key").StartsWith("error:"), "re-arming is not possible from Linux");
            v.SetPolicy("OPENAI_API_KEY", "ask");
            string answer = null;
            shell.SecretCommand("secret get OPENAI_API_KEY agent", r => answer = r);
            Check(answer == null, "policy ask: waits for the user");
            var modal = shell.GetComponentsInChildren<UiPanel>(true).LastOrDefault(p => p.Id == "modal" && p.IsOpen);
            Check(modal != null, "approval dialog shown in VR");
            if (modal != null)
            {
                modal.ForceRefresh();
                RenderPanel(modal, "ui-vault-request.png");
                modal.Elements.OfType<UiButton>().First(b => b.Text == "ALLOW ONCE").Click();
            }
            Check(answer == "value sk-test-1234567890", "approved in VR: handed out");
            Check(v.Audit.Count >= 4 && v.Audit.Any(a => a.Decision == "DENY"), $"access log: {v.Audit.Count} entries");
            var list = Ask("secret list");
            Check(list.Contains("OPENAI_API_KEY") && !list.Contains("sk-test"), "secret list never shows values");
            var v2 = new MetaCodex.Security.SecretVault(v.FilePath, v.Sealer);
            Check(v2.Reveal("OPENAI_API_KEY") == "sk-test-1234567890", "vault reopened from disk");

            // no plain text anywhere: a locked vault (no keystore) stores nothing, never falls back to a key in a file
            string vdir = Path.Combine(Path.GetDirectoryName(v.FilePath), "..", "vault-tests");
            if (Directory.Exists(vdir)) Directory.Delete(vdir, true);
            Directory.CreateDirectory(vdir);
            var locked = new MetaCodex.Security.SecretVault(Path.Combine(vdir, "locked.json"), new MetaCodex.Security.LockedSealer("test"));
            Check(!locked.Usable && locked.Set("X", "plain-value") != null && !File.Exists(Path.Combine(vdir, "locked.json")) && locked.Ensure("K", 32) == null,
                "a locked vault (keystore unavailable) refuses to store: no plain-text fallback, nothing written");
            // an unreadable vault file is kept aside, never overwritten by the next save
            File.WriteAllText(Path.Combine(vdir, "broken.json"), "{ not json");
            var sealer = new MetaCodex.Security.DevFileSealer(Path.Combine(vdir, "a.key"));
            var broken = new MetaCodex.Security.SecretVault(Path.Combine(vdir, "broken.json"), sealer);
            broken.Set("NEW", "v1");
            Check(broken.LoadError != null && Directory.GetFiles(vdir, "broken.json.unreadable-*").Length == 1, "an unreadable vault file is kept aside (" + broken.LoadError + "), not wiped");
            // secrets an older build sealed with a key in a file move under the real key, and that key is deleted
            string devDir = Path.Combine(vdir, "vault-dev");
            var old = new MetaCodex.Security.SecretVault(Path.Combine(devDir, "vault.json"), new MetaCodex.Security.DevFileSealer(Path.Combine(devDir, "dev.key")));
            old.Set("OLD_KEY", "old-secret", "api-key", "ask");
            var fresh = new MetaCodex.Security.SecretVault(Path.Combine(vdir, "fresh.json"), new MetaCodex.Security.DevFileSealer(Path.Combine(vdir, "b.key")));
            int moved = fresh.AdoptDevFile(devDir);
            Check(moved == 1 && fresh.Reveal("OLD_KEY") == "old-secret" && fresh.Find("OLD_KEY").policy == "ask" && !File.Exists(Path.Combine(devDir, "dev.key")) && !File.Exists(Path.Combine(devDir, "vault.json")),
                "secrets sealed with an old file key are resealed under the vault key; the old key and file are deleted");
            // the MQH token typed in Settings > DAEMON is sealed in the vault, never written to settings.json
            shell.SetMqhTokenOverride("dev-token-abcdef");
            shell.OnSettingsChanged();
            string settingsText = File.ReadAllText(shell.SettingsPathForTest);
            Check(v.Has(MetaCodexShell.MqhTokenOverrideName) && v.Find(MetaCodexShell.MqhTokenOverrideName).policy == "deny" && shell.Mqh.Token == "dev-token-abcdef" && !settingsText.Contains("dev-token-abcdef"),
                "the MQH token override is sealed in the vault (policy deny), used by the client, and not in settings.json");
            shell.SetMqhTokenOverride("");
            Check(!v.Has(MetaCodexShell.MqhTokenOverrideName) && shell.Mqh.Token == v.Reveal(MetaCodexShell.MqhTokenName), "clearing the override goes back to the vault's own MQH token");
            Directory.Delete(vdir, true);

            foreach (var id in shell.PanelIds.ToList()) shell.ClosePanel(id);
            shell.OpenPanel("vault");
            shell.RefreshVaultPanel();
            RenderPanel(shell.PanelsById["vault"], "ui-vault.png");
            v.Delete("test.key");
        }

        // ------------------------------------------------------------------ links in terminals, look, layouts

        private static void Links(MetaCodexShell shell, VtTerminal term)
        {
            string url = "https://claude.ai/oauth/authorize?code=true&client_id=9d1c250a-e61b-44d9-88ed-5944d1962f5e&response_type=code&redirect_uri=http%3A%2F%2Flocalhost%3A54545%2Fcallback&scope=user%3Ainference&state=abcdefghijklmnopqrstuvwxyz0123456789";
            term.Feed("\r\nBrowser didn't open? Use the url below to sign in:\r\n\r\n" + url + "\r\n\r\nPaste code here if prompted > ");
            var found = TerminalLinks.Last(term);
            Check(found == url, $"sign-in link found across {Math.Ceiling(url.Length / (double)term.Cols)} wrapped rows ({found?.Length} chars)");
        }

        private static void LookAndLayouts(MetaCodexShell shell, Transform head)
        {
            // one standard appearance: LOOK has comfort settings only (no themes, accents, scanlines, grid)
            foreach (var id in shell.PanelIds.ToList()) shell.ClosePanel(id);
            shell.OpenPanel("settings");
            var st = shell.PanelsById["settings"];
            st.Elements.OfType<UiTabs>().First().Select(2);
            bool gone = !st.Elements.OfType<UiSegmented>().Any(sg => sg.Text == "THEME") && !st.Elements.OfType<UiToggle>().Any(tg => tg.Text == "Scanlines" || tg.Text == "Grid")
                && !st.Elements.OfType<UiButton>().Any(b => b.Text.StartsWith("ACCENT"));
            Check(gone, "LOOK: no theme presets, accent, scanlines or grid");
            var opacity = st.Elements.OfType<UiSlider>().First(sl => sl.Text == "PANEL OPACITY");
            var orr = opacity.Rect;
            Click(shell.Router, st, new Vector2(orr.xMax - 0.02f, orr.center.y));
            var mat = UiTheme.Current.ShapeMaterial;
            Check(mat != null && mat.GetFloat("_Opacity") > 0.9f && mat.GetFloat("_Scanlines") < 0.01f && mat.GetFloat("_Grid") < 0.01f, $"panel opacity follows its slider ({mat?.GetFloat("_Opacity"):F2}); scanlines and grid off");
            // the wrist menu's glass follows the same value (its own material): read at 60 %, then the setting goes back
            string opNow = shell.GetSetting("panelOpacity");
            shell.SetSetting("panelOpacity", "0.6");
            var wristMat = shell.Wrist.GetComponent<MeshRenderer>().sharedMaterial;
            float panelAt60 = mat.GetFloat("_Opacity"), wristAt60 = wristMat.GetFloat("_Opacity");
            shell.SetSetting("panelOpacity", opNow);
            Check(Mathf.Abs(panelAt60 - 0.6f) < 1e-3f && Mathf.Abs(wristAt60 - 0.6f) < 1e-3f,
                $"the wrist menu's glass follows PANEL OPACITY too: at 60 % the panels take {panelAt60:F2}, the wrist glass {wristAt60:F2}");
            foreach (var id in new[] { "files", "git", "dock" }) shell.OpenPanel(id);
            foreach (var p in shell.PanelsById.Values) p.ForceRefresh();
            Render(shell, "ui-look.png", head.position, Quaternion.identity, 100f, 1600, 900);

            foreach (var id in shell.PanelIds.ToList()) shell.ClosePanel(id);
            shell.OpenPanel("files");
            shell.OpenPanel("git");
            var files0 = shell.PanelsById["files"].transform.position - head.position;
            shell.SaveNamedLayout("desk");
            head.Rotate(0f, 90f, 0f);
            head.position += new Vector3(1f, 0f, 1f);
            shell.ClosePanel("git");
            Check(shell.LoadNamedLayout("desk"), "layout 'desk' loads");
            var files1 = Quaternion.Euler(0f, -90f, 0f) * (shell.PanelsById["files"].transform.position - head.position);
            Check(shell.IsPanelOpen("git") && (files1 - files0).magnitude < 0.02f, $"saved layout restored around the user ({(files1 - files0).magnitude * 100f:F1} cm)");
            head.position -= new Vector3(1f, 0f, 1f);
            head.rotation = Quaternion.identity;
        }

        // ------------------------------------------------------------------ agents and threads (MQH)

        private static void AgentsAndThreads(MetaCodexShell shell)
        {
            try { shell.Mqh.Send(HttpMethod.Get, "/api/v1/system/version").GetAwaiter().GetResult(); }
            catch (Exception) { Line("skip agents/threads (MQH not running)"); return; }
            foreach (var id in shell.PanelIds.ToList()) shell.ClosePanel(id);
            shell.PumpForTest(4f); // settings pushes and earlier calls finish first
            shell.OpenPanel("agents");
            shell.PumpForTest(6f);
            var ap = shell.PanelsById["agents"];
            var agents = ap.Elements.OfType<UiList>().First();
            Check(agents.Items.Count >= 3, "AGENTS lists MQH's providers: " + string.Join(", ", agents.Items.Select(i => i.Title + "=" + i.Badge)));
            int cc = agents.Items.ToList().FindIndex(i => (string)i.Tag == "claude-code");
            if (cc >= 0) agents.SelectIndex(cc);
            ap.ForceRefresh();
            RenderPanel(ap, "ui-agents.png");
            shell.SignIn("claude-code");
            Check(shell.Toasts.Last != null && shell.Toasts.Last.Contains("claude auth login"), "SIGN IN uses the provider's own login command: " + shell.Toasts.Last);

            shell.ClosePanel("agents");
            shell.OpenPanel("threads");
            shell.PumpForTest(6f);
            var tp = shell.PanelsById["threads"];
            var projects = tp.Elements.OfType<UiList>().First();
            Line($"     projects: {string.Join(", ", projects.Items.Select(i => i.Title))}");
            tp.ForceRefresh();
            RenderPanel(tp, "ui-threads.png");

            shell.ClosePanel("threads");
            shell.OpenNewThread("claude-code");
            shell.PumpForTest(8f);
            var np = shell.PanelsById["thread-new"];
            var models = np.Elements.OfType<UiList>().ElementAt(1);
            Check(models.Items.Count >= 1, "NEW THREAD lists the provider's models: " + string.Join(", ", models.Items.Take(4).Select(i => i.Title)));
            np.ForceRefresh();
            RenderPanel(np, "ui-thread-new.png");
            var firstProject = projects.Items.FirstOrDefault(i => (i.Tag as string ?? "").StartsWith("p:")); // the rows under the type headers
            if (firstProject == null) { Line("skip thread start: no project in MQH (add one: + PROJECT…)"); return; }
            string projectId = ((string)firstProject.Tag).Substring(2);
            string model = models.Items.Count > 0 ? (string)models.Items[models.Items.Count - 1].Tag : null;
            JToken created = null;
            shell.Mqh.CreateThread(shell.NewThreadBody(projectId, "claude-code", model, "MetaCodex self-test: reply with OK"), r => created = r);
            shell.PumpForTest(8f);
            Check(created?["id"] != null, "thread created through MQH: " + (created?["id"] ?? shell.Mqh.LastError));
            if (created?["id"] == null) return;
            shell.ClosePanel("thread-new");
            shell.OpenThread((string)created["id"]);
            for (int i = 0; i < 6; i++) { shell.PumpForTest(2f); System.Threading.Thread.Sleep(800); shell.PollThread(); }
            shell.PumpForTest(3f);
            var th = shell.PanelsById["thread"];
            var tl = th.Elements.OfType<UiLabel>().Select(l => l.Text ?? "").FirstOrDefault(t => t.Contains("YOU"));
            Check(tl != null && tl.Contains("self-test"), "THREAD shows the timeline (your message" + (tl != null && tl.Contains("AGENT") ? " and the agent's reply" : "") + ")");
            th.ForceRefresh();
            RenderPanel(th, "ui-thread.png");
            JToken env = null;
            shell.Mqh.SetMachineEnvironment("MCX_SELFTEST_KEY", "value-from-vault", "self-test", r => env = r);
            shell.PumpForTest(4f);
            var names = (env?["variables"] as JArray)?.Select(x => (string)x["name"]).ToList();
            Check(names != null && names.Contains("MCX_SELFTEST_KEY") && !env.ToString().Contains("value-from-vault"), "secret pushed to MQH's machine environment (write-only there)");
            shell.Mqh.DeleteMachineEnvironment("MCX_SELFTEST_KEY", r => { });
            shell.PumpForTest(2f);
        }

        // ------------------------------------------------------------------ 0.6: views, workspace, threads, vault git, focus, system

        private static void Views(MetaCodexShell shell, Transform head, string tmp)
        {
            foreach (var id in shell.PanelIds.ToList()) shell.ClosePanel(id);
            shell.SetLayout("fixed");
            string msg = "Here:\n```mermaid\ngraph LR\n  A[Parse] -->|ok| B{Valid?}\n  B -- yes --> C([Run])\n  B -.-> D((Report))\n  A & C --> E[Done]\n```\nand\n```mcx-view\n{\"id\":\"t1\",\"title\":\"Times\",\"components\":[{\"type\":\"chart\",\"kind\":\"bar\",\"labels\":[\"a\",\"b\"],\"data\":[3,5]}]}\n```\n```mcx-view\n{bad json\n```";
            var blocks = MetaCodexShell.ExtractViewBlocks(msg);
            Check(blocks.Count == 3 && blocks[0].spec != null && blocks[1].spec != null && blocks[2].spec == null && blocks[2].error != null, $"view blocks in a message: mermaid + mcx-view + a bad one reported ({blocks.Count}: {blocks.Last().error})");
            Check(MetaCodexShell.StripViewBlocks(msg).Contains("[▣ Times]") && !MetaCodexShell.StripViewBlocks(msg).Contains("```"), "the timeline shows a marker instead of the block: " + MetaCodexShell.StripViewBlocks(msg).Replace("\n", " ⏎ "));
            Progress("views: blocks parsed");
            var flow = new UiFlow();
            UiMermaid.ParseFlow((string)blocks[0].spec["components"][0]["text"], flow, out string ferr);
            Check(ferr == null && flow.Nodes.Count == 5 && flow.Edges.Count == 5 && flow.Direction == "LR" && flow.Get("B").Shape == "diamond" && flow.Get("C").Shape == "stadium" && flow.Get("D").Shape == "circle"
                && flow.Edges.Any(e => e.From == "A" && e.To == "B" && e.Label == "ok") && flow.Edges.Any(e => e.From == "B" && e.To == "C" && e.Label == "yes") && flow.Edges.Any(e => e.To == "D" && e.Style == "dotted"),
                $"mermaid flowchart: {flow.Nodes.Count} nodes, {flow.Edges.Count} edges, shapes and labels ({string.Join(", ", flow.Edges.Select(e => e.From + ">" + e.To + (e.Label != null ? ":" + e.Label : "")))})");
            var seq = new UiSequence();
            UiMermaid.ParseSequence("sequenceDiagram\nparticipant U as You\nU->>App: talk\nApp-->>U: done\nNote over U,App: fast\nloop retry\nApp->>App: again\nend", seq, out string serr);
            Check(serr == null && seq.Participants.Count == 2 && seq.Messages.Count == 4 && seq.Messages[1].dashed && seq.Messages[2].note, $"mermaid sequence: {seq.Participants.Count} participants, {seq.Messages.Count} rows");
            var pie = new UiChart();
            UiMermaid.ParsePie("pie title Time\n\"Render\" : 46\n\"Idle\" : 24", pie, out string perr);
            Check(perr == null && pie.Labels.Count == 2 && pie.Series[0].data.Sum() == 70 && pie.Title == "Time", "mermaid pie");

            Progress("views: mermaid parsed");
            shell.ShowDemoView();
            Progress("views: demo built");
            var vp = shell.PanelsById["view-demo"];
            vp.ForceRefresh();
            Check(vp.IsOpen && vp.Elements.OfType<UiChart>().Count() == 2 && vp.Elements.OfType<UiFlow>().Count() == 1 && vp.Elements.OfType<UiViewBlock>().Count() == 2,
                $"demo view: {vp.Elements.OfType<UiChart>().Count()} charts, {vp.Elements.OfType<UiFlow>().Count()} diagram, {vp.Elements.OfType<UiViewBlock>().Count()} blocks, {vp.Width:F2} x {vp.Height:F2} m");
            Progress("views: demo refreshed");
            var stage = vp.GetComponentInChildren<ViewStage>();
            Check(stage != null && stage.Count == 9, "scene3d: a physics stage with 9 bodies under the view: " + (stage != null ? stage.Count.ToString() : "none"));
            if (stage != null)
            {
                var mode = Physics.simulationMode;
                Physics.simulationMode = SimulationMode.Script;
                try
                {
                    for (int i = 0; i < 240; i++) { stage.Step(1f / 72f); Physics.Simulate(1f / 72f); }
                    var bodies = stage.GetComponentsInChildren<Rigidbody>().Where(b => !b.isKinematic).ToList();
                    var local = bodies.Select(b => stage.transform.InverseTransformPoint(b.position)).ToList();
                    bool onPlate = local.All(p => p.y > -0.02f && p.y < 0.3f && Mathf.Abs(p.x) < stage.Width / 2 + 0.04f && Mathf.Abs(p.z) < stage.Depth / 2 + 0.04f);
                    float maxSpeed = bodies.Count > 0 ? bodies.Max(b => b.linearVelocity.magnitude) : 0f;
                    Check(bodies.Count == 9 && onPlate, $"scene3d: after 3.3 s of physics the bodies are on the plate (lowest y {local.Min(p => p.y) * 100f:F1} cm, max speed {maxSpeed:F2} m/s)");
                    stage.SetRunning(false);
                    var frozen = bodies.Select(b => b.position).ToList();
                    for (int i = 0; i < 30; i++) { stage.Step(1f / 72f); Physics.Simulate(1f / 72f); }
                    Check(bodies.Select((b, i) => Vector3.Distance(b.position, frozen[i])).Max() < 1e-4f, "scene3d: PAUSE freezes the bodies");
                    stage.SetRunning(true);
                    vp.transform.position += new Vector3(0.3f, 0f, 0f);
                    stage.Step(1f / 72f);
                    var after = stage.GetComponentsInChildren<Rigidbody>().Where(b => !b.isKinematic).Select(b => stage.transform.InverseTransformPoint(b.position)).ToList();
                    Check(after.Zip(local, (a, b) => Vector3.Distance(a, b)).Max() < 0.05f, "scene3d: moving the panel carries the bodies along");
                    vp.transform.position -= new Vector3(0.3f, 0f, 0f);
                    stage.Step(1f / 72f);
                }
                finally { Physics.simulationMode = mode; }
            }
            Progress("views: physics done");
            var v = shell.FindView("demo");
            string answer = null;
            Check(shell.ViewAsyncCommand("view wait demo", r => answer = r), "view wait registers");
            var choice = vp.Elements.OfType<UiButton>().FirstOrDefault(b => b.Text == "Physics");
            choice?.Click();
            Check(choice != null && v.Answer == "Physics" && answer == "answer Physics", "a tapped choice answers the waiting `view wait`: " + answer);
            vp.ForceRefresh();
            var pos = vp.transform.position - vp.transform.forward * 0.75f + Vector3.up * 0.18f;
            RenderAt("ui-view-demo.png", pos, Quaternion.LookRotation((vp.transform.position + Vector3.down * 0.16f - pos).normalized), 62f, 1600, 1200);

            Progress("views: rendered");
            // control port: a spec file, live update by id, list, schema
            string rel = "unity/Temp/mcx-uitest/view-spec.json";
            Directory.CreateDirectory(Path.GetDirectoryName(Path.Combine("..", rel)));
            File.WriteAllText(Path.Combine("..", rel), "{\"id\":\"live\",\"title\":\"Live\",\"components\":[{\"type\":\"progress\",\"items\":[{\"label\":\"build\",\"value\":0.3}]},{\"type\":\"table\",\"columns\":[\"file\",\"lines\"],\"rows\":[[\"a.cs\",12],[\"b.cs\",340]]},{\"type\":\"kanban\",\"columns\":[{\"title\":\"todo\",\"cards\":[\"x\",\"y\"]},{\"title\":\"done\",\"cards\":[{\"text\":\"z\",\"tone\":\"ok\"}]}]}]}");
            string r1 = shell.ControlCommand("view", "show /" + rel);
            File.WriteAllText(Path.Combine("..", rel), "{\"id\":\"live\",\"title\":\"Live 2\",\"components\":[{\"type\":\"progress\",\"items\":[{\"label\":\"build\",\"value\":0.9}]}]}");
            string r2 = shell.ControlCommand("view", "show /" + rel);
            Check(r1 == "ok live" && r2 == "ok live" && shell.Views.Count(x => x.Id == "live") == 1 && shell.FindView("live").Title == "Live 2" && shell.PanelsById["view-live"].IsOpen,
                $"control port: view show reads a spec file; the same id updates in place ({r1}, {r2})");
            Check(shell.ControlCommand("view", "list").Contains("live") && shell.ControlCommand("view", "schema").Contains("scene3d") && shell.ControlCommand("view", "show /nope.json").StartsWith("error"), "view list / schema / errors");
            Check(shell.ControlCommand("view", "mermaid graph TD\\nX-->Y").StartsWith("ok"), "view mermaid from one line");
            shell.ControlCommand("view", "clear");
            Check(shell.Views.Count == 0 && !shell.PanelsById.Keys.Any(k => k.StartsWith("view-")), "view clear removes the views and their panels");
        }

        /// The voice bench's inputs from the app itself (tools/voicebench): the command registry as plugins and the LLM
        /// see it, the settings catalogue, the exact LLM request, and what today's fast path makes of every bench sentence.
        private static void VoiceExport(MetaCodexShell shell)
        {
            string dir = Path.Combine("Bench", "voice");
            Directory.CreateDirectory(dir);
            var v = shell.Voice;
            // the registry as agents and plugins see it (VoiceCommands.Describe), plus the settings voice can change
            var reg = v.Describe();
            reg["settings"] = MetaCodexShell.VoiceSettingsForBench();
            var cmds = (JArray)reg["commands"];
            File.WriteAllText(Path.Combine(dir, "registry.json"), reg.ToString());
            var req = shell.VoiceLlmRequest("{{SAID}}");
            req["max_tokens"] = 48; // as the pipeline sends it
            File.WriteAllText(Path.Combine(dir, "llm-request.json"), req.ToString());
            string evalPath = Path.GetFullPath(Path.Combine("..", "tools", "voicebench", "eval.jsonl"));
            int n = 0, hit = 0;
            var sb = new StringBuilder();
            foreach (var line in File.ReadAllLines(evalPath))
            {
                if (string.IsNullOrWhiteSpace(line)) continue;
                var row = JObject.Parse(line);
                var t0 = System.Diagnostics.Stopwatch.StartNew();
                var all = v.FastMatchAll((string)row["text"]);
                double ms = t0.Elapsed.TotalMilliseconds;
                var res = new JObject { ["id"] = row["id"], ["ms"] = Math.Round(ms, 3) };
                res["matches"] = all == null ? null : new JArray(all.Select(m => new JObject { ["command"] = m.Command.Name, ["args"] = m.Args, ["corrected"] = m.Corrected }));
                sb.AppendLine(res.ToString(Newtonsoft.Json.Formatting.None));
                n++;
                if (all != null) hit++;
            }
            File.WriteAllText(Path.Combine(dir, "fastpath.jsonl"), sb.ToString());
            Check(n >= 170 && cmds.Count >= 13, $"voice bench export: {cmds.Count} commands, {n} sentences, the fast path matched {hit} ({Path.GetFullPath(dir)})");
        }

        /// The decision model's mechanics without the AI service: an oracle embedder gives each example and test clause the
        /// vector of the command it belongs to, so what is checked is the rest: clauses and their order, values read
        /// from the schema, a verbless part continuing the previous command, "none", verbatim
        /// commands only with their verb, disabled / removed commands never offered, the registry complete.
        private static void Decider(MetaCodexShell shell)
        {
            var v = shell.Voice;
            foreach (var id in shell.PanelIds.ToList()) shell.ClosePanel(id);
            var lint = v.All.Where(c => c.Owner == "shell").SelectMany(c => VoiceCommands.Lint(c).Select(w => c.Name + ": " + w)).ToList();
            Check(lint.Count == 0, lint.Count == 0 ? $"every shell command is complete for voice and agents ({v.All.Count(c => c.Owner == "shell")} commands: description, samples, described parameters)" : "registry gaps: " + string.Join("; ", lint));
            var reg = v.Describe();
            var regCmds = (JArray)reg["commands"];
            Check(regCmds.Count == v.All.Count(c => c.Enabled) && regCmds.All(c => c["samples"] is JArray && c["parameters"]?["properties"] != null && c["synonyms"] != null),
                $"registry JSON (voice registry): {regCmds.Count} enabled commands, each with schema, names, phrases, samples");

            string J(List<string> l) => string.Join(" | ", l);
            var cl = VoiceDecider.Clauses("Turn on ambient focus and set the intensity to 100%.");
            Check(J(cl) == "turn on ambient focus | set the intensity to 100%", "clauses in the spoken order: " + J(cl));
            cl = VoiceDecider.Clauses("Turn on the focus and then set the radius to two meters and a half.");
            Check(J(cl) == "turn on the focus | set the radius to two meters and a half", "clauses (“and a half” stays in its part): " + J(cl));
            cl = VoiceDecider.Clauses("Hey MetaCodex, open the files please!");
            Check(J(cl) == "open the files", "fillers and punctuation off: " + J(cl));
            var vals = new (string said, string want)[]
            {
                ("at 30 percent", "30 percent"), ("to two meters and a half", "2.5 m"), ("to half a meter", "half m"), ("to the maximum", "max"), ("to fifty percent", "50 percent"),
                ("twenty three", "23"), ("ninety nine percent", "99 percent"), ("more transparent", "-"), ("a bit bigger", "+"), ("a little smaller", "-"), ("a bit more", "+"), ("50 cm", "50 cm"),
            };
            var badVals = vals.Where(x => VoiceDecider.ValueText(VoiceDecider.Fold(x.said)) != x.want).Select(x => $"{x.said} -> {VoiceDecider.ValueText(VoiceDecider.Fold(x.said)) ?? "null"} (want {x.want})").ToList();
            Check(badVals.Count == 0, badVals.Count == 0 ? $"values read ({vals.Length} cases: percent, metres, half, max, number words, more / less)" : "values: " + string.Join("; ", badVals));
            string dec = VoiceDecider.ValueText(VoiceDecider.Clauses("set the distance to 2,5 meters")[0]);
            Check(dec == "2.5 m", "a decimal comma: “2,5 meters” -> " + dec);

            // the oracle: example texts -> their command's axis, test clauses -> the intended command's axis
            var probe = new VoiceDecider(v, (t, done) => done(null, "unused"));
            var ex = probe.BuildExamples();
            var axes = new Dictionary<string, int>(); // command -> its axis; commands registered later get new ones
            int Axis(string c) { if (!axes.TryGetValue(c, out int i)) { i = axes.Count; axes[c] = i; } return i; }
            var oracle = new Dictionary<string, string>();
            foreach (var (t, c) in ex) if (!oracle.ContainsKey(t)) oracle[t] = c;
            var said = new Dictionary<string, string>
            {
                ["turn on ambient focus"] = "set_focus", ["set the intensity to 100 percent"] = "set_setting", ["set the volume to 30 percent"] = "set_setting",
                ["open the files"] = "open_panel", ["show the keyboard"] = "show_keyboard", ["hello world"] = "type_text", ["type hello there"] = "type_text",
                ["run ls in the terminal"] = "run_in_terminal", ["use the pitch black preset"] = "focus_preset", ["turn the carousel to the right"] = "spin_carousel",
                ["visit the website github dot com"] = "open_url", ["turn off the focus"] = "set_focus", ["let me see the settings"] = "open_panel", ["good morning"] = "none",
                ["greet me"] = "say_hello",
            };
            float[] Vec(string text)
            {
                string t = text.StartsWith("query: ") ? text.Substring(7) : text;
                var vec = new float[128];
                string c = said.TryGetValue(t, out var s1) ? s1 : oracle.TryGetValue(t, out var s2) ? s2 : null;
                if (c != null) vec[Axis(c)] = 1f;
                return vec;
            }
            var d = new VoiceDecider(v, (texts, done) => done(texts.Select(Vec).ToArray(), null));
            string err = "pending";
            d.Build(e => err = e);
            Check(err == null && d.Ready && d.ExampleCount > 300, $"decision model built from the registry: {d.Status} ({err})");
            VoiceDecider.Result D(string text) { VoiceDecider.Result r = null; d.Decide(text, x => r = x); return r; }
            string M(VoiceDecider.Result r) => r == null ? "no answer" : string.Join(" ; ", r.Matches.Select(m => m.Command.Name + " " + m.Args.ToString(Newtonsoft.Json.Formatting.None))) + (r.Error != null ? " ERROR " + r.Error : "");
            bool Is(VoiceDecider.Result r, int i, string cmd, string key = null, string val = null) =>
                r != null && r.Matches.Count > i && r.Matches[i].Command.Name == cmd && (key == null || (string)r.Matches[i].Args[key] == val);

            var r1 = D("Turn on ambient focus and set the intensity to 100 percent.");
            // focus on: set_focus on and set_setting focus on run the same SetFocus (as in voicewords)
            bool focusOn1 = Is(r1, 0, "set_focus", "state", "on") || Is(r1, 0, "set_setting", "setting", "focus") && (string)r1.Matches[0].Args["value"] == "on";
            Check(r1.Matches.Count == 2 && focusOn1 && Is(r1, 1, "set_setting", "setting", "focus_intensity") && (string)r1.Matches[1].Args["value"] == "100 percent",
                "composite, in order: focus on, then intensity 100 % -> " + M(r1));
            var r2 = D("Set the volume to 30 percent and the softness to half a metre.");
            Check(r2.Matches.Count == 2 && Is(r2, 0, "set_setting", "setting", "volume") && (string)r2.Matches[0].Args["value"] == "30 percent" && Is(r2, 1, "set_setting", "setting", "focus_softness")
                && (string)r2.Matches[1].Args["value"] == "half m" && r2.Clauses[1].Note != null, "a verbless part continues the command: volume 30 %, softness half a metre -> " + M(r2));
            var r3 = D("open the files and then the terminal");
            Check(r3.Matches.Count == 2 && Is(r3, 0, "open_panel", "panel", "files") && Is(r3, 1, "open_panel", "panel", "terminals"), "“open the files and then the terminal” -> " + M(r3));
            var r4 = D("hello world");
            Check(r4.Matches.Count == 0 && (r4.Clauses.FirstOrDefault()?.Note ?? "").Contains("verb"), "verbatim typing never without its verb: “hello world” -> " + (r4.Clauses.FirstOrDefault()?.Note ?? M(r4)));
            var r5 = D("type hello there");
            Check(Is(r5, 0, "type_text", "text", "hello there"), "“type hello there” -> " + M(r5));
            var r6 = D("run ls in the terminal");
            Check(Is(r6, 0, "run_in_terminal", "command", "ls"), "“run ls in the terminal” -> " + M(r6));
            var r7 = D("use the pitch black preset");
            Check(Is(r7, 0, "focus_preset", "preset", "blackout"), "“use the pitch black preset” -> " + M(r7));
            var r8 = D("turn the carousel to the right");
            Check(Is(r8, 0, "spin_carousel", "direction", "right") && r8.Matches[0].Args["panels"] == null, "“turn the carousel to the right” -> " + M(r8));
            var r9 = D("visit the website github dot com");
            Check(Is(r9, 0, "open_url") && MetaCodexShell.SpokenUrl((string)r9.Matches[0].Args["url"]) == "https://github.com/", "“visit the website github dot com” -> " + M(r9) + " -> " + MetaCodexShell.SpokenUrl((string)r9.Matches.FirstOrDefault()?.Args["url"]));
            var r10 = D("good morning");
            Check(r10.Matches.Count == 0 && r10.Clauses.Count == 1 && r10.Clauses[0].Command == "none", "chit-chat is no command: " + r10);
            var j10 = r1.ToJson();
            Check(j10["matches"] is JArray jm && jm.Count == 2 && j10["clauses"] is JArray jc && jc.Count == 2 && jc[0]["score"] != null, "decide JSON (voice decide): matches + clauses with scores");

            // disabled: not offered at once (before the rebuild), not in the rebuilt examples, back when enabled
            v.SetEnabled("show_keyboard", false);
            var r11 = D("show the keyboard");
            bool staleOk = d.Usable && !d.Ready && r11.Matches.Count == 0;
            err = "pending"; d.Build(e => err = e);
            var r12 = D("show the keyboard");
            bool rebuiltOk = err == null && d.Ready && r12.Matches.Count == 0 && probe.BuildExamples().All(e => e.command != "show_keyboard");
            v.SetEnabled("show_keyboard", true);
            err = "pending"; d.Build(e => err = e);
            var r13 = D("show the keyboard");
            Check(staleOk && rebuiltOk && Is(r13, 0, "show_keyboard"), $"a disabled command is never offered (at once: {staleOk}, rebuilt: {rebuiltOk}) and is back when enabled: {M(r13)}");

            // a plugin's command: in the registry with its samples, gone when the plugin is disabled
            var host = shell.Scripts;
            string pdir = Path.Combine(host.PluginsDir, "voicetest");
            Directory.CreateDirectory(pdir);
            File.WriteAllText(Path.Combine(pdir, "plugin.json"), "{ \"id\": \"voicetest\", \"name\": \"Voice test\", \"version\": \"1.0.0\", \"description\": \"a voice command for the decider test\" }");
            File.WriteAllText(Path.Combine(pdir, "main.lua"),
                "voice.command{ name = \"say_hello\", description = \"Say hello back (test plugin)\", phrases = { \"say hello\" },\n" +
                "  samples = { \"greet me\", \"say hi to me\", \"give me a hello\", \"welcome me back\" }, run = function(a) return \"hello\" end }\n" +
                "voice.command{ name = \"bare_test\", run = function(a) return \"ok\" end }\n");
            host.Scan();
            var pe = host.Find("plugin/voicetest");
            var hello = v.Find("say_hello");
            bool inReg = v.Describe()["commands"].Any(c => (string)c["name"] == "say_hello" && ((JArray)c["samples"]).Count == 4 && ((JArray)c["warnings"]).Count == 0);
            bool warned = pe != null && pe.Log.Any(l => l.Contains("bare_test") && l.Contains("samples"));
            err = "pending"; d.Build(e => err = e);
            var r14 = D("greet me");
            host.SetEnabled("plugin/voicetest", false);
            bool gone = v.Find("say_hello") == null && v.Find("bare_test") == null && !v.Describe()["commands"].Any(c => (string)c["name"] == "say_hello")
                && probe.BuildExamples().All(e => e.command != "say_hello");
            var r15 = D("greet me");
            Check(hello != null && inReg && warned && Is(r14, 0, "say_hello") && gone && r15.Matches.Count == 0,
                $"plugin command: registered with samples ({inReg}), lint warns about the bare one ({warned}), decided ({M(r14)}); plugin disabled: gone everywhere ({gone}), not offered ({M(r15)})");
            host.SetEnabled("plugin/voicetest", true);
            try { Directory.Delete(pdir, true); } catch (Exception) { }
            host.Scan();

            // the shell's pipeline: fast path misses -> the decision model -> the commands run in order
            var orig = shell.Decider;
            shell.UseDeciderForTest(d, true);
            shell.Settings.focusEnabled = true;
            VoiceResult vr = null;
            // "let me see ..." matches no phrase template: the whole sentence goes to the decision model
            shell.VoiceProcessText("Turn off the focus, then let me see the settings.", x => vr = x);
            Check(vr != null && vr.Ok && !shell.Settings.focusEnabled && shell.IsPanelOpen("settings") && vr.Via == "decide",
                "the shell decides and runs it: “turn off the focus, then let me see the settings” -> " + vr);
            shell.UseDeciderForTest(orig, false);
            foreach (var id in shell.PanelIds.ToList()) shell.ClosePanel(id);
        }

        /// The 180 English bench sentences (English only since 2026-11-12) through the app's own pipeline (fast path -> decision model) with the real model in the
        /// dev AI service (tools/local-ai-dev.sh pc start). Writes Bench/voice/app.jsonl; tools/voicebench/score_app.py scores it.
        private static void VoiceDecideBench(MetaCodexShell shell)
        {
            string devPath = Path.Combine("Bench", "qvac-dev.json");
            if (!File.Exists(devPath)) { Fail("decision bench: no Bench/qvac-dev.json (tools/local-ai-dev.sh pc start)"); return; }
            var dev = JObject.Parse(File.ReadAllText(devPath));
            string url = ((string)dev["url"]).TrimEnd('/') + "/v1/embeddings";
            var http = new System.Net.Http.HttpClient { Timeout = TimeSpan.FromSeconds(120) };
            http.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", (string)dev["token"]);
            int calls = 0;
            void Embed(IList<string> texts, Action<float[][], string> done)
            {
                try
                {
                    calls++;
                    string body = new JObject { ["input"] = new JArray(texts) }.ToString(Newtonsoft.Json.Formatting.None);
                    string txt = System.Threading.Tasks.Task.Run(async () =>
                    {
                        var resp = await http.PostAsync(url, new System.Net.Http.StringContent(body, Encoding.UTF8, "application/json")).ConfigureAwait(false);
                        return await resp.Content.ReadAsStringAsync().ConfigureAwait(false);
                    }).Result;
                    var j = JObject.Parse(txt);
                    if (!(j["data"] is JArray data)) { done(null, (string)j["error"] ?? "no data"); return; }
                    var vecs = new float[data.Count][];
                    foreach (var e in data) vecs[(int)e["index"]] = ((JArray)e["embedding"]).Select(x => (float)x).ToArray();
                    done(vecs, null);
                }
                catch (Exception ex) { done(null, ex.GetBaseException().Message); }
            }
            // honest numbers: no sample and no "none" example may be a bench sentence (AGENTS.md, the command registry)
            string evalFile = Path.GetFullPath(Path.Combine("..", "tools", "voicebench", "eval.jsonl"));
            var benchTexts = new HashSet<string>(File.ReadAllLines(evalFile).Where(l => l.Trim().Length > 0).Select(l => VoiceDecider.Fold((string)JObject.Parse(l)["text"]).Trim('.', ' ')));
            var copies = shell.Voice.All.SelectMany(c => (c.Samples ?? new string[0]).Select(x => c.Name + ": " + x)).Concat(VoiceDecider.NoneExamples.Select(x => "none: " + x))
                .Where(x => benchTexts.Contains(VoiceDecider.Fold(x.Substring(x.IndexOf(": ") + 2)).Trim('.', ' '))).ToList();
            Check(copies.Count == 0, copies.Count == 0 ? "no sample or none example is a bench sentence" : "bench sentences copied into the registry: " + string.Join("; ", copies));
            var d = new VoiceDecider(shell.Voice, Embed);
            var sw = System.Diagnostics.Stopwatch.StartNew();
            string err = "pending";
            d.Build(e => err = e);
            Check(err == null && d.Ready, $"decision model built with the real model: {d.Status} in {sw.ElapsedMilliseconds} ms ({err ?? "ok"})");
            if (!d.Ready) return;
            string evalPath = Path.GetFullPath(Path.Combine("..", "tools", "voicebench", "eval.jsonl"));
            var sb = new StringBuilder();
            int n = 0, fast = 0, decided = 0, none = 0, errors = 0;
            var ms = new List<double>();
            foreach (var line in File.ReadAllLines(evalPath))
            {
                if (string.IsNullOrWhiteSpace(line)) continue;
                var row = JObject.Parse(line);
                string text = (string)row["text"];
                var t0 = System.Diagnostics.Stopwatch.StartNew();
                var fm = shell.Voice.FastMatchAll(text);
                var o = new JObject { ["id"] = row["id"] };
                if (fm != null)
                {
                    fast++;
                    o["via"] = "fast";
                    o["matches"] = new JArray(fm.Select(m => new JObject { ["command"] = m.Command.Name, ["args"] = m.Args }));
                }
                else
                {
                    VoiceDecider.Result res = null;
                    d.Decide(text, x => res = x);
                    var jr = res.ToJson();
                    o["via"] = "decide";
                    o["matches"] = jr["matches"];
                    o["clauses"] = jr["clauses"];
                    if (res.Error != null) { o["error"] = res.Error; errors++; }
                    if (res.Matches.Count > 0) decided++; else none++;
                    ms.Add(t0.Elapsed.TotalMilliseconds);
                }
                o["ms"] = Math.Round(t0.Elapsed.TotalMilliseconds, 2);
                sb.AppendLine(o.ToString(Newtonsoft.Json.Formatting.None));
                n++;
            }
            string outPath = Path.Combine("Bench", "voice", "app.jsonl");
            Directory.CreateDirectory(Path.GetDirectoryName(outPath));
            File.WriteAllText(outPath, sb.ToString());
            ms.Sort();
            double med = ms.Count > 0 ? ms[ms.Count / 2] : 0, p90 = ms.Count > 0 ? ms[(int)(ms.Count * 0.9)] : 0;
            Check(n >= 170 && errors == 0, $"decision bench: {n} sentences, fast path {fast}, decision model {decided} + {none} no command, {errors} errors; decide {med:0} ms median, {p90:0} ms p90 (HTTP + model, PC) -> {Path.GetFullPath(outPath)} (score: tools/voicebench/score_app.py)");
            // the commands added with the decision model (not in the bench sentences): own wordings, not their samples
            var extra = new (string said, string cmd, string key, string val)[]
            {
                ("switch to the gentle focus preset", "focus_preset", "preset", "subtle"), ("put on the cinema preset please", "focus_preset", "preset", "blackout"),
                ("spin the ring to the left", "spin_carousel", "direction", "left"), ("move the windows towards the right", "spin_carousel", "direction", "right"),
                ("i want to select an area on this panel", "select_mode", "state", "on"), ("switch on the selection tool", "select_mode", "state", "on"),
                ("visit the site wikipedia dot org", "open_url", null, null), ("navigate to example dot com", "open_url", null, null),
            };
            var got = new List<string>();
            int hits = 0;
            foreach (var x in extra)
            {
                var fm2 = shell.Voice.FastMatchAll(x.said);
                List<VoiceMatch> ms2 = fm2;
                if (ms2 == null) { VoiceDecider.Result r2 = null; d.Decide(x.said, rr => r2 = rr); ms2 = r2?.Matches; }
                var first = ms2?.FirstOrDefault();
                bool ok = first != null && first.Command.Name == x.cmd && (x.key == null || (string)first.Args[x.key] == x.val)
                    && (x.cmd != "open_url" || MetaCodexShell.SpokenUrl((string)first.Args["url"]) is string u && (u.Contains("wikipedia.org") || u.Contains("example.com")));
                if (ok) hits++;
                got.Add($"{(ok ? "ok" : "MISS")} “{x.said}” -> {(first == null ? "none" : first.Command.Name + " " + first.Args.ToString(Newtonsoft.Json.Formatting.None))}{(fm2 != null ? " (fast)" : "")}");
            }
            Check(hits >= 6, $"new commands with the real model: {hits}/{extra.Length}\n    " + string.Join("\n    ", got));
        }

        /// 0.7.3 chores: panels keep the UI scale when they open; VOICE fits its panel; no SHARPNESS; the wrist menu.
        private static void Qol(MetaCodexShell shell, Transform head, SimulatedHands hands)
        {
            foreach (var id in shell.PanelIds.ToList()) shell.ClosePanel(id);
            // a panel opened after the UI scale was set keeps it (the open animation used to end at 1.0, and the next
            // settings change put it back: "the UI resizes by itself")
            float s0 = shell.Settings.uiScale;
            shell.Settings.uiScale = 1.3f;
            shell.OnSettingsChanged();
            shell.OpenPanel("files");
            var fp = shell.PanelsById["files"];
            fp.ForceRefresh();
            Check(Mathf.Abs(fp.transform.localScale.x - 1.3f) < 0.001f && Mathf.Abs(fp.BaseScale.x - 1.3f) < 0.001f,
                $"a panel opened at UI scale 1.3 stays 1.3 when its open animation ends ({fp.transform.localScale.x:F2}): no resizing by itself");
            shell.ClosePanel("files");
            shell.Settings.uiScale = s0;
            shell.OnSettingsChanged();

            // VOICE: everything inside the panel, the FEMALE and MALE voices in line
            shell.OpenPanel("voice");
            var vp = shell.PanelsById["voice"];
            vp.ForceRefresh();
            var vc = vp.Content;
            // the header's own buttons (close, pin) sit above the content on purpose: only the body is checked
            bool Inside(UiElement e) => e.Rect.xMin >= vc.xMin - 0.001f && e.Rect.xMax <= vc.xMax + 0.001f && e.Rect.yMax <= vc.yMax + 0.001f;
            var outside = vp.Elements.Where(e => e.Visible && e.Rect.yMin >= vc.yMin - 0.001f && !Inside(e)).Select(e => e.GetType().Name + (e is UiSegmented sg ? " " + sg.Text : "")).ToList();
            var fm = vp.Elements.OfType<UiSegmented>().Where(x => x.Text == "FEMALE" || x.Text == "MALE").ToList();
            bool aligned = fm.Count == 2 && Mathf.Abs(fm[0].Rect.x - fm[1].Rect.x) < 1e-4f && Mathf.Abs(fm[0].Rect.width - fm[1].Rect.width) < 1e-4f && fm[0].LabelWidth == fm[1].LabelWidth;
            Check(outside.Count == 0 && aligned, outside.Count == 0 && aligned ? "VOICE: every control inside the panel; FEMALE and MALE voices line up" : $"VOICE layout: outside {string.Join(", ", outside)}; aligned {aligned}");
            RenderPanel(vp, "ui-voice-panel.png");
            shell.ClosePanel("voice");
            // the same rule for every panel (reported; VOICE is the one that must hold)
            // the body only, as for VOICE: the header's close / pin buttons sit right of the content on purpose (they were
            // every panel's "UiButton" in this line, and the first 12 of those hid any real spill)
            var spill = shell.PanelsById.Values.SelectMany(p => p.Elements.Where(e => e.Visible && e.Rect.yMin >= p.Content.yMin - 0.001f && (e.Rect.xMax > p.Content.xMax + 0.002f || e.Rect.yMax > p.Content.yMax + 0.002f))
                .Select(e => p.Id + ":" + e.GetType().Name + (e is UiButton sb && !string.IsNullOrEmpty(sb.Text) ? " " + sb.Text.Replace("\n", " ") : ""))).Distinct().ToList();
            Line("controls past their panel's edge (all panels' bodies): " + (spill.Count == 0 ? "none" : spill.Count + ": " + string.Join(", ", spill)));

            // SHARPNESS is gone: no slider, no voice setting, the render scale fixed at 1.6
            bool sliderGone = !shell.PanelsById["settings"].Elements.OfType<UiSlider>().Any(x => x.Text == "SHARPNESS");
            bool voiceGone = !(shell.Voice.Find("set_setting").Properties["setting"]["enum"] as JArray).Any(x => (string)x == "sharpness");
            Check(sliderGone && voiceGone && MetaCodexShell.RenderScale == 1.6f && Mathf.Approximately(shell.Settings.renderScale, 1.6f),
                $"SHARPNESS gone (slider {sliderGone}, voice {voiceGone}); the render scale is {MetaCodexShell.RenderScale}");

            // the WRIST main menu has its own section (wrist)
            // no TABLE on the wrist any more (our table keyboard is gone): KEYS opens the system keyboard; one voice command for it
            bool noTable = shell.Wrist != null && !shell.Wrist.Outer.Any(it => it.Label == "TABLE") && shell.Wrist.Outer.Any(it => it.Label == "KEYS" && it.Icon == "keyboard")
                && !UiIconAtlas.TryGet("tablekeys", out _) && shell.Voice.Find("table_keyboard") == null;
            Check(noTable, $"the wrist has KEYS and no TABLE, and there is no table_keyboard command ({noTable})");
            string Cmd(string said) => shell.Voice.FastMatch(said)?.Command?.Name ?? "none";
            Check(Cmd("open the table keyboard") == "show_keyboard" && Cmd("turn on the surface keyboard") == "show_keyboard" && Cmd("open the keyboard") == "show_keyboard",
                $"voice (all the system keyboard): “open the table keyboard” -> {Cmd("open the table keyboard")}, “turn on the surface keyboard” -> {Cmd("turn on the surface keyboard")}, “open the keyboard” -> {Cmd("open the keyboard")}");
        }

        /// The WRIST main menu (owner 2026-11-09): firm on the wrist, never closing or running off while the right hand
        /// covers the left one, a press only for a real poke, every change animated both ways; pictures of the timeline.
        private static void Wrist(MetaCodexShell shell, Transform head, SimulatedHands hands)
        {
            foreach (var id in shell.PanelIds.ToList()) shell.ClosePanel(id);
            shell.SetWristMenu(true);
            Check(!shell.IsPanelOpen("dock"), "WRIST mode puts the dock away");
            var eye = head.position;
            var rot0 = head.rotation;
            var palm = eye + head.forward * 0.33f - head.up * 0.13f - head.right * 0.03f;
            var toEye = (eye - palm).normalized;
            var lFingers = (head.right * 0.9f + head.forward * 0.35f).normalized;
            void LeftAt(Vector3 at) => hands.SetOpen(Handedness.Left, at, lFingers, -toEye); // palm away: the back faces the eyes
            var rightRest = eye + head.forward * 0.25f + head.right * 0.16f - head.up * 0.22f;
            void RightAway() => hands.SetOpen(Handedness.Right, rightRest, head.forward, -head.up);
            LeftAt(palm);
            RightAway();
            var w = shell.Wrist;
            w.HideNow();
            void Step(int n, float dt = 0.05f) { for (int i = 0; i < n; i++) { shell.TickGesturesForTest(dt); shell.TickWristForTest(dt); } }
            void Tip(Vector3 local) => hands.Frames[1].IndexTip = w.transform.TransformPoint(local);
            // a real poke: over the part for a moment, into it, back out (steps of 0.03 s: the gesture engine ticks at
            // 36 Hz, so a shorter step would not pass the hand on)
            void Poke(Vector3 world)
            {
                var lp = w.transform.InverseTransformPoint(world);
                for (int k = 0; k < 3; k++) { Tip(new Vector3(lp.x, lp.y, -0.02f)); Step(1, 0.03f); }
                Tip(new Vector3(lp.x, lp.y, 0.002f)); Step(1, 0.03f);
                Tip(new Vector3(lp.x, lp.y, -0.035f)); Step(1, 0.03f);
            }
            // the face comes in with its animation, never at once
            Step(2);
            float mid = w.Shown01;
            Step(10);
            Check(mid > 0.05f && mid < 0.6f && w.Visible && w.Shown01 > 0.99f && !w.Expanded,
                $"looking at the wrist as at a watch brings the watch face in over {w.AppearTime:F2} s: {mid:F2} after 0.1 s, {w.Shown01:F2} after 0.6 s");
            // it floats in front of the hand that carries it, towards the eyes (0.7.5: the fingers were in front of the rings)
            float ahead = Vector3.Distance(eye, hands.Frames[0].Wrist) - Vector3.Distance(eye, w.transform.position);
            Check(ahead > 0.07f, $"the menu floats {ahead * 100f:F1} cm in front of the left wrist, towards the eyes: the hand stays behind the rings");
            Poke(w.WorldOf(-2));
            Step(3);
            float bloomMid = w.Expand01;
            Step(12);
            Check(w.Expanded && bloomMid > 0.05f && bloomMid < 0.7f && w.Expand01 > 0.99f,
                $"touching the face unfolds the rings over {w.BloomTime:F2} s ({bloomMid:F2} after 0.18 s, {w.Expand01:F2} after 0.78 s)");
            // a picture: the finger hovering over FILES
            var hoverAt = w.transform.InverseTransformPoint(w.WorldOf(2));
            Tip(new Vector3(hoverAt.x, hoverAt.y, -0.012f));
            Step(4);
            int verts = w.GetComponent<MeshFilter>().sharedMesh.vertexCount;
            Check(w.Hover == 2 && verts > 200 && verts < 6000, $"the fingertip over FILES highlights it (hover {w.Hover}); the menu is one mesh of {verts} vertices");
            RenderAt("ui-wrist-menu.png", eye, Quaternion.LookRotation(w.transform.position - eye, head.up), 34f, 1400, 1050);
            // the hand cut-outs (capsules on the joints, drawn last, clear to the real hands): the left hand is behind the
            // menu and must not cut it (0.7.5, owner: "the hand cuts it off"); the right fingertip in front shows through
            shell.TickFocusForTest(0.1f); // places the capsules on the hands (UI occlusion is on by default)
            var camRot = Quaternion.LookRotation(w.transform.position - eye, head.up);
            const int cw = 700, ch = 525;
            w.DepthWrite = false; shell.TickWristForTest(0.01f);
            UnityEngine.Object.DestroyImmediate(RenderRgba(null, eye, camRot, 34f, cw, ch));
            var cutNoDepth = RenderRgba("ui-wrist-cutout-no-depth.png", eye, camRot, 34f, cw, ch);
            w.DepthWrite = true; shell.TickWristForTest(0.01f);
            var cut = RenderRgba("ui-wrist-cutout.png", eye, camRot, 34f, cw, ch);
            int Lit(Texture2D t) => t.GetPixels32().Count(c => c.a > 40);
            float AlphaAt(Texture2D t, Vector3 world) { var q = ScreenOf(world, eye, camRot, 34f, cw, ch); return t.GetPixel(q.x, q.y).a; }
            var coreBand = w.transform.TransformPoint(new Vector3(0.0096f, 0f, 0f)); // on the core's ring
            int litNoDepth = Lit(cutNoDepth), litDepth = Lit(cut);
            float coreNoDepth = AlphaAt(cutNoDepth, coreBand), coreDepth = AlphaAt(cut, coreBand), tipA = AlphaAt(cut, hands.Frames[1].IndexTip);
            Check(litDepth > litNoDepth * 1.15f && coreDepth > 0.25f && coreNoDepth < 0.1f && tipA < 0.1f,
                $"the left hand behind the menu no longer cuts it: {litNoDepth} -> {litDepth} lit pixels, the core ring {coreNoDepth:F2} -> {coreDepth:F2}; the right fingertip in front still shows through ({tipA:F2})");
            UnityEngine.Object.DestroyImmediate(cutNoDepth);
            UnityEngine.Object.DestroyImmediate(cut);
            shell.HandCutoutsForTest = false; shell.TickFocusForTest(0.05f); shell.HandCutoutsForTest = true; // capsules away for the next pictures
            shell.ClosePanel("files");
            int pressed = -99;
            w.Pressed += k => pressed = k;
            Poke(w.WorldOf(2));
            Check(pressed == 2 && shell.IsPanelOpen("files"), $"touching the FILES tile opens FILES (pressed {pressed})");
            shell.ClosePanel("clipboard");
            Step(8); // past the 0.3 s between two presses (a double tap is not two commands)
            Poke(w.WorldOf(w.Inner.Count + w.Outer.FindIndex(x => x.Label == "CLIP")));
            Check(shell.IsPanelOpen("clipboard"), "the outer ring works too: CLIP opens CLIPBOARD");
            // a fingertip that goes into the surface and stays there presses once, not again and again
            Step(8);
            int presses = 0;
            w.Pressed += k => presses++;
            for (int k = 0; k < 3; k++) { Tip(new Vector3(hoverAt.x, hoverAt.y, -0.02f)); Step(1, 0.03f); }
            Tip(new Vector3(hoverAt.x, hoverAt.y, 0.003f));
            Step(10);
            Check(presses == 1, $"a fingertip that stays in the surface presses once ({presses} presses in 0.5 s)");
            // a touch, not a push through (owner 2026-11-09: "I need to pass my finger through more than I expected"): the
            // tracked tip 5 mm short of the surface presses
            Tip(new Vector3(hoverAt.x, hoverAt.y, -0.04f)); Step(8);
            presses = 0;
            for (int k = 0; k < 3; k++) { Tip(new Vector3(hoverAt.x, hoverAt.y, -0.02f)); Step(1, 0.03f); }
            Tip(new Vector3(hoverAt.x, hoverAt.y, -0.0045f)); Step(2, 0.03f);
            Check(presses == 1, $"the fingertip 5 mm short of the surface presses (a touch, not a push through): {presses} press");
            Tip(new Vector3(hoverAt.x, hoverAt.y, -0.04f));
            Step(8);
            // on the way to the left side the fingertip crosses the face (0.7.6: that folded the menu): no press, whether
            // it slides along the surface or dips through it sideways
            presses = 0;
            Tip(new Vector3(0.0205f, 0.002f, -0.02f)); Step(3, 0.03f);   // in front of the gap between the face and the inner ring
            for (int k = 0; k <= 10; k++) { Tip(new Vector3(Mathf.Lerp(0.0205f, -0.040f, k / 10f), 0.002f, 0.001f)); Step(1, 0.03f); }
            Tip(new Vector3(-0.04f, 0.002f, -0.03f)); Step(2, 0.03f);
            bool slid = w.Expanded && presses == 0;
            Tip(new Vector3(0.03f, -0.002f, -0.02f)); Step(1, 0.03f);
            Tip(new Vector3(0.0f, -0.002f, 0.002f)); Step(1, 0.03f);     // 3 cm sideways while going in, over the face
            Tip(new Vector3(-0.03f, -0.002f, 0.004f)); Step(1, 0.03f);
            Tip(new Vector3(-0.03f, -0.002f, -0.03f)); Step(2, 0.03f);
            Check(slid && w.Expanded && presses == 0, $"a fingertip sliding across the face, or dipping through it sideways, presses nothing: still unfolded ({w.Expanded}), {presses} presses");

            // the right hand over the left side of the menu hides the left hand from the cameras: lost, the menu stays
            // open and does not move; a jump of the left hand's pose under it moves nothing
            Tip(new Vector3(-0.055f, 0.010f, -0.015f));
            Step(2);
            var pos0 = w.transform.position;
            hands.Frames[0].Tracked = false;
            Step(30);
            float drift = (w.transform.position - pos0).magnitude;
            Check(w.Engaged && w.Visible && w.Expanded && w.Shown01 > 0.99f && drift < 0.001f,
                $"the left hand lost for 1.5 s under the right one: the menu stays open ({w.Expanded}, shown {w.Shown01:F2}) and still ({drift * 1000f:F1} mm)");
            LeftAt(palm + head.right * 0.25f); Step(4);
            LeftAt(palm); Step(2);
            drift = (w.transform.position - pos0).magnitude;
            Check(drift < 0.001f && w.Expanded, $"the left wrist's pose jumping 25 cm under the right hand moves nothing ({drift * 1000f:F1} mm)");
            // the right hand away: a short jump of the tracked wrist is ignored, a real move followed at once
            RightAway();
            Step(14);
            // (up, away from the right hand: a jump towards it would be the hands over each other, which holds it)
            var pos1 = w.transform.position;
            LeftAt(palm + head.up * 0.25f); Step(3);
            LeftAt(palm); Step(2);
            float jumped = (w.transform.position - pos1).magnitude;
            LeftAt(palm + head.up * 0.05f); Step(6);
            float follow = Vector3.Dot(w.transform.position - pos1, head.up);
            Step(30);
            float settled = Vector3.Dot(w.transform.position - pos1, head.up);
            Check(!w.Engaged && jumped < 0.01f && settled > 0.03f && follow > 0.85f * settled,
                $"right hand away: a 0.15 s jump of the tracked wrist (25 cm) drags it {jumped * 100f:F1} cm; a real 5 cm move of the wrist is followed {follow * 100f:F1} of {settled * 100f:F1} cm within 0.3 s");
            LeftAt(palm); Step(8);
            // the left hand lost with the right hand away: it waits (1.5 s) and is still there when the hand is back
            hands.Frames[0].Tracked = false;
            Step(20);
            bool waited = w.Visible && w.Expanded;
            LeftAt(palm);
            Step(4);
            Check(waited && w.Expanded && w.Shown01 > 0.99f, $"the left hand lost for 1 s, the right hand away: the menu waits ({waited}) and is open when the hand is back");

            // looking away: it waits, folds and goes with its animation
            head.rotation = Quaternion.Euler(0f, 70f, 0f) * rot0;
            Step(8);
            bool grace = w.Expanded && w.Visible;
            Step(5);
            float folding = w.Expand01;
            Step(16);
            Check(grace && folding > 0.1f && folding < 0.9f && !w.Visible && !w.Expanded,
                $"looking away: it waits {w.TurnAwayGrace:F2} s ({grace}), folds with its animation ({folding:F2} 0.25 s later), then goes (shown {w.Shown01:F2})");
            // half way out, a look back brings it back from where it is
            head.rotation = rot0;
            Step(12);
            head.rotation = Quaternion.Euler(0f, 70f, 0f) * rot0;
            Step(12);
            float leaving = w.Shown01;
            head.rotation = rot0;
            Step(2);
            float turned = w.Shown01;
            Step(3);
            float after = w.Shown01;
            Check(leaving > 0.2f && leaving < 0.95f && turned > 0.1f && after > turned, $"a look back while it goes brings the face back from where it is: {leaving:F2} -> {turned:F2} -> {after:F2} (no jump)");
            Step(10);

            // pictures of the timeline: the face appearing, the rings unfolding
            var camRot2 = Quaternion.LookRotation(w.transform.position - eye, head.up);
            int fi = 0;
            foreach (var (pp, bb) in new[] { (0.22f, 0f), (0.45f, 0f), (0.7f, 0f), (1f, 0f), (1f, 0.16f), (1f, 0.32f), (1f, 0.5f), (1f, 0.7f), (1f, 1f) })
            {
                w.PoseTimelineForTest(pp, bb);
                RenderAt($"ui-wrist-anim-{fi++}.png", eye, camRot2, 30f, 640, 480);
            }
            // the icons
            var atlas = UiIconAtlas.Texture;
            var raw = UiIconAtlas.Pixels;
            var sheet = new Texture2D(atlas.width, atlas.height, TextureFormat.RGBA32, false);
            var px = new Color32[raw.Length];
            for (int k = 0; k < raw.Length; k++) { float v = Mathf.SmoothStep(0f, 1f, Mathf.InverseLerp(0.44f, 0.56f, raw[k] / 255f)); byte b = (byte)(30 + 225 * v); px[k] = new Color32(b, b, b, 255); }
            sheet.SetPixels32(px);
            sheet.Apply();
            File.WriteAllBytes(Path.Combine(MetaCodexSetup.ReportDir, "ui-icons.png"), sheet.EncodeToPNG());
            UnityEngine.Object.DestroyImmediate(sheet);
            Line("icons: " + string.Join(" ", UiIconAtlas.Names));

            // DOCK mode: the wrist menu leaves with its animation, then is off
            w.PoseTimelineForTest(1f, 1f);
            Step(2);
            shell.SetWristMenu(false);
            bool goingOut = w.Visible;
            Step(24);
            Check(shell.IsPanelOpen("dock") && goingOut && !w.Visible && !w.enabled, $"DOCK mode: the dock is back; the wrist menu leaves with its animation ({goingOut}), then is off ({!w.enabled})");
            head.rotation = rot0;
            RightAway();
            foreach (var id in shell.PanelIds.ToList()) if (id != "dock") shell.ClosePanel(id);
        }

        /// WHERE A PANEL OPENS (owner 2026-11-10: "smart positions"): the free place that costs least in the user's view;
        /// beside the window it was opened from; back where it was when that is still free and in view; in front of the
        /// body when the user looks down at the wrist; never inside a wall.
        private static void Placement(MetaCodexShell shell, Transform head)
        {
            foreach (var id in shell.PanelIds.ToList()) shell.ClosePanel(id);
            shell.SetWristMenu(true);
            var keepPos = head.position; var keepRot = head.rotation;
            float keepScale = shell.Settings.uiScale, keepDist = shell.Settings.panelDistance;
            head.SetPositionAndRotation(new Vector3(0f, 1.4f, 0f), Quaternion.identity);
            var terms = shell.GetComponentsInChildren<UiPanel>(true).Where(x => x.Id != null && x.Id.StartsWith("term-") && x.gameObject.activeSelf).ToList();
            foreach (var tm in terms) tm.gameObject.SetActive(false);
            UiPanel.AnimateInEditor = false;
            shell.ClearRoomPoints();
            foreach (var pp in shell.PanelsById.Values) pp.transform.position = Vector3.up * 50f; // nothing is "back where the user left it"
            for (int k = 0; k < 30; k++) shell.TickOrbForTest(0.05f); // the frame the orb floats in: where the body faces
            MetaCodexShell.ViewRect R(string id) { MetaCodexShell.ViewRectOf(shell.PanelsById[id], head.position, 0f, out var r); return r; }
            float Dist(string id) => Vector3.Distance(shell.PanelsById[id].transform.position, head.position);
            void CloseAll() { foreach (var id in shell.PanelIds.ToList()) shell.ClosePanel(id); }
            try
            {
                // the first panel: in front, just below eye level, at the panel distance
                shell.OpenPanel("files");
                var f = R("files");
                float want = shell.Settings.panelDistance * 0.95f;
                Check(Mathf.Abs(f.YawMid) < 1.5f && Mathf.Abs(f.PitchMid + 4.5f) < 3f && Mathf.Abs(Dist("files") - want) < 0.02f,
                    $"the first panel opens in front of the user: {f.YawMid:F0} deg, {f.PitchMid:F0} deg, {Dist("files"):F2} m");
                // more panels: each takes the nearest free place, none over another, all within reach of a turn of the head
                // (SETTINGS brings CONTROL's rail on its left: the pair takes the place, the rail counts as a window)
                var ids = new[] { "files", "settings", "threads", "terminals", "clipboard" };
                foreach (var id in ids.Skip(1)) shell.OpenPanel(id);
                shell.UpdateControlRailForTest();
                var all = ids.Concat(new[] { MetaCodexShell.ControlId }).ToArray();
                int over = 0;
                for (int i = 0; i < all.Length; i++) for (int j = i + 1; j < all.Length; j++) if (R(all[i]).Overlaps(R(all[j]), 0f)) over++;
                bool reach = ids.All(id => Mathf.Abs(R(id).YawMid) <= MetaCodexShell.ViewYaw + 4f); // every one in view (a pair's middle may sit a little past)
                var second = R("settings");
                var railR = R(MetaCodexShell.ControlId);
                bool pair = railR.YawMid < second.YawMid && railR.Gap(second).yaw < 2f;
                bool besideFirst = Mathf.Min(railR.Gap(f).yaw, second.Gap(f).yaw) < 4f;
                Check(over == 0 && reach && pair && besideFirst && Mathf.Abs(second.PitchMid - f.PitchMid) < 2f,
                    $"five panels and the rail: none over another ({over} pairs), the second (with its rail on its left: {pair}) right beside the first ({Mathf.Min(railR.Gap(f).yaw, second.Gap(f).yaw):F1} deg apart, same height): " + string.Join(", ", all.Select(id => $"{id} {R(id).YawMid:F0}")));
                Render(shell, "ui-placement.png", head.position - Vector3.forward * 0.1f, Quaternion.identity, 110f, 1800, 1000);

                // opened from a window's button: right beside that window (the user looks at it while pressing)
                CloseAll();
                shell.OpenPanel("threads"); // its + NEW THREAD… opens NEW THREAD
                var thp = shell.PanelsById["threads"];
                thp.ForceRefresh();
                var newBtn = thp.Elements.OfType<UiButton>().FirstOrDefault(b => b.Visible && b.Text != null && b.Text.Contains("NEW THREAD"));
                if (newBtn != null) Click(shell.Router, thp, newBtn.Rect.center);
                var tr = R("threads"); var nr = R("thread-new");
                bool beside = shell.IsPanelOpen("thread-new") && !nr.Overlaps(tr, 0f) && nr.Gap(tr).yaw < 4f && Mathf.Abs(nr.YawMid) <= MetaCodexShell.ViewYaw + 1f;
                Check(newBtn != null && beside, $"NEW THREAD opened from THREADS' button opens right beside THREADS and in view ({nr.Gap(tr).yaw:F1} deg apart; THREADS at {tr.YawMid:F0}, NEW THREAD at {nr.YawMid:F0})");
                // the user has turned away (100 deg) when it opens from that window: it opens in front of them, never out of view
                shell.ClosePanel("thread-new");
                shell.PanelsById["thread-new"].transform.position = Vector3.up * 50f;
                head.rotation = Quaternion.Euler(0f, 100f, 0f);
                if (newBtn != null) Click(shell.Router, thp, newBtn.Rect.center);
                MetaCodexShell.ViewRectOf(shell.PanelsById["thread-new"], head.position, 100f, out var nr2);
                Check(shell.IsPanelOpen("thread-new") && Mathf.Abs(nr2.YawMid) <= MetaCodexShell.ViewYaw + 1f,
                    $"opened while the user looks elsewhere (100 deg), NEW THREAD opens in their view (its middle {nr2.YawMid:F0} deg from where they face)");
                head.rotation = Quaternion.identity;
                // in CONTROL a category opened from another one's button takes its place (SYSTEM > STORAGE > AI MODELS)
                CloseAll();
                shell.OpenPanel("storage");
                var sys = shell.PanelsById["system"];
                sys.ForceRefresh();
                var sysCorner = sys.transform.TransformPoint(new Vector3(-sys.Width * 0.5f, sys.Height * 0.5f, 0f));
                var models = sys.Elements.OfType<UiButton>().FirstOrDefault(b => b.Visible && b.Text != null && b.Text.Contains("AI MODELS"));
                if (models != null) Click(shell.Router, sys, models.Rect.center);
                var vp = shell.PanelsById["voice"];
                var voiceCorner = vp.transform.TransformPoint(new Vector3(-vp.Width * 0.5f, vp.Height * 0.5f, 0f));
                Check(models != null && vp.IsOpen && !sys.IsOpen && Vector3.Distance(sysCorner, voiceCorner) < 0.002f && shell.ControlRailPage == "voice",
                    $"in CONTROL, SYSTEM's AI MODELS opens VOICE in SYSTEM's place (top-left corners {Vector3.Distance(sysCorner, voiceCorner) * 1000f:F1} mm apart), the rail stays beside it ({shell.ControlRailPage})");

                // a panel that was open comes back where it was: when that place is free and in view
                CloseAll();
                shell.OpenPanel("files");
                var filesP = shell.PanelsById["files"];
                filesP.transform.position += head.right * 0.35f + Vector3.up * 0.05f; // the user moved it
                var left = filesP.transform.position;
                shell.ClosePanel("files");
                shell.OpenPanel("files");
                bool back = Vector3.Distance(filesP.transform.position, left) < 1e-4f;
                shell.ClosePanel("files");
                shell.OpenPanel("settings");
                var st = shell.PanelsById["settings"];
                st.transform.SetPositionAndRotation(left, filesP.transform.rotation); // another window takes its place
                shell.OpenPanel("files");
                bool moved = !R("files").Overlaps(R("settings"), 0f) && Vector3.Distance(filesP.transform.position, left) > 0.2f;
                CloseAll();
                head.rotation = Quaternion.Euler(0f, 150f, 0f); // the user faces the other way
                shell.OpenPanel("files");
                MetaCodexShell.ViewRectOf(filesP, head.position, 150f, out var turned);
                Check(back && moved && Mathf.Abs(turned.YawMid) < 2f,
                    $"closed and opened again it is back where the user left it ({back}); when another window took that place it opens beside it ({moved}); when the user faces the other way it opens in front of them ({turned.YawMid:F0} deg)");
                head.rotation = Quaternion.identity;

                // looking down at the wrist to press: the panel opens where the body faces, not where the nose points
                CloseAll();
                for (int k = 0; k < 30; k++) shell.TickOrbForTest(0.05f);
                head.rotation = Quaternion.Euler(32f, -20f, 0f); // down and to the left wrist
                shell.OpenPanel("agents");
                shell.UpdateControlRailForTest();
                var ag = R("agents");
                float agPair = (R(MetaCodexShell.ControlId).Yaw0 + ag.Yaw1) * 0.5f; // AGENTS comes with CONTROL's rail: the pair is centred
                head.rotation = Quaternion.Euler(0f, -20f, 0f); // looking that way on purpose: there it opens
                shell.PanelsById["threads"].transform.position = Vector3.up * 50f;
                shell.OpenPanel("threads");
                var th = R("threads");
                Check(Mathf.Abs(agPair) < 3f && !th.Overlaps(ag, 0f), $"the user looks down at the wrist menu (20 deg to the left) and presses: the window (with its rail) opens in front of the body ({agPair:F0} deg), not at the wrist's side");
                head.rotation = Quaternion.identity;

                // smaller windows further away: above a window is nearer than a far turn of the head, and tilted to the user
                CloseAll();
                shell.Settings.uiScale = 0.8f; shell.Settings.panelDistance = 1.3f;
                shell.OnSettingsChanged();
                // (one CONTROL window at a time: SETTINGS, with its rail; the others are work windows)
                var many = new[] { "files", "settings", "threads", "terminals", "clipboard", "browser" };
                foreach (var id in many) shell.OpenPanel(id);
                shell.UpdateControlRailForTest();
                var many2 = many.Concat(new[] { MetaCodexShell.ControlId }).ToArray();
                int over2 = 0;
                for (int i = 0; i < many2.Length; i++) for (int j = i + 1; j < many2.Length; j++) if (R(many2[i]).Overlaps(R(many2[j]), 0f)) over2++;
                // the fifth does not fit beside the others within an easy turn of the head: it goes above the row
                var last = R("clipboard");
                var lastP = shell.PanelsById["clipboard"];
                bool above = last.PitchMid - R("files").PitchMid > 12f && Mathf.Abs(last.YawMid) < 50f && last.Pitch1 <= MetaCodexShell.PlaceTop + 1f;
                bool tilted = Vector3.Angle(lastP.transform.forward, lastP.transform.position - head.position) < 3f && Mathf.Abs(lastP.transform.forward.y) > 0.1f;
                bool notBelow = many.All(id => R(id).PitchMid > R("files").PitchMid - 6f); // below is where the hands and the dock are
                Check(over2 == 0 && above && tilted && notBelow, $"six smaller windows: none over another ({over2}); one goes above the row, nearer than a far turn of the head ({last.YawMid:F0} deg, {last.PitchMid:F0} deg up), tilted to face the user ({tilted}); none below it ({notBelow}): "
                    + string.Join(", ", many.Select(id => $"{id} {R(id).YawMid:F0}/{R(id).PitchMid:F0}")));
                Render(shell, "ui-placement-rows.png", head.position - Vector3.forward * 0.1f, Quaternion.Euler(-8f, 0f, 0f), 110f, 1800, 1000);
                CloseAll();
                shell.Settings.uiScale = keepScale; shell.Settings.panelDistance = keepDist;
                shell.OnSettingsChanged();

                // never inside a wall: a wall 0.62 m in front of the user, and the panel opens in front of it
                var wall = new List<Vector3>();
                for (float x = -1.2f; x <= 1.2f; x += 0.04f) for (float y = 0.6f; y <= 2.2f; y += 0.04f) wall.Add(new Vector3(x, y, 0.62f));
                shell.SetRoomPoints(wall);
                filesP.transform.position = Vector3.up * 50f; // placed anew
                shell.OpenPanel("files");
                float nearest = float.MinValue;
                foreach (var corner in new[] { new Vector2(0f, 0f), new Vector2(filesP.Width, 0f), new Vector2(0f, filesP.Height), new Vector2(filesP.Width, filesP.Height) })
                    nearest = Mathf.Max(nearest, filesP.SurfacePoint(corner).z);
                var wr = R("files");
                Check(nearest < 0.62f - 0.04f && Mathf.Abs(wr.YawMid) < 2f, $"a wall 0.62 m in front: the panel opens in front of it (its furthest corner at {nearest:F2} m), still in front of the user");
                shell.ClosePanel("files");
                // a reading of a hand (nearer than 0.55 m) is not the room
                var hand = new List<Vector3>();
                for (float x = -0.1f; x <= 0.1f; x += 0.01f) for (float y = 1.2f; y <= 1.4f; y += 0.01f) hand.Add(new Vector3(x, y, 0.35f));
                shell.SetRoomPoints(hand);
                filesP.transform.position += Vector3.right * 5f; // not back where it was: placed anew
                shell.OpenPanel("files");
                Check(Mathf.Abs(Dist("files") - want) < 0.02f, $"a hand in front of the eyes is not a wall: the panel opens at its distance ({Dist("files"):F2} m)");
            }
            finally
            {
                shell.ClearRoomPoints();
                shell.Settings.uiScale = keepScale; shell.Settings.panelDistance = keepDist;
                shell.OnSettingsChanged();
                foreach (var id in shell.PanelIds.ToList()) shell.ClosePanel(id);
                foreach (var tm in terms) if (tm != null) tm.gameObject.SetActive(true);
                shell.SetWristMenu(false);
                head.SetPositionAndRotation(keepPos, keepRot);
                shell.TickOrbForTest(0.05f);
            }
        }

        /// MQH by itself, agents to GET, an API key pasted once (owner 2026-11-10: "I'd rather not connect MQH manually",
        /// "a way to download Opencode, Claude Code, Codex, More (coming soon) disabled", "an easy way to paste my key").
        private static void AgentGetSection(MetaCodexShell shell)
        {
            foreach (var id in shell.PanelIds.ToList()) shell.ClosePanel(id);
            bool L(string u) => MqhHost.IsLocalForTest(u);
            Check(L("http://127.0.0.1:38990") && L("http://localhost:38990/") && L("") && !L("http://192.168.1.5:38990") && !L("http://127.0.0.1:4000"),
                "MQH starts by itself for the local URL (127.0.0.1:38990); a developer's other URL (a PC's MQH) turns that off");
            Check(shell.MqhHost != null && shell.MqhHost.State == MqhHost.HostState.Off, $"in the Editor MQH is not started (no Linux): {shell.MqhHost?.State}");

            // GET: three agents and MORE (coming soon, not clickable); versions from the Linux
            shell.AgentProbeForTest = () => "opencode 1.19.5\nclaude -\ncodex -";
            shell.OpenPanel("agents");
            var ap = shell.PanelsById["agents"];
            ap.ForceRefresh();
            string Tile(string id) => shell.GetTileTextForTest(id) ?? "";
            bool tiles = Tile("opencode").StartsWith("OPENCODE") && Tile("claude").StartsWith("CLAUDE CODE") && Tile("codex").StartsWith("CODEX")
                && ap.Elements.OfType<UiButton>().Any(b => b.Text.StartsWith("MORE") && b.Text.Contains("COMING SOON")) && !shell.MoreTileEnabledForTest;
            Check(tiles && Tile("opencode").Contains("✓ 1.19.5") && Tile("claude").Contains("GET") && Tile("codex").Contains("GET"),
                $"AGENTS > GET: “{Tile("opencode").Replace("\n", " ")}”, “{Tile("claude").Replace("\n", " ")}”, “{Tile("codex").Replace("\n", " ")}”, MORE · COMING SOON not clickable ({tiles})");
            string cmd = MetaCodexShell.InstallCommand(MetaCodexShell.GetAgents.First(a => a.Id == "opencode"));
            Check(cmd.StartsWith("npm i -g --allow-scripts=opencode-ai opencode-ai@latest") && cmd.Contains("opencode --version"),
                "GET runs the official npm package in a terminal, its install script allowed (npm 11 skips it otherwise): " + cmd);
            RenderPanel(ap, "ui-agents-get.png");

            // API KEY: the providers of the agent, a key sealed (allow) and kept for MQH until it answers
            shell.OpenAgentKey("opencode");
            var kp = shell.PanelsById["agent-key"];
            var list = kp.Elements.OfType<UiList>().First();
            bool providers = list.Items.Count == 7 && list.Items.Any(i => i.Sub == "ANTHROPIC_API_KEY") && list.Items.Any(i => i.Sub == "OPENROUTER_API_KEY") && shell.AgentKeyEnvForTest == "OPENCODE_API_KEY";
            const string Env = "ANTHROPIC_API_KEY";
            bool had = shell.Vault != null && shell.Vault.Has(Env);
            string old = had ? shell.Vault.Reveal(Env) : null;
            try
            {
                shell.SelectAgentKeyForTest(Env);
                string keepClip = GUIUtility.systemCopyBuffer;
                GUIUtility.systemCopyBuffer = "sk-ant-test-0123456789abcdef";
                shell.PasteAgentKeyForTest();
                GUIUtility.systemCopyBuffer = keepClip;
                var status = kp.Elements.OfType<UiLabel>().Select(x => x.Text).LastOrDefault(x => !string.IsNullOrEmpty(x)) ?? "";
                bool pasted = status.Contains("Pasted 28 characters") && status.Contains("sk-a…cdef");
                shell.SetAgentKeyForTest("sk-ant-test-0123456789abcdef");
                var e = shell.Vault?.Find(Env);
                bool sealedAllow = e != null && e.policy == "allow" && shell.Vault.Reveal(Env) == "sk-ant-test-0123456789abcdef";
                bool notInSettings = !File.ReadAllText(shell.SettingsPathForTest).Contains("sk-ant-test");
                RenderPanel(kp, "ui-agent-key.png");
                bool heading = kp.Elements.OfType<UiLabel>().Any(x => x.Text == "SUPPORTED PROVIDERS");
                bool noLinks = !kp.Elements.OfType<UiButton>().Any(b => b.Text.Contains("GET A KEY") || b.Text.Contains("⇗"));
                Check(heading && noLinks, $"API KEY: the list says SUPPORTED PROVIDERS ({heading}); no link to any provider's site ({noLinks})");
                Check(providers && pasted && sealedAllow && notInSettings,
                    $"API KEY: opencode's 7 providers ({providers}); ⎘ PASTE takes the clipboard ({pasted}); SAVE seals {Env} in the vault, policy allow ({sealedAllow}), never in settings.json ({notInSettings})");
            }
            finally
            {
                if (had) shell.Vault.Set(Env, old, "api-key", shell.Vault.Find(Env)?.policy ?? "allow", "restored after the self-test");
                else shell.Vault?.Delete(Env);
                shell.ClosePanel("agent-key");
                shell.ClosePanel("agents");
                shell.AgentProbeForTest = null;
            }

            // AGENTS lists the installed-only agents too (opencode: not in providers/state, in execution-options), as ready
            var keepStates = shell.ProviderStatesForTest;
            shell.ProviderStatesForTest = new JArray(new JObject { ["providerId"] = "codex", ["displayName"] = "Codex", ["status"] = "not_installed" });
            bool merged = shell.MergeInstalledProviders(JObject.Parse("{\"providers\":[{\"id\":\"codex\"},{\"id\":\"acp-opencode\",\"displayName\":\"opencode\"}]}"));
            var oc = shell.ProviderStatesForTest.FirstOrDefault(x => (string)x["providerId"] == "acp-opencode");
            bool again = shell.MergeInstalledProviders(JObject.Parse("{\"providers\":[{\"id\":\"acp-opencode\"}]}"));
            Check(merged && oc != null && (string)oc["status"] == "ready" && (string)oc["loginCommand"] == "opencode auth login" && !again && shell.ProviderStatesForTest.Count == 2,
                $"AGENTS adds opencode from MQH's execution options (it is not in providers/state): {oc?["status"]}, sign in “{oc?["loginCommand"]}”, once ({!again})");
            shell.ProviderStatesForTest = keepStates;

            // only Claude Code, Codex and OpenCode, in that order (Cursor and Pi are not MetaCodex's)
            var mqhStates = JArray.Parse("[{\"providerId\":\"acp-cursor\",\"displayName\":\"Cursor\",\"status\":\"not_installed\"},{\"providerId\":\"pi\",\"displayName\":\"Pi\",\"status\":\"not_installed\"},{\"providerId\":\"codex\",\"displayName\":\"Codex\",\"status\":\"ready\"}]");
            var supported = shell.SupportedOnlyForTest(mqhStates);
            string names = string.Join(", ", supported.Select(x => (string)x["displayName"] + ":" + (string)x["status"]));
            Check(supported.Count == 3 && (string)supported[0]["providerId"] == "claude-code" && (string)supported[1]["providerId"] == "codex" && (string)supported[2]["providerId"] == "acp-opencode"
                && !supported.Any(x => ((string)x["providerId"]).Contains("cursor") || (string)x["providerId"] == "pi") && (string)supported[1]["status"] == "ready",
                "AGENTS lists Claude Code, Codex, OpenCode only (no Cursor, no Pi): " + names);

            string V(string said) { var m = shell.Voice.FastMatch(said); return m == null ? "none" : m.Command.Name + " " + (string)m.Args["agent"]; }
            Check(V("install opencode") == "install_agent opencode" && V("download codex") == "install_agent codex" && V("add an api key for claude code") == "agent_key claude",
                $"voice: “install opencode” -> {V("install opencode")}, “download codex” -> {V("download codex")}, “add an api key for claude code” -> {V("add an api key for claude code")}");
        }

        /// The headset's system keyboard everywhere text is typed (owner 2026-11-13: our table keyboard removed). In the
        /// Editor SystemKeyboard.FakeForTest stands in for it (its text box and status are set here): a field opens it
        /// with its text, what is typed reaches the field once, Cancel keeps the old text, a secret field is masked; the
        /// terminal gets each key once ("ls", not "lsls": the same keys as text events are dropped while it is open),
        /// Done presses Enter, "show" twice keeps it open; the tip shows the first time only.
        private static void SystemKeyboardSection(MetaCodexShell shell)
        {
            var keepProvider = UiKeyboard.EditorProvider;
            UiKeyboard.EditorProvider = null;
            SystemKeyboard.FakeForTest = true;
            var go = new GameObject("TestTerminalInput");
            try
            {
                shell.ResetKeyboardTipForTest();
                int tips0 = shell.KeyboardTipCountForTest;
                // a field: opens with its text; typing reaches it once; Done keeps it
                string got = null; int changes = 0;
                UiKeyboard.Edit("ab", false, x => changes++, x => got = x, "TEST · NAME");
                bool opened = SystemKeyboard.IsOpen && SystemKeyboard.FakeText == "ab" && UiKeyboard.Active;
                SystemKeyboard.FakeText = "abc"; UiKeyboard.Pump();
                UiKeyboard.Pump(); // nothing new: no second change
                string mid = UiKeyboard.EditingTextForTest;
                SystemKeyboard.FakeStatus = TouchScreenKeyboard.Status.Done; UiKeyboard.Pump();
                Check(opened && mid == "abc" && changes == 1 && got == "abc" && !SystemKeyboard.IsOpen && !UiKeyboard.Active,
                    $"a field opens the system keyboard with its text ({opened}); “abc” reaches it once ({changes} change, “{mid}”); Done gives “{got}” and it closes");
                int tips1 = shell.KeyboardTipCountForTest;
                // Cancel: the old text back
                got = null;
                UiKeyboard.Edit("old", false, null, x => got = x, "TEST · NAME");
                SystemKeyboard.FakeText = "new"; UiKeyboard.Pump();
                SystemKeyboard.FakeStatus = TouchScreenKeyboard.Status.Canceled; UiKeyboard.Pump();
                Check(got == "old", $"Cancel keeps the old text (“{got}”)");
                Check(tips1 == tips0 + 1 && shell.KeyboardTipCountForTest == tips1,
                    $"the Surface keyboard tip shows the first time the keyboard opens, once a session ({tips1 - tips0}, then {shell.KeyboardTipCountForTest - tips1} more)");
                // the terminal: each key once, Done presses Enter, the physical text events dropped while it is open
                var input = go.AddComponent<TerminalInput>();
                var sent = new StringBuilder();
                input.Send += b => sent.Append(Encoding.UTF8.GetString(b));
                var poll = typeof(TerminalInput).GetMethod("PollSystemKeyboard", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
                var onText = typeof(TerminalInput).GetMethod("OnText", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
                input.ShowSystemKeyboard();
                input.ShowSystemKeyboard();
                bool stays = input.SystemKeyboardOpen;
                SystemKeyboard.FakeText = "l"; poll.Invoke(input, null); onText.Invoke(input, new object[] { 'l' });
                SystemKeyboard.FakeText = "ls"; poll.Invoke(input, null); onText.Invoke(input, new object[] { 's' });
                poll.Invoke(input, null);
                string typed = sent.ToString();
                SystemKeyboard.FakeText = "l"; poll.Invoke(input, null); // a backspace in its box: one DEL
                string afterBack = sent.ToString();
                SystemKeyboard.FakeStatus = TouchScreenKeyboard.Status.Done; poll.Invoke(input, null);
                string afterDone = sent.ToString();
                Check(stays && typed == "ls" && afterBack == "ls\x7f" && afterDone.Length > afterBack.Length && !input.SystemKeyboardOpen,
                    $"the terminal: show twice keeps it open ({stays}); “ls” typed arrives once as {Esc(typed)} (not “lsls”), a backspace as DEL, Done presses Enter ({Esc(afterDone)})");
                input.ToggleSystemKeyboard();
                bool on = input.SystemKeyboardOpen;
                input.ToggleSystemKeyboard();
                Check(on && !input.SystemKeyboardOpen, $"KEYS / the frame's ⌨ / the key bar's ⌨ toggle it for the terminal ({on}, then {input.SystemKeyboardOpen})");
                // a field taking the keyboard from the terminal: the terminal sees it closed
                input.ShowSystemKeyboard();
                UiKeyboard.Edit("", true, null, _ => { }, "TEST · SECRET");
                bool taken = !input.SystemKeyboardOpen && SystemKeyboard.IsOpen && UiKeyboard.Active;
                SystemKeyboard.FakeStatus = TouchScreenKeyboard.Status.Done; UiKeyboard.Pump();
                Check(taken && !SystemKeyboard.IsOpen, $"one keyboard at a time: a field takes it from the terminal ({taken})");
            }
            finally
            {
                SystemKeyboard.Close();
                SystemKeyboard.FakeForTest = false;
                UiKeyboard.EditorProvider = keepProvider;
                UnityEngine.Object.DestroyImmediate(go);
            }
        }

        private static string Esc(string s) => "“" + (s ?? "").Replace("\x7f", "<DEL>").Replace("\r", "<CR>").Replace("\n", "<LF>") + "”";

        /// WEB, the in-app browser (owner 2026-11-10: "it's boring to have the system browser turn the app off"):
        /// every page the app opens goes to this window, beside the rest; it never bends; a pointer on the page
        /// becomes a touch at the right page pixel. (No WebView in the Editor: the page itself is a headset check.)
        private static void WebSection(MetaCodexShell shell, Transform head)
        {
            foreach (var id in shell.PanelIds.ToList()) shell.ClosePanel(id);
            head.SetPositionAndRotation(new Vector3(0f, 1.4f, 0f), Quaternion.identity);
            string A(string u) => MetaCodexShell.WebAddress(u);
            Check(A("localhost:5173") == "http://localhost:5173" && A("127.0.0.1:8080/x") == "http://127.0.0.1:8080/x" && A("192.168.1.7:3000") == "http://192.168.1.7:3000"
                && A("github.com") == "https://github.com" && A("https://a.dev/b?c=1") == "https://a.dev/b?c=1" && A("unity fwidth") == "https://duckduckgo.com/?q=unity%20fwidth"
                && A("https://") == "" && A("  ") == "",
                $"addresses: localhost:5173 -> {A("localhost:5173")}, github.com -> {A("github.com")}, words -> a search, an empty https:// -> nothing");

            // sign-in pages never load in the app's WebView (its cookies would keep the account's session): Horizon's browser
            bool S(string u) => MetaCodexShell.IsSignInPage(u);
            Check(S("https://claude.ai/oauth/authorize?code=true") && S("https://console.anthropic.com/") && S("https://auth.openai.com/authorize") && S("https://accounts.google.com/o/oauth2")
                && S("https://github.com/login/device") && !S("http://localhost:5173/login") && !S("http://127.0.0.1:1455/auth/callback") && !S("https://developer.mozilla.org/en-US/")
                && !S("https://github.com/SolidityDrone/MetaCodex") && !S("about:blank"),
                "sign-in pages (claude.ai, anthropic, openai, Google, github.com/login) go to Horizon's browser; localhost callbacks, docs and repos stay in WEB");
            shell.OpenBrowser("https://claude.ai/oauth/authorize?code=true"); // a terminal's sign-in link (the tests never launch anything)
            Check(!shell.IsPanelOpen("web") && SystemBridge.LastResult.Contains("claude.ai/oauth"),
                $"a sign-in link from a terminal opens in Horizon's browser ({SystemBridge.LastResult}), not in the WEB window ({shell.IsPanelOpen("web")})");

            // the dock's WEB, the wrist's WEB and voice open the WEB window (not Horizon OS's browser, not the links)
            var dockWeb = shell.PanelsById["dock"].Elements.OfType<UiButton>().FirstOrDefault(b => b.Text.EndsWith("WEB"));
            dockWeb?.Click();
            bool byDock = shell.IsPanelOpen("web") && !shell.IsPanelOpen("browser");
            shell.ClosePanel("web");
            shell.Wrist.Inner.Concat(shell.Wrist.Outer).FirstOrDefault(x => x.Label == "WEB")?.Act?.Invoke();
            bool byWrist = shell.IsPanelOpen("web");
            shell.ClosePanel("web");
            var vb = shell.Voice.FastMatch("open the browser");
            var vl = shell.Voice.FastMatch("open the bookmarks");
            Check(byDock && byWrist && (string)vb?.Args["panel"] == "web" && (string)vl?.Args["panel"] == "links",
                $"the dock's WEB ({byDock}) and the wrist's WEB ({byWrist}) open the WEB window; voice “open the browser” -> {(string)vb?.Args["panel"]}, “open the bookmarks” -> {(string)vl?.Args["panel"]}");
            shell.OpenPanel("links");
            bool linksOpen = shell.IsPanelOpen("links") && shell.IsPanelOpen("browser");
            shell.TogglePanel("links");
            Check(linksOpen && !shell.IsPanelOpen("browser"), $"“links” is the bookmarks panel: opens ({linksOpen}) and toggles closed ({!shell.IsPanelOpen("browser")})");

            // a link from anywhere (a terminal, a bookmark, voice, Lua) loads in the window
            shell.OpenPanel("web");
            bool start = shell.WebOnStartForTest;
            shell.OpenBrowser("localhost:5173");
            var wp = shell.PanelsById["web"];
            Check(start && shell.IsPanelOpen("web") && shell.WebUrlForTest == "http://localhost:5173" && !shell.WebOnStartForTest,
                $"WEB opens on its start page ({start}); OpenBrowser(\"localhost:5173\") loads {shell.WebUrlForTest} in the window");

            // the page: 16:10 inside the window, a drawn quad of the window (cast with it), a 1280x800 texture
            var r = shell.WebPageRectForTest;
            var c = wp.Content;
            float aspect = r.width / Mathf.Max(1e-5f, r.height);
            var quad = wp.GetComponentsInChildren<MeshRenderer>(true).FirstOrDefault(m => m.name == "web page");
            var tex = shell.WebTextureForTest;
            Check(r.width > 0.6f && Mathf.Abs(aspect - 1.6f) < 0.01f && r.xMin >= c.xMin - 1e-4f && r.xMax <= c.xMax + 1e-4f && r.yMax <= c.yMax + 1e-4f
                && quad != null && quad.sharedMaterial != null && quad.sharedMaterial.shader.name == "MetaCodex/ViewImage" && tex != null && tex.width == 1280 && tex.height == 800,
                $"the page: {r.width * 100f:F1} x {r.height * 100f:F1} cm (aspect {aspect:F3}) inside the window, its quad {(quad != null ? quad.sharedMaterial?.shader.name : "missing")}, texture {tex?.width}x{tex?.height}");
            var spill = wp.Elements.Where(e => e.Visible && e.Rect.yMin >= c.yMin - 0.001f && (e.Rect.xMax > c.xMax + 0.002f || e.Rect.yMax > c.yMax + 0.002f || e.Rect.xMin < c.xMin - 0.002f))
                .Select(e => e.GetType().Name + (e is UiButton wb ? " " + wb.Text : "")).ToList();
            Check(spill.Count == 0, spill.Count == 0 ? "every WEB control inside the window" : "WEB controls past the edge: " + string.Join(", ", spill));

            // a pinch on the page is a touch at that page pixel; a drag moves it; the release lifts it
            var page = wp.HitTest(r.center);
            var at = new Vector2(r.x + r.width * 0.25f, r.y + r.height * 0.75f);
            page?.OnPress(new UiPointerEvent { Point = r.center, PressPoint = r.center });
            var down = shell.WebLastTouchForTest;
            page?.OnDrag(new UiPointerEvent { Point = at, PressPoint = r.center });
            var move = shell.WebLastTouchForTest;
            page?.OnRelease(new UiPointerEvent { Point = at, PressPoint = r.center }, true);
            var up = shell.WebLastTouchForTest;
            bool Near(Vector2 a, float x, float y) => Mathf.Abs(a.x - x) < 1f && Mathf.Abs(a.y - y) < 1f;
            Check(page != null && down.action == 0 && Near(down.page, 640f, 400f) && move.action == 2 && Near(move.page, 320f, 600f) && up.action == 1 && Near(up.page, 320f, 600f),
                $"a pinch at the page's centre touches page pixel ({down.page.x:F0}, {down.page.y:F0}); dragged to a quarter across and three quarters down: ({move.page.x:F0}, {move.page.y:F0}); lifted there");

            // it never bends (the page is one flat picture), when the other windows do
            bool bend0 = shell.Settings.bendPanels;
            shell.Settings.bendPanels = true;
            shell.OpenPanel("files");
            shell.UpdateFixedBend();
            float webBend = wp.BendRadius, filesBend = shell.PanelsById["files"].BendRadius;
            Check(webBend == 0f && filesBend > 0f, $"with Bend panels on, FILES bends ({filesBend:F2} m) and WEB stays flat ({webBend:F2})");
            shell.Settings.bendPanels = bend0;
            shell.UpdateFixedBend();
            shell.ClosePanel("files");
            wp.ForceRefresh();
            RenderPanel(wp, "ui-web-panel.png");
            shell.ClosePanel("web");
        }

        /// What a frame costs on the CPU, per part, in a busy scene (owner 2026-11-10: "ultra optimized, more prompt"):
        /// five windows, the wrist menu open, the orb, a message, the right hand's ray sweeping over a list. Times are the
        /// Editor's (the headset's cores are several times slower); allocations are the same everywhere, and garbage per
        /// frame is what makes the headset hitch when the collector runs.
        private static void Perf(MetaCodexShell shell, Transform head, SimulatedHands hands)
        {
            foreach (var id in shell.PanelIds.ToList()) shell.ClosePanel(id);
            head.SetPositionAndRotation(new Vector3(0f, 1.4f, 0f), Quaternion.identity);
            UiPanel.AnimateInEditor = false;
            foreach (var pp in shell.PanelsById.Values) pp.transform.position = Vector3.up * 50f;
            foreach (var id in new[] { "files", "settings", "threads", "mqh", "agents" }) shell.OpenPanel(id);
            foreach (var pp in shell.PanelsById.Values) if (pp.IsOpen) pp.ForceRefresh();
            shell.SetWristMenu(true);
            shell.Toast("perf: a message beside the orb");
            // the wrist menu open on the left wrist (as in the wrist section)
            var eye = head.position;
            var palm = eye + head.forward * 0.33f - head.up * 0.13f - head.right * 0.03f;
            var toEye = (eye - palm).normalized;
            var lFingers = (head.right * 0.9f + head.forward * 0.35f).normalized;
            hands.SetOpen(Handedness.Left, palm, lFingers, -toEye);
            var files = shell.PanelsById["files"];
            var list = files.Elements.OfType<UiList>().FirstOrDefault();
            const int frames = 300;
            const float dt = 1f / 72f;
            var names = new[] { "router + gestures", "shell (voice, focus, layout, orb logic)", "panels' Update", "panel meshes (Flush)", "texts (changed ones)", "wrist menu", "orb body", "messages" };
            var ticks = new long[names.Length];
            var bytes = new long[names.Length];
            var worst = new double[names.Length];
            var sw = new System.Diagnostics.Stopwatch();
            // the per-thread allocation counter is not kept by every Mono garbage collector: trust a 0 only if it sees a
            // known allocation, else count the managed heap's growth (a collection in between is reported)
            long probe0 = GC.GetAllocatedBytesForCurrentThread();
            var probe = new byte[8192];
            bool perThread = GC.GetAllocatedBytesForCurrentThread() - probe0 >= 8192;
            GC.KeepAlive(probe);
            long Alloc() => perThread ? GC.GetAllocatedBytesForCurrentThread() : GC.GetTotalMemory(false);
            int gc0 = GC.CollectionCount(0);
            var panels = shell.GetComponentsInChildren<UiPanel>(true);
            var meshes = shell.GetComponentsInChildren<UiShapeMesh>(true);
            var texts = shell.GetComponentsInChildren<TMPro.TextMeshPro>(true);
            for (int f = -30; f < frames; f++) // 30 frames to settle first
            {
                // the right hand's ray sweeps down the list and back: hovers change, the pointer light moves
                float k = Mathf.PingPong(f / 90f, 1f);
                var target = list != null ? files.SurfacePoint(new Vector2(list.Rect.center.x, list.Rect.y + list.Rect.height * k)) : files.transform.position;
                var rp = eye + head.right * 0.18f - head.up * 0.25f + head.forward * 0.2f;
                hands.SetOpen(Handedness.Right, rp, (target - rp).normalized, Vector3.down);
                hands.Frames[1].PointerValid = true; hands.Frames[1].PointerOrigin = rp; hands.Frames[1].PointerDirection = (target - rp).normalized;
                for (int part = 0; part < names.Length; part++)
                {
                    long b0 = Alloc();
                    sw.Restart();
                    switch (part)
                    {
                        case 0: shell.TickGesturesForTest(dt); shell.Router.UpdateForTest(); break;
                        case 1: shell.UpdateForTest(); break;
                        case 2: foreach (var p in panels) if (p != null && p.gameObject.activeInHierarchy) p.UpdateForTest(); break;
                        case 3: foreach (var m in meshes) if (m != null && m.gameObject.activeInHierarchy) m.Flush(); break;
                        case 4: foreach (var t in texts) if (t != null && t.gameObject.activeInHierarchy && t.havePropertiesChanged) t.ForceMeshUpdate(); break;
                        case 5: shell.TickWristForTest(dt); break;
                        case 6: shell.Orb?.Tick(dt); break;
                        case 7: shell.Toasts.Tick(Time.unscaledTime); break;
                    }
                    sw.Stop();
                    if (f < 0) continue;
                    ticks[part] += sw.ElapsedTicks;
                    bytes[part] += Math.Max(0L, Alloc() - b0);
                    worst[part] = Math.Max(worst[part], sw.Elapsed.TotalMilliseconds);
                }
            }
            double total = 0, totalKb = 0;
            Line($"perf: {frames} frames, {panels.Count(p => p != null && p.gameObject.activeInHierarchy)} panels drawn, wrist menu {(shell.Wrist != null && shell.Wrist.Visible ? "shown" : "hidden")}, orb {(shell.OrbActive ? "on" : "off")}; "
                + $"garbage counted {(perThread ? "per thread" : "as heap growth")}, {GC.CollectionCount(0) - gc0} collections");
            for (int part = 0; part < names.Length; part++)
            {
                double ms = ticks[part] * 1000.0 / System.Diagnostics.Stopwatch.Frequency / frames, kb = bytes[part] / 1024.0 / frames;
                total += ms; totalKb += kb;
                Line($"perf:   {names[part],-42} {ms,7:F3} ms a frame (worst {worst[part],6:F2} ms), {kb,7:F2} KB of garbage a frame");
            }
            Line($"perf:   {"total",-42} {total,7:F3} ms a frame, {totalKb,7:F2} KB of garbage a frame");
            Check(total < 50.0, $"perf: the scene ran ({total:F2} ms a frame in the Editor)");
            hands.Frames[0].Tracked = false; hands.Frames[1].Tracked = false; hands.Frames[1].PointerValid = false;
            foreach (var id in shell.PanelIds.ToList()) shell.ClosePanel(id);
            shell.SetWristMenu(false);
        }

        /// THE ORB (owner 2026-11-10): a sphere of energy that floats by the user, casts the panels, is where the voice comes
        /// from, and shows the app's messages beside it. It comes and goes with its own light, keeps a free place up to
        /// the right, follows lazily, is touched for its menu and pinched to be put somewhere.
        private static void OrbSection(MetaCodexShell shell, Transform head, SimulatedHands hands)
        {
            foreach (var id in shell.PanelIds.ToList()) shell.ClosePanel(id);
            shell.SetWristMenu(true); // no dock in the pictures
            var keepPos = head.position; var keepRot = head.rotation;
            head.SetPositionAndRotation(new Vector3(0f, 1.4f, 0f), Quaternion.identity);
            hands.Frames[0].Tracked = false; hands.Frames[1].Tracked = false;
            var orb = shell.Orb;
            var keepCaster = UiPanelFx.Caster;
            // an empty room: no terminal, no old messages
            var terms = shell.GetComponentsInChildren<UiPanel>(true).Where(x => x.Id != null && x.Id.StartsWith("term-") && x.gameObject.activeSelf).ToList();
            foreach (var tm in terms) tm.gameObject.SetActive(false);
            foreach (var old in shell.Toasts.Panels.ToList()) old.CloseAndDestroy();
            shell.Toasts.Tick(float.MaxValue);
            void Step(int n, float dt = 0.05f) { for (int i = 0; i < n; i++) { shell.TickGesturesForTest(dt); shell.TickOrbForTest(dt); } }
            (float yaw, float pitch, float dist) Where()
            {
                var rel = orb.Position - head.position;
                float yaw = Mathf.DeltaAngle(Mathf.Atan2(head.forward.x, head.forward.z) * Mathf.Rad2Deg, Mathf.Atan2(rel.x, rel.z) * Mathf.Rad2Deg);
                return (yaw, Mathf.Asin(rel.y / rel.magnitude) * Mathf.Rad2Deg, rel.magnitude);
            }
            try
            {
                Check(orb != null && shell.Settings.orb && shell.OrbActive, "the orb is there by default");
                // it comes with its own light, never at once
                shell.SetOrb(false);
                Step(12);
                bool gone = orb.Appear <= 0.001f && !orb.Drawn && UiPanelFx.Caster() == null;
                shell.SetOrb(true);
                Step(3);
                float mid = orb.Appear;
                Step(16);
                Check(gone && mid > 0.05f && mid < 0.6f && orb.Appear > 0.999f && orb.Drawn,
                    $"Orb off: it goes and nothing casts ({gone}); on: it ignites over 0.7 s ({mid:F2} after 0.15 s, {orb.Appear:F2} after 0.95 s) and is drawn");
                // where it floats: up to the right of where the user looks, about half a metre away
                Step(20);
                var w0 = Where();
                Check(Mathf.Abs(w0.yaw - 32f) < 3f && Mathf.Abs(w0.pitch - 16f) < 3f && Mathf.Abs(w0.dist - MetaCodexShell.OrbDistance) < 0.03f,
                    $"it floats up to the right of the user's view: {w0.yaw:F0} deg right, {w0.pitch:F0} deg up, {w0.dist:F2} m away");
                RenderAt("ui-orb.png", head.position, Quaternion.LookRotation(orb.Position - head.position, Vector3.up), 16f, 900, 900);
                // a glance is not a turn: it stays where it is in the room; a real turn, and it comes along round the user
                var before = orb.Position;
                head.rotation = Quaternion.Euler(0f, 20f, 0f);
                Step(30);
                bool stays = Vector3.Distance(orb.Position, before) < 0.02f;
                head.rotation = Quaternion.Euler(0f, 150f, 0f);
                float nearest = float.MaxValue;
                for (int k = 0; k < 60; k++) { Step(1); nearest = Mathf.Min(nearest, Vector3.Distance(orb.Position, head.position)); }
                var w1 = Where();
                Check(stays && Mathf.Abs(w1.yaw - 32f) < 4f && Mathf.Abs(w1.pitch - 16f) < 4f && nearest > 0.42f,
                    $"a glance of 20 deg leaves it where it is in the room ({stays}); after a turn of 130 deg it is up to the right again ({w1.yaw:F0}, {w1.pitch:F0} deg) and it went round the user, never nearer than {nearest:F2} m");
                head.position += new Vector3(0.7f, 0f, 0.4f);
                Step(40);
                var w2 = Where();
                Check(Mathf.Abs(w2.yaw - 32f) < 4f && Mathf.Abs(w2.dist - MetaCodexShell.OrbDistance) < 0.04f, $"the user walks 0.8 m away: it comes along ({w2.yaw:F0} deg right, {w2.dist:F2} m)");
                head.SetPositionAndRotation(new Vector3(0f, 1.4f, 0f), Quaternion.identity);
                Step(60);

                // never in front of a window: a window where it floats, and it takes the next free place; it comes back later
                UiPanel.AnimateInEditor = false;
                shell.OpenPanel("files");
                var files = shell.PanelsById["files"];
                var dir = (orb.Position - head.position).normalized;
                files.transform.SetPositionAndRotation(head.position + dir * 0.9f, Quaternion.LookRotation(dir, Vector3.up));
                files.ForceRefresh();
                Step(30);
                var spot = shell.OrbSpotForTest;
                bool clear = !files.Raycast(new Ray(head.position, (orb.Position - head.position).normalized), out _, out _);
                Check(clear && (spot.yaw != 32f || spot.pitch != 16f), $"a window where it floats: it moves to a free place ({spot.yaw:F0}, {spot.pitch:F0} deg), in front of no window ({clear})");
                shell.ClosePanel("files");
                Step(30);
                bool waits = shell.OrbSpotForTest.yaw == spot.yaw;
                Step(50);
                Check(waits && shell.OrbSpotForTest.yaw == 32f, $"the window gone, it waits a moment ({waits}) and then goes back to its place");

                // it casts the panels: the scan line leaves from its side, its rings turn to the panel
                UiPanel.AnimateInEditor = true;
                var mqh = shell.PanelsById["mqh"];
                mqh.transform.position = Vector3.up * 50f; // not back where a test before left it: in front of the user
                shell.OpenPanel("mqh");
                // the orb is up to the right of the panel: the line starts at the panel's right edge and moves left
                var orbLocal = mqh.transform.InverseTransformPoint(orb.Position);
                bool fromOrb = UiPanelFx.Caster() != null && mqh.TransitionPhase == UiPanel.Phase.Opening && orbLocal.x > 0.05f && mqh.Fx.Axis == 0 && mqh.Fx.Sign < 0f;
                Step(3);
                for (int k = 0; k < 3; k++) mqh.TickTransitionForTest(0.05f);
                float aim = orb.Aim;
                float toPanel = Vector3.Dot(orb.Look, (mqh.transform.position - orb.Position).normalized);
                Check(fromOrb && aim > 0.6f && toPanel > 0.9f, $"a panel that opens is cast by the orb: the line leaves from the orb's side ({fromOrb}: the orb {orbLocal.x:F2} m to the panel's right, axis {mqh.Fx.Axis}, sign {mqh.Fx.Sign:F0}), its rings aim at the panel (aim {aim:F2}, look {toPanel:F2})");
                mqh.ForceRefresh();
                mqh.PoseTransitionForTest(true, 0.46f);
                var wide = head.position - head.forward * 0.25f + Vector3.up * 0.04f;
                RenderAt("ui-orb-cast.png", wide, Quaternion.LookRotation(head.forward + head.right * 0.14f, Vector3.up), 62f, 1400, 900);
                mqh.ForceRefresh();
                mqh.PoseTransitionForTest(false, 0.86f);
                RenderAt("ui-orb-absorb.png", wide, Quaternion.LookRotation(head.forward + head.right * 0.14f, Vector3.up), 62f, 1400, 900);
                mqh.ForceRefresh();
                UiPanel.AnimateInEditor = false;
                shell.ClosePanel("mqh");
                Step(90); // back at its place

                // the voice: it pulses with the loudness of what is spoken and sends ripples out, then rests
                var env = new float[100];
                for (int k = 0; k < env.Length; k++) env[k] = (k / 12) % 2 == 0 ? 0.9f : 0.05f; // syllables
                shell.OrbSpeakForTest(env);
                float loud = 0f, quiet = 1f; int ripples = 0;
                for (int k = 0; k < 30; k++) { Step(1); if (orb.State == UiOrb.Mood.Speaking) { loud = Mathf.Max(loud, orb.Pulse); quiet = Mathf.Min(quiet, orb.Pulse); ripples = Math.Max(ripples, orb.Ripples); } }
                bool speaking = orb.State == UiOrb.Mood.Speaking;
                RenderAt("ui-orb-speaking.png", head.position, Quaternion.LookRotation(orb.Position - head.position, Vector3.up), 16f, 900, 900);
                Step(30);
                Check(speaking && loud > 0.6f && quiet < 0.35f && ripples >= 2 && orb.State == UiOrb.Mood.Idle,
                    $"it speaks: its pulse follows the voice (from {quiet:F2} to {loud:F2}), {ripples} ripples leave it; when the words end it rests ({orb.State})");
                Check(MetaCodexShell.SpeechEnvelope(new float[] { 0f, 0f, 1f, -1f, 0.5f, -0.5f, 0f, 0f }, 1, 100).Length == 4
                    && MetaCodexShell.SpeechEnvelope(new float[] { 0f, 0f, 1f, -1f, 0.5f, -0.5f, 0f, 0f }, 1, 100)[1] > 0.99f
                    && MetaCodexShell.SpeechEnvelope(new float[] { 0f, 0f, 1f, -1f, 0.5f, -0.5f, 0f, 0f }, 1, 100)[0] < 0.01f, "the loudness curve of a clip: 50 values a second, loudest = 1");
                // listening and thinking: its mood, and the voice HUD is the first card of its column
                shell.SetVoiceStateForTest("listening", "LISTENING", "speak now");
                Step(12);
                var hud = shell.Toasts.Pinned;
                float hudOff = hud != null ? Vector3.Distance(hud.transform.position, orb.Position) : -1f;
                float hudTurn = hud != null ? Vector3.Angle(hud.transform.forward, hud.transform.position - head.position) : -1f;
                bool hudThere = hud != null && hud.IsOpen && hudOff < 0.45f && hudTurn < 8f;
                bool listening = orb.State == UiOrb.Mood.Listening && orb.Aim > 0.6f && Vector3.Dot(orb.Look, (head.position - orb.Position).normalized) > 0.9f;
                if (hud != null) hud.ForceRefresh();
                RenderAt("ui-orb-listening.png", head.position, Quaternion.LookRotation(orb.Position - head.right * 0.12f - head.position, Vector3.up), 34f, 1200, 800);
                shell.SetVoiceStateForTest("thinking", "THINKING", "");
                Step(12);
                bool thinking = orb.State == UiOrb.Mood.Thinking && orb.Tint.r > orb.Tint.g * 0.6f; // violet in it
                RenderAt("ui-orb-thinking.png", head.position, Quaternion.LookRotation(orb.Position - head.position, Vector3.up), 16f, 900, 900);
                shell.SetVoiceStateForTest("idle", "", "");
                if (hud != null && hud.IsOpen) hud.Close();
                Step(12);
                Check(listening && hudThere && thinking && orb.State == UiOrb.Mood.Idle,
                    $"listening: it turns its rings to the user ({listening}) and the voice HUD stands beside it, turned to the user ({hudThere}: {hudOff:F2} m from it, {hudTurn:F1} deg off); thinking: it spins, violet ({thinking})");

                // what the app has to say: callouts in a column beside it, always on its right (owner 2026-11-11: never swapped)
                foreach (var old in shell.Toasts.Panels.ToList()) old.CloseAndDestroy();
                shell.Toasts.Tick(float.MaxValue);
                shell.ClearMessages(); // earlier sections' messages: not new here (LOG's CLEAR ALL asks first; the control section tests it)
                shell.Toast("Build finished: 0 errors", "ok");
                Step(8);
                var card = shell.Toasts.Panels.FirstOrDefault();
                var toCard = card != null ? card.transform.position - orb.Position : Vector3.zero;
                float cardTurn = card != null ? Vector3.Angle(card.transform.forward, card.transform.position - head.position) : -1f;
                bool beside = card != null && Vector3.Dot(toCard, head.right) > 0.05f && Mathf.Abs(toCard.y) < 0.03f && toCard.magnitude < 0.40f && cardTurn < 8f;
                bool green = orb.Tint.g > orb.Tint.b;
                shell.Toast("The headset is at 18 % battery", "warn");
                Step(8);
                var cards = shell.Toasts.Panels.ToList();
                bool stacked = cards.Count == 2 && cards[1].transform.position.y > cards[0].transform.position.y + 0.02f;
                Check(beside && stacked && shell.Toasts.RailShown && shell.Toasts.History.Count == 2 && shell.Toasts.History[0].Kind == "warn",
                    $"a message is a callout beside the orb, on its right, level with it and turned to the user ({beside}: {toCard.magnitude:F2} m from it, {toCard.y * 100f:F1} cm higher, {cardTurn:F1} deg off); the next one takes that place and the first moves up ({stacked}); a rail joins them to the orb; both are kept");
                Check(green, "the orb flashes in the colour of the message (green for ok)");
                // four of them: an arc round the orb's right side, concentric with it, centred on its level, each turned to the user
                shell.Toast("Thread “Fix the build” answered: tap to open it", "info");
                shell.Toast("Layout DESK saved", "ok");
                Step(40);
                cards = shell.Toasts.Panels.ToList();
                var arc = cards.Select(c => OrbPlaneNear(c, orb.Position, head.position)).ToList();
                float rMin = arc.Min(x => x.near), rMax = arc.Max(x => x.near);
                bool arcRight = arc.All(x => x.centre.x > 0.05f), arcFacing = cards.All(c => Vector3.Angle(c.transform.forward, c.transform.position - head.position) < 3f);
                float stackMid = (arc.Max(x => x.centre.y) + arc.Min(x => x.centre.y)) * 0.5f;
                bool bows = arc[0].centre.x < arc[1].centre.x - 0.004f && arc[3].centre.x < arc[2].centre.x - 0.004f; // the ends come round the orb
                bool newestLow = Enumerable.Range(1, cards.Count - 1).All(i => arc[i].centre.y > arc[i - 1].centre.y + 0.02f);
                Check(cards.Count == 4 && rMax - rMin < 0.003f && arcRight && arcFacing && Mathf.Abs(stackMid) < 0.005f && bows && newestLow,
                    $"four callouts sit on an arc round the orb's right side: every card's corner nearest the orb {rMin * 100f:F1}-{rMax * 100f:F1} cm from its centre (one circle), all on its right ({arcRight}), the stack centred on its level ({stackMid * 100f:F1} cm), its ends bowed round it ({bows}), the newest lowest ({newestLow}), each turned to the user ({arcFacing})");
                foreach (var cd in cards) cd.ForceRefresh();
                RenderAt("ui-orb-callouts.png", head.position, Quaternion.LookRotation(orb.Position + head.right * 0.14f - head.position, Vector3.up), 46f, 1400, 900);

                // a touch: its menu, an arc of four squares round its left side (the callouts take its right), each turned to the user
                var rest = head.position + head.forward * 0.3f + head.right * 0.25f - head.up * 0.3f;
                void TipAt(Vector3 at, float pinch = 0f) => hands.SetOpen(Handedness.Right, at - head.forward * 0.12f, head.forward, -head.up, 0f, pinch);
                void TipTo(Vector3 tip, float pinch = 0f) { TipAt(tip, pinch); hands.Frames[1].IndexTip = tip; if (pinch > 0.5f) hands.Frames[1].ThumbTip = tip; }
                TipTo(orb.Position - head.forward * 0.12f); Step(3);
                TipTo(orb.Position); Step(2);
                var acts = shell.OrbActionsForTest;
                bool opened = shell.OrbMenuOpen;
                TipTo(rest); Step(14);
                var ri = shell.OrbMenuRadiusForTest;
                var onArc = acts.Select(a => OrbPlaneNear(a, orb.Position, head.position)).ToList();
                // plain squares: no frame of their own, one button in each, filling it
                bool small = acts.Count == 4 && acts.All(a => a.Frameless && a.Width <= MetaCodexShell.OrbActW + 1e-4f && a.Elements.OfType<UiButton>().Count() == 1 && !a.Elements.OfType<UiList>().Any()
                    && a.Elements.OfType<UiButton>().All(b => b.Rect.size == new Vector2(MetaCodexShell.OrbActW, MetaCodexShell.OrbActH)));
                // the gap between neighbours, measured between their edges: the same all round the arc
                float GapBetween(int k)
                {
                    var p = onArc[k].centre; var q = onArc[k + 1].centre;
                    float gx = Mathf.Max(0f, Mathf.Abs(p.x - q.x) - acts[k].Width * acts[k].transform.lossyScale.x);
                    float gy = Mathf.Max(0f, Mathf.Abs(p.y - q.y) - acts[k].Height * acts[k].transform.lossyScale.y);
                    return Mathf.Sqrt(gx * gx + gy * gy);
                }
                float wantGap = MetaCodexShell.OrbActGap * acts[0].transform.lossyScale.x;
                var gaps = Enumerable.Range(0, 3).Select(k => GapBetween(k)).ToList();
                bool even = acts.Count == 4 && gaps.All(g => Mathf.Abs(g - wantGap) < 0.0005f);
                // the cast's light still runs on a square (no frame to light): posed half open, then whole again
                acts[1].PoseTransitionForTest(true, 0.5f);
                bool lit = acts[1].TransitionPhase == UiPanel.Phase.Opening && Mathf.Abs(acts[1].TransitionT - 0.5f) < 1e-4f;
                acts[1].ForceRefresh();
                bool concentric = onArc.All(x => Mathf.Abs(x.centre.magnitude - ri) < 0.002f);
                bool leftSide = onArc.All(x => x.centre.x < -0.03f) && Enumerable.Range(1, 3).All(k => onArc[k].centre.y < onArc[k - 1].centre.y - 0.01f);
                bool menuFacing = acts.All(a => Vector3.Angle(a.transform.forward, a.transform.position - head.position) < 3f);
                bool clearOfOrb = onArc.All(x => x.near > shell.OrbOuterRadiusForTest);
                var log = acts.Count == 4 ? acts[3].Elements.OfType<UiButton>().FirstOrDefault(b => b.Text.Contains("✉")) : null;
                bool counted = log != null && log.Text.Contains("4 NEW") && shell.OrbUnread == 4;
                float labelDeg = Mathf.Atan(UiTheme.Current.FontSizeFor(MetaCodexShell.OrbActText) * 0.1f * acts[0].transform.lossyScale.y / Vector3.Distance(head.position, acts[0].transform.position)) * Mathf.Rad2Deg;
                foreach (var a in acts) a.ForceRefresh();
                foreach (var cd in shell.Toasts.Panels) cd.ForceRefresh();
                RenderAt("ui-orb-menu.png", head.position, Quaternion.LookRotation(orb.Position - head.right * 0.03f - head.position, Vector3.up), 46f, 1400, 1000);
                Check(opened && small && even && lit && concentric && leftSide && menuFacing && clearOfOrb && counted && labelDeg >= 0.88f,
                    $"a fingertip in the orb opens its menu ({opened}): four plain squares, no frame, one button filling each ({small}); the gaps between neighbours {string.Join(" / ", gaps.Select(g => (g * 1000f).ToString("F1")))} mm, all {wantGap * 1000f:F1} mm ({even}); the light crosses a square ({lit}); on an arc round its left side, TALK at the top ({leftSide}), every centre {ri * 100f:F1} cm from the orb's ({concentric}), clear of its rings ({clearOfOrb}), each turned to the user ({menuFacing}); labels {labelDeg:F2} deg high; LOG says 4 NEW ({counted})");
                log?.Click();
                Step(2);
                var msgs = shell.PanelsById.TryGetValue("messages", out var mp) ? mp : null;
                var mlist = msgs != null ? msgs.Elements.OfType<UiList>().FirstOrDefault() : null;
                bool listed = msgs != null && msgs.IsOpen && mlist != null && mlist.Items.Count == 4 && mlist.Items[0].Title.Contains("DESK") && shell.OrbUnread == 0 && orb.Unread == 0;
                bool billboardAway = !shell.OrbMenuOpen;
                if (msgs != null) { msgs.ForceRefresh(); RenderPanel(msgs, "ui-orb-messages.png"); }
                shell.ClosePanel("messages");
                Step(14);
                Check(listed && billboardAway, $"LOG opens MESSAGES, newest first, and they are read now ({listed}); the menu goes ({billboardAway})");
                TipTo(orb.Position - head.forward * 0.12f); Step(12);
                TipTo(orb.Position); Step(2);
                bool reopened = shell.OrbMenuOpen;
                TipTo(rest); Step(14);
                TipTo(orb.Position - head.forward * 0.12f); Step(12);
                TipTo(orb.Position); Step(2);
                bool closed = !shell.OrbMenuOpen;
                TipTo(rest); Step(14);
                Check(reopened && closed, $"a touch opens the menu again ({reopened}), the next one closes it ({closed})");

                // pointing at it from afar and pinching does the same, and does not click the window behind it
                var palm = head.position + head.right * 0.15f - head.up * 0.25f + head.forward * 0.15f;
                shell.OpenPanel("files");
                var past = (orb.Position - palm).normalized; // a window half a metre behind the orb, seen from the hand
                files.transform.SetPositionAndRotation(orb.Position + past * 0.5f, Quaternion.LookRotation(past, Vector3.up));
                files.ForceRefresh();
                void PointAt(Vector3 target, float pinch)
                {
                    hands.SetOpen(Handedness.Right, palm, (target - palm).normalized, Vector3.down, 0f, pinch);
                    // the hand's own pointer pose (as the headset gives it): straight at the target
                    hands.Frames[1].PointerValid = true; hands.Frames[1].PointerOrigin = palm; hands.Frames[1].PointerDirection = (target - palm).normalized;
                }
                PointAt(files.transform.position + files.transform.right * 0.25f, 0f); Step(4);
                bool rayFree = !shell.OrbHoldsHandForTest(1) && files.Raycast(hands.Frames[1].Ray(), out _, out _);
                PointAt(orb.Position, 0f); Step(4);
                bool holds = shell.OrbHoldsHandForTest(1), behind = files.Raycast(hands.Frames[1].Ray(), out _, out _);
                bool blocked = rayFree && shell.Router.HandBlocked != null && holds && behind;
                PointAt(orb.Position, 1f); Step(3);
                bool farOpened = shell.OrbMenuOpen;
                PointAt(orb.Position, 0f); Step(14);
                PointAt(orb.Position, 1f); Step(3);
                bool farClosed = !shell.OrbMenuOpen;
                PointAt(orb.Position, 0f); Step(3);
                TipTo(rest); Step(8);
                shell.ClosePanel("files");
                hands.Frames[1].PointerValid = false;
                Check(blocked && farOpened && farClosed, $"a ray that points at the orb, a window behind it, is the orb's: the router leaves that hand alone ({blocked}: free beside it {rayFree}, held on it {holds}, the window behind {behind}); a pinch from afar opens its menu ({farOpened}) and closes it ({farClosed})");

                // a pinch at it: it goes where the fingers go and keeps that place by the user
                Step(40);
                TipTo(orb.Position + head.right * 0.03f, 0f); Step(3);
                TipTo(orb.Position + head.right * 0.03f, 1f); Step(3);
                bool held = shell.OrbGrabHandForTest == 1;
                var put = head.position + head.forward * 0.45f - head.right * 0.22f - head.up * 0.12f;
                for (int k = 1; k <= 10; k++) { TipTo(Vector3.Lerp(orb.Position, put, k / 10f), 1f); Step(2); }
                TipTo(put, 1f); Step(6);
                bool follows = Vector3.Distance(orb.Position, put) < 0.06f;
                TipTo(put, 0f); Step(4);
                TipTo(rest); Step(20);
                bool pinned = shell.Settings.orbPinned && shell.OrbGrabHandForTest < 0 && Vector3.Distance(orb.Position, put) < 0.06f;
                head.rotation = Quaternion.Euler(0f, -90f, 0f);
                Step(70);
                var w3 = Where();
                bool keeps = Mathf.Abs(w3.yaw - shell.Settings.orbYaw) < 4f && Mathf.Abs(w3.pitch - shell.Settings.orbPitch) < 4f;
                head.rotation = Quaternion.identity;
                shell.ResetOrbPlace();
                Step(70);
                var w4 = Where();
                Check(held && follows && pinned && keeps && !shell.Settings.orbPinned && Mathf.Abs(w4.yaw - 32f) < 4f,
                    $"a pinch at the orb holds it ({held}), it goes with the fingers ({follows}) and stays where it is let go ({pinned}); the user turns and it keeps that place by them ({keeps}: {w3.yaw:F0}, {w3.pitch:F0} deg); PLACE lets it choose again ({w4.yaw:F0} deg)");
                hands.Frames[1].Tracked = false;
                Step(4);

                // off: messages float in the upper view as before, panels are cast from their left edge
                shell.SetOrb(false);
                Step(12);
                foreach (var old in shell.Toasts.Panels.ToList()) old.CloseAndDestroy();
                shell.Toasts.Tick(float.MaxValue);
                shell.Toast("no orb");
                var freeCard = shell.Toasts.Panels.FirstOrDefault();
                bool free = freeCard != null && Vector3.Distance(freeCard.transform.position, head.position + head.forward * 0.75f + Vector3.up * (0.20f + freeCard.Height * 0.5f)) < 0.02f && !shell.Toasts.RailShown;
                Check(!shell.OrbActive && UiPanelFx.Caster() == null && free, $"Orb off: no caster, a message floats in the upper view as before ({free})");
                shell.SetOrb(true);
                Step(24);
                Check(shell.OrbActive && orb.Appear > 0.99f, "Orb on again: it is back");
            }
            finally
            {
                UiPanel.AnimateInEditor = false;
                UiPanelFx.Caster = keepCaster;
                hands.Frames[0].Tracked = false; hands.Frames[1].Tracked = false;
                foreach (var old in shell.Toasts.Panels.ToList()) old.CloseAndDestroy();
                shell.Toasts.Tick(float.MaxValue);
                foreach (var pp in shell.GetComponentsInChildren<UiPanel>(true)) if (pp != null && pp.Transitioning) pp.ForceRefresh();
                shell.CloseOrbMenuForTest();
                foreach (var a in shell.OrbActionsForTest) a.ForceRefresh();
                shell.Settings.orbPinned = false;
                foreach (var id in shell.PanelIds.ToList()) shell.ClosePanel(id);
                foreach (var tm in terms) if (tm != null) tm.gameObject.SetActive(true);
                shell.SetWristMenu(false);
                head.SetPositionAndRotation(keepPos, keepRot);
                shell.TickOrbForTest(0.05f);
            }
        }

        /// The look at rest (owner 2026-11-10: "much better aesthetics"): thick glass with a lattice behind its surface, a
        /// hand's light on it, quiet list rows; pictures at about the headset's pixel density.
        private static void Look(MetaCodexShell shell, Transform head)
        {
            foreach (var id in shell.PanelIds.ToList()) shell.ClosePanel(id);
            UiPanel.AnimateInEditor = false;
            head.SetPositionAndRotation(new Vector3(0f, 1.4f, 0f), Quaternion.identity);
            var terms = shell.GetComponentsInChildren<UiPanel>(true).Where(x => x.Id != null && x.Id.StartsWith("term-") && x.gameObject.activeSelf).ToList();
            foreach (var tm in terms) tm.gameObject.SetActive(false);
            try
            {
                shell.OpenPanel("files");
                var p = shell.PanelsById["files"];
                p.transform.SetPositionAndRotation(head.position + Vector3.forward * 0.8f, Quaternion.identity);
                p.SetBend(0f);
                p.ForceRefresh();
                var eye = head.position;
                var look = Quaternion.LookRotation(p.transform.position - eye, Vector3.up);
                shell.Router.SetPointerLightForTest(0, Vector3.zero, 0f);
                shell.Router.SetPointerLightForTest(1, Vector3.zero, 0f);
                var dark = RenderRgba("ui-look-files.png", eye, look, 50f, 1500, 1100);
                // a fingertip 2 cm off the glass, right of the middle: the lattice under it lights up
                var tip = p.transform.TransformPoint(new Vector3(0.12f, -0.03f, -0.02f));
                shell.Router.SetPointerLightForTest(1, tip, 1f);
                var lit = RenderRgba("ui-look-files-finger.png", eye, look, 50f, 1500, 1100);
                // compare the glass round the fingertip and far from it
                var cam = new GameObject("probe-cam") { hideFlags = HideFlags.DontSave }.AddComponent<Camera>();
                cam.fieldOfView = 50f; cam.aspect = 1500f / 1100f; cam.transform.SetPositionAndRotation(eye, look);
                var at = cam.WorldToViewportPoint(p.transform.TransformPoint(new Vector3(0.12f, -0.03f, 0f)));
                var farAt = cam.WorldToViewportPoint(p.transform.TransformPoint(new Vector3(-0.2f, 0.12f, 0f)));
                UnityEngine.Object.DestroyImmediate(cam.gameObject);
                float Gain(Vector3 vp)
                {
                    float sum = 0f; int n = 0;
                    for (int dy = -14; dy <= 14; dy += 2) for (int dx = -14; dx <= 14; dx += 2)
                    {
                        int x = (int)(vp.x * 1500f) + dx, y = (int)(vp.y * 1100f) + dy;
                        var a = dark.GetPixel(x, y); var b = lit.GetPixel(x, y);
                        sum += (b.r + b.g + b.b) - (a.r + a.g + a.b); n++;
                    }
                    return sum / Mathf.Max(1, n);
                }
                float nearGain = Gain(at), farGain = Gain(farAt);
                UnityEngine.Object.DestroyImmediate(dark); UnityEngine.Object.DestroyImmediate(lit);
                Check(nearGain > 0.02f && farGain < 0.004f, $"a fingertip near the glass lights the lattice under it (+{nearGain:F3}) and nothing far from it (+{farGain:F3})");
                shell.Router.SetPointerLightForTest(1, Vector3.zero, 0f);
                // a change of colour or state only (a hover, a click's flash, a selection) rewrites the colours and states of
                // that shape, not the panel's geometry; and it still shows
                {
                    var shapes = p.Shapes;
                    p.ForceRefresh();
                    int rb0 = shapes.RebuildsForTest, rs0 = shapes.RestylesForTest;
                    var btn = p.Elements.OfType<UiButton>().First(b => b.Visible && b.Style == UiButtonStyle.Normal);
                    var plain = RenderRgba(null, eye, look, 50f, 750, 550);
                    btn.SetSelected(true);
                    shapes.Flush();
                    int rb1 = shapes.RebuildsForTest, rs1 = shapes.RestylesForTest;
                    var chosen = RenderRgba(null, eye, look, 50f, 750, 550);
                    var pa = plain.GetPixels32(); var pb = chosen.GetPixels32();
                    int changed = 0;
                    for (int k = 0; k < pa.Length; k++) if (Math.Abs(pa[k].r - pb[k].r) + Math.Abs(pa[k].g - pb[k].g) + Math.Abs(pa[k].b - pb[k].b) > 24) changed++;
                    UnityEngine.Object.DestroyImmediate(plain); UnityEngine.Object.DestroyImmediate(chosen);
                    btn.SetSelected(false);
                    shapes.Flush();
                    Check(rb1 == rb0 && rs1 == rs0 + 1 && changed > 150 && changed < pa.Length / 20,
                        $"selecting a button rewrites its colours only: {rb1 - rb0} geometry rebuilds, {rs1 - rs0} restyle, {changed} pixels changed (the button, nothing else)");
                    int rb2 = shapes.RebuildsForTest;
                    shapes.SetRect(0, shapes.Get(0).Center, shapes.Get(0).Size); // the same rect again
                    shapes.Flush();
                    Check(shapes.RebuildsForTest == rb2, "setting a shape's rect to the one it has rebuilds nothing");
                }
                // the glass is flat: the lattice is printed on it (0.7.9 drew it behind the surface, with parallax)
                RenderAt("ui-look-files-left.png", eye - Vector3.right * 0.25f, Quaternion.LookRotation(p.transform.position - (eye - Vector3.right * 0.25f), Vector3.up), 50f, 1500, 1100);
                shell.OpenPanel("settings");
                var st = shell.PanelsById["settings"];
                st.transform.SetPositionAndRotation(head.position + Vector3.forward * 0.8f + Vector3.right * 2f, Quaternion.identity);
                st.SetBend(0f);
                st.ForceRefresh();
                RenderAt("ui-look-settings.png", eye + Vector3.right * 2f, Quaternion.identity, 50f, 1500, 1100);
                // list rows: quiet glass, the chosen one flooded with the accent
                shell.OpenPanel("gestures");
                var mq = shell.PanelsById["mqh"];
                mq.transform.SetPositionAndRotation(head.position + Vector3.forward * 0.8f - Vector3.right * 2f, Quaternion.identity);
                mq.SetBend(0f);
                var gl = mq.Elements.OfType<UiList>().FirstOrDefault(l => l.Visible && l.Items.Count > 2);
                if (gl != null) gl.SelectIndex(1);
                mq.ForceRefresh();
                RenderAt("ui-look-list.png", eye - Vector3.right * 2f, Quaternion.identity, 50f, 1500, 1100);
            }
            finally
            {
                shell.Router.SetPointerLightForTest(0, Vector3.zero, 0f);
                shell.Router.SetPointerLightForTest(1, Vector3.zero, 0f);
                foreach (var tm in terms) if (tm != null) tm.gameObject.SetActive(true);
                foreach (var id in shell.PanelIds.ToList()) shell.ClosePanel(id);
            }
        }

        /// Panels open and close with a transition (owner 2026-11-09: "very cool spawning and disappearing transitions",
        /// UiPanelFx): closed at once for everything else, animated out; reversed half way from where it is; dialogs and
        /// toasts go with it; an open panel asked again pings; pictures of both timelines.
        private static void Transitions(MetaCodexShell shell, Transform head)
        {
            foreach (var id in shell.PanelIds.ToList()) shell.ClosePanel(id);
            UiPanel.AnimateInEditor = false;
            shell.OpenPanel("mqh");
            var p = shell.PanelsById["mqh"];
            p.ForceRefresh();
            var rest = p.transform.localScale;
            var restPos = p.transform.position;
            var keepCaster = UiPanelFx.Caster;
            UiPanel.AnimateInEditor = true;
            try
            {
                UiPanelFx.Caster = null; // no orb: the line crosses from the left
                void Step(int n, float dt = 0.05f) { for (int i = 0; i < n; i++) p.TickTransitionForTest(dt); }
                bool Still() => (p.transform.localScale - rest).sqrMagnitude < 1e-10f && (p.transform.position - restPos).sqrMagnitude < 1e-10f;
                float ScanNow() => UiPanelFx.Evaluate(p.TransitionPhase == UiPanel.Phase.Opening, p.TransitionT, false).Scan;
                // closing: closed for everything else at once, still drawn while the line takes it back
                int closedEvents = 0;
                p.Closed += _ => closedEvents++;
                p.Close();
                bool logical = !p.IsOpen && p.gameObject.activeSelf && closedEvents == 1 && p.TransitionPhase == UiPanel.Phase.Closing;
                Step(4);
                float mid = ScanNow();
                bool cast = p.FxDataForTest.x > 0.5f && Still();
                // changed its mind: it is cast again from where the line is (no jump)
                p.Open();
                p.TickTransitionForTest(0.02f);
                float back = ScanNow();
                Step(12);
                bool restored = p.IsOpen && p.TransitionPhase == UiPanel.Phase.Open && p.FxDataForTest == Vector4.zero && p.ScanDataForTest == Vector4.zero && Still();
                Check(logical && cast && mid > 0.05f && mid < 0.95f && back >= mid - 0.001f && restored,
                    $"Close: closed at once for the rest of the app ({logical}), the line takes it back (built {mid:F2} after 0.2 s) and nothing is moved or scaled ({cast}); Open half way sends the line out again from there ({mid:F2} -> {back:F2}), and the panel is at rest ({restored})");
                // all the way out: off at the end, nothing left of the transition
                int absorbed = 0;
                Action<UiPanel> onAbsorbed = q => { if (q == p) absorbed++; };
                UiPanelFx.Absorbed += onAbsorbed;
                p.Close();
                Step(4);
                bool during = p.gameObject.activeSelf && p.Transitioning;
                Step(6);
                UiPanelFx.Absorbed -= onAbsorbed;
                bool off = !p.gameObject.activeSelf && !p.Transitioning && p.FxDataForTest == Vector4.zero && Still();
                Check(during && off && absorbed == 1, $"a closing panel is drawn for {UiPanelFx.CloseTime:F2} s ({during}), then turned off with nothing left of the cast ({off}); its spark reaches the caster once ({absorbed})");
                // opening: open at once for the app, the line builds it in 0.64 s
                int castEvents = 0;
                Action<UiPanel, bool> onCast = (q, opens) => { if (q == p && opens) castEvents++; };
                UiPanelFx.Casting += onCast;
                shell.OpenPanel("mqh");
                UiPanelFx.Casting -= onCast;
                bool opening = p.IsOpen && p.TransitionPhase == UiPanel.Phase.Opening && ScanNow() < 0.01f && p.FxDataForTest.x > 0.5f && castEvents == 1;
                var scans = new List<float>();
                for (int k = 0; k < 14; k++) { p.TickTransitionForTest(0.05f); scans.Add(p.Transitioning ? ScanNow() : 1f); }
                bool rising = true;
                for (int k = 1; k < scans.Count; k++) if (scans[k] < scans[k - 1] - 1e-4f) rising = false;
                bool done = p.TransitionPhase == UiPanel.Phase.Open && p.FxDataForTest == Vector4.zero && Still();
                Check(opening && done && rising && scans[4] > 0.1f && scans[4] < 0.9f,
                    $"Open: open at once for the app ({opening}), the line crosses once ({string.Join(" ", scans.Take(13).Select(y => y.ToString("F2")))}) and the panel is at rest, never moved or scaled ({done})");
                // the scan plane the shaders get: at the line's place on the panel, facing the way it moves
                p.PoseTransitionForTest(true, 0.5f);
                float half = UiPanelFx.Evaluate(true, 0.5f, false).Scan;
                var plane = p.ScanDataForTest;
                var n3 = new Vector3(plane.x, plane.y, plane.z);
                var onLine = p.transform.TransformPoint(new Vector3(p.Fx.LinePlace(p, half), 0.03f, 0f));
                var leftEdge = p.transform.TransformPoint(new Vector3(-p.Width * 0.5f, 0f, 0f));
                var rightEdge = p.transform.TransformPoint(new Vector3(p.Width * 0.5f, 0f, 0f));
                bool planeOk = Mathf.Abs(Vector3.Dot(onLine, n3) - plane.w) < 1e-4f && Vector3.Dot(leftEdge, n3) < plane.w && Vector3.Dot(rightEdge, n3) > plane.w && Vector3.Angle(n3, p.transform.right) < 0.1f;
                Check(planeOk && p.Fx.Axis == 0 && p.Fx.Sign > 0f, $"without a caster the line starts at the left edge and moves right; half way the shaders' plane is on it, the left built, the right not yet ({planeOk})");
                // the line leaves from the caster's side: a caster to the right, above, below
                var tr = p.transform;
                (Vector3 at, int axis, float sign, string name)[] sides =
                {
                    (tr.position + tr.right * 0.6f - tr.forward * 0.4f, 0, -1f, "right"),
                    (tr.position - tr.right * 0.6f - tr.forward * 0.4f, 0, 1f, "left"),
                    (tr.position + tr.up * 0.6f - tr.forward * 0.3f, 1, -1f, "above"),
                    (tr.position - tr.up * 0.6f - tr.forward * 0.3f, 1, 1f, "below"),
                };
                p.ForceRefresh();
                var sideLog = new List<string>();
                bool sidesOk = true;
                foreach (var sd in sides)
                {
                    var at = sd.at;
                    UiPanelFx.Caster = () => at;
                    p.ForceRefresh();
                    p.PoseTransitionForTest(true, 0.3f);
                    var pl = p.ScanDataForTest;
                    // the built side is the caster's side
                    var near = tr.position + (sd.axis == 0 ? tr.right : tr.up) * (-sd.sign) * (sd.axis == 0 ? p.Width : p.Height) * 0.45f * (sd.axis == 0 ? tr.lossyScale.x : tr.lossyScale.y);
                    bool ok = p.Fx.Axis == sd.axis && Mathf.Approximately(p.Fx.Sign, sd.sign) && Vector3.Dot(near, new Vector3(pl.x, pl.y, pl.z)) < pl.w;
                    sidesOk &= ok;
                    sideLog.Add($"{sd.name} {(p.Fx.Axis == 0 ? "x" : "y")}{(p.Fx.Sign > 0 ? "+" : "-")}");
                }
                p.ForceRefresh();
                Check(sidesOk, "the scan line leaves from the caster's side and builds that side first: " + string.Join(", ", sideLog));

                // opened again while open: a ping (a flash, the brackets round it, the caster's light on it), nothing cast again
                var caster = tr.position - tr.right * 0.42f - tr.up * 0.16f - tr.forward * 0.55f;
                UiPanelFx.Caster = () => caster;
                shell.OpenPanel("mqh");
                Step(2);
                var fxLight = p.GetComponentsInChildren<UiPanelFx>(true).FirstOrDefault();
                bool ping = fxLight != null && fxLight.GetComponent<MeshRenderer>().enabled && p.FxDataForTest.x < 0.5f && p.FxDataForTest.y > 0.01f && Still();
                Step(10);
                bool pingDone = fxLight != null && !fxLight.GetComponent<MeshRenderer>().enabled && p.FxDataForTest == Vector4.zero;
                Check(ping && pingDone, $"an open panel opened again pings: its light shows ({ping}) and nothing is cast again, then it goes ({pingDone})");

                // pictures: the cast and the taking back, frame by frame, with a caster where the orb floats
                var cam = p.transform.position - p.transform.forward * 1.45f - p.transform.right * 0.10f;
                var look = Quaternion.LookRotation(p.transform.position + p.transform.right * -0.05f - cam, p.transform.up);
                float[] openAt = { 0.05f, 0.14f, 0.24f, 0.36f, 0.48f, 0.60f, 0.72f, 0.84f, 0.95f };
                for (int k = 0; k < openAt.Length; k++) { p.ForceRefresh(); p.PoseTransitionForTest(true, openAt[k]); RenderAt($"ui-open-{k}.png", cam, look, 46f, 800, 600); }
                float[] closeAt = { 0.06f, 0.18f, 0.32f, 0.46f, 0.58f, 0.70f, 0.78f, 0.88f, 0.96f };
                for (int k = 0; k < closeAt.Length; k++) { p.ForceRefresh(); p.PoseTransitionForTest(false, closeAt[k]); RenderAt($"ui-close-{k}.png", cam, look, 46f, 800, 600); }
                // half way: the side behind the line is drawn, the side before it is not there yet
                p.ForceRefresh();
                p.PoseTransitionForTest(true, 0.42f); // the line is about in the middle of the panel
                var straight = Quaternion.LookRotation(p.transform.forward, p.transform.up);
                var halfPic = RenderRgba("ui-open-half.png", p.transform.position - p.transform.forward * 1.2f, straight, 46f, 800, 600);
                int builtPx = 0, aheadPx = 0;
                for (int y = 200; y < 400; y += 4)
                {
                    for (int x = 150; x < 300; x += 4) { var c = halfPic.GetPixel(x, y); if (c.r + c.g + c.b > 0.20f) builtPx++; }
                    for (int x = 520; x < 650; x += 4) { var c = halfPic.GetPixel(x, y); if (c.r + c.g + c.b > 0.20f) aheadPx++; }
                }
                UnityEngine.Object.DestroyImmediate(halfPic);
                Check(builtPx > 600 && aheadPx < 40, $"half way through the cast the side behind the line is drawn ({builtPx} of 1900 probes lit) and the side before it is not there yet ({aheadPx} of 1650)");
                // a bent panel: the brackets, the line and the fan follow its curve (pictures; the bend eases off afterwards)
                float bend0 = p.BendRadius;
                p.SetBend(0.55f);
                p.ForceRefresh();
                foreach (var (opens, tt, name) in new[] { (true, 0.10f, "ui-open-bent-line.png"), (true, 0.50f, "ui-open-bent.png"), (false, 0.60f, "ui-close-bent.png") })
                {
                    p.ForceRefresh();
                    p.PoseTransitionForTest(opens, tt);
                    RenderAt(name, cam + p.transform.up * 0.05f, look, 46f, 800, 600);
                }
                // a caster above a bent panel: the line is an arc, the fan follows it
                var above = tr.position + tr.up * 0.45f - tr.forward * 0.5f;
                UiPanelFx.Caster = () => above;
                p.ForceRefresh();
                p.PoseTransitionForTest(true, 0.5f);
                RenderAt("ui-open-bent-down.png", cam + p.transform.up * 0.05f, look, 46f, 800, 600);
                UiPanelFx.Caster = () => caster;
                p.SetBend(bend0);
                // the last frames of the cast look like the panel at rest (owner 2026-11-09: they did not): the
                // brackets have handed over to the frame's own, nothing else is left of the transition
                p.ForceRefresh();
                var restPic = RenderRgba("ui-open-rest.png", cam, look, 46f, 800, 600);
                p.PoseTransitionForTest(true, 0.985f);
                var lastPic = RenderRgba("ui-open-last.png", cam, look, 46f, 800, 600);
                var ra = restPic.GetPixels32(); var la = lastPic.GetPixels32();
                int differ = 0;
                for (int k = 0; k < ra.Length; k++) if (Math.Abs(ra[k].r - la[k].r) + Math.Abs(ra[k].g - la[k].g) + Math.Abs(ra[k].b - la[k].b) > 18) differ++;
                UnityEngine.Object.DestroyImmediate(restPic); UnityEngine.Object.DestroyImmediate(lastPic);
                Check(differ < ra.Length / 400, $"the cast's last frames look like the panel at rest: {differ} of {ra.Length} pixels differ (the brackets hand over, no pop at the end)");
                p.ForceRefresh();
                Check(p.IsOpen && !p.Transitioning && p.FxDataForTest == Vector4.zero && Still(), "ForceRefresh ends a transition: the panel is at rest");

                // a dialog: comes with the cast, goes with the closing, then is destroyed
                UiPanel dlg = null;
                shell.Modal("TRANSITION TEST", "A dialog for the transition test.", new[] { "OK" }, _ => { });
                dlg = UiModal.OpenPanels.FirstOrDefault();
                bool dlgOpening = dlg != null && dlg.TransitionPhase == UiPanel.Phase.Opening;
                if (dlg != null) { for (int k = 0; k < 14; k++) dlg.TickTransitionForTest(0.05f); dlg.Elements.OfType<UiButton>().First(b => b.Text == "OK").Click(); }
                bool dlgClosing = dlg != null && dlg.gameObject.activeSelf && dlg.TransitionPhase == UiPanel.Phase.Closing && UiModal.OpenCount == 0;
                if (dlg != null) for (int k = 0; k < 10; k++) { if (dlg == null) break; dlg.TickTransitionForTest(0.05f); }
                Check(dlgOpening && dlgClosing && dlg == null, $"a dialog opens with the transition ({dlgOpening}), its answer closes it with the transition ({dlgClosing}), then it is destroyed ({dlg == null})");
                // a toast comes in with the light variant (no brackets, 0.30 s)
                shell.Toast("transition test");
                var toast = shell.GetComponentsInChildren<UiPanel>(true).FirstOrDefault(x => x.Id == "toast" && x.Transitioning);
                var toastsNow = shell.GetComponentsInChildren<UiPanel>(true).Where(x => x.Id == "toast").Select(x => $"{x.TransitionPhase}/{(x.gameObject.activeSelf ? "on" : "off")}/slot {x.Slot}").ToList();
                Check(toast != null && toast.TransitionPhase == UiPanel.Phase.Opening, "a toast comes in with the light transition"
                    + (toast != null ? "" : $" (toasts: {string.Join(", ", toastsNow)}; {shell.Toasts.Count} shown; under the shell: {shell.Toasts.transform.IsChildOf(shell.transform)})"));
            }
            finally
            {
                UiPanelFx.Caster = keepCaster;
                UiPanel.AnimateInEditor = false;
                foreach (var pp in shell.GetComponentsInChildren<UiPanel>(true)) if (pp != null && pp.Transitioning) pp.ForceRefresh();
                foreach (var id in shell.PanelIds.ToList()) shell.ClosePanel(id);
            }
        }

        /// MQH (owner 2026-11-09): one panel for SKILLS, PLUGINS, SCRIPTS, GESTURES and the daemon link; the skills that came
        /// with MetaCodex or MQH are read-only, only added ones can be removed; fewer dock and wrist entries.
        private static void MqhPanel(MetaCodexShell shell)
        {
            foreach (var id in shell.PanelIds.ToList()) shell.ClosePanel(id);
            var mp = shell.PanelsById["mqh"];
            var tabs = shell.MqhTabsForTest;
            Check(string.Join(",", tabs.Names) == "SKILLS,PLUGINS,SCRIPTS,GESTURES,STATUS" && !shell.PanelIds.Contains("scripts") && !shell.PanelIds.Contains("gestures"),
                "MQH: one panel with SKILLS, PLUGINS, SCRIPTS, GESTURES, STATUS (the old DAEMON; no separate SCRIPTS or GESTURES panel)");
            var tabOf = new (string id, int tab)[] { ("skills", 0), ("plugins", 1), ("scripts", 2), ("gestures", 3), ("daemon", 4) };
            var bad = new List<string>();
            foreach (var (id, tab) in tabOf)
            {
                shell.OpenPanel(id);
                if (!mp.IsOpen || tabs.Selected != tab || !shell.IsPanelOpen(id) || tabOf.Any(o => o.id != id && shell.IsPanelOpen(o.id))) bad.Add(id);
            }
            shell.TogglePanel("daemon");
            Check(bad.Count == 0 && !mp.IsOpen, bad.Count == 0 ? "the old ids open their MQH tab (ui.open, voice, bindings keep working); toggling the shown tab closes MQH" : "wrong tab for: " + string.Join(", ", bad));
            var settingsTabs = shell.PanelsById["settings"].Elements.OfType<UiTabs>().First();
            var dock = shell.PanelsById["dock"].Elements.OfType<UiButton>().Select(b => b.Text).ToList();
            var outer = shell.Wrist.Outer.Select(x => x.Label).ToList();
            Check(!settingsTabs.Names.Contains("DAEMON") && dock.Any(x => x.EndsWith("CONTROL")) && !dock.Any(x => x.EndsWith("MQH") || x.EndsWith("SCRIPTS") || x.EndsWith("GESTURE"))
                && !outer.Contains("MQH") && !outer.Contains("SCRIPTS") && !outer.Contains("GESTURE") && shell.ControlCategories.Any(c => c.Key == "mqh" && c.Panel == "mqh"),
                $"Settings without DAEMON; MQH is a CONTROL category (not on the dock or the wrist menu: {dock.Count} dock buttons, {outer.Count} outer blades)");

            // SKILLS: what MQH lists (canned), seen through the app's rules
            shell.SetShippedSkillsForTest(new[] { "metacodex-context", "quest-native-apps", "unity-cli" });
            string Id(char c) => "skill_" + new string(c, 64);
            var list = new JObject
            {
                ["skills"] = new JArray
                {
                    new JObject { ["id"] = Id('a'), ["name"] = "metacodex-context", ["description"] = "Where you are in MetaCodex", ["provider"] = null, ["scope"] = "plugin", ["pluginId"] = "metacodex-guide", ["filePath"] = "/root/.mqh/plugins/metacodex-guide/skills/metacodex-context/SKILL.md", ["manageable"] = false, ["registrySkillId"] = null },
                    new JObject { ["id"] = Id('b'), ["name"] = "metacodex-context", ["description"] = "Where you are in MetaCodex", ["provider"] = "claude-code", ["scope"] = "provider-user", ["pluginId"] = null, ["filePath"] = "/root/.claude/skills/metacodex-context/SKILL.md", ["manageable"] = true, ["registrySkillId"] = null },
                    new JObject { ["id"] = Id('c'), ["name"] = "my-notes", ["description"] = "How I like my commits", ["provider"] = null, ["scope"] = "bb-user", ["pluginId"] = null, ["filePath"] = "/root/.mqh/skills/my-notes/SKILL.md", ["manageable"] = true, ["registrySkillId"] = null },
                    new JObject { ["id"] = Id('d'), ["name"] = "skill-creator", ["description"] = "Create or improve MQH skills", ["provider"] = null, ["scope"] = "bb-builtin", ["pluginId"] = null, ["filePath"] = "/root/.mqh/builtin-skills/skill-creator/SKILL.md", ["manageable"] = false, ["registrySkillId"] = null },
                    new JObject { ["id"] = Id('e'), ["name"] = "pdf", ["description"] = "Read and fill PDF files", ["provider"] = "claude-code", ["scope"] = "provider-user", ["pluginId"] = null, ["filePath"] = "/root/.claude/skills/pdf/SKILL.md", ["manageable"] = true, ["registrySkillId"] = "anthropics/skills/pdf" },
                },
            };
            shell.OpenPanel("skills");
            shell.SetSkillsFromMqh(list);
            var rows = shell.SkillRowsForTest;
            string RowOf(string n) { var r = rows.FirstOrDefault(x => x.name == n); return r.name == null ? "missing" : r.group + (r.locked ? "/locked" : ""); }
            Check(rows.Count == 4 && RowOf("my-notes") == "YOURS" && RowOf("pdf") == "YOURS" && RowOf("metacodex-context") == "METACODEX/locked" && RowOf("skill-creator") == "MQH/locked"
                && rows[0].group == "YOURS",
                $"SKILLS: one row per skill, yours first; shipped and MQH's own read-only, even where a copy sits in an editable folder ({string.Join(", ", rows.Select(x => x.name + ":" + x.group + (x.locked ? "/locked" : "")))})");
            var items = mp.Elements.OfType<UiList>().First(l => l.Items.Any(i => i.Title == "my-notes")).Items.ToList();
            int iCtx = items.FindIndex(i => i.Title == "metacodex-context"), iMine = items.FindIndex(i => i.Title == "my-notes");
            string noCtx = shell.SkillRemoveRefusal(iCtx), noMine = shell.SkillRemoveRefusal(iMine);
            Check(noCtx != null && noCtx.Contains("read-only") && noMine == null, $"REMOVE refuses a shipped skill (“{noCtx}”) and allows one you added");
            shell.SelectSkillForTest("metacodex-context");
            mp.ForceRefresh();
            RenderPanel(mp, "ui-mqh-skills.png");
            var page = new JObject { ["skills"] = new JArray { new JObject { ["id"] = "anthropics/skills/xlsx", ["source"] = "anthropics/skills", ["skillId"] = "xlsx", ["name"] = "xlsx", ["installs"] = 1234, ["stars"] = 99, ["installUrl"] = null, ["url"] = "https://www.skills.sh/anthropics/skills/xlsx", ["topic"] = null, ["summary"] = "Spreadsheets" } } };
            shell.SetSkillResultsForTest(page, "spreadsheet");
            var reg = mp.Elements.OfType<UiList>().First(l => l.Items.Any(i => i.Title == "xlsx"));
            Check(shell.SkillsRegistryForTest && reg.Items.Count == 1, "⌕ FIND shows the registry's results to install");
            mp.ForceRefresh();
            RenderPanel(mp, "ui-mqh-skills-registry.png");
            string prompt = MetaCodexShell.NewSkillPrompt("tell the agents how I name my branches");
            Check(prompt.Contains("~/.mqh/skills") && prompt.Contains("skill-creator") && prompt.Contains("metacodex-context") && prompt.Contains("Never edit the skills that came with"),
                "+ NEW SKILL: the agent's brief names the place (~/.mqh/skills), skill-creator, metacodex-context, and the read-only rule");
            // voice: the new names, and the old ones still reach their tab
            var v = shell.Voice;
            var ms = v.FastMatch("open the skills");
            var mpl = v.FastMatch("show plugins");
            Check(ms != null && (string)ms.Args["panel"] == "skills" && mpl != null && (string)mpl.Args["panel"] == "plugins" && MetaCodexShell.VoicePanels.Contains("mqh"),
                $"voice: “open the skills” -> {(string)ms?.Args["panel"]}, “show plugins” -> {(string)mpl?.Args["panel"]}");
            foreach (var id in shell.PanelIds.ToList()) shell.ClosePanel(id);
        }

        /// Captures (owner 2026-11-09): SELECT keeps the window's area as an opaque picture of the window alone (nothing of
        /// the room even at its lowest opacity), the image clipboard keeps the newest N, CLIPBOARD lists them, and a
        /// thread's + attaches captures or Linux files to the next message through a modal inside the thread panel.
        private static void Captures(MetaCodexShell shell, string tmp)
        {
            foreach (var id in shell.PanelIds.ToList()) shell.ClosePanel(id);
            string dir = Path.Combine(tmp, "captures");
            if (Directory.Exists(dir)) Directory.Delete(dir, true);
            shell.SetClipboardDirForTest(dir);
            string op0 = shell.GetSetting("panelOpacity");
            int max0 = shell.Settings.clipboardMax;
            shell.OpenPanel("system");
            var p = shell.PanelsById["system"];
            p.ForceRefresh();
            var area = new Rect(0.04f, 0.07f, p.Width - 0.08f, p.Height * 0.45f);
            Texture2D Png(CaptureClipboard.Item it) { var t = new Texture2D(2, 2); t.LoadImage(File.ReadAllBytes(shell.Clipboard.HostPath(it))); return t; }
            shell.SetSetting("panelOpacity", "1");
            var full = shell.CapturePanelArea(p, area);
            // the same window at its lowest opacity, with something else (magenta, a colour the UI never uses) in front
            shell.SetSetting("panelOpacity", "0.5");
            var red = GameObject.CreatePrimitive(PrimitiveType.Quad);
            red.name = "uitest magenta";
            UnityEngine.Object.DestroyImmediate(red.GetComponent<Collider>());
            var redMat = new Material(Shader.Find("Universal Render Pipeline/Unlit")) { color = Color.magenta };
            redMat.SetColor("_BaseColor", Color.magenta);
            red.GetComponent<MeshRenderer>().sharedMaterial = redMat;
            red.transform.SetPositionAndRotation(p.transform.TransformPoint(p.PanelToLocal(area.center, -0.04f)), p.transform.rotation);
            red.transform.localScale = Vector3.one * 0.12f;
            var thin = shell.CapturePanelArea(p, area);
            float opAfter = shell.ShapeOpacityForTest;
            UnityEngine.Object.DestroyImmediate(red);
            var a = Png(full); var b = Png(thin);
            var pa = a.GetPixels32(); var pb = b.GetPixels32();
            bool Magenta(Color32 c) => c.r > 200 && c.b > 200 && c.g < 60;
            int reds = pb.Count(Magenta) - pa.Count(Magenta);
            double diff = pa.Length == pb.Length ? pa.Zip(pb, (x, y) => Math.Abs(x.r - y.r) + Math.Abs(x.g - y.g) + Math.Abs(x.b - y.b)).Average() / 3.0 : 999;
            Check(full != null && thin != null && a.width == full.w && reds == 0 && diff < 2.0 && pb.All(c => c.a == 255) && Mathf.Approximately(opAfter, 0.5f),
                $"capture: the window alone and opaque at 50 % opacity: {thin?.w}x{thin?.h}, {reds} pixels of the magenta thing in front, mean difference to 100 % opacity {diff:F2}/255; the window's opacity back to {opAfter:F2} after");
            UnityEngine.Object.DestroyImmediate(a); UnityEngine.Object.DestroyImmediate(b);
            shell.SetSetting("panelOpacity", op0);
            // the clipboard keeps the newest N: one more deletes the oldest file
            shell.Settings.clipboardMax = 3;
            var c3 = shell.CapturePanelArea(p, new Rect(0.04f, 0.07f, 0.2f, 0.12f));
            var c4 = shell.CapturePanelArea(p, new Rect(0.30f, 0.07f, 0.2f, 0.12f));
            var items = shell.Clipboard.Items.ToList();
            Check(items.Count == 3 && items[0].file == c4.file && !File.Exists(shell.Clipboard.HostPath(full)) && Directory.GetFiles(dir, "*.png").Length == 3,
                $"the clipboard keeps the newest {shell.Clipboard.Max}: the oldest capture's file is gone ({items.Count} listed, {Directory.GetFiles(dir, "*.png").Length} files)");
            var listed = JArray.Parse(shell.CaptureCommand("list"));
            Check(listed.Count == 3 && ((string)listed[0]["path"]).StartsWith(MetaCodexShell.CapturesLinuxDir + "/capture-"), "mcx-ui capture list: the Linux paths, newest first: " + (string)listed[0]["path"]);
            // SELECT (owner 2026-11-09): the orange frame goes as the picture is taken, from the window and from the picture
            var selArea = new Rect(0.05f, 0.08f, 0.25f, 0.14f);
            string newest0 = shell.Clipboard.Items.FirstOrDefault()?.file;
            shell.SetSelectionMode(true);
            Click(shell.Router, p, selArea.min, selArea.max);
            var shot = shell.Clipboard.Items.FirstOrDefault();
            bool took = shot != null && shot.file != newest0;
            bool frameGone = !shell.Router.SelectionShown(p);
            var plain = shell.CapturePanelArea(p, selArea); // the same area, no selection: to compare
            int OrangeEdge(Texture2D tx)
            {
                var px = tx.GetPixels32();
                var wc = (Color32)UiTheme.Current.Warn;
                int n = 0;
                for (int y = 0; y < tx.height; y++)
                    for (int x = 0; x < tx.width; x++)
                    {
                        if (x > 6 && x < tx.width - 7 && y > 6 && y < tx.height - 7) continue;
                        var c = px[y * tx.width + x];
                        if (Math.Abs(c.r - wc.r) < 45 && Math.Abs(c.g - wc.g) < 45 && Math.Abs(c.b - wc.b) < 45) n++;
                    }
                return n;
            }
            int oSel = -1, oPlain = -1;
            if (took && plain != null)
            {
                var ts = Png(shot); var tq = Png(plain);
                oSel = OrangeEdge(ts); oPlain = OrangeEdge(tq);
                UnityEngine.Object.DestroyImmediate(ts); UnityEngine.Object.DestroyImmediate(tq);
            }
            Check(took && frameGone && !shell.Router.SelectionMode && oSel >= 0 && oSel <= oPlain + 4,
                $"SELECT: the capture is taken ({took}), the orange frame is gone from the window ({frameGone}) and is not in the picture ({oSel} orange border pixels, {oPlain} in the same area without a selection)");
            // CLIPBOARD: the list and a preview
            shell.OpenPanel("clipboard");
            var cp = shell.PanelsById["clipboard"];
            cp.ForceRefresh();
            Check(shell.ClipListForTest.Items.Count == 3 && shell.ClipPreviewForTest != null && shell.ClipPreviewForTest.activeSelf, "CLIPBOARD lists the captures and shows the selected one");
            RenderPanel(cp, "ui-clipboard.png");
            // CLEAR ALL pressed three times (owner 2026-11-09): one dialog, parallel to CLIPBOARD, centred on it and in
            // front of it (never through it or half off its edge, a bent window's sides too); taps that land on it
            // right away answer nothing
            int clips0 = shell.Clipboard.Items.Count;
            var clearAll = cp.Elements.OfType<UiButton>().First(b => b.Text == "CLEAR ALL");
            for (int i = 0; i < 3; i++) Click(shell.Router, cp, clearAll.Rect.center);
            var dlgs = UiModal.OpenPanels.ToList();
            var dlg = dlgs.FirstOrDefault();
            var ct = cp.transform;
            float bendR = cp.BendNow * ct.lossyScale.x;
            float Sag(float x) => bendR > 0.01f ? bendR - Mathf.Sqrt(Mathf.Max(0f, bendR * bendR - Mathf.Min(x * x, bendR * bendR))) : 0f;
            float dAng = dlg != null ? Quaternion.Angle(dlg.transform.rotation, ct.rotation) : 999f;
            float dAhead = dlg != null ? Vector3.Dot(ct.position - dlg.transform.position, ct.forward) : 0f;
            float dOff = dlg != null ? Vector3.ProjectOnPlane(dlg.transform.position - ct.position, ct.forward).magnitude : 999f;
            float clearance = dlg == null ? 0f : new[] { Vector2.zero, new Vector2(dlg.Width, 0f), new Vector2(0f, dlg.Height), new Vector2(dlg.Width, dlg.Height) }
                .Min(c => { var rel = dlg.transform.TransformPoint(dlg.PanelToLocal(c)) - ct.position; return -Vector3.Dot(rel, ct.forward) - Sag(Vector3.Dot(rel, ct.right)); });
            Check(dlgs.Count == 1 && dAng < 0.5f && dAhead > 0.03f && dOff < 0.005f && clearance > 0.03f && shell.Clipboard.Items.Count == clips0,
                $"CLEAR ALL pressed 3 times: {dlgs.Count} dialog, parallel to CLIPBOARD ({dAng:F1}°), centred on it ({dOff * 100f:F1} cm off), {dAhead * 100f:F1} cm in front, its nearest corner {clearance * 100f:F1} cm clear of the window (bend {bendR:F2} m); the extra taps deleted nothing ({shell.Clipboard.Items.Count}/{clips0})");
            if (dlg != null) dlg.ForceRefresh(); // edit mode: build its shapes before the picture
            if (dlg != null) RenderAt("ui-dialog-over-window.png", ct.position - ct.forward * 0.9f + ct.right * 0.25f, Quaternion.LookRotation(ct.position - (ct.position - ct.forward * 0.9f + ct.right * 0.25f), Vector3.up), 50f, 900, 700);
            if (dlg != null)
            {
                System.Threading.Thread.Sleep(Mathf.CeilToInt(UiModal.InputGrace * 1000f) + 80);
                dlg.Elements.OfType<UiButton>().First(b => b.Text == "CANCEL").Click();
            }
            Check(UiModal.OpenCount == 0 && shell.Clipboard.Items.Count == clips0, $"CANCEL closes it, the captures stay ({UiModal.OpenCount} open, {shell.Clipboard.Items.Count} captures)");
            shell.ClosePanel("clipboard");
            // a thread: + -> the menu; REFERENCE CAPTURE -> a modal inside the thread panel; two picked -> two chips
            string root = GitService.ToLinuxPath(Path.GetFullPath(".."));
            var row = new JObject { ["id"] = "thr_att", ["projectId"] = "prj_att", ["providerId"] = "claude-code", ["title"] = "Attachments", ["status"] = "idle", ["runtime"] = new JObject { ["displayStatus"] = "idle" }, ["environmentPath"] = "/userland", ["updatedAt"] = 0 };
            var rows = new JArray { Row("conversation", 1, new JObject { ["role"] = "user", ["text"] = "Look at this" }) };
            shell.LoadThreadForTest(row, rows, null, null, new JArray { new JObject { ["id"] = "prj_att", ["name"] = "att", ["sources"] = new JArray() } }, false);
            shell.OpenPanel("thread");
            var tp = shell.PanelsById["thread"];
            shell.PressPlusForTest();
            bool menu = shell.AttachMenuOpenForTest && tp.Elements.OfType<UiButton>().Any(x => x.Visible && x.Text.Contains("REFERENCE CAPTURE")) && tp.Elements.OfType<UiButton>().Any(x => x.Visible && x.Text.Contains("ATTACH FILES"));
            shell.OpenAttachModal("capture");
            var timeline = tp.Elements.OfType<UiLabel>().FirstOrDefault(l => (l.Text ?? "").Contains("Look at this"));
            bool modal = shell.AttachModalOpenForTest && shell.AttachModalListForTest.Visible && shell.AttachModalListForTest.Items.Count == 3 && (timeline == null || !timeline.Visible);
            shell.PickInAttachModalForTest(0);
            shell.PickInAttachModalForTest(1);
            tp.ForceRefresh();
            RenderPanel(tp, "ui-thread-attach-capture.png");
            shell.ConfirmAttachModalForTest();
            Check(menu && modal && !shell.AttachModalOpenForTest && shell.AttachmentsForTest.Count == 2 && shell.AttachmentsForTest.All(x => x.Kind == "image"),
                $"+ opens ATTACH FILES / REFERENCE CAPTURE; the capture modal is inside the thread panel over the timeline; two picked -> {shell.AttachmentsForTest.Count} chips");
            // ATTACH FILES: a Linux folder, a file picked
            shell.OpenAttachModal("files");
            shell.SetAttachDirForTest("/userland/overlay/usr/local/bin");
            bool listsFiles = shell.AttachModalListForTest.Items.Any(x => x.Title == "mcx-app");
            shell.PickInAttachModalForTest("mcx-app");
            shell.ConfirmAttachModalForTest();
            var body = shell.ThreadSendBodyForTest("what is in these?");
            var input = body["input"] as JArray;
            var imgs = input.Where(x => (string)x["type"] == "localImage").ToList();
            var fileIn = input.FirstOrDefault(x => (string)x["type"] == "localFile");
            Check(listsFiles && input.Count == 4 && (string)input[0]["text"] == "what is in these?" && imgs.Count == 2 && imgs.All(x => ((string)x["path"]).StartsWith(MetaCodexShell.CapturesLinuxDir))
                && fileIn != null && (string)fileIn["path"] == "/userland/overlay/usr/local/bin/mcx-app" && (string)fileIn["name"] == "mcx-app" && fileIn["sizeBytes"].Value<long>() > 0,
                $"SEND posts the text, 2 localImage captures and 1 localFile ({input.Count} inputs): " + Trim(body["input"].ToString(Newtonsoft.Json.Formatting.None), 200));
            tp.ForceRefresh();
            RenderPanel(tp, "ui-thread-attachments.png");
            var only = shell.ThreadSendBodyForTest("");
            Check(((string)only["input"][0]["text"]).StartsWith("See the 2 attached images and the attached file"), "attachments alone are a message: " + (string)only["input"][0]["text"]);
            // voice and the wrist menu reach it
            var v = shell.Voice.FastMatch("open the clipboard");
            Check(v != null && (string)v.Args["panel"] == "clipboard" && shell.Wrist.Outer.Any(x => x.Label == "CLIP") && shell.PanelsById["dock"].Elements.OfType<UiButton>().Any(x => x.Text.EndsWith("CLIP")),
                $"voice “open the clipboard” -> {(string)v?.Args["panel"]}; CLIP on the wrist menu and the dock");
            shell.Settings.clipboardMax = max0;
            shell.ClosePanel("thread");
            foreach (var id in shell.PanelIds.ToList()) shell.ClosePanel(id);
        }

        private static string Trim(string s, int n) => s == null ? "" : s.Length > n ? s.Substring(0, n) + "…" : s;

        /// Core Session Context Protocol (docs/session-context.md): questions about the session are told apart from
        /// commands and answered from what the app knows (exact facts first), for the local model only.
        private static void SessionContextSection(MetaCodexShell shell, string tmp)
        {
            foreach (var id in shell.PanelIds.ToList()) shell.ClosePanel(id);
            // interpretation or command
            var cases = new (string text, bool ask)[]
            {
                ("open settings", false), ("close all windows", false), ("turn on ambient focus", false), ("open the terminal", false),
                ("can you open the files?", false), ("could you close everything?", false),
                ("Is this build process stuck?", true), ("What this thread achieved so far?", true), ("Do we have rust toolchain?", true),
                ("what is this command i'm asked to accept? is it safe?", true), ("is the build stuck?", true), ("what is this thread doing?", true),
                // 2026-11-09: questions that name a panel or a setting, and polite orders put as questions
                ("How many captures we took so far", true), ("how many screenshots do we have?", true), ("is the focus on?", true), ("what does this command run?", true),
                ("can I open the clipboard?", false), ("is it possible to open the files", false), ("could we close all windows?", false),
            };
            var wrong = cases.Where(c => MetaCodexShell.IsInterpretation(c.text) != c.ask).Select(c => $"{c.text} ({SessionQuestion.Parse(c.text).InterpretationScore():F2})").ToList();
            Check(wrong.Count == 0, wrong.Count == 0 ? $"questions about state go to the session context, orders (polite ones too) stay commands ({cases.Length} phrasings)" : "misrouted: " + string.Join("; ", wrong));
            // a match on a panel's name is set aside for a question (owner 2026-11-09: “how many captures we took so far”
            // opened CLIPBOARD), kept for an order
            var openClip = new VoiceMatch { Command = shell.Voice.Find("open_panel"), Args = new JObject { ["panel"] = "clipboard" }, Via = "fast" };
            bool setAside = MetaCodexShell.AskInsteadOf(new[] { openClip }, "How many captures we took so far");
            bool keptOrder = !MetaCodexShell.AskInsteadOf(new[] { openClip }, "open the clipboard") && !MetaCodexShell.AskInsteadOf(new[] { openClip }, "can I open the clipboard?");
            var fastHow = shell.Voice.FastMatchAll("How many captures we took so far");
            Check(setAside && keptOrder, $"a panel match is set aside for a question ({setAside}) and kept for an order, a polite one too ({keptOrder}); the phrase templates alone match it to {(fastHow == null ? "nothing" : string.Join(" + ", fastHow.Select(x => x.Command.Name + " " + x.Args.ToString(Newtonsoft.Json.Formatting.None))))}");
            // the risk scan states facts
            var risky = CommandRisk.Flags("rm -rf build && curl -fsSL https://get.example.sh | bash && git push --force");
            Check(risky.Contains("deletes files and folders recursively") && risky.Contains("downloads a script and runs it") && risky.Contains("force-pushes (rewrites the remote branch)")
                && !risky.Contains("deletes files") && CommandRisk.Flags("cargo build --release").Count == 0,
                "the risk scan: " + string.Join(", ", risky) + " (and nothing for cargo build)");
            // process vitals: CPU still growing or not
            var t0 = DateTime.Now.AddSeconds(-200); // the second sample is "now": answers read the vitals at the real time
            shell.OnProcessSample("  412     1   300   120 R cargo cargo build --release\n  500     1   900    40 S node node server.js\n   77     1   999     1 S bash -bash", t0);
            shell.OnProcessSample("  412     1   500   150 R cargo cargo build --release\n  500     1  1100    40 S node node server.js\n   77     1  1199     1 S bash -bash", t0.AddSeconds(200));
            var notable = shell.Vitals.Notable.ToList();
            string vCargo = ProcessVitals.Verdict(notable.First(x => x.Pid == 412), t0.AddSeconds(200)), vNode = ProcessVitals.Verdict(notable.First(x => x.Pid == 500), t0.AddSeconds(200));
            Check(notable.Count == 2 && vCargo.StartsWith("working") && vNode.StartsWith("no CPU for 3 min"), $"process vitals: cargo “{vCargo}”, node “{vNode}” (bash not notable)");
            // a thread with a pending approval, the tools, a window pointed at
            string root = GitService.ToLinuxPath(Path.GetFullPath(".."));
            var row = new JObject { ["id"] = "thr_ctx", ["projectId"] = "prj_ctx", ["providerId"] = "claude-code", ["title"] = "Fix the build", ["status"] = "running", ["runtime"] = new JObject { ["displayStatus"] = "waiting for approval" }, ["environmentPath"] = "/root/app", ["updatedAt"] = 1 };
            var rows = new JArray
            {
                Row("conversation", 1, new JObject { ["role"] = "user", ["text"] = "the release build fails, fix it" }),
                Row("work", 2, new JObject { ["workKind"] = "command", ["status"] = "completed", ["command"] = "cargo build --release", ["exitCode"] = 101 }),
                Row("work", 3, new JObject { ["workKind"] = "file-change", ["status"] = "completed", ["change"] = new JObject { ["path"] = "/root/app/src/main.rs", ["kind"] = "update", ["diffStats"] = new JObject { ["added"] = 12, ["removed"] = 3 } } }),
                Row("conversation", 4, new JObject { ["role"] = "assistant", ["text"] = "The build failed on a missing feature flag; I added it. Cleaning the target folder next." }),
            };
            shell.LoadThreadForTest(row, rows, null, null, new JArray { new JObject { ["id"] = "prj_ctx", ["name"] = "app", ["sources"] = new JArray() } }, false);
            shell.SetInteractionsForTest(new JArray { new JObject { ["id"] = "ia1", ["status"] = "pending", ["payload"] = new JObject { ["kind"] = "approval", ["subject"] = new JObject { ["command"] = "rm -rf target && cargo build --release" }, ["reason"] = "clean rebuild" } } });
            shell.SetToolsForTest(new[] { ("rustc", true, "1.91.0"), ("cargo", true, "1.91.0"), ("node", true, "24.11.0"), ("go", false, "") });
            shell.OpenPanel("thread");
            shell.SetPointedForTest("thread");
            string Ask(string question) { string a = null; shell.AnswerSessionQuestion(question, x => a = x); return a; }
            string rust = Ask("Do we have rust toolchain?"), goAns = Ask("do we have go?");
            Check(rust == "Yes: rustc 1.91.0, cargo 1.91.0." && goAns.StartsWith("No, go is not installed"), $"exact answers: “{rust}” / “{goAns}”");
            string cmd = Ask("what is this command i'm asked to accept? is it safe?");
            Check(cmd.Contains("Fix the build") && cmd.Contains("rm -rf target") && cmd.Contains("deletes files and folders recursively") && cmd.Contains("ALLOW or DENY") && !cmd.ToLowerInvariant().Contains(" is safe"),
                "“is this command safe?” without naming the thread: " + cmd);
            string stuck = Ask("Is this build process stuck?");
            Check(stuck.StartsWith("cargo build --release") && stuck.Contains("working") && !stuck.Contains("node"), "“is this build stuck?” from the process vitals, the build first: " + stuck);
            string busy = Ask("is anything still running?");
            Check(busy.Contains("cargo build") && busy.Contains("node server.js") && busy.Contains("no CPU for 3 min"), "“is anything still running?”: " + busy);
            string ctx = shell.SessionContextFor("What this thread achieved so far?");
            string recap = Ask("What this thread achieved so far?");
            Check(ctx.Length <= 1800 && ctx.Contains("FOCUS: pointing at") && ctx.Contains("asked: the release build fails") && ctx.Contains("1 command (1 failed), 1 file changed (+12 -3)")
                && ctx.Contains("APPROVAL: approval asked") && recap.Contains("the release build fails"),
                $"the recap's context ({ctx.Length} chars, for the local model) knows the thread pointed at; without the AI service: “{Trim(recap, 120)}”");
            // routed from voice / typed text, answered and marked as an answer
            VoiceResult vr = null;
            shell.VoiceProcessText("Do we have rust toolchain?", x => vr = x);
            Check(vr != null && vr.Ok && vr.Command == "ask_about_session" && vr.Answer && vr.Reply == rust, $"a typed question goes to ask_about_session: {vr?.Command} -> “{vr?.Reply}”");
            VoiceResult vo = null;
            shell.VoiceProcessText("open settings", x => vo = x);
            Check(vo != null && vo.Command == "open_panel" && shell.IsPanelOpen("settings"), $"“open settings” stays a command ({vo?.Command})");
            // spoken back (owner 2026-11-13: "I see the text but it doesn't seem to speak"): status questions are session
            // questions, answered aloud; a thread that ends or needs the user is said too (VOICE > "Say when a thread...")
            var said2 = new List<string>();
            var keepSpeak = shell.SpeakForTest;
            shell.SpeakForTest = (text, cb) => said2.Add(text); // asked to speak is enough (never answered: nothing plays)
            try
            {
                foreach (var q in new[] { "give me a status update on the ongoing threads", "how are my threads doing", "what's the status of the agents" })
                {
                    said2.Clear();
                    shell.StopSpeaking();
                    VoiceResult vs = null;
                    shell.VoiceProcessText(q, x => vs = x);
                    for (int k = 0; k < 4 && vs == null; k++) shell.UpdateForTest(); // a matched command answers on the next frame
                    Check(vs != null && vs.Command == "ask_about_session" && vs.Answer && said2.Count > 0,
                        $"“{q}” -> {vs?.Command} via {vs?.Via}, spoken ({said2.Count} sentence(s): “{Trim(said2.FirstOrDefault() ?? "", 60)}”)");
                }
                shell.StopSpeaking();
                said2.Clear();
                var tBefore = new ThreadPulse { State = "working", Title = "Docs pass" };
                shell.ThreadPulseChangeForTest("thr_alert", tBefore, new ThreadPulse { State = "idle", Title = "Docs pass" });
                string doneLine = shell.LastThreadAlertForTest;
                bool doneSaid = said2.Any(x => x.Contains("is done"));
                shell.StopSpeaking();
                said2.Clear();
                shell.ThreadPulseChangeForTest("thr_alert", tBefore, new ThreadPulse { State = "waiting", Title = "Docs pass", Doing = "waiting for your approval (bash)" });
                string askLine = shell.LastThreadAlertForTest;
                bool askSaid = said2.Any(x => x.Contains("approval"));
                shell.StopSpeaking();
                Check(doneLine == "The thread “Docs pass” is done." && doneSaid && askLine == "The agent in “Docs pass” needs your approval." && askSaid,
                    $"thread alerts are spoken: “{doneLine}” ({doneSaid}), “{askLine}” ({askSaid})");
            }
            finally { shell.SpeakForTest = keepSpeak; shell.StopSpeaking(); }
            // "it" (owner 2026-11-13: "close the settings panel", 5 s later "reopen it"): the window acted on last
            var refCases = new (string said, string last, string want)[]
            {
                ("reopen it", "settings", "open settings"), ("open it again", "settings", "open settings"), ("bring it back", "files", "open files"),
                ("Close it.", "voice", "close voice"), ("hide that one", "threads", "close threads"), ("reopen the files", null, "open the files"),
                ("open settings", "files", null), ("close the terminal", "files", null), ("reopen it", null, null),
            };
            var refBad = refCases.Where(c => MetaCodexShell.ResolveReference(c.said, c.last) != c.want).Select(c => $"{c.said} [{c.last}] -> {MetaCodexShell.ResolveReference(c.said, c.last) ?? "null"}").ToList();
            Check(refBad.Count == 0, $"“it” resolved to the window acted on last ({refCases.Length} cases" + (refBad.Count > 0 ? "; wrong: " + string.Join("; ", refBad) : ")"));
            shell.OpenPanel("settings");
            VoiceResult vc = null, vr2 = null;
            shell.VoiceProcessText("close the settings panel", x => vc = x);
            for (int k = 0; k < 4 && vc == null; k++) shell.UpdateForTest();
            bool closedS = !shell.IsPanelOpen("settings");
            shell.VoiceProcessText("reopen it", x => vr2 = x);
            for (int k = 0; k < 4 && vr2 == null; k++) shell.UpdateForTest();
            Check(closedS && vr2 != null && vr2.Command == "open_panel" && shell.IsPanelOpen("settings") && shell.LastRefForTest == "settings",
                $"“close the settings panel”, then “reopen it”: {vr2?.Command} ({vr2?.Corrected}), SETTINGS open again ({shell.IsPanelOpen("settings")})");
            shell.ClosePanel("settings");
            // “how many captures…” end to end (owner 2026-11-09): answered with the count, CLIPBOARD stays shut
            shell.ClosePanel("clipboard");
            int clipN = shell.Clipboard.Items.Count;
            foreach (var said in new[] { "How many captures we took so far", "how many screenshots do we have?" })
            {
                VoiceResult vh = null;
                shell.VoiceProcessText(said, x => vh = x);
                bool counted = vh?.Reply != null && (clipN == 0 ? vh.Reply.StartsWith("No captures") || vh.Reply.StartsWith("The clipboard is empty") : vh.Reply.StartsWith(clipN + " capture"));
                Check(vh != null && vh.Command == "ask_about_session" && counted && !shell.IsPanelOpen("clipboard"),
                    $"“{said}” -> {vh?.Command} via {vh?.Via}: “{vh?.Reply}” (CLIPBOARD {(shell.IsPanelOpen("clipboard") ? "OPENED" : "stays shut")})");
            }
            // the mid-term memory: the approval was logged, survives a restart within its window, old events go
            var events = shell.SessionMemory.Events.Select(e => e.Text).ToList();
            var log = new SessionMemory();
            string lp = Path.Combine(tmp, "session-context-test.jsonl");
            log.Add("thread", "t", "old", DateTime.Now.AddHours(-4));
            log.Add("approval", "t", "asked in “Fix the build”: rm -rf target");
            log.Save(lp);
            var back = new SessionMemory();
            back.Load(lp, DateTime.Now);
            Check(events.Any(e => e.StartsWith("asked in “Fix the build”")) && back.Events.Count == 1 && back.Events[0].Text.Contains("rm -rf target"),
                $"the mid-term log: the approval recorded; after a reload {back.Events.Count} event (the 4 h old one dropped)");
            // never for the ACP agents: no control-port command reads it
            Check(shell.ControlCommand("context", "") == null && shell.ControlCommand("session", "") == null, "the session context is not on the control port (agents cannot read it)");
            shell.SetInteractionsForTest(new JArray());
            foreach (var id in shell.PanelIds.ToList()) shell.ClosePanel(id);
        }



        private static void VoiceWords(MetaCodexShell shell)
        {
            foreach (var id in shell.PanelIds.ToList()) shell.ClosePanel(id);
            var v = shell.Voice;
            // misheard words are fixed against the commands' own vocabulary
            var m = v.FastMatch("Open ambient fogus.");
            Check(m != null && m.Command.Name == "open_panel" && (string)m.Args["panel"] == "focus", "“open ambient fogus” -> open_panel focus");
            var mc = v.FastMatch("turn on ambient fogus");
            bool focusOn = mc != null && (mc.Command.Name == "set_focus" && (string)mc.Args["state"] == "on" || mc.Command.Name == "set_setting" && (string)mc.Args["setting"] == "focus" && (string)mc.Args["value"] == "on");
            Check(focusOn, $"“turn on ambient fogus” -> focus on ({mc?.Command.Name} {mc?.Args.ToString(Newtonsoft.Json.Formatting.None)})");
            // the words split into the right slots: "dome radius" is the setting, not "dome" + "radius to ..."
            var sp = v.FastMatch("set the dome radius to two meters");
            Check(sp != null && sp.Command.Name == "set_setting" && (string)sp.Args["setting"] == "focus_radius" && (string)sp.Args["value"] == "two meters",
                $"“set the dome radius to two meters” -> set_setting focus_radius “two meters” ({sp?.Command.Name} {sp?.Args.ToString(Newtonsoft.Json.Formatting.None)})");
            VoiceResult r = null;
            shell.VoiceProcessText("Open ambient fogus.", x => r = x);
            Check(r != null && r.Ok && shell.IsPanelOpen("focus") && shell.PanelsById["settings"].IsOpen, "… and it opens Settings on the FOCUS tab: " + r);
            // several commands in one sentence
            float i0 = shell.Settings.focusIntensity, r0 = shell.Settings.focusRadius, ui0 = shell.Settings.uiScale, op0 = shell.Settings.panelOpacity, sv0 = shell.Settings.soundVolume;
            bool s0 = shell.Settings.sounds, f0 = shell.Settings.focusEnabled;
            var all = v.FastMatchAll("Turn on ambient focus and set intensity to 0.");
            Check(all != null && all.Count == 2 && all[0].Command.Name == "set_focus" && all[1].Command.Name == "set_setting" && (string)all[1].Args["setting"] == "focus_intensity",
                "“turn on ambient focus and set intensity to 0” -> two commands: " + (all == null ? "no match" : string.Join(" + ", all.Select(x => x.Command.Name + " " + x.Args.ToString(Newtonsoft.Json.Formatting.None)))));
            r = null;
            shell.VoiceProcessText("Turn on ambient focus and set intensity to 0.", x => r = x);
            Check(r != null && r.Ok && shell.Settings.focusEnabled && shell.Settings.focusIntensity < 0.001f, "… both run: focus on, intensity 0 (" + r?.Reply + ")");
            var slider = shell.PanelsById["settings"].Elements.OfType<UiSlider>().FirstOrDefault(x => x.Text == "INTENSITY");
            Check(slider != null && slider.Value < 0.001f, "… and the INTENSITY slider moved with it");
            // numbers, units, percent, more / less
            foreach (var (said, key, want) in new[]
            {
                ("set the dome radius to two meters", "focusRadius", 2f), ("radius 50 centimeters", "focusRadius", 0.5f), ("set the intensity to fifty percent", "focusIntensity", 0.5f),
                ("set intensity to 30", "focusIntensity", 0.3f), ("set the opacity to max", "panelOpacity", 1f), ("set the volume to a quarter", "soundVolume", 0.25f),
                ("set the focus intensity to twenty percent", "focusIntensity", 0.2f), ("turn the volume up to eighty percent", "soundVolume", 0.8f),
            })
            {
                r = null;
                shell.VoiceProcessText(said, x => r = x);
                float got = float.Parse(shell.Settings.Get(key), System.Globalization.CultureInfo.InvariantCulture);
                Check(r != null && r.Ok && Mathf.Abs(got - want) < 0.011f, $"“{said}” -> {key} {got:0.##} ({r?.Reply ?? r?.Error})");
            }
            r = null;
            shell.VoiceProcessText("turn off the sounds", x => r = x);
            Check(r != null && r.Ok && !shell.Settings.sounds, "“turn off the sounds” -> sounds off");
            r = null;
            shell.VoiceProcessText("make the windows bigger", x => r = x);
            Check(r != null && r.Ok && shell.Settings.uiScale > ui0 + 0.05f, $"“make the windows bigger” -> UI scale {ui0:0.00} -> {shell.Settings.uiScale:0.00} ({r?.Reply ?? r?.Error})");
            float vol0 = shell.Settings.soundVolume;
            r = null;
            shell.VoiceProcessText("volume a bit less", x => r = x);
            Check(r != null && r.Ok && shell.Settings.soundVolume < vol0 - 0.05f, $"“volume a bit less” -> volume {vol0:0.00} -> {shell.Settings.soundVolume:0.00} ({r?.Reply ?? r?.Error})");
            double n;
            Check(VoiceCommands.TryNumber("two point five", null, null, null, out n) && Mathf.Approximately((float)n, 2.5f)
                && VoiceCommands.TryNumber("twenty five", null, null, null, out n) && Mathf.Approximately((float)n, 25f)
                && VoiceCommands.TryNumber("0,75", null, null, null, out n) && Mathf.Approximately((float)n, 0.75f), "spoken numbers: “two point five”, “twenty five”, “0,75”");
            // the LLM may answer with several commands, held to valid ones by the grammar
            string g = v.LlmGrammar();
            var lines = v.ParseLines("set_focus on ; set_setting focus_intensity 0", out string perr);
            Check(g.Contains("cmd ::=") && g.Contains("\" ; \"") && g.Contains("\"set_setting\"") && lines != null && lines.Count == 2, "LLM: up to three commands per answer (“set_focus on ; set_setting focus_intensity 0”)" + (perr != null ? " " + perr : ""));
            // a plugin's own vocabulary is heard and fixed too
            var cmd = new VoiceCommand { Name = "zz_test_warp", Description = "test", Owner = "plugin/test", Phrases = new[] { "engage [the] warpdrive" }, Vocabulary = new[] { "warpdrive" }, Run = a => "Engaged" };
            v.Register(cmd);
            var wm = v.FastMatch("engage the warpdrove");
            Check(wm != null && wm.Command.Name == "zz_test_warp" && wm.Corrected == "engage the warpdrive", $"a plugin command's vocabulary fixes misheard words too (“warpdrove” -> “{wm?.Corrected}”)");
            v.Remove("zz_test_warp", "plugin/test");
            // restore
            shell.Settings.focusIntensity = i0; shell.Settings.focusRadius = r0; shell.Settings.uiScale = ui0; shell.Settings.sounds = s0;
            shell.Settings.panelOpacity = op0; shell.Settings.soundVolume = sv0;
            shell.SetFocus(f0, false);
            shell.OnSettingsChanged();
            shell.SyncSettingsControls();
            shell.TickFocusForTest(1f);
        }

        /// Renders with a transparent clear colour (like the passthrough underlay) and returns the pixels (RGBA).
        /// Renders the eye buffer (320 x 240, 60 degrees, looking along +z) with a synthetic environment depth: a 4 x 4 m
        /// wall `dist` metres ahead, nothing elsewhere (raw depth 1 = "far"), stored as Meta's texture does ((ndc + 1) / 2)
        /// with the same world -> depth clip matrix for both eyes.
        /// Meta's ndc -> linear depth parameters for a projection with these planes: linear = x / (ndc + y).
        private static Vector4 DepthZParams(float n, float f) => new Vector4(-2f * f * n / (f - n), -(f + n) / (f - n), 0f, 0f);

        /// A synthetic environment depth as Meta's texture stores it ((ndc + 1) / 2), both eyes the same, from a
        /// function giving each texel's distance along its ray (0 = no reading).
        private static Texture2DArray DepthTexture(Matrix4x4 m, Vector3 eyePos, int dw, int dh, Func<int, int, Vector3, float> distance)
        {
            var inv = m.inverse;
            var depth = new float[dw * dh];
            for (int y = 0; y < dh; y++)
                for (int x = 0; x < dw; x++)
                {
                    var farP = inv * new Vector4((x + 0.5f) / dw * 2f - 1f, (y + 0.5f) / dh * 2f - 1f, 1f, 1f);
                    var dir = ((Vector3)farP / farP.w - eyePos).normalized;
                    float r = distance(x, y, dir);
                    float d = 1f;
                    if (r > 0f)
                    {
                        var c = m * new Vector4(eyePos.x + dir.x * r, eyePos.y + dir.y * r, eyePos.z + dir.z * r, 1f);
                        d = Mathf.Clamp01((c.z / c.w + 1f) * 0.5f);
                    }
                    depth[y * dw + x] = d;
                }
            var tex = new Texture2DArray(dw, dh, 2, TextureFormat.RFloat, false, true) { filterMode = FilterMode.Point, wrapMode = TextureWrapMode.Clamp };
            tex.SetPixelData(depth, 0, 0);
            tex.SetPixelData(depth, 0, 1);
            tex.Apply(false);
            return tex;
        }

        /// The sharp depth's slice 0 read back (metres, row 0 first).
        private static float[] ReadSharp(MetaCodexShell shell, out int w, out int h)
        {
            var rt = shell.SharpDepthForTest;
            w = rt != null ? rt.width : 0;
            h = rt != null ? rt.height : 0;
            if (rt == null) return new float[0];
            var req = UnityEngine.Rendering.AsyncGPUReadback.Request(rt, 0, 0, w, 0, h, 0, 1, TextureFormat.RHalf, null);
            req.WaitForCompletion();
            if (req.hasError) return new float[0];
            var raw = req.GetData<ushort>(0);
            var outp = new float[raw.Length];
            for (int k = 0; k < raw.Length; k++) outp[k] = Mathf.HalfToFloat(raw[k]);
            return outp;
        }

        private static Texture2D RenderWithDepth(MetaCodexShell shell, string file, Vector3 head, float dist, int dw, int dh)
        {
            const float fov = 60f, aspect = 320f / 240f;
            var view = Matrix4x4.Scale(new Vector3(1f, 1f, -1f)) * Matrix4x4.TRS(head, Quaternion.identity, Vector3.one).inverse;
            var m = Matrix4x4.Perspective(fov, aspect, 0.1f, 20f) * view;
            var inv = m.inverse;
            var depth = new float[dw * dh];
            for (int y = 0; y < dh; y++)
                for (int x = 0; x < dw; x++)
                {
                    var farP = inv * new Vector4((x + 0.5f) / dw * 2f - 1f, (y + 0.5f) / dh * 2f - 1f, 1f, 1f);
                    var dir = ((Vector3)farP / farP.w - head).normalized;
                    float d = 1f;
                    if (dir.z > 1e-4f)
                    {
                        var hit = head + dir * (dist / dir.z);
                        if (Mathf.Abs(hit.x - head.x) <= 2f && Mathf.Abs(hit.y - head.y) <= 2f)
                        {
                            var c = m * new Vector4(hit.x, hit.y, hit.z, 1f);
                            d = Mathf.Clamp01((c.z / c.w + 1f) * 0.5f);
                        }
                    }
                    depth[y * dw + x] = d;
                }
            var tex = new Texture2DArray(dw, dh, 2, TextureFormat.RFloat, false, true) { filterMode = FilterMode.Bilinear, wrapMode = TextureWrapMode.Clamp };
            tex.SetPixelData(depth, 0, 0);
            tex.SetPixelData(depth, 0, 1);
            tex.Apply(false);
            shell.SetDepthForTest(tex, new[] { m, m }, DepthZParams(0.1f, 20f));
            shell.TickFocusForTest(0.2f);
            // a manual Editor render right after a material keyword changes still draws the old variant (measured:
            // 1.00 then 0.00 for the same frame): one warm-up render first
            UnityEngine.Object.DestroyImmediate(RenderRgba(null, head, Quaternion.identity, fov, 320, 240));
            var img = RenderRgba(file, head, Quaternion.identity, fov, 320, 240);
            UnityEngine.Object.DestroyImmediate(tex);
            return img;
        }

        private static Texture2D RenderRgba(string file, Vector3 pos, Quaternion rot, float fov, int w, int h)
        {
            foreach (var up in UnityEngine.Object.FindObjectsByType<UiPanel>(FindObjectsSortMode.None)) up.ApplyBendNow();
            var camGo = new GameObject("uitest-cam-rgba") { hideFlags = HideFlags.DontSave };
            try
            {
                var cam = camGo.AddComponent<Camera>();
                cam.clearFlags = CameraClearFlags.SolidColor;
                cam.backgroundColor = new Color(0f, 0f, 0f, 0f);
                cam.fieldOfView = fov;
                cam.nearClipPlane = 0.02f;
                cam.transform.SetPositionAndRotation(pos, rot);
                var rt = new RenderTexture(w, h, 24, RenderTextureFormat.ARGB32) { antiAliasing = 1 };
                cam.targetTexture = rt;
                cam.aspect = (float)w / h;
                cam.Render();
                RenderTexture.active = rt;
                var tex = new Texture2D(w, h, TextureFormat.RGBA32, false);
                tex.ReadPixels(new Rect(0, 0, w, h), 0, 0);
                tex.Apply();
                RenderTexture.active = null;
                if (file != null) File.WriteAllBytes(Path.Combine(MetaCodexSetup.ReportDir, file), tex.EncodeToPNG());
                cam.targetTexture = null;
                rt.Release();
                UnityEngine.Object.DestroyImmediate(rt);
                return tex;
            }
            finally { UnityEngine.Object.DestroyImmediate(camGo); }
        }

        /// The screen pixel of a world point for a camera at pos/rot (fov vertical, w x h).
        private static Vector2Int ScreenOf(Vector3 world, Vector3 pos, Quaternion rot, float fov, int w, int h)
        {
            var local = Quaternion.Inverse(rot) * (world - pos);
            float f = 1f / Mathf.Tan(fov * 0.5f * Mathf.Deg2Rad);
            float x = local.x / local.z * f / ((float)w / h), y = local.y / local.z * f;
            return new Vector2Int(Mathf.RoundToInt((x * 0.5f + 0.5f) * w), Mathf.RoundToInt((y * 0.5f + 0.5f) * h));
        }

        private static void OpacityAndBend(MetaCodexShell shell, Transform head)
        {
            foreach (var id in shell.PanelIds.ToList()) shell.ClosePanel(id);
            shell.SetLayout("fixed");
            float opacity0 = shell.Settings.panelOpacity;
            shell.Settings.panelOpacity = 1f;
            shell.OnSettingsChanged();
            var files = shell.PanelsById["files"];
            var settings = shell.PanelsById["settings"];
            foreach (var p in new[] { files, settings }) { p.gameObject.SetActive(true); p.SetBend(0f); }
            var fwd = Vector3.forward;
            files.transform.SetPositionAndRotation(head.position + fwd * 1.25f, Quaternion.identity);      // behind
            settings.transform.SetPositionAndRotation(head.position + fwd * 0.80f + Vector3.right * 0.05f, Quaternion.identity); // in front, overlapping
            foreach (var p in new[] { files, settings }) p.ForceRefresh();
            // 1) at 100 % opacity nothing of the passthrough shows through a panel: not its body, not its buttons
            var tex = RenderRgba("ui-opacity-alpha.png", head.position, Quaternion.identity, 70f, 640, 480);
            var btn = settings.Elements.OfType<UiButton>().First(b => b.Visible && b.Rect.width > 0.05f && !b.Text.Contains("✕"));
            var bp = ScreenOf(settings.SurfacePoint(btn.Rect.center, -0.001f), head.position, Quaternion.identity, 70f, 640, 480);
            var body = ScreenOf(settings.SurfacePoint(new Vector2(settings.Width * 0.5f, settings.Height * 0.6f)), head.position, Quaternion.identity, 70f, 640, 480);
            float aBtn = tex.GetPixel(bp.x, bp.y).a, aBody = tex.GetPixel(body.x, body.y).a;
            Check(aBtn > 0.985f && aBody > 0.985f, $"opacity 100 %: the eye buffer is opaque over a button ({aBtn:F3}) and the body ({aBody:F3}): no passthrough through them");
            UnityEngine.Object.DestroyImmediate(tex);
            // 2) the text of a window behind does not show through an opaque window in front
            var withBack = RenderRgba("ui-opacity-front.png", head.position, Quaternion.identity, 70f, 640, 480);
            files.gameObject.SetActive(false);
            var noBack = RenderRgba(null, head.position, Quaternion.identity, 70f, 640, 480);
            files.gameObject.SetActive(true);
            // compare inside the front panel's body, away from its own texts: sample a grid of pixels on it
            int diff = 0, n = 0;
            for (float u = 0.15f; u < 0.86f; u += 0.05f)
                for (float v = 0.25f; v < 0.95f; v += 0.05f)
                {
                    var sp = ScreenOf(settings.SurfacePoint(new Vector2(settings.Width * u, settings.Height * v)), head.position, Quaternion.identity, 70f, 640, 480);
                    if (sp.x < 0 || sp.y < 0 || sp.x >= 640 || sp.y >= 480) continue;
                    var c1 = withBack.GetPixel(sp.x, sp.y); var c2 = noBack.GetPixel(sp.x, sp.y);
                    n++;
                    if (Mathf.Abs(c1.r - c2.r) + Mathf.Abs(c1.g - c2.g) + Mathf.Abs(c1.b - c2.b) > 0.02f) diff++;
                }
            Check(n > 50 && diff == 0, $"opacity 100 %: nothing of the window behind shows through the window in front ({diff} of {n} sampled pixels differ)");
            UnityEngine.Object.DestroyImmediate(withBack);
            UnityEngine.Object.DestroyImmediate(noBack);
            files.gameObject.SetActive(false);
            // 3) bent: the ray lands where the panel is drawn (the cylinder), not on its flat projection
            settings.transform.SetPositionAndRotation(head.position + fwd * 0.8f, Quaternion.identity);
            settings.SetBend(0.8f);
            settings.ForceRefresh();
            int good = 0, total = 0;
            foreach (var target in new[] { new Vector2(0.04f, 0.2f), new Vector2(settings.Width * 0.5f, 0.3f), new Vector2(settings.Width - 0.04f, 0.45f), new Vector2(settings.Width - 0.02f, settings.Height - 0.03f) })
            {
                var world = settings.SurfacePoint(target);
                var ray = new Ray(head.position, (world - head.position).normalized);
                total++;
                if (settings.Raycast(ray, out var hit, out _) && Vector2.Distance(hit, target) < 0.002f) good++;
                else Line($"     bent hit for {target}: {(settings.Raycast(ray, out var h2, out _) ? h2.ToString("F3") : "miss")}");
            }
            Check(good == total, $"bent panel: rays hit the drawn (curved) surface: {good}/{total} spots within 2 mm");
            settings.SurfaceOf(settings.SurfacePoint(new Vector2(settings.Width - 0.03f, 0.2f), -0.01f), out var pp, out float depth);
            Check(Vector2.Distance(pp, new Vector2(settings.Width - 0.03f, 0.2f)) < 0.002f && Mathf.Abs(depth + 0.01f) < 0.002f, $"bent panel: a fingertip 1 cm in front of an edge maps to that spot ({pp.x:F3}, {pp.y:F3}, depth {depth * 100f:F1} cm)");
            Render(shell, "ui-bent.png", head.position, Quaternion.identity, 80f, 1280, 900);
            // 4) the bend eases: no steps
            settings.SetBend(0f);
            settings.SnapBend();
            var steps = new System.Collections.Generic.List<float>();
            settings.SetBend(0.8f);
            if (!Application.isPlaying) { settings.SnapBend(); settings.SetBend(0f); settings.SnapBend(); }
            // SetBend snaps in the Editor; drive the easing by hand from flat to 0.8 m
            typeof(UiPanel).GetField("_bendTarget", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance).SetValue(settings, 0.8f);
            for (int i = 0; i < 40; i++) { settings.EaseBendForTest(1f / 72f); steps.Add(settings.BendNow > 0 ? 1f / settings.BendNow : 0f); }
            float maxJump = 0f;
            for (int i = 1; i < steps.Count; i++) maxJump = Mathf.Max(maxJump, Mathf.Abs(steps[i] - steps[i - 1]));
            Check(Mathf.Abs(settings.BendNow - 0.8f) < 0.02f && maxJump < 0.2f, $"bend eases smoothly to its target (curvature step at most {maxJump:F3} /m per frame, now {settings.BendNow:F2} m)");
            settings.SetBend(0f);
            settings.SnapBend();
            shell.Settings.panelOpacity = opacity0;
            shell.OnSettingsChanged();
            settings.gameObject.SetActive(false);
        }

        private static JObject Row(string kind, int seq, JObject fields) { var o = new JObject { ["kind"] = kind, ["sourceSeqEnd"] = seq, ["id"] = "r" + seq }; foreach (var p in fields.Properties()) o[p.Name] = p.Value; return o; }

        private static void Workspace(MetaCodexShell shell, Transform head)
        {
            foreach (var id in shell.PanelIds.ToList()) shell.ClosePanel(id);
            shell.SetLayout("fixed");
            shell.ForgetSidebarMemoryForTest();
            string root = GitService.ToLinuxPath(Path.GetFullPath(".."));
            long now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            var projects = new JArray { new JObject { ["id"] = "prj_t", ["name"] = "metacodex", ["sources"] = new JArray { new JObject { ["path"] = root } } } };
            var row = new JObject
            {
                ["id"] = "thr_selftest", ["projectId"] = "prj_t", ["providerId"] = "claude-code", ["title"] = "Rework the thread panel", ["status"] = "running",
                ["runtime"] = new JObject { ["displayStatus"] = "running" }, ["environmentPath"] = root, ["environmentBranchName"] = "main", ["environmentIsWorktree"] = false,
                ["environmentId"] = "env_t", ["updatedAt"] = now,
            };
            string diff = "diff --git a/x b/x\n--- a/x\n+++ b/x\n@@ -1,3 +1,4 @@\n using System;\n-old line\n+new line\n+another\n context\n";
            var rows = new JArray
            {
                Row("conversation", 1, new JObject { ["role"] = "user", ["text"] = "Show me the workspace as a diagram" }),
                Row("work", 3, new JObject { ["workKind"] = "file-change", ["status"] = "completed", ["change"] = new JObject { ["path"] = root + "/unity/Assets/MetaCodex/Runtime/Shell/ShellWorkspace.cs", ["kind"] = "add", ["movePath"] = null, ["diff"] = diff, ["diffStats"] = new JObject { ["added"] = 120, ["removed"] = 0 } } }),
                Row("work", 4, new JObject { ["workKind"] = "command", ["status"] = "completed", ["command"] = "dotnet build", ["exitCode"] = 0 }),
                Row("work", 5, new JObject { ["workKind"] = "file-change", ["status"] = "completed", ["change"] = new JObject { ["path"] = "unity/Assets/MetaCodex/Runtime/UI/UiViz.cs", ["kind"] = "update", ["movePath"] = null, ["diff"] = null, ["diffStats"] = new JObject { ["added"] = 40, ["removed"] = 12 } } }),
                Row("conversation", 6, new JObject { ["role"] = "assistant", ["text"] = "Done. The module:\n```mermaid\ngraph LR\nT[Thread] --> W[Workspace]\nW --> C[Changes]\nW --> G[Git]\nW --> F[Files]\n```" }),
            };
            var usage = new JObject { ["usedTokens"] = 84000, ["modelContextWindow"] = 200000, ["estimated"] = false };
            var exec = new JObject { ["model"] = "claude-opus", ["reasoningLevel"] = "high", ["permissionMode"] = "full" };
            shell.LoadThreadForTest(row, rows, usage, exec, projects, true);
            shell.PumpForTest(1f);
            var th = shell.PanelsById["thread"];
            var ws = shell.PanelsById[MetaCodexShell.SidebarId];
            Check(!ws.IsOpen, "a thread seen for the first time opens with its sidebar folded");
            shell.SetSidebar(true, MetaCodexShell.TabChanges);
            shell.TickLayoutForTest(0.05f, 3);
            var rel = th.transform.InverseTransformPoint(ws.transform.position);
            Check(th.IsOpen && ws.IsOpen && rel.x > th.Width * 0.5f && Mathf.Abs(rel.y) < 0.01f, $"the sidebar opens at the thread's right edge ({rel.x * 100f:F0} cm from its centre)");
            var touched = shell.Touched.Values.Where(f => f.ByThread).ToList();
            Check(touched.Count == 2 && touched.Any(f => f.Path == "unity/Assets/MetaCodex/Runtime/Shell/ShellWorkspace.cs" && f.Kind == "add" && f.Added == 120) && touched.Any(f => f.Path.EndsWith("UiViz.cs") && f.Kind == "modify" && f.Removed == 12),
                "CHANGES: the thread's file changes, paths relative to the project folder: " + string.Join(", ", touched.Select(f => $"{f.Path} {f.Kind} +{f.Added}/-{f.Removed}")));
            string tl = shell.TimelineTextForTest;
            Check(tl.Contains("[▣ Diagram]") && !tl.Contains("```") && tl.Contains("+120"), "THREAD timeline: view block as a marker, file changes with their line counts");
            Check(shell.Views.Count(x => x.ThreadId == "thr_selftest") == 1 && shell.PanelsById.Keys.Any(k => k.StartsWith("view-") && shell.PanelsById[k].IsOpen), "the agent's diagram became a view and opened");
            string chips = shell.ChipsForTest;
            Check(chips.Contains("claude-opus") && chips.Contains("High") && chips.Contains("Full access") && chips.Contains("checkout") && chips.Contains("42%"), "composer chips: model + effort, access, workspace, context: " + chips);
            shell.PumpForTest(4f); // git status of the repo (WSL) and the merged list
            shell.PickChangedFileForTest("unity/Assets/MetaCodex/Runtime/Shell/ShellWorkspace.cs");
            shell.PumpForTest(3f);
            Check(shell.SidebarOpenDiffForTest == "unity/Assets/MetaCodex/Runtime/Shell/ShellWorkspace.cs" && shell.DiffHeadForTest.Contains("ShellWorkspace.cs"), $"picking a file opens its diff ({shell.DiffViewForTest.Doc?.RowCount ?? 0} rows)");
            // the thread moves: the workspace follows; dragged elsewhere it keeps its new place relative to the thread
            th.transform.position += new Vector3(0.2f, 0.1f, 0f);
            shell.TickLayoutForTest(0.05f, 2);
            var want = shell.SidebarPoseForTest();
            Check(Vector3.Distance(ws.transform.position, want.pos) < 0.001f && Quaternion.Angle(ws.transform.rotation, want.rot) < 0.1f, "the sidebar follows the thread panel (at its right edge after the move)");
            // more work arrives: a new file lights up
            shell.AppendTimelineForTest(new JArray { Row("work", 9, new JObject { ["workKind"] = "file-change", ["status"] = "completed", ["change"] = new JObject { ["path"] = root + "/docs/ui.md", ["kind"] = "update", ["movePath"] = null, ["diff"] = null, ["diffStats"] = new JObject { ["added"] = 8, ["removed"] = 2 } } }) });
            var fresh = shell.Touched.TryGetValue("docs/ui.md", out var fr) ? fr : null;
            Check(fresh != null && fresh.Added == 8 && fresh.Seq == 9, "a file the agent just edited joins the thread's changes");
            string handover = shell.HandoverContext();
            Check(handover.Contains("USER: Show me the workspace") && handover.Contains("Files touched") && handover.Contains("ShellWorkspace.cs") && !handover.Contains("```"), "fork to another agent: the conversation and touched files are handed over (" + handover.Length + " chars)");
            shell.ShowPermissionPickerForTest();
            Check(shell.PanelsById["picker"].IsOpen && shell.PickerListForTest.Items.Count == 3, "ACCESS chip: a picker with Edits / Auto / Full access");
            shell.ClosePanel("picker");
            // float: the sidebar rides at its thread's right edge on the ring, nothing overlaps
            shell.OpenPanel("files");
            shell.SetLayout("float");
            shell.TickLayoutForTest(0.05f, 100);
            var c = shell.CarouselCenter;
            float Ang(UiPanel p) { var d = p.transform.position - c; return Mathf.Atan2(d.x, d.z) * Mathf.Rad2Deg; }
            float gap = Mathf.DeltaAngle(Ang(th), Ang(ws));
            float r = new Vector2(th.transform.position.x - c.x, th.transform.position.z - c.z).magnitude;
            float expect = (th.Width * th.transform.lossyScale.x + ws.Width * ws.transform.lossyScale.x) * 0.5f / r * Mathf.Rad2Deg;
            Check(gap > 0f && Mathf.Abs(gap - expect) < 2f, $"float: the sidebar sits right of its thread on the ring ({gap:F1}° apart, {expect:F1}° + gap expected)");
            var files = shell.PanelsById["files"];
            float df = Mathf.Abs(Mathf.DeltaAngle(Ang(files), Ang(ws))), dt = Mathf.Abs(Mathf.DeltaAngle(Ang(files), Ang(th)));
            float halfF = files.Width * files.transform.lossyScale.x * 0.5f / r * Mathf.Rad2Deg;
            Check(df > halfF + ws.Width * ws.transform.lossyScale.x * 0.5f / r * Mathf.Rad2Deg && dt > halfF + th.Width * th.transform.lossyScale.x * 0.5f / r * Mathf.Rad2Deg, "float: no other panel overlaps the thread + sidebar pair");
            shell.SetLayout("fixed");
            shell.ClosePanel("thread");
            Check(!ws.IsOpen, "closing the thread closes its sidebar");
        }

        private static void ThreadGroups(MetaCodexShell shell)
        {
            foreach (var id in shell.PanelIds.ToList()) shell.ClosePanel(id);
            long now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            // projects and their folder names, the type from a path, the draft title (pure)
            string R = ProjectKinds.Root;
            Check(ProjectKinds.FolderName("My Weather App!") == "my-weather-app" && ProjectKinds.FolderName("  Notes  2.0 ") == "notes-2.0" && ProjectKinds.FolderName("../etc") == "etc"
                  && ProjectKinds.FolderName("!!!") == "" && ProjectKinds.FolderName("Café  Menu") == "caf-menu",
                "a project name as a folder: lower case, spaces -> '-', only a-z 0-9 . _ -, no leading dot");
            Check(ProjectKinds.PathFor("research", "Solar Panels") == R + "/research/solar-panels" && ProjectKinds.PathFor("native", "Weather") == R + "/native/weather"
                  && ProjectKinds.PathFor("bogus", "x") == null && ProjectKinds.PathFor("default", "???") == null,
                "a new project's folder: metacodex-projects/<type>/<folder name>");
            Check(ProjectKinds.TypeFromPath(R + "/native/app") == "native" && ProjectKinds.TypeFromPath(R + "/research/notes/sub") == "research" && ProjectKinds.TypeFromPath(R + "/default/site") == "default"
                  && ProjectKinds.TypeFromPath(R + "/native") == null && ProjectKinds.TypeFromPath(R + "/other/x") == null && ProjectKinds.TypeFromPath("/root/app") == null,
                "a project's type from its path under metacodex-projects/ (else none: OTHER)");
            Check(ProjectKinds.TypeFromJson("{\n  \"type\": \"general\"\n}") == "default" && ProjectKinds.TypeFromJson("{\"type\": \"research\"}") == "research" && ProjectKinds.TypeFromJson("{}") == null,
                "a project's type from .metacodex/project.json (the old “general” is DEFAULT)");
            Check(ProjectKinds.DraftTitle("atlas") == "New thread · atlas" && ProjectKinds.DraftTitle(null) == "New thread · no project", "a draft thread's title: New thread · <project>");
            var projects = new JArray
            {
                new JObject { ["id"] = "p1", ["name"] = "metacodex", ["sources"] = new JArray { new JObject { ["path"] = R + "/native/metacodex" } } },
                new JObject { ["id"] = "p2", ["name"] = "sandbox", ["sources"] = new JArray { new JObject { ["path"] = R + "/research/sandbox" } } },
                new JObject { ["id"] = "p3", ["name"] = "empty", ["sources"] = new JArray() },
            };
            JObject T(string id, string pid, string title, string st, long ago, bool ask = false) => new JObject { ["id"] = id, ["projectId"] = pid, ["title"] = title, ["providerId"] = "claude-code", ["status"] = st, ["runtime"] = new JObject { ["displayStatus"] = st }, ["updatedAt"] = now - ago, ["hasPendingInteraction"] = ask, ["environmentIsWorktree"] = id == "t2", ["environmentBranchName"] = "feat/x" };
            var threads = new JArray { T("t1", "p1", "Voice tuning", "idle", 3600000), T("t2", "p1", "Thread workspace", "running", 60000), T("t3", "p2", "Try physics", "idle", 7200000, true) };
            shell.OpenPanel("threads");
            shell.SetThreadsForTest(projects, threads);
            var list = shell.ThreadListForTest;
            var tags = list.Items.Select(i => i.Tag as string).ToList();
            // the types NATIVE, DEFAULT, RESEARCH (each shown; an empty one offers a project), then OTHER (p3: no folder);
            // p1 has a worktree: its folders are rows (the checkout first), the threads under them; p2 only its checkout
            Check(tags.SequenceEqual(new[] { "g:native", "p:p1", "w:p1|checkout", "t:t1", "w:p1|wt:feat/x", "t:t2", "g:default", "n:default", "g:research", "p:p2", "t:t3", "g:other", "p:p3", "p:p3" })
                  && list.Items[0].Title == "NATIVE" && list.Items[0].Badge == "1 RUNNING" && list.Items[8].Badge == "1 NEED YOU" && list.Items[11].Title == "OTHER"
                  && list.Items[1].Indent == 1 && list.Items[2].Indent == 2 && list.Items[3].Indent == 3 && list.Items[10].Indent == 2
                  && list.Items[1].Badge == "1 RUNNING" && list.Items[9].Badge == "1 NEED YOU" && list.Items[4].Title == "feat/x" && list.Items[1].Sub == "…/native/metacodex",
                "THREADS grouped by type, then project, then folder (checkout, worktrees) when a project has a worktree, newest first, with running / needs-you badges: " + string.Join(" ", tags));
            shell.PickThreadRowForTest("p:p1");
            Check(shell.ThreadListForTest.Items.Count == 10 && shell.ThreadListForTest.Items[1].Icon == "▸", "tapping a project folds it");
            shell.PickThreadRowForTest("p:p1");
            shell.PickThreadRowForTest("g:research");
            Check(shell.ThreadListForTest.Items.Count == 12 && shell.ThreadListForTest.Items[8].Icon == "▸" && shell.ThreadListForTest.Items[9].Tag as string == "g:other", "tapping a type header folds its projects");
            shell.PickThreadRowForTest("g:research");
            shell.SetThreadsForTest(new JArray { projects[2] }, new JArray());
            var other = shell.ThreadListForTest.Items.Select(i => i.Tag as string).ToList();
            Check(other.SequenceEqual(new[] { "g:native", "n:native", "g:default", "n:default", "g:research", "n:research", "g:other", "p:p3", "p:p3" }), "OTHER only when a project is outside the types: " + string.Join(" ", other));
            shell.SetThreadsForTest(new JArray { projects[0] }, new JArray());
            Check(!shell.ThreadListForTest.Items.Any(i => i.Tag as string == "g:other"), "no OTHER header when every project has a type");
            shell.SetThreadsForTest(projects, threads);
            shell.PanelsById["threads"].ForceRefresh();
            RenderPanel(shell.PanelsById["threads"], "ui-threads-grouped.png");
        }

        private static void VaultGit(MetaCodexShell shell)
        {
            foreach (var id in shell.PanelIds.ToList()) shell.ClosePanel(id);
            Check(shell.OpenPanel("git") && shell.IsPanelOpen("git") && shell.PanelsById["vault"].IsOpen && shell.VaultTabsForTest.Selected == 1, "“git” opens the vault on its GIT tab");
            shell.VaultTabsForTest.Select(0);
            Check(!shell.IsPanelOpen("git") && shell.IsPanelOpen("vault"), "on the SECRETS tab, git does not count as open");
            shell.TogglePanel("git");
            Check(shell.IsPanelOpen("git"), "toggle git: shows the GIT tab of the open vault");
            shell.TogglePanel("git");
            Check(!shell.PanelsById["vault"].IsOpen, "toggle git again: closes it");
            shell.OpenPanel("git");
            bool had = shell.Vault.Has(MetaCodexShell.GitHubTokenName);
            if (!had) shell.Vault.Set(MetaCodexShell.GitHubTokenName, "ghp_selftest_not_a_token", "token", "allow", "self-test");
            shell.RefreshGitHubStatus();
            shell.PumpForTest(3f);
            System.Threading.Thread.Sleep(1500);
            shell.PumpForTest(2f);
            Check(shell.GitHubStatusForTest.Contains("token in the vault"), "GIT tab: the GitHub account shows the vault's token: " + shell.GitHubStatusForTest.Replace("\n", " ⏎ "));
            var vp = shell.PanelsById["vault"];
            vp.ForceRefresh();
            RenderPanel(vp, "ui-vault-git.png");
            if (!had) shell.Vault.Delete(MetaCodexShell.GitHubTokenName);
            shell.VaultTabsForTest.Select(0);
        }

        private static float Luma(string file, Rect area)
        {
            var tex = new Texture2D(2, 2);
            tex.LoadImage(File.ReadAllBytes(Path.Combine(MetaCodexSetup.ReportDir, file)));
            float sum = 0; int n = 0;
            for (int y = (int)(area.yMin * tex.height); y < (int)(area.yMax * tex.height); y += 4)
                for (int x = (int)(area.xMin * tex.width); x < (int)(area.xMax * tex.width); x += 4) { var c = tex.GetPixel(x, y); sum += c.r * 0.3f + c.g * 0.59f + c.b * 0.11f; n++; }
            UnityEngine.Object.DestroyImmediate(tex);
            return n > 0 ? sum / n : 0f;
        }

        private static void Focus(MetaCodexShell shell, Transform head, UiPanel frame)
        {
            foreach (var id in shell.PanelIds.ToList()) shell.ClosePanel(id);
            var sh = Resources.Load<Shader>("Shaders/AmbientFocus");
            int errors = sh == null ? -1 : ShaderUtil.GetShaderMessages(sh).Count(m => m.severity == UnityEditor.Rendering.ShaderCompilerMessageSeverity.Error);
            Check(errors == 0, $"shader MetaCodex/AmbientFocus compiles ({errors} errors)");
            foreach (var name in new[] { "Shaders/StageLit", "Shaders/ViewImage", "Shaders/FocusMesh", "Shaders/FocusDepthCopy" })
            {
                var s2 = Resources.Load<Shader>(name);
                int e2 = s2 == null ? -1 : ShaderUtil.GetShaderMessages(s2).Count(m => m.severity == UnityEditor.Rendering.ShaderCompilerMessageSeverity.Error);
                Check(e2 == 0, $"shader {name} compiles ({e2} errors)");
            }
            shell.OpenPanel("files");
            var down = Quaternion.Euler(70f, 0f, 0f);
            var up = Quaternion.Euler(-25f, 0f, 0f);
            shell.SetFocus(false, false);
            shell.TickFocusForTest(1f);
            Render(shell, "ui-focus-off-up.png", head.position, up, 70f, 480, 270);
            float offUp = Luma("ui-focus-off-up.png", new Rect(0f, 0.6f, 1f, 0.4f));
            shell.ApplyFocusPreset("focus");
            shell.TickFocusForTest(1f);
            Check(shell.FocusSphereForTest != null && shell.FocusSphereForTest.activeSelf && shell.FocusShownForTest >= 1f, "ambient focus: the sphere fades in around the head");
            // the dome itself, judged with the void's effects off (the plain darkness, as it was before them)
            shell.FocusMaterialForTest.SetFloat("_Void", 0f);
            Render(shell, "ui-focus-up-plain.png", head.position, up, 70f, 480, 270);
            Render(shell, "ui-focus-down.png", head.position, down, 70f, 480, 270);
            float onUp = Luma("ui-focus-up-plain.png", new Rect(0f, 0.6f, 1f, 0.4f)), onDown = Luma("ui-focus-down.png", new Rect(0.3f, 0.3f, 0.4f, 0.4f));
            Check(onUp < offUp * 0.5f && onDown > onUp * 1.5f, $"ambient focus: the room above the horizon darkens ({offUp:F3} -> {onUp:F3}); the floor near you stays ({onDown:F3})");
            // the void (owner 2026-11-09: "not simple black"): deep space, a little lighter than the plain darkness,
            // still well darker than the room
            shell.FocusMaterialForTest.SetFloat("_Void", 1f);
            Render(shell, "ui-focus-up.png", head.position, up, 70f, 480, 270);
            float voidUp = Luma("ui-focus-up.png", new Rect(0f, 0.6f, 1f, 0.4f));
            Check(voidUp > onUp + 0.003f && voidUp < offUp * 0.7f, $"the void outside the dome: deep space, not plain black ({onUp:F3} -> {voidUp:F3}), still well darker than the room ({offUp:F3})");
            shell.ApplyFocusPreset("blackout");
            shell.TickFocusForTest(1f);
            // the stars twinkle and the sky turns with the clock: fixed here, so the measure is the same every run
            shell.FocusMaterialForTest.SetFloat("_VoidTime", 0f);
            shell.FocusMaterialForTest.SetVector("_VoidRot", new Vector4(1f, 0f, 0f, 0f));
            Render(shell, "ui-focus-blackout.png", head.position, Quaternion.identity, 100f, 1280, 720);
            float black = Luma("ui-focus-blackout.png", new Rect(0f, 0.85f, 1f, 0.15f));
            float panel = Luma("ui-focus-blackout.png", new Rect(0.42f, 0.4f, 0.16f, 0.2f));
            // the void (2026-11-09): deep space, not black, but dark: the windows stand out of it
            // (0.049-0.051 from run to run with a free clock: dark is under 0.06, the room is 0.11)
            Check(black < 0.06f && panel > black + 0.02f, $"BLACKOUT: dark space around you ({black:F3}), the windows stand out ({panel:F3})");
            // the dome from live depth, as Meta's EnvironmentDepthManager gives it (no Space Setup): a synthetic depth
            // map of a wall straight ahead, judged on the eye buffer's alpha (0 = the real world shows, 1 = black)
            shell.FloorYOverride = 0f; // the head stands 1.4 m above it
            shell.Settings.focusRadius = 2.5f;
            shell.Settings.focusSoftness = 0.2f;
            shell.Settings.focusIntensity = 1f;
            shell.Settings.focusBlur = 0f;
            shell.SetFocus(true, false);
            shell.ClosePanel("files");
            const int dw = 160, dh = 120;
            var near = RenderWithDepth(shell, "ui-focus-wall-near.png", head.position, 1.0f, dw, dh);
            // in the band above the test terminal (it sits in front, opaque by design); there the wall is 0.5 m above the
            // eyes at 1 m, 1.8 m at 4 m (still on the 4 m wall)
            int[] xs = { 80, 160, 240 };
            float aNear = xs.Max(x => near.GetPixel(x, 215).a);
            var far = RenderWithDepth(shell, "ui-focus-wall-far.png", head.position, 4.0f, dw, dh);
            float aFar = xs.Min(x => far.GetPixel(x, 215).a);
            Check(aNear < 0.05f && aFar > 0.95f, $"the dome from live depth: a real wall 1 m away stays visible ({aNear:F2}), one 4 m away is outside a 2.5 m dome and fades ({aFar:F2})");
            UnityEngine.Object.DestroyImmediate(near);
            UnityEngine.Object.DestroyImmediate(far);
            // BLUR: outside the dome the camera's image, soft and darkened by INTENSITY, covers the passthrough;
            // inside it the passthrough stays sharp; beyond what the camera has seen, the room's colours, frosted (never sharp)
            shell.Settings.focusBlur = 0.5f;
            shell.Settings.focusIntensity = 0.5f;
            var camImg = new Texture2D(64, 48, TextureFormat.RGBA32, true, false) { wrapMode = TextureWrapMode.Clamp };
            var orange = new Color32[64 * 48];
            for (int k = 0; k < orange.Length; k++) orange[k] = new Color32(255, 128, 0, 255);
            camImg.SetPixels32(orange);
            camImg.Apply(true);
            var camPose = new Pose(head.position, Quaternion.identity);
            shell.SetCameraForTest(camImg, camPose, new Vector2(457f, 457f), new Vector2(320f, 240f), new Vector2Int(640, 480), new Vector2Int(640, 480));
            // the windows out of the way for these pictures (where the terminal is depends on the sections before)
            var windows = UnityEngine.Object.FindObjectsByType<UiPanel>(FindObjectsSortMode.None).Select(x => x.gameObject)
                .Concat(UnityEngine.Object.FindObjectsByType<TerminalView>(FindObjectsSortMode.None).Select(x => x.gameObject)).Where(g => g.activeSelf).Distinct().ToList();
            foreach (var g in windows) g.SetActive(false);
            var blurFar = RenderWithDepth(shell, "ui-focus-blur-far.png", head.position, 4.0f, dw, dh);
            // probes over the middle of the view, inside the camera's round mask: the room's blurred picture covers it
            var blurNear = RenderWithDepth(shell, "ui-focus-blur-near.png", head.position, 1.0f, dw, dh);
            float aBlurNear = xs.Max(x => blurNear.GetPixel(x, 215).a);
            int orangeProbes = 0, probes = 0;
            for (int y = 70; y < 170; y += 5)
                for (int x = 60; x < 260; x += 5)
                {
                    var px = blurFar.GetPixel(x, y);
                    probes++;
                    if (px.a > 0.95f && px.r > 0.15f && px.r > px.g && px.g > px.b) orangeProbes++;
                }
            Check(orangeProbes > probes * 3 / 4 && aBlurNear < 0.05f,
                $"BLUR: outside the dome the room's blurred picture covers the passthrough ({orangeProbes} of {probes} probes orange in the middle of the view); inside it stays clear ({aBlurNear:F2})");
            // the blur is a panorama of the room, painted through a round mask: the picture's corners are not in it
            // (a rectangle would be), and it stays with the room when the head turns; walking away starts a new one
            float fwd = shell.BlurCoverageForTest(Vector3.forward);
            Vector3 cornerUp = new Vector3(0.69f, 0.52f, 1f), cornerDown = new Vector3(0.69f, -0.52f, 1f); // a mirrored pair: row order does not matter
            float cu = shell.BlurCoverageForTest(cornerUp), cd = shell.BlurCoverageForTest(cornerDown), side = shell.BlurCoverageForTest(Vector3.right);
            Check(fwd > 0.9f && cu < 0.05f && cd < 0.05f && side < 0.01f,
                $"BLUR panorama: ahead seen ({fwd:F2}); the picture's corners left out by the round mask ({cu:F2}, {cd:F2}: no rectangle); 90° right not seen yet ({side:F2})");
            shell.AddCameraFrameForTest(camImg, new Pose(head.position, Quaternion.Euler(0f, 90f, 0f)), new Vector2(457f, 457f), new Vector2(320f, 240f), new Vector2Int(640, 480), new Vector2Int(640, 480));
            float fwd2 = shell.BlurCoverageForTest(Vector3.forward), side2 = shell.BlurCoverageForTest(Vector3.right);
            Check(fwd2 > 0.9f && side2 > 0.9f, $"BLUR stays with the room: the head turned 90°, what it saw ahead is kept ({fwd2:F2}) and the new side is added ({side2:F2})");
            shell.AddCameraFrameForTest(camImg, new Pose(head.position + new Vector3(2f, 0f, 0f), Quaternion.Euler(0f, 90f, 0f)), new Vector2(457f, 457f), new Vector2(320f, 240f), new Vector2Int(640, 480), new Vector2Int(640, 480), 1);
            float fwd3 = shell.BlurCoverageForTest(Vector3.forward);
            Check(fwd3 < 0.05f, $"BLUR: 2 m further on, a new panorama starts (what was ahead from the old spot is dropped: {fwd3:F2})");
            // a narrow camera: the top of the view is beyond its image -> the room's colours, frosted (covers the passthrough)
            shell.SetCameraForTest(camImg, camPose, new Vector2(1800f, 1800f), new Vector2(320f, 240f), new Vector2Int(640, 480), new Vector2Int(640, 480));
            var narrow = RenderWithDepth(shell, "ui-focus-blur-narrow.png", head.position, 4.0f, dw, dh);
            var cn = narrow.GetPixel(160, 215);
            Check(cn.a > 0.95f && cn.r > 0.15f && cn.r > cn.b, $"BLUR: beyond what the camera has seen, the room's colours, frosted: it covers the passthrough there too (alpha {cn.a:F2}, red {cn.r:F2}, blue {cn.b:F2})");
            foreach (var g in windows) g.SetActive(true);
            foreach (var t in new[] { blurFar, blurNear, narrow }) UnityEngine.Object.DestroyImmediate(t);
            shell.SetCameraForTest(null, default, default, default, default, default);
            UnityEngine.Object.DestroyImmediate(camImg);
            shell.SetDepthForTest(null, null, default);
            shell.Settings.focusBlur = 0f;
            shell.Settings.focusIntensity = 1f;
            // the floor under the feet (SandyBridge's rule): the lowest level with enough depth points near you, not
            // a table, not a patch of floor read too high; it counts once three readings agree
            var rng = new System.Random(7);
            float Noise() => (float)(rng.NextDouble() - 0.5) * 0.02f;
            var pts = new System.Collections.Generic.List<Vector3>();
            var hp = new Vector3(0f, 1.6f, 0f);
            for (int k = 0; k < 220; k++) pts.Add(new Vector3((float)rng.NextDouble() * 2f - 1f, -0.12f + Noise(), (float)rng.NextDouble() * 2f - 1f));
            for (int k = 0; k < 60; k++) pts.Add(new Vector3((float)rng.NextDouble() - 0.5f, -0.02f + Noise(), 0.5f + (float)rng.NextDouble() * 0.3f)); // misread patch
            for (int k = 0; k < 150; k++) pts.Add(new Vector3((float)rng.NextDouble() - 0.5f, 0.75f + Noise(), 0.8f)); // a table top
            float est = MetaCodexShell.EstimateFloor(pts, hp, 0f);
            var tableOnly = pts.Where(p => p.y > 0.5f).ToList();
            float none = MetaCodexShell.EstimateFloor(tableOnly, hp, 0f);
            shell.ResetFloorForTest();
            shell.FeedFloorForTest(pts, hp, 0f);
            bool earlyStable = !float.IsNaN(shell.FeetFloorForTest);
            shell.FeedFloorForTest(pts, hp, 0f);
            shell.FeedFloorForTest(pts, hp, 0f);
            Check(Mathf.Abs(est + 0.12f) < 0.02f && float.IsNaN(none) && !earlyStable && Mathf.Abs(shell.FeetFloorForTest + 0.12f) < 0.02f && shell.FocusStatus.Contains("measured under your feet"),
                $"floor from depth: {est * 100f:0.0} cm (real -12, a patch read at -2 and a table ignored), none from a table alone, counts after 3 readings ({shell.FeetFloorForTest * 100f:0.0} cm): {shell.FocusStatus}");
            shell.ResetFloorForTest();
            // the floor readback's copy shader (never drawn otherwise in the Editor): every texel carries the depth
            // at its own uv, so the reader does not depend on row order
            {
                var copyMat = new Material(Resources.Load<Shader>("Shaders/FocusDepthCopy"));
                var arr = new Texture2DArray(4, 4, 2, TextureFormat.RFloat, false, true) { filterMode = FilterMode.Point, wrapMode = TextureWrapMode.Clamp };
                var vals = Enumerable.Range(0, 16).Select(k => (k + 1) / 17f).ToArray();
                arr.SetPixelData(vals, 0, 0);
                arr.SetPixelData(vals, 0, 1);
                arr.Apply(false);
                Shader.SetGlobalTexture("_EnvironmentDepthTexture", arr);
                var crt = new RenderTexture(4, 4, 0, RenderTextureFormat.ARGBFloat);
                var ccb = new UnityEngine.Rendering.CommandBuffer();
                ccb.SetRenderTarget(crt);
                ccb.DrawProcedural(Matrix4x4.identity, copyMat, 0, MeshTopology.Triangles, 3);
                Graphics.ExecuteCommandBuffer(ccb);
                ccb.Release();
                RenderTexture.active = crt;
                var back = new Texture2D(4, 4, TextureFormat.RGBAFloat, false, true);
                back.ReadPixels(new Rect(0, 0, 4, 4), 0, 0);
                back.Apply();
                RenderTexture.active = null;
                int good = 0;
                for (int y = 0; y < 4; y++)
                    for (int x = 0; x < 4; x++)
                    {
                        var c = back.GetPixel(x, y);
                        int sx = Mathf.Clamp(Mathf.FloorToInt(c.g * 4f), 0, 3), sy = Mathf.Clamp(Mathf.FloorToInt(c.b * 4f), 0, 3);
                        if (c.a > 0.5f && Mathf.Abs(c.r - vals[sy * 4 + sx]) < 1e-4f) good++;
                    }
                Check(good == 16, $"floor readback copy: each texel carries its depth and its own uv ({good}/16)");
                crt.Release();
                foreach (var o in new UnityEngine.Object[] { copyMat, arr, crt, back }) UnityEngine.Object.DestroyImmediate(o);
            }
            // SHARP DEPTH (RGB-guided, SandyBridge docs/DEPTH_UPSCALE_PLAN.md option A): 1.5 x the sensor's resolution
            // (2 x 2 readings per texel, 2026-11-13); the sensor puts a near object's edge between readings 29 and 30, the
            // camera picture half a reading further: without the camera the sharp edge stays at the sensor's, with it it
            // moves to the picture's
            {
                const int sw = 64, shh = 64;
                var eyeP = head.position;
                var viewM = Matrix4x4.Scale(new Vector3(1f, 1f, -1f)) * Matrix4x4.TRS(eyeP, Quaternion.identity, Vector3.one).inverse;
                var mm = Matrix4x4.Perspective(60f, 1f, 0.1f, 20f) * viewM;
                var zp = DepthZParams(0.1f, 20f);
                var edgeTex = DepthTexture(mm, eyeP, sw, shh, (x, y, dir) => (x < 30 ? 1.0f : 3.0f) / dir.z);
                var pic = new Texture2D(128, 128, TextureFormat.RGBA32, false, false) { wrapMode = TextureWrapMode.Clamp, filterMode = FilterMode.Bilinear };
                var pix = new Color32[128 * 128];
                for (int y = 0; y < 128; y++) for (int x = 0; x < 128; x++) pix[y * 128 + x] = x < 61 ? new Color32(230, 40, 30, 255) : new Color32(30, 60, 230, 255);
                pic.SetPixels32(pix);
                pic.Apply(false);
                float fpx = 64f / Mathf.Tan(30f * Mathf.Deg2Rad);
                int EdgeColumn(float[] d, int w, int hh) { int row = hh / 2; for (int x = 0; x < w; x++) if (d[row * w + x] > 2f) return x; return -1; }
                shell.SetCameraForTest(null, default, default, default, default, default);
                shell.SetDepthForTest(edgeTex, new[] { mm, mm }, zp);
                var plain = ReadSharp(shell, out int w1, out int h1);
                int e0 = EdgeColumn(plain, w1, h1);
                shell.SetCameraForTest(pic, new Pose(eyeP, Quaternion.identity), new Vector2(fpx, fpx), new Vector2(64f, 64f), new Vector2Int(128, 128), new Vector2Int(128, 128));
                shell.SetDepthForTest(edgeTex, new[] { mm, mm }, zp);
                var guided = ReadSharp(shell, out int w2, out int h2);
                int e1 = EdgeColumn(guided, w2, h2);
                float nearV = guided.Length > 0 ? guided[(h2 / 2) * w2 + w2 * 5 / 32] : 0f, farV = guided.Length > 0 ? guided[(h2 / 2) * w2 + w2 * 25 / 32] : 0f;
                // 96 columns over 64 readings: reading x sits at column 1.5 x + 0.25; the sensor's edge (29.5) at 44.5, the
                // picture's (30) at 45.25
                Check(w1 == 96 && h1 == 96 && e0 == 45 && e1 == 46 && Mathf.Abs(nearV - 1f) < 0.02f && Mathf.Abs(farV - 3f) < 0.05f,
                    $"sharp depth: {w1} x {h1} from 64 x 64; edge at column {e0} from the depth alone (sensor 45), {e1} with the picture (picture 46); flat parts kept ({nearV:F2} m, {farV:F2} m)");
                // temporal: a wall 2 m away read with ±2 cm noise each frame: the noise drops frame after frame
                var rnd = new System.Random(3);
                float WallNoise() => (float)(rnd.NextDouble() - 0.5) * 0.04f;
                float Err(float[] d, int w, int hh)
                {
                    double sum = 0; int cnt = 0;
                    for (int y = hh / 4; y < hh * 3 / 4; y++) for (int x = w / 4; x < w * 3 / 4; x++) { float e = d[y * w + x] - 2f; sum += e * e; cnt++; }
                    return cnt > 0 ? (float)Math.Sqrt(sum / cnt) : 1f;
                }
                shell.SetCameraForTest(null, default, default, default, default, default);
                var n0 = DepthTexture(mm, eyeP, 32, 32, (x, y, dir) => (2f + WallNoise()) / dir.z);
                shell.SetDepthForTest(n0, new[] { mm, mm }, zp);
                float err0 = Err(ReadSharp(shell, out int nw, out int nh), nw, nh);
                UnityEngine.Object.DestroyImmediate(n0);
                for (int k = 0; k < 10; k++)
                {
                    var nk = DepthTexture(mm, eyeP, 32, 32, (x, y, dir) => (2f + WallNoise()) / dir.z);
                    shell.SetDepthForTest(nk, new[] { mm, mm }, zp, keepHistory: true);
                    UnityEngine.Object.DestroyImmediate(nk);
                }
                float err1 = Err(ReadSharp(shell, out nw, out nh), nw, nh);
                Check(err1 < err0 * 0.75f, $"sharp depth over time: noise {err0 * 100f:F2} cm on the first frame, {err1 * 100f:F2} cm after 10 (raw ±2 cm)");
                UnityEngine.Object.DestroyImmediate(edgeTex);
                UnityEngine.Object.DestroyImmediate(pic);
            }
            // REAL THINGS IN FRONT OF WINDOWS: a hand-sized thing 0.5 m away in front of the terminal (0.9 m): the terminal
            // is hidden there and only there; the setting off covers it again
            {
                // the terminal where these pictures expect it (other sections move it): 0.85 m ahead, a little below the eyes
                Vector3 framePos = frame != null ? frame.transform.position : default;
                Quaternion frameRot = frame != null ? frame.transform.rotation : default;
                if (frame != null) { if (!frame.IsOpen) frame.Open(); frame.PlaceInFront(head, 0.85f, 0.12f); frame.ForceRefresh(); }
                shell.SetFocus(false, false);
                shell.TickFocusForTest(1f);
                shell.Settings.occludeUi = true;
                var eyeP = head.position;
                var viewM = Matrix4x4.Scale(new Vector3(1f, 1f, -1f)) * Matrix4x4.TRS(eyeP, Quaternion.identity, Vector3.one).inverse;
                var mm = Matrix4x4.Perspective(60f, 320f / 240f, 0.1f, 20f) * viewM;
                var occTex = DepthTexture(mm, eyeP, 160, 120, (x, y, dir) => (Mathf.Abs(x - 80) < 16 && Mathf.Abs(y - 60) < 16 ? 0.5f : 3f) / dir.z);
                shell.SetDepthForTest(occTex, new[] { mm, mm }, DepthZParams(0.1f, 20f));
                shell.TickFocusForTest(0.2f);
                UnityEngine.Object.DestroyImmediate(RenderRgba(null, eyeP, Quaternion.identity, 60f, 320, 240));
                var occ = RenderRgba("ui-occlusion.png", eyeP, Quaternion.identity, 60f, 320, 240);
                float aThing = occ.GetPixel(160, 120).a, aPanel = occ.GetPixel(160, 60).a;
                shell.Settings.occludeUi = false;
                shell.TickFocusForTest(0.2f);
                UnityEngine.Object.DestroyImmediate(RenderRgba(null, eyeP, Quaternion.identity, 60f, 320, 240));
                var off = RenderRgba(null, eyeP, Quaternion.identity, 60f, 320, 240);
                float aOff = off.GetPixel(160, 120).a;
                shell.Settings.occludeUi = true;
                Check(aThing < 0.05f && aPanel > 0.9f && aOff > 0.9f, $"real things in front of windows: the terminal is hidden where something real is 0.5 m away ({aThing:F2}), kept elsewhere ({aPanel:F2}); setting off: covered again ({aOff:F2})");
                // a window marked clear (no room point in front of it) skips the per-pixel test: the same thing no longer
                // cuts it (the shell marks it so only when readings show nothing in front); and the CPU rule itself
                shell.PanelClearOverride = true;
                shell.TickFocusForTest(0.1f);
                UnityEngine.Object.DestroyImmediate(RenderRgba(null, eyeP, Quaternion.identity, 60f, 320, 240));
                var skip = RenderRgba(null, eyeP, Quaternion.identity, 60f, 320, 240);
                float aSkip = skip.GetPixel(160, 120).a;
                UnityEngine.Object.DestroyImmediate(skip);
                shell.PanelClearOverride = null;
                shell.TickFocusForTest(0.1f);
                bool clearAfter = frame != null && shell.PanelClearForTest(frame);
                var toPanel = Matrix4x4.TRS(eyeP + Vector3.forward, Quaternion.identity, Vector3.one).inverse; // a 0.8 x 0.5 m window 1 m ahead
                var inFront = new List<Vector3> { eyeP + new Vector3(0.1f, 0f, 0.5f) };                         // a thing 0.5 m away, in its view
                var beside = new List<Vector3> { eyeP + new Vector3(0.6f, 0f, 0.5f) };                          // 0.5 m away, beside it (seen at 1.2 m)
                var behind = new List<Vector3> { eyeP + new Vector3(0f, 0f, 1.6f) };                            // the wall behind it
                bool rFront = MetaCodexShell.MayBeInFront(inFront, eyeP, toPanel, 0.8f, 0.5f, 0.15f);
                bool rBeside = MetaCodexShell.MayBeInFront(beside, eyeP, toPanel, 0.8f, 0.5f, 0.15f);
                bool rBehind = MetaCodexShell.MayBeInFront(behind, eyeP, toPanel, 0.8f, 0.5f, 0.15f);
                Check(frame == null || (aSkip > 0.9f && !clearAfter && rFront && !rBeside && !rBehind),
                    $"a window with nothing real in front skips the depth test (marked clear: {aSkip:F2} where the thing is; unmarked without room readings: {(clearAfter ? "clear" : "tested")}); in front {rFront}, beside {rBeside}, behind {rBehind}");
                // the Horizon OS menu: while it is open (input focus lost / system gesture) the windows step aside
                shell.UiStepAsideOverride = true;
                shell.TickFocusForTest(0.5f);
                UnityEngine.Object.DestroyImmediate(RenderRgba(null, eyeP, Quaternion.identity, 60f, 320, 240));
                var aside = RenderRgba("ui-step-aside.png", eyeP, Quaternion.identity, 60f, 320, 240);
                float aAside = aside.GetPixel(160, 60).a;
                shell.UiStepAsideOverride = null;
                shell.TickFocusForTest(0.5f);
                UnityEngine.Object.DestroyImmediate(RenderRgba(null, eyeP, Quaternion.identity, 60f, 320, 240));
                var back = RenderRgba(null, eyeP, Quaternion.identity, 60f, 320, 240);
                float aBack = back.GetPixel(160, 60).a;
                Check(aAside < 0.05f && aBack > 0.9f, $"the Horizon OS menu: the windows step aside while it is open ({aAside:F2}) and come back after ({aBack:F2})");
                // the system gesture itself (palm towards you, pinch) hides nothing: only an opened system menu does
                var sim = shell.SimHands;
                sim.SetOpen(Handedness.Left, eyeP + new Vector3(-0.1f, -0.15f, 0.35f), Vector3.up, -Vector3.forward);
                sim.Frames[0].SystemGesture = true;
                shell.TickGesturesForTest(0.1f);
                shell.TickFocusForTest(0.5f);
                float hideDuringGesture = Shader.GetGlobalFloat("_McxUiHide");
                sim.Frames[0].SystemGesture = false;
                sim.Frames[0].Tracked = false;
                shell.TickGesturesForTest(0.1f);
                shell.TickFocusForTest(0.5f);
                Check(hideDuringGesture < 0.01f, $"the Horizon OS hand gesture (palm towards you, pinch) leaves the windows in place (UI hide {hideDuringGesture:F2})");
                foreach (var t in new[] { occ, off, aside, back }) UnityEngine.Object.DestroyImmediate(t);
                shell.SetDepthForTest(null, null, default);
                UnityEngine.Object.DestroyImmediate(occTex);
                shell.SetFocus(true, false);
                if (frame != null) frame.transform.SetPositionAndRotation(framePos, frameRot);
            }
            // hands: always clear, even at BLACKOUT
            shell.ApplyFocusPreset("blackout");
            shell.PoseHand(Handedness.Right, 0.1f, 0.1f, 0.1f, 0.1f, 0.1f);
            shell.TickGesturesForTest(0.1f);
            shell.TickFocusForTest(0.6f); // past the 0.4 s fade-in (focus was turned on again just before)
            var palmW = shell.Gestures.Right.Frame.Palm;
            var hands = RenderRgba("ui-focus-hands.png", head.position, Quaternion.LookRotation((palmW - head.position).normalized), 60f, 320, 240);
            float aPalm = hands.GetPixel(160, 120).a, aAround = hands.GetPixel(10, 230).a;
            Check(aPalm < 0.1f && aAround > 0.95f, $"hands stay visible in the dark (palm {aPalm:F2}, around {aAround:F2})");
            UnityEngine.Object.DestroyImmediate(hands);
            // ... and in front of a window: SETTINGS (opaque) 0.4 m behind the palm, focus off, no depth: the hand
            // cut-outs are drawn after the windows and pass their depth only where the hand is in front
            {
                shell.SetFocus(false, false);
                shell.TickFocusForTest(1f);
                shell.OpenPanel("settings");
                var sp = shell.PanelsById["settings"];
                var dirP = (palmW - head.position).normalized;
                sp.transform.SetPositionAndRotation(palmW + dirP * 0.4f, Quaternion.LookRotation(dirP));
                shell.TickFocusForTest(0.2f);
                var look = Quaternion.LookRotation(dirP);
                UnityEngine.Object.DestroyImmediate(RenderRgba(null, head.position, look, 60f, 320, 240));
                var front = RenderRgba("ui-hand-in-front.png", head.position, look, 60f, 320, 240);
                float aHand = front.GetPixel(160, 120).a;
                var px = front.GetPixels();
                float win = px.Count(c => c.a > 0.9f) / (float)px.Length; // the window behind fills much of the view
                Check(aHand < 0.1f && win > 0.2f, $"a hand in front of a window stays visible (palm {aHand:F2}; the window covers {win * 100f:0}% of the view around it)");
                UnityEngine.Object.DestroyImmediate(front);
                // the same with "Real things in front of windows" off (the owner's headset, 2026-11-10): the hands still show,
                // the setting is only the room's depth
                shell.Settings.occludeUi = false;
                shell.TickFocusForTest(0.2f);
                UnityEngine.Object.DestroyImmediate(RenderRgba(null, head.position, look, 60f, 320, 240));
                var front2 = RenderRgba("ui-hand-in-front-no-depth.png", head.position, look, 60f, 320, 240);
                float aHand2 = front2.GetPixel(160, 120).a;
                int cut = shell.HandCutoutsShownForTest;
                UnityEngine.Object.DestroyImmediate(front2);
                shell.Settings.occludeUi = true;
                shell.TickFocusForTest(0.2f);
                Check(aHand2 < 0.1f && cut >= 7, $"with REAL THINGS IN FRONT off a hand in front of a window still shows (palm {aHand2:F2}, {cut} cut-outs drawn)");
                shell.ClosePanel("settings");
            }
            shell.SimHands.Hide(Handedness.Right);
            shell.TickGesturesForTest(0.1f);
            shell.FloorYOverride = null;
            shell.SetFocus(false, false);
            shell.TickFocusForTest(1f);
            Check(!shell.FocusSphereForTest.activeSelf, "ambient focus off: the sphere is gone");
            Check(shell.RunAction("focus:toggle") == null && shell.Settings.focusEnabled, "action focus:toggle");
            shell.SetFocus(false, false);
            shell.TickFocusForTest(1f);
            var st = shell.PanelsById["settings"];
            shell.OpenPanel("settings");
            st.Elements.OfType<UiTabs>().First().Select(2);
            st.ForceRefresh();
            RenderPanel(st, "ui-settings-focus.png");
            st.Elements.OfType<UiTabs>().First().Select(0);
        }

        private static void SystemPanel(MetaCodexShell shell, string tmp)
        {
            foreach (var id in shell.PanelIds.ToList()) shell.ClosePanel(id);
            shell.ParseTools("T|git|/usr/bin/git|git version 2.47.3\nT|node|/usr/local/bin/node|v24.11.0\nT|rustc|/root/.cargo/bin/rustc|rustc 1.91.0 (f8297e351 2025-10-28)\nC|apt|512\nO|Debian GNU/Linux 13 (trixie) aarch64\n");
            var tools = shell.ToolsForTest;
            Check(tools.First(x => x.Name == "git").Version == "2.47.3" && tools.First(x => x.Name == "node").Version == "24.11.0" && tools.First(x => x.Name == "rustc").Version == "1.91.0" && !tools.First(x => x.Name == "bun").Found,
                "tool versions parsed (git 2.47.3, node 24.11.0, rustc 1.91.0; bun missing)");
            string d = Path.Combine(tmp, "sizes");
            Directory.CreateDirectory(Path.Combine(d, "sub"));
            File.WriteAllBytes(Path.Combine(d, "a.bin"), new byte[1000]);
            File.WriteAllBytes(Path.Combine(d, "sub", "b.bin"), new byte[2500]);
            Check(MetaCodexShell.DirSize(d) == 3500, "folder sizes add up (3500 bytes)");
            shell.OpenPanel("system");
            shell.SystemTabsForTest.Select(0);
            shell.RefreshTools(true);
            var sw = System.Diagnostics.Stopwatch.StartNew();
            while (shell.SystemBusyForTest && sw.Elapsed.TotalSeconds < 120) shell.PumpForTest(1f);
            int found = shell.ToolsForTest.Count(x => x.Found);
            Check(shell.ToolsForTest.Any(x => x.Name == "git" && x.Found && !string.IsNullOrEmpty(x.Version)), $"TOOLS: checked in Linux (WSL here): {found} found in {sw.Elapsed.TotalSeconds:F0} s: " + string.Join(", ", shell.ToolsForTest.Where(x => x.Found).Select(x => x.Name + " " + x.Version)));
            string inv = shell.ToolsInventoryPath;
            var invJ = File.Exists(inv) ? JObject.Parse(File.ReadAllText(inv)) : null;
            Check(invJ != null && (invJ["tools"] as JArray).Any(x => (string)x["name"] == "git" && (bool)x["installed"] && !string.IsNullOrEmpty((string)x["version"])),
                "the installed-tools inventory for agents is written (" + inv + ")");
            Check(shell.SystemCommand("tools").Contains("git\t") && shell.SystemCommand("tools json").Contains("\"installed\""), "control port: system tools / tools json");
            // native Quest apps (mcx-app, skill quest-native-apps): what the agents call on the control port
            string apkRel = "mcx-test-" + Guid.NewGuid().ToString("N").Substring(0, 6) + "/hello.apk";
            string apkAndroid = Path.Combine(Application.persistentDataPath, apkRel);
            Directory.CreateDirectory(Path.GetDirectoryName(apkAndroid));
            File.WriteAllBytes(apkAndroid, new byte[] { 0x50, 0x4B, 3, 4 });
            string inst = shell.SystemCommand("install-apk /mnt/quest/" + apkRel);
            bool mapped = (SystemBridge.LastResult ?? "").Replace('\\', '/').EndsWith(apkRel);
            Check(inst.StartsWith("installing") && mapped && shell.SystemCommand("install-apk /root/notes.txt").StartsWith("error: system install-apk")
                && shell.SystemCommand("install-apk /mnt/quest/../x.apk").StartsWith("error: no file") && shell.AndroidPathOf("relative.apk") == null,
                $"control port: system install-apk maps /mnt/quest/{apkRel} to the app's shared folder and hands it to the installer; refuses non-APKs, /../ and relative paths ({inst})");
            Directory.Delete(Path.GetDirectoryName(apkAndroid), true);
            Check(shell.SystemCommand("can-install").StartsWith("yes") && shell.SystemCommand("app com.metacodex.user.x").StartsWith("not installed")
                && shell.SystemCommand("app").StartsWith("error") && shell.SystemCommand("launch").StartsWith("error") && !shell.SystemCommand("first-use").StartsWith("running"),
                "control port: system can-install / app / launch / first-use answer (dry run; first use never runs in the Editor)");
            var catalog = MetaCodexShell.ToolCatalog;
            Check(catalog.Any(c => c.Name == "android-sdk" && c.Group == "android") && catalog.Any(c => c.Name == "openxr" && c.Group == "android")
                && catalog.First(c => c.Name == "ndk").Install == "mcx-setup ndk" && MetaCodexShell.FirstUseCommand.Contains("mcx-setup quest"),
                "TOOLS: android.jar and OpenXR rows; every native app download is pinned in mcx-setup (the NDK row and first use call it)");
            // no logos or pictures (owner, 2026-11-10): the tools are a plain list in groups, and each one says what it does
            var noAbout = MetaCodexShell.ToolCatalog.Where(c => string.IsNullOrWhiteSpace(MetaCodexShell.AboutOf(c.Name)) || MetaCodexShell.AboutOf(c.Name).Length > 60).Select(c => c.Name).ToList();
            Check(noAbout.Count == 0 && MetaCodexShell.ToolAbout.Keys.All(k => MetaCodexShell.ToolCatalog.Any(c => c.Name == k)),
                noAbout.Count == 0 ? $"every one of the {MetaCodexShell.ToolCatalog.Length} tools says what it does in a few words (and no description is left over)" : "tools without a short description: " + string.Join(", ", noAbout));
            Check(Resources.Load<Texture2D>("ToolIcons/tool-icons") == null && !MetaCodexShell.ToolCatalog.Any(c => c.Name == "gh")
                && !MetaCodexShell.ToolGroups.Any(g => g.blurb.IndexOf("github", StringComparison.OrdinalIgnoreCase) >= 0),
                "no tool logos ship (Resources/ToolIcons is gone); git is just git: no host-specific CLI and no hosting company in the list");
            string[] gone = { "php", "ruby", "lua", "deno" };
            Check(!MetaCodexShell.ToolCatalog.Any(c => gone.Contains(c.Name)) && MetaCodexShell.ToolCatalog.All(c => MetaCodexShell.ToolGroups.Any(g => g.id == c.Group)),
                "the catalog: grouped, without php / ruby / lua / deno, with the Android and Quest group (" + string.Join(", ", MetaCodexShell.ToolCatalog.Where(c => c.Group == "android").Select(c => c.Name)) + ")");
            var sp = shell.PanelsById["system"];
            sp.ForceRefresh();
            RenderPanel(sp, "ui-system-tools.png");
            var list = sp.Elements.OfType<UiToolList>().First();
            var rows = list.DrawnRows.ToList();
            var heads = list.DrawnHeaders.ToList();
            var stack = heads.Select(h => h.rect).Concat(rows.Select(r => r.rect)).OrderBy(r => r.y).ToList();
            bool stacked = stack.Zip(stack.Skip(1), (u, v) => v.y >= u.yMax - 0.00001f).All(x => x);
            bool inside = stack.All(r => r.y >= list.Rect.y - 0.0005f && r.yMax <= list.Rect.yMax + 0.0005f && r.width <= list.Rect.width + 0.0001f);
            var agentRows = rows.Where(r => r.row.Group == "agents").Select(r => r.row.Name).ToList();
            Check(heads.Count >= 1 && heads[0].group.Id == "agents" && rows.Count >= 8 && stacked && inside && agentRows.SequenceEqual(new[] { "claude", "codex", "opencode", "mqh" })
                && rows.All(r => !string.IsNullOrEmpty(r.row.About)),
                $"TOOLS is a list: {heads.Count} group header(s) and {rows.Count} rows in view, stacked without overlaps and inside the list, in the catalog's order (agents: {string.Join(" ", agentRows)}), each saying what it does (stacked {stacked}, inside {inside})");
            list.ScrollTo("android");
            RenderPanel(sp, "ui-system-tools-android.png");
            Check(list.DrawnRows.Any(d => d.row.Name == "ndk") && list.DrawnRows.Any(d => d.row.Name == "glslc"), "scrolled to ANDROID · QUEST: ndk, adb, build-tools, glslc in view");
            list.ScrollTo("agents");
            shell.SystemTabsForTest.Select(1);
            sw.Restart();
            while (shell.SystemBusyForTest && sw.Elapsed.TotalSeconds < 120) shell.PumpForTest(1f);
            Check(shell.LinuxBytesForTest >= 0, $"STORAGE measured in {sw.Elapsed.TotalSeconds:F0} s");
            sp.ForceRefresh();
            RenderPanel(sp, "ui-system-storage.png");
            shell.SystemTabsForTest.Select(0);
        }

        // ------------------------------------------------------------------ pictures

        /// A panel in the orb's plane (through the orb, facing the eye; x to the user's right, y up): its centre, and how
        /// far the nearest point of its rectangle (its scaled size, laid in that plane) is from the orb's centre (metres).
        /// Items are turned to the eye one by one; their centres lie in the plane, which is where the arc is laid out.
        private static (Vector2 centre, float near) OrbPlaneNear(UiPanel p, Vector3 orb, Vector3 eye)
        {
            var fwd = (orb - eye).normalized;
            var right = Vector3.Cross(Vector3.up, fwd).normalized;
            var up = Vector3.Cross(fwd, right);
            var w = p.transform.position - orb;
            var c = new Vector2(Vector3.Dot(w, right), Vector3.Dot(w, up));
            float hx = p.Width * p.transform.lossyScale.x * 0.5f, hy = p.Height * p.transform.lossyScale.y * 0.5f;
            var d = new Vector2(Mathf.Max(0f, Mathf.Abs(c.x) - hx), Mathf.Max(0f, Mathf.Abs(c.y) - hy));
            return (c, d.magnitude);
        }

        private static void RenderPanel(UiPanel p, string file)
        {
            p.ForceRefresh();
            float w = p.Width * p.transform.lossyScale.x, h = p.Height * p.transform.lossyScale.y;
            float tan = Mathf.Tan(25f * Mathf.Deg2Rad);
            float dist = Mathf.Max((h / 2f) / tan, (w / 2f) / (tan * 1.6f)) + 0.05f;
            var pos = p.transform.position - p.transform.forward * dist;
            RenderAt(file, pos, p.transform.rotation, 50f, 1600, 1000);
        }

        private static void Render(MetaCodexShell shell, string file, Vector3 pos, Quaternion rot, float fov, int w, int h) => RenderAt(file, pos, rot, fov, w, h);

        private static void RenderAt(string file, Vector3 pos, Quaternion rot, float fov, int w, int h)
        {
            // bent panels: the app bends text in LateUpdate; do it here too, or texts sit flat on curved shapes
            foreach (var up in UnityEngine.Object.FindObjectsByType<UiPanel>(FindObjectsSortMode.None)) up.ApplyBendNow();
            var camGo = new GameObject("uitest-cam") { hideFlags = HideFlags.DontSave };
            try
            {
                var cam = camGo.AddComponent<Camera>();
                cam.clearFlags = CameraClearFlags.SolidColor;
                cam.backgroundColor = new Color(0.10f, 0.11f, 0.13f); // stands in for passthrough
                cam.fieldOfView = fov;
                cam.nearClipPlane = 0.02f;
                cam.transform.SetPositionAndRotation(pos, rot);
                var rt = new RenderTexture(w, h, 24) { antiAliasing = 4 };
                cam.targetTexture = rt;
                cam.aspect = (float)w / h;
                cam.Render();
                RenderTexture.active = rt;
                var tex = new Texture2D(w, h, TextureFormat.RGB24, false);
                tex.ReadPixels(new Rect(0, 0, w, h), 0, 0);
                tex.Apply();
                RenderTexture.active = null;
                File.WriteAllBytes(Path.Combine(MetaCodexSetup.ReportDir, file), tex.EncodeToPNG());
                UnityEngine.Object.DestroyImmediate(tex);
                cam.targetTexture = null;
                rt.Release();
                UnityEngine.Object.DestroyImmediate(rt);
                Line("picture: " + file);
            }
            finally { UnityEngine.Object.DestroyImmediate(camGo); }
        }
    }
}

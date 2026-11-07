using System;
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
    public static class MetaCodexUiTest
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
            Progress("before " + section);
            string only = Environment.GetEnvironmentVariable("MCX_UITEST_ONLY");
            if (string.IsNullOrWhiteSpace(only) && File.Exists(Path.Combine("Bench", "uitest-only"))) only = File.ReadAllText(Path.Combine("Bench", "uitest-only")).Trim();
            return string.IsNullOrWhiteSpace(only) || only.Split(',').Any(x => x.Trim().Length > 0 && section.StartsWith(x.Trim(), StringComparison.OrdinalIgnoreCase));
        }

        [MenuItem("MetaCodex/4. UI self-test")]
        public static string Run()
        {
            _report = new StringBuilder();
            _ok = true;
            string dir = MetaCodexSetup.ReportDir;
            Directory.CreateDirectory(dir);
            SystemBridge.DryRun = true;
            string tmp = Path.GetFullPath(Path.Combine("Temp", "mcx-uitest"));
            if (Directory.Exists(tmp)) Directory.Delete(tmp, true);
            Directory.CreateDirectory(tmp);
            string scripts = Path.Combine(tmp, "scripts");

            foreach (var shaderName in new[] { "MetaCodex/SciFiUI", "MetaCodex/Beam" })
            {
                var sh = Shader.Find(shaderName);
                if (sh == null) { Fail("shader missing: " + shaderName); continue; }
                int errors = ShaderUtil.GetShaderMessages(sh).Count(m => m.severity == UnityEditor.Rendering.ShaderCompilerMessageSeverity.Error);
                Check(errors == 0, $"shader {shaderName} compiles ({errors} errors)");
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
                shell.Build(scripts, hands);
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
            }
            catch (Exception ex) { Fail("exception: " + ex); }
            finally
            {
                SystemBridge.DryRun = false;
                UiKeyboard.EditorProvider = null;
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
            string needed = "▶▤◫⎇◎ƒ⚙⌨⌖⟲✕↑⌂▸·↪●◆◇◎▣⚠✓▌•↓−⟳✋⚿☰⇗⎘◈✎⇢⇣○■▲▼→";
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
            foreach (var id in new[] { "settings", "files", "browser", "scripts", "terminals", "dock", "vault" })
                RenderPanel(shell.PanelsById[id], "ui-" + id + ".png");
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
            foreach (var id in shell.PanelIds.ToList()) if (id != "settings" && id != "files") shell.ClosePanel(id);
            var settings = shell.PanelsById["settings"];
            var tabs = settings.Elements.OfType<UiTabs>().First();
            var displayTab = settings.Elements.OfType<UiButton>().First(b => b.Text == "DISPLAY");
            Click(r, settings, displayTab.Rect.center);
            Check(tabs.Selected == 1, "tab click switches to DISPLAY");
            settings.ForceRefresh();
            RenderPanel(settings, "ui-settings-display.png");
            var lookTab = settings.Elements.OfType<UiButton>().First(b => b.Text == "LOOK");
            Click(r, settings, lookTab.Rect.center);
            Check(tabs.Selected == 2, "tab click switches to LOOK");
            var fov = settings.Elements.OfType<UiToggle>().First(t => t.Text.StartsWith("Foveated"));
            bool before = fov.Value;
            Click(r, settings, fov.Rect.center);
            Check(fov.Value != before && shell.Settings.foveation == fov.Value, "toggle flips Foveated rendering and the setting");
            settings.ForceRefresh();
            RenderPanel(settings, "ui-settings-look.png");
            Click(r, settings, fov.Rect.center); // back off

            var gestTab = settings.Elements.OfType<UiButton>().First(b => b.Text == "GESTURES");
            Click(r, settings, gestTab.Rect.center);
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

            var daemonTab = settings.Elements.OfType<UiButton>().First(b => b.Text == "DAEMON");
            Click(r, settings, daemonTab.Rect.center);
            UiKeyboard.EditorProvider = old => "http://127.0.0.1:38990";
            var url = settings.Elements.OfType<UiTextField>().First(f => f.Caption == "MQH URL");
            Click(r, settings, url.Rect.center);
            Check(shell.Settings.mqhUrl == "http://127.0.0.1:38990", "text field edit (keyboard) updates the MQH URL");
            Check(File.Exists(Path.Combine("Temp", "mcx-uitest", "settings.json")) && File.ReadAllText(Path.Combine("Temp", "mcx-uitest", "settings.json")).Contains("38990"), "settings persisted to disk");

            // files: a list row navigates
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

            // move a panel by pinching its header
            var before2 = settings.transform.position;
            Click(r, settings, new Vector2(0.2f, 0.015f), new Vector2(0.35f, 0.06f));
            Check(Vector3.Distance(settings.transform.position, before2) > 0.05f, "pinch-drag on the header moves the panel");

            // selection tool: rectangle on a panel -> event to scripts
            shell.SetSelectionMode(true);
            int selects = 0;
            r.Selected += (p, rect) => selects++;
            Click(r, files, new Vector2(0.10f, 0.15f), new Vector2(0.30f, 0.30f));
            shell.SetSelectionMode(false);
            Check(selects == 1 && shell.LastSelection.rect.width > 0.15f, $"select tool reports a {shell.LastSelection.rect.width * 100:F0}x{shell.LastSelection.rect.height * 100:F0} cm area");
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
            Check(shell.PanelsById["git"].IsOpen && !shell.PanelsById["files"].IsOpen, "edited script reloaded live: double clap now opens git");

            // 3. a runaway script is stopped, the others keep working
            File.WriteAllText(Path.Combine(dir, "bad.lua"), "gesture.on('clap', function() while true do end end)\n");
            host.Scan();
            shell.ClosePanel("git");
            t += 1.5f;
            var sw = System.Diagnostics.Stopwatch.StartNew();
            Claps(shell, hands, ref t, 2);
            var bad = host.Entries.First(e => e.Name == "bad");
            Check(bad.Error != null && bad.Error.Contains("instructions"), $"endless loop stopped by the instruction budget in {sw.ElapsedMilliseconds} ms");
            Check(shell.PanelsById["git"].IsOpen, "other scripts unaffected");

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

            var sp = shell.PanelsById["scripts"];
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
            var gp = shell.PanelsById["git"];
            typeof(MetaCodexShell).GetMethod("GitShow", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance).Invoke(shell, new object[] { st });
            gp.ForceRefresh();
            RenderPanel(gp, "ui-git.png");
        }

        private static void Layout(MetaCodexShell shell, Transform head, UiPanel frame)
        {
            foreach (var id in new[] { "dock", "settings", "files", "git", "scripts", "browser" }) shell.OpenPanel(id);
            shell.SetLayout("float");
            shell.TickLayoutForTest(0.05f, 80);
            var c = shell.CarouselCenter;
            float r = shell.Settings.carouselRadius;
            bool onCircle = true;
            foreach (var id in new[] { "settings", "files", "git" })
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
            head.position += new Vector3(0.5f, 0.15f, 0.3f); // walk and nod: follow x/z only
            shell.TickLayoutForTest(0.05f, 120);
            var c2 = shell.CarouselCenter;
            Check(Mathf.Abs(c2.x - head.position.x) < 0.02f && Mathf.Abs(c2.z - head.position.z) < 0.02f && Mathf.Abs(c2.y - c.y) < 1e-4f, "carousel follows walking (x/z), not head height");
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
            var core = host.Find("plugin/core-poses");
            var swipes = host.Find("plugin/swipes");
            Check(core != null && core.Error == null && core.Poses == 5, $"starter plugin core-poses loaded ({core?.Poses} poses{(core?.Error != null ? ", " + core.Error : "")})");
            Check(swipes != null && swipes.Error == null && swipes.Detectors == 3, $"starter plugin swipes loaded ({swipes?.Detectors} Lua detectors{(swipes?.Error != null ? ", " + swipes.Error : "")})");
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
            Check(!shell.PanelsById["git"].IsOpen && shell.Toasts.Last == "pushed right", "push detector closed git (reaction ran)");

            // no-code binding
            shell.AddBinding("victory", "open:vault");
            hands.Hide(Handedness.Right);
            shell.TickGesturesForTest(0.2f);
            shell.PoseHand(Handedness.Right, 1, 0, 0, 1, 1);
            shell.TickGesturesForTest(0.4f);
            Check(shell.PanelsById["vault"].IsOpen, "binding victory → open:vault works");
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
            Check(shell.ControlCommand("action", "open:git") == "ok" && shell.PanelsById["git"].IsOpen, "control port: action open:git");
            Check(shell.ControlCommand("gesture", "bind clap toggle:files") == "ok" && shell.ControlCommand("gesture", "bindings").Contains("clap"), "control port: gesture bind");
            shell.ControlCommand("gesture", "unbind clap");

            // the panel, tab by tab (alone, so the picture shows only it)
            foreach (var id in shell.PanelIds.ToList()) shell.ClosePanel(id);
            shell.OpenPanel("gestures");
            var gp = shell.PanelsById["gestures"];
            var tabs = gp.Elements.OfType<UiTabs>().First();
            shell.PoseHand(Handedness.Right, 0, 1, 1, 1, 1, Vector3.up);
            shell.PoseHand(Handedness.Left, 0.2f, 0, 0, 0, 0);
            shell.TickGesturesForTest(0.4f);
            string[] names = { "gestures", "live", "bind", "record", "plugins" };
            for (int i = 0; i < names.Length; i++)
            {
                tabs.Select(i);
                if (i == 1) shell.RefreshGestureLive();
                gp.ForceRefresh();
                RenderPanel(gp, $"ui-gestures-{names[i]}.png");
            }
            tabs.Select(0);
            hands.Hide(Handedness.Left);
            hands.Hide(Handedness.Right);
            shell.TickGesturesForTest(0.2f);
        }

        /// A gesture plugin written by a real agent from the skill (tools/agent-plugin-test.sh puts it in Bench/agent/plugins).
        private static void AgentPlugin(MetaCodexShell shell, SimulatedHands hands, string tmp)
        {
            string src = Path.GetFullPath(Path.Combine("Bench", "agent", "plugins"));
            var dirs = Directory.Exists(src) ? Directory.GetDirectories(src).Where(d => File.Exists(Path.Combine(d, "plugin.json"))).ToArray() : new string[0];
            if (dirs.Length == 0) { Line("skip agent plugin: none in Bench/agent/plugins (tools/agent-plugin-test.sh)"); return; }
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
            // the story the agent was asked for: right thumbs-up opens git; a fast push of an open palm closes it
            foreach (var id in shell.PanelIds.ToList()) shell.ClosePanel(id);
            hands.Hide(Handedness.Left);
            hands.Hide(Handedness.Right);
            shell.TickGesturesForTest(0.3f);
            shell.PoseHand(Handedness.Right, 0, 1, 1, 1, 1, Vector3.up);
            shell.TickGesturesForTest(0.6f);
            Check(shell.PanelsById["git"].IsOpen, "agent plugin: right thumbs-up opened git");
            hands.Hide(Handedness.Right);
            shell.TickGesturesForTest(0.5f);
            var palm = shell.Gestures.Head.position + new Vector3(0.2f, -0.2f, 0.3f);
            Move(shell, hands, palm, new Vector3(0f, 0f, 1.6f), Vector3.forward, 0.3f);
            shell.TickGesturesForTest(0.2f);
            Check(!shell.PanelsById["git"].IsOpen, "agent plugin: pushing an open palm away closed git");
            // a left thumbs-up must not open it (the user asked for the right hand)
            hands.Hide(Handedness.Right);
            shell.TickGesturesForTest(1.2f);
            shell.PoseHand(Handedness.Left, 0, 1, 1, 1, 1, Vector3.up);
            shell.TickGesturesForTest(0.6f);
            Check(!shell.PanelsById["git"].IsOpen, "agent plugin: a LEFT thumbs-up does nothing");
            hands.Hide(Handedness.Left);
            shell.TickGesturesForTest(0.2f);
        }

        // ------------------------------------------------------------------ voice

        /// Voice control: the fast path (phrase templates, synonyms, fillers), the actions, a Lua plugin command,
        /// then, with a dev AI service (tools/qvac-dev.sh device start), speech -> text -> command for real and the
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
            Check(r != null && r.Ok && r.Via == "fast" && !shell.PanelsById["files"].IsOpen && !shell.PanelsById["git"].IsOpen, "“close all windows” closes the panels: " + r);
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
            if (!shell.Qvac.ConnectDevNow()) { Line("skip voice AI: " + shell.Qvac.Status + " (tools/qvac-dev.sh device start)"); RenderVoice(shell); return; }
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
                Check(r != null && r.Ok && r.Command == "open_panel" && r.Transcript.ToLowerInvariant().Contains("git") && shell.PanelsById["git"].IsOpen,
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
            Check(shell.PanelsById["git"].IsOpen && (files1 - files0).magnitude < 0.02f, $"saved layout restored around the user ({(files1 - files0).magnitude * 100f:F1} cm)");
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
            if (projects.Items.Count == 0) { Line("skip thread start: no project in MQH (add one: + PROJECT…)"); return; }
            string projectId = (string)projects.Items[0].Tag;
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

        // ------------------------------------------------------------------ pictures

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

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
                if (Want("views")) Views(shell, head, tmp);
                if (Want("workspace")) Workspace(shell, head);
                if (Want("threadgroups")) ThreadGroups(shell);
                if (Want("vaultgit")) VaultGit(shell);
                if (Want("focus")) Focus(shell, head);
                if (Want("system")) SystemPanel(shell, tmp);
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
            string needed = "▶▤◫⎇◎ƒ⚙⌨⌖⟲✕↑⌂▸·↪●◆◇◎▣⚠✓▌•↓−⟳✋⚿☰⇗⎘◈✎⇢⇣○■▲▼→⑂◧◐☑☐▾❚λ⚒⛁⇅⬢✦‹›";
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
            Check(tabs.Names[tabs.Selected] == "LOOK", "tab click switches to LOOK");
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
            var gp = shell.PanelsById["vault"]; // GIT lives in the vault now
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
            Check(!shell.IsPanelOpen("git") && shell.Toasts.Last == "pushed right", "push detector closed git (reaction ran)");

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
            Check(shell.ControlCommand("action", "open:git") == "ok" && shell.IsPanelOpen("git"), "control port: action open:git");
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
                Check(r != null && r.Ok && r.Command == "open_panel" && r.Transcript.ToLowerInvariant().Contains("git") && shell.IsPanelOpen("git"),
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

        private static JObject Row(string kind, int seq, JObject fields) { var o = new JObject { ["kind"] = kind, ["sourceSeqEnd"] = seq, ["id"] = "r" + seq }; foreach (var p in fields.Properties()) o[p.Name] = p.Value; return o; }

        private static void Workspace(MetaCodexShell shell, Transform head)
        {
            foreach (var id in shell.PanelIds.ToList()) shell.ClosePanel(id);
            shell.SetLayout("fixed");
            shell.Settings.threadWorkspace = true;
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
            var ws = shell.PanelsById["thread-ws"];
            shell.TickLayoutForTest(0.05f, 3);
            var rel = th.transform.InverseTransformPoint(ws.transform.position);
            Check(th.IsOpen && ws.IsOpen && rel.x > th.Width * 0.5f && Mathf.Abs(rel.y) < 0.01f, $"the workspace opens beside the thread, to its right ({rel.x * 100f:F0} cm from its centre)");
            var touched = shell.Touched.Values.Where(f => f.ByThread).ToList();
            Check(touched.Count == 2 && touched.Any(f => f.Path == "unity/Assets/MetaCodex/Runtime/Shell/ShellWorkspace.cs" && f.Kind == "add" && f.Added == 120) && touched.Any(f => f.Path.EndsWith("UiViz.cs") && f.Kind == "modify" && f.Removed == 12),
                "CHANGES: the thread's file changes, paths relative to the workspace: " + string.Join(", ", touched.Select(f => $"{f.Path} {f.Kind} +{f.Added}/-{f.Removed}")));
            Check(shell.ChangeMapForTest.Items.Count >= 2, "the change map has a tile per file: " + shell.ChangeMapForTest.Items.Count);
            string tl = shell.TimelineTextForTest;
            Check(tl.Contains("[▣ Diagram]") && !tl.Contains("```") && tl.Contains("+120"), "THREAD timeline: view block as a marker, file changes with their line counts");
            Check(shell.Views.Count(x => x.ThreadId == "thr_selftest") == 1 && shell.PanelsById.Keys.Any(k => k.StartsWith("view-") && shell.PanelsById[k].IsOpen), "the agent's diagram became a view and opened");
            string chips = shell.ChipsForTest;
            Check(chips.Contains("claude-opus") && chips.Contains("High") && chips.Contains("Full access") && chips.Contains("checkout") && chips.Contains("42%"), "composer chips: model + effort, access, workspace, context: " + chips);
            shell.PickChangedFileForTest("unity/Assets/MetaCodex/Runtime/Shell/ShellWorkspace.cs");
            Check(shell.DiffViewForTest.LineCount >= 8 && shell.DiffHeadForTest.Contains("+120"), $"picking a file shows its diff ({shell.DiffViewForTest.LineCount} lines)");
            // the thread moves: the workspace follows; dragged elsewhere it keeps its new place relative to the thread
            th.transform.position += new Vector3(0.2f, 0.1f, 0f);
            shell.TickLayoutForTest(0.05f, 2);
            var rel2 = th.transform.InverseTransformPoint(ws.transform.position);
            Check((rel2 - rel).magnitude < 0.001f, "the workspace follows the thread panel");
            // more work arrives: a new file lights up
            shell.AppendTimelineForTest(new JArray { Row("work", 9, new JObject { ["workKind"] = "file-change", ["status"] = "completed", ["change"] = new JObject { ["path"] = root + "/docs/ui.md", ["kind"] = "update", ["movePath"] = null, ["diff"] = null, ["diffStats"] = new JObject { ["added"] = 8, ["removed"] = 2 } } }) });
            var fresh = shell.Touched.TryGetValue("docs/ui.md", out var fr) ? fr : null;
            Check(fresh != null && Time.unscaledTime - fresh.Time < 1f && shell.ChangeMapForTest.Items.First(i => i.Path == "docs/ui.md").Heat > 0.9f, "a file the agent just edited lights up on the map");
            string handover = shell.HandoverContext();
            Check(handover.Contains("USER: Show me the workspace") && handover.Contains("Files touched") && handover.Contains("ShellWorkspace.cs") && !handover.Contains("```"), "fork to another agent: the conversation and touched files are handed over (" + handover.Length + " chars)");
            shell.ShowPermissionPickerForTest();
            Check(shell.PanelsById["picker"].IsOpen && shell.PickerListForTest.Items.Count == 3, "ACCESS chip: a picker with Edits / Auto / Full access");
            shell.ClosePanel("picker");
            shell.PumpForTest(2f); // git status of the repo (WSL) feeds CHANGES and GIT
            foreach (var p in new[] { th, ws }) p.ForceRefresh();
            var mid = (th.transform.position + ws.transform.position) * 0.5f;
            Render(shell, "ui-thread-workspace.png", mid - th.transform.forward * 0.95f, th.transform.rotation * Quaternion.Euler(0f, 6f, 0f), 76f, 1920, 1080);
            shell.WorkspaceTabsForTest.Select(1);
            ws.ForceRefresh();
            RenderPanel(ws, "ui-workspace-git.png");
            shell.WorkspaceTabsForTest.Select(2);
            ws.ForceRefresh();
            RenderPanel(ws, "ui-workspace-files.png");
            shell.WorkspaceTabsForTest.Select(0);
            // float: the workspace rides beside its thread on the ring, nothing overlaps
            shell.OpenPanel("files");
            shell.SetLayout("float");
            shell.TickLayoutForTest(0.05f, 100);
            var c = shell.CarouselCenter;
            float Ang(UiPanel p) { var d = p.transform.position - c; return Mathf.Atan2(d.x, d.z) * Mathf.Rad2Deg; }
            float gap = Mathf.DeltaAngle(Ang(th), Ang(ws));
            float r = new Vector2(th.transform.position.x - c.x, th.transform.position.z - c.z).magnitude;
            float expect = (th.Width * th.transform.lossyScale.x + ws.Width * ws.transform.lossyScale.x) * 0.5f / r * Mathf.Rad2Deg;
            Check(gap > 0f && Mathf.Abs(gap - expect) < 4f, $"float: the workspace sits right of its thread on the ring ({gap:F1}° apart, {expect:F1}° + gap expected)");
            var files = shell.PanelsById["files"];
            float df = Mathf.Abs(Mathf.DeltaAngle(Ang(files), Ang(ws))), dt = Mathf.Abs(Mathf.DeltaAngle(Ang(files), Ang(th)));
            float halfF = files.Width * files.transform.lossyScale.x * 0.5f / r * Mathf.Rad2Deg;
            Check(df > halfF + ws.Width * ws.transform.lossyScale.x * 0.5f / r * Mathf.Rad2Deg && dt > halfF + th.Width * th.transform.lossyScale.x * 0.5f / r * Mathf.Rad2Deg, "float: no other panel overlaps the thread + workspace pair");
            shell.SetLayout("fixed");
            shell.ClosePanel("thread");
            Check(!ws.IsOpen, "closing the thread closes its workspace");
        }

        private static void ThreadGroups(MetaCodexShell shell)
        {
            foreach (var id in shell.PanelIds.ToList()) shell.ClosePanel(id);
            long now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            var projects = new JArray
            {
                new JObject { ["id"] = "p1", ["name"] = "metacodex", ["sources"] = new JArray { new JObject { ["path"] = "/root/metacodex" } } },
                new JObject { ["id"] = "p2", ["name"] = "sandbox", ["sources"] = new JArray { new JObject { ["path"] = "/root/sandbox" } } },
                new JObject { ["id"] = "p3", ["name"] = "empty", ["sources"] = new JArray() },
            };
            JObject T(string id, string pid, string title, string st, long ago, bool ask = false) => new JObject { ["id"] = id, ["projectId"] = pid, ["title"] = title, ["providerId"] = "claude-code", ["status"] = st, ["runtime"] = new JObject { ["displayStatus"] = st }, ["updatedAt"] = now - ago, ["hasPendingInteraction"] = ask, ["environmentIsWorktree"] = id == "t2", ["environmentBranchName"] = "feat/x" };
            var threads = new JArray { T("t1", "p1", "Voice tuning", "idle", 3600000), T("t2", "p1", "Thread workspace", "running", 60000), T("t3", "p2", "Try physics", "idle", 7200000, true) };
            shell.OpenPanel("threads");
            shell.SetThreadsForTest(projects, threads);
            var list = shell.ThreadListForTest;
            var tags = list.Items.Select(i => i.Tag as string).ToList();
            Check(tags.SequenceEqual(new[] { "p:p1", "t:t2", "t:t1", "p:p2", "t:t3", "p:p3", "p:p3" }) && list.Items[1].Indent == 1 && list.Items[0].Badge == "1 RUNNING" && list.Items[3].Badge == "1 NEED YOU",
                "THREADS grouped by project, newest first, with running / needs-you badges: " + string.Join(" ", tags));
            shell.PickThreadRowForTest(0);
            Check(shell.ThreadListForTest.Items.Count == 5 && shell.ThreadListForTest.Items[0].Icon == "▸", "tapping a project folds it");
            shell.PickThreadRowForTest(0);
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

        private static void Focus(MetaCodexShell shell, Transform head)
        {
            foreach (var id in shell.PanelIds.ToList()) shell.ClosePanel(id);
            var sh = Resources.Load<Shader>("Shaders/AmbientFocus");
            int errors = sh == null ? -1 : ShaderUtil.GetShaderMessages(sh).Count(m => m.severity == UnityEditor.Rendering.ShaderCompilerMessageSeverity.Error);
            Check(errors == 0, $"shader MetaCodex/AmbientFocus compiles ({errors} errors)");
            foreach (var name in new[] { "Shaders/StageLit", "Shaders/ViewImage" })
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
            Render(shell, "ui-focus-up.png", head.position, up, 70f, 480, 270);
            Render(shell, "ui-focus-down.png", head.position, down, 70f, 480, 270);
            float onUp = Luma("ui-focus-up.png", new Rect(0f, 0.6f, 1f, 0.4f)), onDown = Luma("ui-focus-down.png", new Rect(0.3f, 0.3f, 0.4f, 0.4f));
            Check(onUp < offUp * 0.5f && onDown > onUp * 1.5f, $"ambient focus: the room above the horizon darkens ({offUp:F3} -> {onUp:F3}); the floor near you stays ({onDown:F3})");
            shell.ApplyFocusPreset("blackout");
            shell.TickFocusForTest(1f);
            Render(shell, "ui-focus-blackout.png", head.position, Quaternion.identity, 100f, 1280, 720);
            float black = Luma("ui-focus-blackout.png", new Rect(0f, 0.85f, 1f, 0.15f));
            float panel = Luma("ui-focus-blackout.png", new Rect(0.42f, 0.4f, 0.16f, 0.2f));
            Check(black < 0.01f && panel > black + 0.02f, $"BLACKOUT: black around you ({black:F3}), the windows stay ({panel:F3})");
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
            var sp = shell.PanelsById["system"];
            sp.ForceRefresh();
            RenderPanel(sp, "ui-system-tools.png");
            shell.SystemTabsForTest.Select(1);
            sw.Restart();
            while (shell.SystemBusyForTest && sw.Elapsed.TotalSeconds < 120) shell.PumpForTest(1f);
            Check(shell.LinuxBytesForTest >= 0, $"STORAGE measured in {sw.Elapsed.TotalSeconds:F0} s");
            sp.ForceRefresh();
            RenderPanel(sp, "ui-system-storage.png");
            shell.SystemTabsForTest.Select(0);
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

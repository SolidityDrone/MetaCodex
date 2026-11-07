using System;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text;
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

                Glyphs(shell);
                Panels(shell, head, frame);
                Interactions(shell, head);
                Gestures(shell, hands);
                Scripts(shell, hands, scripts, tmp);
                AgentScript(shell, hands, tmp);
                Services(shell);
                Layout(shell, head, frame);
                Daemon(shell);
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
            string needed = "▶▤◫⎇◎ƒ⚙⌨⌖⟲✕↑⌂▸·↪●◆◇◎▣⚠✓▌•↓−⟳";
            foreach (var (name, font) in new[] { ("display", UiTheme.Current.Display), ("body", UiTheme.Current.Body) })
            {
                font.HasCharacters(needed, out uint[] missing, true, true);
                Check(missing == null || missing.Length == 0, $"{name} font + symbol fallbacks cover the UI glyphs" +
                    (missing != null && missing.Length > 0 ? ": missing " + string.Join(" ", missing.Select(c => "U+" + c.ToString("X4"))) : ""));
            }
        }

        private static void Panels(MetaCodexShell shell, Transform head, UiPanel frame)
        {
            foreach (var id in new[] { "dock", "settings", "files", "git", "browser", "scripts", "terminals" })
                Check(shell.OpenPanel(id), "panel opens: " + id);
            foreach (var p in shell.PanelsById.Values) p.ForceRefresh();
            frame.ForceRefresh();
            Render(shell, "ui-overview.png", head.position, Quaternion.identity, 100f, 1920, 1080);
            foreach (var id in new[] { "settings", "files", "browser", "scripts", "terminals", "dock" })
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

        private static void Interactions(MetaCodexShell shell, Transform head)
        {
            var r = shell.Router;
            var settings = shell.PanelsById["settings"];
            var tabs = settings.Elements.OfType<UiTabs>().First();
            var displayTab = settings.Elements.OfType<UiButton>().First(b => b.Text == "DISPLAY");
            Click(r, settings, displayTab.Rect.center);
            Check(tabs.Selected == 1, "tab click switches to DISPLAY");
            var scan = settings.Elements.OfType<UiToggle>().First(t => t.Text == "Scanlines");
            bool before = scan.Value;
            Click(r, settings, scan.Rect.center);
            Check(scan.Value != before && shell.Settings.scanlines == scan.Value, "toggle flips Scanlines and the setting");
            settings.ForceRefresh();
            RenderPanel(settings, "ui-settings-display.png");
            Click(r, settings, scan.Rect.center); // back on

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
            var host2 = new ScriptHost(shell, dir);
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
            var host = new ScriptHost(shell, dir);
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
            shell.SetLayout("fixed");
            Check(shell.PanelsById["files"].BendRadius == 0f, "fixed layout flattens the panels");
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
